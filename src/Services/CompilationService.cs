using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
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

    // Каждый вызов pdflatex/asy выполняется в одноразовом docker-контейнере с
    // изоляцией (см. ContainerRunCommandBuilder): без сети, без capabilities,
    // от rootfs, непривилегированный UID, лимиты памяти/CPU/PID. Локально и в CI
    // без docker изоляцию можно выключить (EnableIsolation=false) - тогда инструменты
    // запускаются напрямую в процессе приложения.
    private bool IsolationEnabled =>
        _configuration.GetValue("CompilationContainer:EnableIsolation", true);

    private string CompileImage =>
        _configuration.GetValue<string>("CompilationContainer:Image") ?? "texcompiler-base:latest";

    // Каталог рабочих областей задач, как его видит само приложение (сторона
    // контейнера приложения). Все файловые операции идут по нему.
    // Без настройки - системный temp.
    private string WorkspaceRoot =>
        _configuration.GetValue<string>("CompilationContainer:WorkspaceRoot") ?? Path.GetTempPath();

    // Тот же каталог, но в путях ХОСТА: его требует docker run -v, потому что
    // dockerd резолвит источник bind-монтирования на хосте. Если приложение видит
    // каталог по пути /tasks, а на хосте он лежит в /var/lib/texcompiler-tasks,
    // сюда должны прописать именно /var/lib/texcompiler-tasks (workflow делает это
    // автоматически из секрета MOUNT_TASKS_DIR). Передача контейнерного пути
    // приводила к молча созданному dockerd пустому каталогу на хосте: pdflatex
    // видел пустой /work и завершался с кодом 1 без единого файла.
    // По умолчанию совпадает с WorkspaceRoot - так корректно в режиме
    // EnableIsolation=false и при монтировании 1:1.
    private string WorkspaceHostRoot =>
        _configuration.GetValue<string>("CompilationContainer:WorkspaceHostRoot") ?? WorkspaceRoot;

    private string ContainerMemoryMb =>
        _configuration.GetValue("CompilationContainer:MemoryMegabytes", 1024).ToString();

    private string ContainerCpuCount =>
        _configuration.GetValue("CompilationContainer:CpuCount", 1).ToString();

    private string ContainerPidsLimit =>
        _configuration.GetValue("CompilationContainer:PidsLimit", 256).ToString();

    private string ContainerTmpSizeMb =>
        _configuration.GetValue("CompilationContainer:TmpSizeMegabytes", 64).ToString();

    public CompilationService(
        IWebHostEnvironment environment,
        ILogger<CompilationService> logger,
        IConfiguration configuration)
    {
        _environment = environment;
        _logger = logger;
        _configuration = configuration;
        _logDir = ArtifactPath.GetLogDirectory(environment);
        _pdfDir = ArtifactPath.GetPdfDirectory(environment);

        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(_pdfDir);
        Directory.CreateDirectory(WorkspaceRoot);
    }

    public async Task<CompilationResult> CompileAsync(CompilationTask task)
    {
        var startTime = DateTime.UtcNow;

        var workspace = Path.Combine(WorkspaceRoot, $"tex_{task.TaskId:N}");
        Directory.CreateDirectory(workspace);

        var originalFileName = Path.GetFileNameWithoutExtension(task.SourceFile);

        var mainTexFile = Path.GetFileName(task.SourceFile);

        // Рабочий каталог pdflatex - каталог главного tex-файла, а не корень workspace.
        // TeX разрешает относительные пути из \input и \includegraphics относительно
        // рабочего каталога процесса, а не относительно обрабатываемого файла.
        var texDir = workspace;

        string? asymptoteOutput = null;

        try
        {
            // Тип определяем по содержимому, а не по расширению: расширение -
            // это строка, которую прислал клиент. Архив под именем .tex
            // уходил в pdflatex как исходник.
            if (ArchiveDetector.IsZipArchive(task.SourceFile))
            {
                // Только SafeZipExtractor: он отсекает Zip Slip и zip-бомбы.
                // Собственная распаковка здесь раньше писала по путям ../... за пределами workspace
                SafeZipExtractor.ExtractTo(task.SourceFile, workspace);
                var mainTexPath = FindMainTexFile(workspace);

                if (string.IsNullOrEmpty(mainTexPath))
                {
                    return new CompilationResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "В архиве не найден .tex файл"
                    };

                }
                // FindMainTexFile ищет рекурсивно и возвращает полный путь
                texDir = Path.GetDirectoryName(mainTexPath) ?? workspace;
                mainTexFile = Path.GetFileName(mainTexPath);

            }
            else if (Path.GetExtension(task.SourceFile).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Раньше такой  файл падал внутри ZipFile.OpenRead, а пользователь видел
                // "Compilation error..." - сообщение про сбой сервиса, а не про свой файл
                return new CompilationResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Файл с расширение .zip не является архивом. Проверьте, что архив не поврежден."
                };
            }

            else
            {
                File.Copy(task.SourceFile, Path.Combine(workspace, mainTexFile), true);
            }

            // Путь внутри контейнера: только относительный, с разделителям Linux.
            // Записываемый вне workspace каталог контейнер не увидит в принципе:
            // bind-монтирован ровно один каталог.
            var relativeTexDir = Path.GetRelativePath(workspace, texDir)
                .Replace(Path.DirectorySeparatorChar, '/');

            var latexArgs = BuildLatexArguments(mainTexFile);
            var pdfPath = Path.Combine(texDir, Path.GetFileNameWithoutExtension(mainTexFile) + ".pdf");

            // Цепочка проходов идёт до конца независимо от кодов возврата. Ранний выход
            // по ненулевому коду давал ложные отказы: в режиме nonstopmode pdflatex
            // доходит до конца документа и возвращает ненулевой код при любой ошибке TeX,
            // включая полностью восстановимые, а PDF при этом обычно пригоден. Хуже того,
            // отказ первого прохода обрыва цепочку вызовов asy, то есть иллюстрации
            // не строились вовсе - при том, что смысл трех проходов именно в этом.
            var passes = new List<ProcessResult>
            {
                await RunLatexPassAsync(task, workspace, relativeTexDir, latexArgs, 1)
            };

            // Поиск рекурсивный, как и поиск главного tex-файла в FindMainTexFile. Только
            // корень видел лишь файлы inline-режима: {jobname}-*.asy пакет генерирует
            // в рабочем каталоге pdflatex. Принесенные пользователем .asy, разложенные
            // в архиве по подпапкам, не находились вовсе.
            var asyFiles = Directory.GetFiles(workspace, "*.asy", SearchOption.AllDirectories);
            _logger.LogInformation("Found {Count} Asymptote files", asyFiles.Length);

            var nestedAsyFiles = asyFiles
                .Select(file => Path.GetRelativePath(workspace, file))
                .Where(relative => relative.Contains(Path.DirectorySeparatorChar))
                .ToArray();

            if (nestedAsyFiles.Length > 0)
            {
                _logger.LogInformation("Asymptote files outside the root: {Files}",
                    string.Join(", ", nestedAsyFiles));
            }

            if (asyFiles.Length > 0)
            {
                var asyResult = await CompileAllAsymptoteFilesAsync(task, workspace, asyFiles);
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

            passes.Add(await RunLatexPassAsync(task, workspace, relativeTexDir, latexArgs, 2));

            // Время записи PDF до последнего прохода: файл мог остаться от предыдущего
            // и тогда его нельзя молча выдать за результат последнего
            var pdfWriteTimeBeforeLastPass = File.Exists(pdfPath)
                ? File.GetLastWriteTimeUtc(pdfPath)
                : (DateTime?)null;

            // Параноидальная третья компиляция, чтобы точно создалось оглавление
            passes.Add(await RunLatexPassAsync(task, workspace, relativeTexDir, latexArgs, 3));

            // Единственный критерий успеха - наличие PDF
            if (File.Exists(pdfPath))
            {
                if (pdfWriteTimeBeforeLastPass != null
                    && File.GetLastWriteTimeUtc(pdfPath) <= pdfWriteTimeBeforeLastPass)
                {
                    _logger.LogWarning("Task {TaskId}: last pdflatex pass did not update the PDF, returning the result of an earlier pass", task.TaskId);
                }

                var outputPdfPath = Path.Combine(_pdfDir, $"{task.TaskId}.pdf");

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
        catch (InvalidDataException ex)
        {
            // Сюда попадают отклоненные архивы
            _logger.LogWarning(ex, "Rejected archive for task {TaskId}: {Reason}", task.TaskId, ex.Message);

            return new CompilationResult
            {
                IsSuccess = false,
                ErrorMessage = $"Не удалось распаковать архив: {ex.Message}"
            };
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
            // Гарантированное удаление рабочего каталога задачи
            await CleanupTempDirectory(workspace);
        }
    }

    /// <summary>
    /// Аргументы pdflatex списком: транспорт добавляет каждый через
    /// ProcessStartInfo.ArgumentList, без ручного quoting строкой. Shell escape
    /// принудительно выключен: без этого пользовательский документ через \write18
    /// получил бы выполнение произвольных команд (чтение файлов, сеть, изменение
    /// приложения).
    /// </summary>
    internal static IReadOnlyList<string> BuildLatexArguments(string mainTexFile)
    {
        return new List<string> { "-interaction=nonstopmode", "-no-shell-escape", SanitizeFileArgument(mainTexFile) };
    }

    /// <summary>
    /// Аргументы asy - имена файлов. Само слово "asy" сюда не входит: команду
    /// подставляет транспорт (docker run или ProcessStartInfo), иначе она
    /// продублировалась бы в argv (asy asy ...) и asy пытался бы обработать
    /// файл по имени "asy".
    /// </summary>
    internal static IReadOnlyList<string> BuildAsymptoteArguments(IEnumerable<string> fileNames)
    {
        return fileNames.Select(SanitizeFileArgument).ToList();
    }

    /// <summary>
    /// Имя файла, которое уходит процессу аргументом. Web2C (pdflatex) и asy
    /// разбирают любой аргумент с '-' в начале как опцию: файл "-shell-escape"
    /// вернул бы shell escape после нашего запрета, "-output-directory=/x"
    /// перенаправил бы вывод. Префикс './' уводит имя из разбора опций,
    /// оставляя тот же файл в рабочем каталоге. Пробелы, кавычки и прочие
    /// shell-метасимволы не трогаем: ArgumentList передает аргумент как есть,
    /// shell-разбора нет.
    /// </summary>
    internal static string SanitizeFileArgument(string fileName)
    {
        return fileName.StartsWith("-", StringComparison.Ordinal) ? "./" + fileName : fileName;
    }

    /// <summary>
    /// Один проход pdflatex. Ненулевой код возврата фиксируется в журнале, но
    /// отказом не считается - решение принимается один раз по факту наличия PDF
    /// </summary>
    private async Task<ProcessResult> RunLatexPassAsync(
        CompilationTask task, string workspace, string relativeTexDir, IReadOnlyList<string> latexArgs, int pass)
    {
        var result = await RunToolAsync(task, workspace, relativeTexDir, "pdflatex", latexArgs, "pdflatex");

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
    /// Запускает инструмент компиляции напрямую или в одноразовом контейнере -
    /// в зависимости от EnableIsolation. Возвращает результат любого отказа
    /// запуска как ProcessResult, исключения наружу не просачиваются.
    /// </summary>
    private async Task<ProcessResult> RunToolAsync(
        CompilationTask task,
        string workspace,
        string workingSubdirectory,
        string tool,
        IReadOnlyList<string> toolArguments,
        string toolDisplayName)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        string? containerName = null;

        if (IsolationEnabled)
        {
            containerName = $"tex-task-{task.TaskId:N}";
            startInfo.FileName = "docker";

            // workspace дан с контейнерной стороны (например /tasks/tex_x), а -v
            // требует хостового пути: пересобираем его от хостового корня
            var workspaceHostPath = Path.Combine(
                WorkspaceHostRoot,
                Path.GetRelativePath(WorkspaceRoot, workspace));

            foreach (var arg in ContainerRunCommandBuilder.Build(
                CompileImage,
                containerName,
                workspaceHostPath,
                workingSubdirectory,
                ContainerMemoryMb,
                ContainerCpuCount,
                ContainerPidsLimit,
                ContainerTmpSizeMb,
                tool,
                toolArguments))
            {
                startInfo.ArgumentList.Add(arg);
            }
        }
        else
        {
            startInfo.FileName = tool;
            foreach (var arg in toolArguments)
            {
                startInfo.ArgumentList.Add(arg);
            }
            startInfo.WorkingDirectory = Path.Combine(workspace, workingSubdirectory);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            _logger.LogError(ex, "Failed to start {Tool} (isolation {Isolation})",
                toolDisplayName, IsolationEnabled ? "on" : "off");

            return new ProcessResult
            {
                Success = false,
                Output = $"Не удалось запустить {toolDisplayName}: {ex.Message}"
            };
        }

        if (process == null)
        {
            _logger.LogError("Failed to start process {Tool}", toolDisplayName);

            return new ProcessResult
            {
                Success = false,
                Output = $"Не удалось запустить процесс {toolDisplayName}"
            };
        }

        using (process)
        {
            var result = await WaitAndCollectAsync(process, toolDisplayName, containerName);

            // Подробности неуспеха остаются в серверном журнале: без хвостов вывода
            // падение pdflatex/asy/docker видно только по коду возврата, и диагностика
            // превращается в перебор гипотез
            if (!result.Success && !result.TimedOut)
            {
                _logger.LogWarning(
                    "{Tool} завершился неуспешно (код {ExitCode}). Хвост stdout: {OutputTail}{Newline}Хвост stderr: {ErrorTail}",
                    toolDisplayName, result.ExitCode, Tail(result.Output), Environment.NewLine, Tail(result.Error));
            }

            return result;
        }
    }

    /// <summary>Хвост вывода процесса для журнала: полные логи компиляции целиком не нужны</summary>
    private static string Tail(string? text, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text.Substring(text.Length - maxLength);
    }

    /// <summary>
    /// Ожидает завершения процесса, забирая оба потока, и на таймауте убивает
    /// процесс вместе с потомками. Для docker-клиента дополнительно убивает
    /// сам контейнер: убитый docker run оставил бы task-контейнер работать.
    /// </summary>
    private async Task<ProcessResult> WaitAndCollectAsync(Process process, string toolDisplayName, string? containerName)
    {
        var timeout = ProcessTimeout;
        using var cts = new CancellationTokenSource(timeout);

        // Оба потока начинают читаться до ожидания выхода. Перенаправленный поток - это
        // pipe с буфером ограниченного размера (обычно 64 Кб). Пока буфер не заполнен,
        // процесс пишет и работает дальше, а как только заполнится - очередная
        // запись блокируется до того, как кто-нибудь прочитает данные с другого конца.
        // Пока читался только stdout, процесс, пишущий много в stderr, вставал намертво:
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
            _logger.LogError("Process {Tool} exceeded the {Timeout}s limit and will be killed",
                toolDisplayName, timeout.TotalSeconds);

            KillProcessTree(process, toolDisplayName);

            if (containerName != null)
            {
                await KillContainerAsync(containerName);
            }

            await DrainAsync(stdoutTask);
            await DrainAsync(stderrTask);

            return new ProcessResult
            {
                Success = false,
                TimedOut = true,
                Output = $"Процесс {toolDisplayName} превысил лимит {timeout.TotalSeconds:0} c и был принудительно завершен"
            };
        }
    }

    /// <summary>
    /// Останавливает зависший task-контейнер по имени. Лучше лишний раз, чем висящий
    /// контейнер с процессом пользовательского документа.
    /// </summary>
    private async Task KillContainerAsync(string containerName)
    {
        try
        {
            var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"kill {containerName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (kill != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await kill.WaitForExitAsync(cts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill container {Container}", containerName);
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

        return await WaitAndCollectAsync(process, command, containerName: null);
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
    /// Завершает процесс вместе с потомками: убийство одного родителя оставило бы
    /// дочерние процессы (если они появятся) работать. Оставлено как подстраховка
    /// на пути таймаута несмотря на запрещенный shell-escape
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
    private async Task<ProcessResult> CompileAllAsymptoteFilesAsync(CompilationTask task, string workspace, string[] asyFiles)
    {
        if (asyFiles.Length == 0)
            return new ProcessResult { Success = true, Output = "No Asymptote files to compile" };

        var groups = GroupAsymptoteFilesByDirectory(asyFiles, workspace);
        var outputs = new List<string>();
        var allSucceeded = true;

        foreach (var (directory, fileNames) in groups)
        {
            var relativeDirectory = Path.GetRelativePath(workspace, directory)
                .Replace(Path.DirectorySeparatorChar, '/');

            var result = await CompileAsymptoteGroupAsync(task, workspace, relativeDirectory, fileNames);

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

    private async Task<ProcessResult> CompileAsymptoteGroupAsync(
        CompilationTask task, string workspace, string relativeDirectory, string[] fileNames)
    {
        try
        {
            _logger.LogDebug("Compiling {Count} Asymptote files in {Directory}: {Files}",
                fileNames.Length, relativeDirectory, string.Join(", ", fileNames));

            return await RunToolAsync(
                task, workspace, relativeDirectory, "asy", BuildAsymptoteArguments(fileNames), "asy");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compiling Asymptote files in {Directory}", relativeDirectory);
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
