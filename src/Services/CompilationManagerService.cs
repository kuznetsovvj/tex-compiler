using TexCompiler.Models;

namespace TexCompiler.Services
{
    public class CompilationManagerService
    {
        private readonly ITaskStorageService _taskStorage;
        private readonly CompilationQueue _queue;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<CompilationManagerService> _logger;
        private readonly string _storagePath;

        public CompilationManagerService(
            ITaskStorageService taskStorage,
            CompilationQueue queue,
            IWebHostEnvironment environment,
            ILogger<CompilationManagerService> logger)
        {
            _taskStorage = taskStorage;
            _queue = queue;
            _environment = environment;
            _logger = logger;
            _storagePath = Path.Combine(_environment.ContentRootPath, "storage");

            Directory.CreateDirectory(_storagePath);
        }

        /// <summary>
        /// Сохраняет загруженный файл и передает задачу потребителю очереди
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

                await _queue.EnqueueAsync(task);
                
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
    }
}