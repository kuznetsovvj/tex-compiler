using TexCompiler.Models;

namespace TexCompiler.Services
{
    public class CleanupService : BackgroundService
    {
        private readonly ILogger<CleanupService> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly ITaskStorageService _taskStorage;
        private readonly IConfiguration _configuration;

        // Конфигурируемые интервалы
        private TimeSpan CleanupInterval => TimeSpan.FromMinutes(
            _configuration.GetValue<int>("CleanupSettings:IntervalMinutes", 15));

        private TimeSpan TempRetentionTime => TimeSpan.FromMinutes(
            _configuration.GetValue<int>("CleanupSettings:TempRetentionMinutes", 15));

        private TimeSpan PdfRetentionTime => TimeSpan.FromMinutes(
            _configuration.GetValue<int>("CleanupSettings:PdfRetentionMinutes", 60));

        private TimeSpan TaskRetentionTime => TimeSpan.FromHours(
            _configuration.GetValue<int>("CleanupSettings:TaskRetentionHours", 2));

        /// По умолчанию совпадает со сроком жизни задачи и заведомо не меньше срока
        /// хранения PDF: исходник не должен исчезать раньше собранного из него результата,
        /// иначе повторить компиляцию при разборе инцидента будет нечем.
        private TimeSpan SourceRetentionTime => TimeSpan.FromMinutes(
            _configuration.GetValue<int>("CleanupSettings:SourceRetentionMinutes", 120));

        public CleanupService(
            ILogger<CleanupService> logger,
            IWebHostEnvironment environment,
            ITaskStorageService taskStorage,
            IConfiguration configuration)
        {
            _logger = logger;
            _environment = environment;
            _taskStorage = taskStorage;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Unified Cleanup Service started. Interval: {Interval} minutes",
                CleanupInterval.TotalMinutes);

            // Небольшая задержка при старте приложения
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    PerformFullCleanup();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during unified cleanup");
                }

