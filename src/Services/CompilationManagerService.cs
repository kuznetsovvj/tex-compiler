using TexCompiler.Models;

namespace TexCompiler.Services
{
    public class CompilationManagerService
    {
        private readonly ITaskStorageService _taskStorage;
        private readonly CompilationService _compilationService;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<CompilationManagerService> _logger;
        private readonly object _processingLock = new object();
        private readonly string _storagePath;
        private bool _isProcessing = false;

        public CompilationManagerService(
            ITaskStorageService taskStorage,
            CompilationService compilationService,
            IWebHostEnvironment environment,
            ILogger<CompilationManagerService> logger)
        {
            _taskStorage = taskStorage;
            _compilationService = compilationService;
            _environment = environment;
            _logger = logger;
            _storagePath = Path.Combine(_environment.ContentRootPath, "storage");

            Directory.CreateDirectory(_storagePath);
        }

        /// <summary>
        /// Добавляет задачу в очередь и запускает обработку если очередь пуста
        /// </summary>
        public async Task<Guid> SubmitTaskAsync(IFormFile file)
        {
            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var safeFileName = Path.GetFileNameWithoutExtension(file.FileName)
                    .Replace(" ", "_")
                    .Replace("/", "_")
                    .Replace("\\", "_");

                var fileName = $"{timestamp}_{safeFileName}.tex";

                var filePath = Path.Combine(_storagePath, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var task = new CompilationTask(fileName, filePath);

                _taskStorage.AddTask(task);

                StartProcessingIfNeeded();
                
                return task.TaskId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting task for file: {FileName}", file.FileName);
                throw;
            }
        }


        /// <summary>
        /// Получает статус задачи по ID
        /// </summary>
        public CompilationTask GetTaskStatus(Guid taskId)
        {
            return _taskStorage.GetTask(taskId);
        }

        /// <summary>
        /// Запускает обработку очереди если она не активна
        /// </summary>
        private void StartProcessingIfNeeded()
        {
            lock (_processingLock)
            {
                if (!_isProcessing)
                {
                    _isProcessing = true;
                    _ = Task.Run(ProcessQueueAsync);
                }
            }
        }

        /// <summary>
        /// Асинхронно обрабатывает очередь задач
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            try
            {
                while (true)
                {
                    var task = _taskStorage.GetNextTask();
                    if (task == null)
                    {
                        // Очередь пуста - завершаем обработку
                        break;
                    }

                    await ProcessSingleTaskAsync(task);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during queue processing");
            }
            finally
            {
                lock (_processingLock)
                {
                    _isProcessing = false;
                }
                _logger.LogInformation("Queue processing completed");
            }
        }

        /// <summary>
        /// Обрабатывает одну задачу компиляции
        /// </summary>
        private async Task ProcessSingleTaskAsync(CompilationTask task)
        {
            _logger.LogInformation("Processing task: {TaskId}", task.TaskId);

            try
            {
                task.TaskStatus = CompilationTaskStatus.Processing;
                task.StartedAt = DateTime.UtcNow;
                _taskStorage.UpdateTask(task);

                var result = await _compilationService.CompileAsync(task);

                task.TaskStatus = result.IsSuccess ?
                    CompilationTaskStatus.Completed :
                    CompilationTaskStatus.Failed;

                task.CompletedAt = DateTime.UtcNow;
                task.PdfFilePath = result.FilePath;
                task.LogFilePath = result.LogFilePath;
                task.ErrorMessage = result.ErrorMessage;

                _taskStorage.UpdateTask(task);

                _logger.LogInformation("Task {TaskId} completed with status: {Status}",
                    task.TaskId, task.TaskStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing task: {TaskId}", task.TaskId);

                // Обновляем задачу с информацией об ошибке
                task.TaskStatus = CompilationTaskStatus.Failed;
                task.CompletedAt = DateTime.UtcNow;
                task.ErrorMessage = $"Internal error: {ex.Message}";
                _taskStorage.UpdateTask(task);
            }
        }
    }
}