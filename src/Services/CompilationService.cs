using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using TexCompiler.Models;

public class CompilationService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<CompilationService> _logger;

    public CompilationService(IWebHostEnvironment environment, ILogger<CompilationService> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public async Task<CompilationResult> CompileAsync(CompilationTask task)
    {
        var result = new CompilationResult();
        var startTime = DateTime.UtcNow;

        var tempDir = Path.Combine(Path.GetTempPath(), $"tex_compile_{Guid.NewGuid()}");
        var logDir = Path.Combine(_environment.WebRootPath, "logs");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(logDir);

        try
        {
            if (Path.GetExtension(task.FileName).ToLower() == ".zip")
            {
                await ExtractZipArchive(task.SourceFileFullPath, tempDir);
                var mainTexFile = FindMainTexFile(tempDir);

                if (mainTexFile == null)
                {
                    return new CompilationResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "В архиве не найден .tex файл"
                    };

                }
                task.FileName = Path.GetFileName(mainTexFile);
            }
            else
            {
                File.Copy(task.SourceFileFullPath, Path.Combine(tempDir, task.FileName), true);
            }



            // Первая компиляция LaTeX
            var latexArgs = $"-interaction=nonstopmode -shell-escape \"{task.FileName}\"";
            var latexResult = await RunProcessAsync("pdflatex", latexArgs, tempDir);

            if (!latexResult.Success)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "LaTeX compilation failed";
                await SaveLogToFile(result, task, logDir, tempDir);
                return result;
            }

            var asyFiles = Directory.GetFiles(tempDir, "*.asy");
            _logger.LogInformation("Found {Count} Asymptote files", asyFiles.Length);

            if (asyFiles.Length > 0)
            {
                var asyResult = await CompileAllAsymptoteFilesAsync(asyFiles, tempDir);
            }

            latexResult = await RunProcessAsync("pdflatex", latexArgs, tempDir);
            if (!latexResult.Success)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "LaTeX compilation failed";
                await SaveLogToFile(result, task, logDir, tempDir);
                return result;
            }

            // Проверяем, создался ли PDF
            var pdfPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(task.FileName) + ".pdf");
            if (File.Exists(pdfPath))
            {
                // Исправляем имя файла: убираем .tex и добавляем .pdf
                var outputPdfName = $"{Path.GetFileNameWithoutExtension(task.FileName)}.pdf";
                var outputPdfPath = Path.Combine(_environment.WebRootPath, "pdfs", outputPdfName);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPdfPath));
                File.Copy(pdfPath, outputPdfPath, overwrite: true);
                _logger.LogInformation("PDF successfully created: {OutputPath}", outputPdfPath);

                result.IsSuccess = true;
                result.FilePath = outputPdfPath;

            }
            else
            {
                result.IsSuccess = false;
                result.ErrorMessage = "PDF file was not generated";
            }
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.ErrorMessage = $"Compilation error: {ex.Message}";
            await SaveLogToFile(result, task, logDir, tempDir);
            _logger.LogError(ex, "Compilation error for task {TaskId}", task.TaskId);
        }
        finally
        {
            await SaveLogToFile(result, task, logDir, tempDir);
            // Гарантированное удаление временной папки
            await CleanupTempDirectory(tempDir);
        }

        result.Duration = DateTime.UtcNow - startTime;
        return result;
    }

    private string FindMainTexFile(string directory)
    {
        var texFiles = Directory.GetFiles(directory, "*.tex", SearchOption.AllDirectories);

        // Приоритет: ищем файл с "main" в названии
        var mainFile = texFiles.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).ToLower().Contains("main"));

        // Или берем первый .tex файл
        return mainFile ?? texFiles.FirstOrDefault();
    }

    private async Task ExtractZipArchive(string zipPath, string extractPath)
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

    private async Task<ProcessResult> RunProcessAsync(string command, string arguments, string workingDir)
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
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult
        {
            Success = process.ExitCode == 0,
            Output = output
        };
    }

    /// <summary>
    /// Компилирует все Asymptote файлы за один вызов
    /// </summary>
    private async Task<ProcessResult> CompileAllAsymptoteFilesAsync(string[] asyFiles, string workingDir)
    {
        if (asyFiles.Length == 0)
            return new ProcessResult { Success = true, Output = "No Asymptote files to compile" };

        try
        {
            // Создаем аргументы командной строки со всеми файлами
            var fileNames = asyFiles.Select(f => $"\"{Path.GetFileName(f)}\"");
            var arguments = string.Join(" ", fileNames);

            _logger.LogDebug("Compiling {Count} Asymptote files: {Files}",
                asyFiles.Length, string.Join(", ", fileNames));

            return await RunProcessAsync("asy", arguments, workingDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compiling Asymptote files");
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

    private async Task SaveLogToFile(CompilationResult result, CompilationTask task, string logsDir, string tempDir)
    {
        try
        {
            var logFilePath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(task.FileName) + ".log");
            if (File.Exists(logFilePath))
            {
                var outputLogName = $"{Path.GetFileNameWithoutExtension(task.FileName)}.log";
                var outputLogPath = Path.Combine(_environment.WebRootPath, "logs", outputLogName);

                Directory.CreateDirectory(Path.GetDirectoryName(outputLogPath));
                File.Copy(logFilePath, outputLogPath, overwrite: true);

                result.LogFilePath = outputLogPath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save log file for task {TaskId}", task.TaskId);
        }
    }
}
    