                await Task.Delay(CleanupInterval, stoppingToken);
            }
        }

        /// <summary>
        /// Выполняет очистку: временные директории, PDF, загруженные исходники, таски в очередях
        /// </summary>
        public void PerformFullCleanup()
        {

            try
            {
                CleanupTempDirectories();
                CleanupOldPdfFiles();
                CleanupOldSourceFiles();
                CleanupOldTasks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unified cleanup failed");
                throw;
            }
        }

        /// <summary>
        /// Очищает временные директории компиляции
        /// </summary>
        private int CleanupTempDirectories()
        {
            try
            {
                var tempPath = Path.GetTempPath();
                var cutoffTime = DateTime.UtcNow - TempRetentionTime;

                var tempDirs = Directory.GetDirectories(tempPath, "tex_compile_*")
                    .Where(dir => Directory.GetCreationTimeUtc(dir) < cutoffTime)
                    .ToList();

                var deletedCount = 0;

                foreach (var dir in tempDirs)
                {
                    try
                    {
                        if (IsDirectoryInUse(dir))
                        {
                            _logger.LogDebug("Skipping directory in use: {Directory}", dir);
                            continue;
                        }

                        Directory.Delete(dir, true);
                        deletedCount++;
                        _logger.LogDebug("Deleted temp directory: {Directory}", dir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete temp directory: {Directory}", dir);
                    }
                }

                _logger.LogInformation("Temp directories cleanup: {Deleted}/{Total}",
                    deletedCount, tempDirs.Count);

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during temp directories cleanup");
                return 0;
            }
        }

        /// <summary>
        /// Очищает старые PDF файлы
        /// </summary>
        private int CleanupOldPdfFiles()
        {
            try
            {
                var pdfsPath = Path.Combine(_environment.WebRootPath, "pdfs");
                if (!Directory.Exists(pdfsPath))
                    return 0;

                var cutoffTime = DateTime.UtcNow - PdfRetentionTime;
                var pdfFiles = Directory.GetFiles(pdfsPath, "*.pdf")
                    .Where(file => File.GetLastWriteTimeUtc(file) < cutoffTime)
                    .ToList();

                var referencedPaths = GetReferencedPaths(task => task.PdfFilePath);
                var deletedCount = 0;

                foreach (var file in pdfFiles)
                {
                    try
                    {
                        // Проверяем, не ссылается ли на этот файл активная задача
                        if (referencedPaths.Contains(Path.GetFullPath(file)))
                        {
                            _logger.LogDebug("Skipping referenced PDF file: {File}", file);
                            continue;
                        }

                        File.Delete(file);
                        deletedCount++;
                        _logger.LogInformation("Deleted orphaned PDF file: {File}", Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete PDF file: {File}", file);
                    }
                }

                _logger.LogInformation("PDF files cleanup: {Deleted}/{Total}",
                    deletedCount, pdfFiles.Count);

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during PDF files cleanup");
                return 0;
            }
        }

        /// <summary>
        /// Очищает старые задачи из хранилища
        /// </summary>
        private int CleanupOldTasks()
        {
            try
            {
                var allTasks = _taskStorage.GetAllTasks();
                var cutoffTime = DateTime.UtcNow - TaskRetentionTime;

                var oldTasks = allTasks
                    .Where(task => task.CreatedAt < cutoffTime)
                    .Where(task => task.TaskStatus == Models.CompilationTaskStatus.Completed || task.TaskStatus == Models.CompilationTaskStatus.Failed)
                    .ToList();

                var removedCount = 0;

                foreach (var task in oldTasks)
                {
                    try
                    {
                        // Удаляем задачу из хранилища
                        if (_taskStorage.TryRemoveTask(task.TaskId))
                        {
                            removedCount++;
                            _logger.LogDebug("Removed old task: {TaskId}", task.TaskId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove old task: {TaskId}", task.TaskId);
                    }
                }

                _logger.LogInformation("Old tasks cleanup: {Removed}/{Total}",
                    removedCount, oldTasks.Count);

                return removedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during old tasks cleanup");
                return 0;
            }
        }

        private int CleanupOldSourceFiles()
        {
            try
            {
                var storagePath = Path.Combine(_environment.ContentRootPath, "storage");
                if (!Directory.Exists(storagePath))
                {
                    return 0;
                }

                var cutoffTime = DateTime.UtcNow - SourceRetentionTime;
                // Каждая загрузка лежит в своем подкаталоге storage/{guid}/. Отдельные
                // файлы в корне остались от прежней раскладки: каталог смонтирован как том
                // с хоста и переживает пересоздание контейнера, так что их тоже надо убрать.
                var candidates = Directory.GetDirectories(storagePath)
                    .Where(dir => Directory.GetLastWriteTimeUtc(dir) < cutoffTime)
                    .Concat(Directory.GetFiles(storagePath))
                    .Where(file => File.GetLastWriteTimeUtc(file) < cutoffTime)
                    .ToList();

                var referencedSource = GetReferencedPaths(task => task.SourceFile);
                var referencedDirectories = new HashSet<string>(StringComparer.Ordinal);

                foreach (var source in referencedSource)
                {
                    var directory = Path.GetDirectoryName(source);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        referencedDirectories.Add(directory);
                    }
                }

                var deletedCount = 0;
                foreach (var path in candidates)
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(path);

                        // Задача в статуте Queued или Processing тоже есть в списке задач
                        // поэтому её исходник считается нужным и срок хранения его не касается
                        if (referencedSource.Contains(fullPath) || referencedDirectories.Contains(fullPath))
                        {
                            _logger.LogDebug("Skipping referenced source file: {Path}", path);
                            continue;
                        }

                        if (Directory.Exists(fullPath))
                        {
                            Directory.Delete(fullPath, recursive: true);
                        }
                        else
                        {
                            File.Delete(fullPath);
                        }

                        deletedCount++;
                        _logger.LogInformation("Deleted orphaned source file: {Path}", Path.GetFileName(fullPath));
                    }
                    catch (Exception)
                    {
                        _logger.LogWarning("Failed to delete source file: {Path}", path);
                    }
                }
                _logger.LogInformation("Source file cleanup: {Deleted}/{Total}",
                    deletedCount, candidates.Count);

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during source file cleanup");
                return 0;
            }
        }

        /// <summary>
        /// Проверяет, используется ли директория
        /// </summary>
        private bool IsDirectoryInUse(string directoryPath)
        {
            try
            {
                var files = Directory.GetFiles(directoryPath);
                var directories = Directory.GetDirectories(directoryPath);
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
        /// Собирает пути к PDF файлам или к исходникам, смотря какое свойство передали,
        /// которые еще упоминаются в задачах
        /// </summary>
        /// <returns></returns>
        private HashSet<string> GetReferencedPaths(Func<CompilationTask, string?> pathSelector)
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);

            foreach (var task in _taskStorage.GetAllTasks())
            {
                var path = pathSelector(task);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                try
                {
                    referenced.Add(path);
                }
                catch (Exception ex)
                {
                    // Некорректный путь в задаче не должен ронять всю чистку
                    _logger.LogWarning(ex, "Invalid path in task {TaskId}: {Path}",
                        task.TaskId, path);
                }
            }

            return referenced;
        }
    }
}