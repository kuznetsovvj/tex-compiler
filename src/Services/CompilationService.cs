using Microsoft.Extensions.Logging;
using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using TexCompiler.Models;
using TexCompiler.Services;

public class CompilationService : ICompilationService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<CompilationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _logDir;
    private readonly string _pdfDir;

    private TimeSpan ProcessTimeout => TimeSpan.FromSeconds(
        _configuration.GetValue<int>("CompilationSettings:ProcessTimeoutSeconds", 600));

    public CompilationService(
        IWebHostEnvironment environment,
        ILogger<CompilationService> logger,
        IConfiguration configuration)
    {
        _environment = environment;
        _logger = logger;
        _configuration = configuration;
        _logDir = Path.Combine(_environment.WebRootPath, "logs");
        _pdfDir = Path.Combine(_environment.WebRootPath, "pdfs");

        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(_pdfDir);
    }

    public async Task<CompilationResult> CompileAsync(CompilationTask task)
    {
        var startTime = DateTime.UtcNow;

        var tempDir = Path.Combine(Path.GetTempPath(), $"tex_compile_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        var originalFileName = Path.GetFileNameWithoutExtension(task.SourceFile);

        var mainTexFile = Path.GetFileName(task.SourceFile);

        // Рабочий каталог pdflatex - каталог главного tex-файла, а не корень tempDir.
        // TeX разрешает относительные пути из \input и \includegraphics относительно
        // рабочего каталога процесса, а не относительно обрабатываемого файла.
        var texDir = tempDir;


        string? asymptoteOutput = null;

        try
        {
            if (Path.GetExtension(task.SourceFile).ToLower() == ".zip")
            {
                ExtractZipArchive(task.SourceFile, tempDir);
                var mainTexPath = FindMainTexFile(tempDir);

                if (string.IsNullOrEmpty(mainTexPath))
                {
                    return new CompilationResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "В архиве не найден .tex файл"
                    };

                }
                // FindMainTexFile ищет рекурсивно и возвращает полный путь
                texDir = Path.GetDirectoryName(mainTexPath) ?? tempDir;
                mainTexFile = Path.GetFileName(mainTexPath);

            }
            else
            {
                File.Copy(task.SourceFile, Path.Combine(tempDir, mainTexFile), true);
            }


            var latexArgs = $"-interaction=nonstopmode -shell-escape \"{mainTexFile}\"";
            var pdfPath = Path.Combine(texDir, Path.GetFileNameWithoutExtension(mainTexFile) + ".pdf");

            // Цепочка проходов идёт до конца независимо от кодов возврата. Ранний выход
            // по ненулевому коду давал ложные отказы: в режиме nonstopmode pdflatex
            // доходит до конца документа и возвращает ненулевой код при любой ошибке TeX,
            // включая полностью восстановимые, а PDF при этом обычно пригоден. Хуже того,
            // отказ первого прохода обрыва цепочку вызовов asy, то есть иллюстрации
            // не строились вовсе - при том, что смысл трех проходов именно в этом.
            var passes = new List<ProcessResult>
            {
                await RunLatexPassAsync(latexArgs, texDir, 1)
            };

            // Поиск рекурсивный, как и поиск главного tex-файла в FindMainTexFile. Только
            // корень видел лишь файлы inline-режима: {jobname}-*.asy пакет генерирует
            // в рабочем каталоге pdflatex. Принесенные пользователем .asy, разложенные
            // в архиве по подпапкам, не находились вовсе.
            var asyFiles = Directory.GetFiles(tempDir, "*.asy", SearchOption.AllDirectories);
            _logger.LogInformation("Found {Count} Asymptote files", asyFiles.Length);

            var nestedAsyFiles = asyFiles
                .Select(file => Path.GetRelativePath(tempDir, file))
                .Where(relative => relative.Contains(Path.DirectorySeparatorChar))
                .ToArray();

            if (nestedAsyFiles.Length > 0)
            {
                _logger.LogInformation("Asymptote files outside the root: {Files}",
                    string.Join(", ", nestedAsyFiles));
            }

            if (asyFiles.Length > 0)
            {
                var asyResult = await CompileAllAsymptoteFilesAsync(asyFiles, texDir);
                asymptoteOutput = asyResult.Output;

                // Неуспех asy намеренно не превращается в неуспех компиляции: asy охотно
                // возвращает ненулевой код на предупреждениях, и часть документов при этом
                // собирается в пригодный pdf.
                if (!asyResult.Success)
                {
                    _logger.LogWarning("Task {TaskId}: Assymptote failed (exit code {ExitCode}). Output: {Output}",
                        task.TaskId, asyResult.ExitCode, asyResult.Output);
                }
            }

            passes.Add(await RunLatexPassAsync(latexArgs, texDir, 2));

            // Время записи PDF до последнего прохода: файл мог остаться от предыдущего
            // и тогда его нельзя молча выдать за результат последнего
            var pdfWriteTimeBeforeLastPass = File.Exists(pdfPath)
                ? File.GetLastWriteTimeUtc(pdfPath)
                : (DateTime?)null;

            // Параноидальная третья компиляция, чтобы точно создалось оглавление
            passes.Add(await RunLatexPassAsync(latexArgs, texDir, 3));

            // Единственный критерий успеха - наличие PDF
            if (File.Exists(pdfPath))
            {
                if (pdfWriteTimeBeforeLastPass != null
                    && File.GetLastWriteTimeUtc(pdfPath) <= pdfWriteTimeBeforeLastPass)
                {
                    _logger.LogWarning("Task {TaskId}: last pdflatex pass did not update the PDF, returning the result of an earlier pass", task.TaskId);
                }

                var outputPdfName = Path.GetFileNameWithoutExtension(task.SourceFile) + ".pdf";
                var outputPdfPath = Path.Combine(_pdfDir, outputPdfName);

                File.Copy(pdfPath, outputPdfPath, overwrite: true);
                _logger.LogInformation("PDF successfully created: {OutputPath}", outputPdfPath);

                return new CompilationResult
                {
                    IsSuccess = true,
                    FilePath = outputPdfPath,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            else
            {
                return new CompilationResult
                {
                    IsSuccess = false,
                    ErrorMessage = DescribeMissingPdf(passes)
                };

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compilation error for task {TaskId}", task.TaskId);
            return new CompilationResult
            {
                IsSuccess = false,
                ErrorMessage = $"Compilation error: {ex.Message}"
            };
        }
        finally
        {
            SaveLogToFile(task, mainTexFile, texDir, asymptoteOutput);
            // Гарантированное удаление временной папки
            await CleanupTempDirectory(tempDir);
        }
    }

    /// <summary>
    /// Один проход pdflatex. Ненулевой код возврата фиксируется в журнале, но
    /// отказом не считается - решение принимается один раз по факту наличия PDF
    /// </summary>
    private async Task<ProcessResult> RunLatexPassAsync(string latexArgs, string tempDir, int pass)
    {
        var result = await RunProcessAsync("pdflatex", latexArgs, tempDir);

        if (result.ExitCode == null)
        {
            _logger.LogError("pdflatex pass {Pass}: process could not be started", pass);
        }

        else if (result.ExitCode != 0)
        {
            // Ожидаемая ситуация для восстановимых ошибок TeX: продолжаем цепочку
            _logger.LogWarning("pdflatex pass {Pass} exited with code {ExitCode}, continuing", pass, result.ExitCode);
        }
        else
        {
            _logger.LogInformation("pdflatex pass {Pass} exited with code 0", pass);
        }

        return result;
    }

    /// <summary>
    /// PDF не создан - надо отличить "pdflatex не запустился" от "запускался, но
    /// результата нет"
    /// </summary>
    private static string DescribeMissingPdf(IReadOnlyList<ProcessResult> passes)
    {
        if (passes.Any(pass => pass.ExitCode == null))
        {
            return "Не удалось запустить pdflatex";
        }

        var codes = string.Join(", ", passes.Select((pass, index) => $"Проход {index + 1}: {pass.ExitCode}"));

        return $"PDF не был создан. Коды возврата pdflatex - {codes}. Подробности в логи компиляции.";
    }

    private static string DescribeLatexFailure(ProcessResult result)
    {
        return result.TimedOut
            ? "Превышено время компиляции LaTeX, процесс остановлен"
            : "Ошибка компиляции LaTeX";
    }

    /// <summary>
    /// Ищет главный tex-файл рекурсивно и возвращает полный путь. От его каталога
    /// зависит рабочий каталог pdflatex, поэтому метод покрыт тестами и открыт как 
    /// internal
    /// </summary>
    internal static string FindMainTexFile(string directory)
    {
        var texFiles = Directory.GetFiles(directory, "*.tex", SearchOption.AllDirectories);

        if (texFiles.Length == 0) {
            return string.Empty;
        }

        // Приоритет: ищем файл с "main" в названии
        var mainFile = texFiles.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).ToLower().Contains("main"));

        // Или берем первый .tex файл
        return mainFile ?? texFiles.First();
    }

    private void ExtractZipArchive(string zipPath, string extractPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var fullPath = Path.Combine(extractPath, entry.FullName);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            if (!entry.FullName.EndsWith("/")) // не директория
                entry.ExtractToFile(fullPath, true);
        }
    }

    private bool IsDirectoryInUse(string tempDir)
    {
        try
        {
            var files = Directory.GetFiles(tempDir);
            var directories = Directory.GetDirectories(tempDir);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal async Task<ProcessResult> RunProcessAsync(string command, string arguments, string workingDir)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);

        if (process == null)
        {
            _logger.LogError("Failed to start process {Command}. Args: {Arguments}", command, arguments);

            return new ProcessResult
            {
                Success = false,
                Output = $"Не удалось запустить процесс {command}"
            };
        }

        var timeout = ProcessTimeout;
        using var cts = new CancellationTokenSource(timeout);

        // Оба потока начинают читаться до ожидания выхода. Перенаправленный поток - это
        // pipe с буфером ограниченного размера (обычно 64 Кб). Пока буфер не заполнен,
        // процесс пишет и работает дальше, а как только заполнится - очередная
        // запись блокируется до того, как кто-нибудь прочитает данные с другого конца.
        // Пока читался только stdout, процесс, пишущий много в stderr, вставал намерство:
        // родитель ждал завершения, процесс ждал возможности запись.

        // Токен передается и в чтение вывода, и в ожидание выхода: процесс может 
        // зависнуть, ничего не записав, тогда ReadToEndAsync без токена не вернется.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        

        try
        {
            await process.WaitForExitAsync(cts.Token);

            return new ProcessResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = await stdoutTask,
                Error = await stderrTask
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Process {Command} exceeded the {Timeout}s limit and will be killed. Arguments {Arguments}",
                command, timeout.TotalSeconds, arguments);

            KillProcessTree(process, command);

            await DrainAsync(stdoutTask);
            await DrainAsync(stderrTask);

            return new ProcessResult
            {
                Success = false,
                TimedOut = true,
                Output = $"Процесс {command} превысил лимит {timeout.TotalSeconds:0} c и был принудительно завершен"
            };
        }
    }

    /// <summary>
    /// Дожидается отмененной задачи чтения, поглощая исключения. Нужна только на пути таймаута
    /// Данные из отмененного ReadToAsync уже недоступны
    /// </summary>
    private static async Task DrainAsync(Task<string> readTask)
    {
        try
        {
            await readTask;
        }
        catch (OperationCanceledException)
        {
            // Чтение отменено вместе с процессом
        }
        catch (Exception)
        {
            // Поток мог закрыться вместе с убитым процессом
        }
    }

    /// <summary>
    /// Завершает процесс вместе с потомками: pdflatex c -shell-escape порождает дочерние процессы и убийство одного родителя
    /// оставило бы их работать
    /// </summary>
    private void KillProcessTree(Process process, string command)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            // Процесс мог завершиться сам собой между проверкой и вызовом Kill
            _logger.LogWarning(ex, "Failed to kill process {Command}", command);
        }
    }

    /// <summary>
    /// Компилирует все Asymptote файлы за один вызов
    /// </summary>
    private async Task<ProcessResult> CompileAllAsymptoteFilesAsync(string[] asyFiles, string tempDir)
    {
        if (asyFiles.Length == 0)
            return new ProcessResult { Success = true, Output = "No Asymptote files to compile" };

        var groups = GroupAsymptoteFilesByDirectory(asyFiles, tempDir);
        var outputs = new List<string>();
        var allSucceeded = true;

        foreach (var (directory, fileNames) in groups)
        {
            var result = await CompileAsymptoteGroupAsync(directory, fileNames);
            var relativeDirectory = Path.GetRelativePath(tempDir, directory);

            if (!result.Success)
            {
                allSucceeded = false;
                _logger.LogWarning("Asymptote failed in {Directory}: {Output}",
                    relativeDirectory, result.Output);
            }

            var text = string.Join(Environment.NewLine,
                new[] { result.Output, result.Error }.Where(part => !string.IsNullOrWhiteSpace(part)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                outputs.Add($"--- {relativeDirectory} ---{Environment.NewLine}{text}");
            }
        }

        return new ProcessResult
        {
            Success = allSucceeded,
            Output = string.Join(Environment.NewLine, outputs)
        };
    }

    /// <summary>
    /// Раскладывает найденные .asy по каталогам, в которых они лежат
    /// 
    /// Asy кладет результат в свой рабочий каталог, а includegraphics из tex-файла
    /// ищет картинку рядом с собой. Один вызов из корня с полными путями сложил бы все 
    /// картинки в корень, и подключение бы сломалось.
    /// </summary>
    internal static List<(string Directory, string[] FileNames)> GroupAsymptoteFilesByDirectory(
        string[] asyFiles, string tempDir)
    {

        return asyFiles
            .GroupBy(file => Path.GetDirectoryName(Path.GetFullPath(file)) ?? Path.GetFullPath(tempDir))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (
                Directory: group.Key,
                FileNames: group.Select(Path.GetFileName).OfType<string>().OrderBy(name => name, StringComparer.Ordinal).ToArray()))
            .ToList();
    }

    private async Task<ProcessResult> CompileAsymptoteGroupAsync(string directory, string[] fileNames)
    { 
        try
        {
            var quotedNames = fileNames.Select(name => $"\"{name}\"");
            var arguments = string.Join(" ", quotedNames);

            _logger.LogDebug("Compiling {Count} Asymptote files in {Directory}: {Files}",
                fileNames.Length, directory, string.Join(", ", fileNames));

            return await RunProcessAsync("asy", arguments, directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compiling Asymptote files in {Directory}", directory);
            return new ProcessResult
            {
                Success = false,
                Output = $"Asymptote compilation failed: {ex.Message}"
            };
        }
    }

    private async Task CleanupTempDirectory(string tempDir)
    {
        if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir))
            return;

        try
        {
            // Даем процессам время завершиться
            await Task.Delay(1000);

            // Пытаемся удалить несколько раз с задержкой
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(tempDir, true);
                    _logger.LogDebug("Successfully deleted temp directory: {Directory}", tempDir);
                    return; // Успешно удалили, выходим
                }
                catch (IOException ioEx) when (attempt < 2)
                {
                    _logger.LogDebug("Directory busy (attempt {Attempt}), retrying...: {Error}",
                        attempt + 1, ioEx.Message);
                    await Task.Delay(500 * (attempt + 1));
                }
                catch (UnauthorizedAccessException authEx) when (attempt < 2)
                {
                    _logger.LogDebug("Access denied (attempt {Attempt}), retrying...: {Error}",
                        attempt + 1, authEx.Message);
                    await Task.Delay(500 * (attempt + 1));
                }
            }

            // Если не удалось удалить после 3 попыток, логируем предупреждение
            _logger.LogWarning("Failed to delete temp directory after 3 attempts: {Directory}", tempDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during temp directory cleanup: {Directory}", tempDir);
        }
    }

    /// <summary>
    /// Копирует лог pdflatex из временной директории в хранилище логов.
    /// Имя лога определяет сам pdflatex по своему входному файлу, поэтому оно
    /// производится от главного tex-файла, а не от имени загруженного: для
    /// zip-архива это разные имена, и лог по имени файла не нашелся бы никогда.
    /// </summary>
    private void SaveLogToFile(CompilationTask task, string mainTexFile, string tempDir, string? asymptoteOutput)
    {
        try
        {
            var outputLogName = $"{task.TaskId}.log";
            var outputLogFilePath = Path.Combine(_logDir, outputLogName);

            var logFileName = Path.GetFileNameWithoutExtension(mainTexFile) + ".log";

            var logFilePath = Path.Combine(tempDir, logFileName);
            if (!File.Exists(logFilePath))
            {
                var foundLogs = Directory.GetFiles(tempDir, "*.log").Select(f => Path.GetFileName(f));

                _logger.LogWarning("Compilation log {Excepted} not found for task {TaskId}. Logs in temp dir: {Found}",
                    logFileName, task.TaskId, string.Join(", ", foundLogs));
                return;
            }

            File.Copy(logFilePath, outputLogFilePath, overwrite: true);

            if (string.IsNullOrWhiteSpace(asymptoteOutput))
            {
                return;
            }

            // Дописываем после копирования лога pdflatex, а не вместо него
            File.AppendAllText(outputLogFilePath,
                $"{Environment.NewLine}========== ASYMPTOTE OUTPUT =============={Environment.NewLine}{asymptoteOutput}{Environment.NewLine}");

            task.LogFilePath = outputLogFilePath;

            _logger.LogInformation("Compilation log saved for task {TaskId}", task.TaskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save log file for task {TaskId}", task.TaskId);
        }
    }
}
    
