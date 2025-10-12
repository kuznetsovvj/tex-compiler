using TexCompiler.Models;

namespace TexCompiler.Services
{
    public class CompilationManagerService
    {
        private readonly ITaskStorageService _taskStorage;
        private readonly ICompilationService _compilationService;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<CompilationManagerService> _logger;
        private readonly string _storagePath;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);


        public CompilationManagerService(
            ITaskStorageService taskStorage,
            ICompilationService compilationService,
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
                var filePath = GenerateFilePath(file.FileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var task = new CompilationTask(filePath);

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

        private string GenerateFilePath(string sourceFileName)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var safeFileName = Path.GetFileNameWithoutExtension(sourceFileName)
                .Replace(" ", "_")
                .Replace("/", "_")
                .Replace("\\", "_");

            var fileName = $"{timestamp}_{safeFileName}" + Path.GetExtension(sourceFileName);
            var filePath = Path.Combine(_storagePath, fileName);

            return filePath;
        }


        /// <summary>
        /// Получает статус задачи по ID
        /// </summary>
        public CompilationTask? GetTaskStatus(Guid taskId)
        {
            return _taskStorage.GetTask(taskId);
        }

        /// <summary>
        /// Запускает обработку очереди если она не активна
        /// </summary>
        private async Task StartProcessingIfNeeded()
        {
            if (await _semaphore.WaitAsync(0))
            {
                try
                {
                    await ProcessQueueAsync();
                }
                catch
                {
                    _semaphore.Release();
                    throw;
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
                _semaphore.Release();
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
                _taskStorage.UpdateTask(task.SetProcessing());

                var result = await _compilationService.CompileAsync(task);

                _taskStorage.UpdateTask(task.SetCompleted(result));
                _logger.LogInformation("Task {TaskId} completed with status: {Status}",
                    task.TaskId, task.TaskStatus);
            }
            catch (Exception ex)
            {
                _taskStorage.UpdateTask(task.SetFailed(ex));
                _logger.LogError(ex, "Error processing task: {TaskId}", task.TaskId);
              
            }
        }
    }
}