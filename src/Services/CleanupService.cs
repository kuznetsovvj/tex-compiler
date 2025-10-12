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
        /// Выполняет очистку: временные директории, PDF, таски в очередях
        /// </summary>
        public void PerformFullCleanup()
        {

            try
            {
                CleanupTempDirectories();
                CleanupOldPdfFiles();
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

                var deletedCount = 0;

                foreach (var file in pdfFiles)
                {
                    try
                    {
                        // Проверяем, не ссылается ли на этот файл активная задача
                        if (IsPdfFileReferenced(file))
                        {
                            _logger.LogDebug("Skipping referenced PDF file: {File}", file);
                            continue;
                        }

                        File.Delete(file);
                        deletedCount++;
                        _logger.LogDebug("Deleted PDF file: {File}", Path.GetFileName(file));
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
        /// Проверяет, ссылается ли на PDF файл какая-либо активная задача
        /// </summary>
        private bool IsPdfFileReferenced(string pdfFilePath)
        {
            try
            {
                var fileName = Path.GetFileName(pdfFilePath);
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(fileName), out var taskId))
                {
                    var task = _taskStorage.GetTask(taskId);
                    // Если задача существует и была создана недавно (меньше чем PdfRetentionTime),
                    // то файл еще может быть нужен
                    return task != null && task.CreatedAt > DateTime.UtcNow - PdfRetentionTime;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking if PDF file is referenced: {File}", pdfFilePath);
                return true; // В случае ошибки лучше не удалять файл
            }
        }
    }
}