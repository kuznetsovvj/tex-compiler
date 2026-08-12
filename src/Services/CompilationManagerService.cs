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

        /// <summary>
        /// Каждая загрузка кладется в собственный подкаталог со случайным именем.
        /// Уникальность обеспечивает каталог, поэтому имя самого файла остается читаемым -
        /// от него зависит имя, под которым пользователь получит результат
        /// </summary>
        /// <param name="sourceFileName"></param>
        /// <returns></returns>
        private string GenerateFilePath(string sourceFileName)
        {
            var originalName = Path.GetFileName(sourceFileName);

            var safeFileName = Path.GetFileNameWithoutExtension(sourceFileName)
                .Replace(" ", "_")
                .Replace("/", "_")
                .Replace("\\", "_");

            // Имя из одних точек (".", "..") дало бы путь на сам каталог загрузки
            if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.Trim('.').Length == 0)
            {
                safeFileName = "document";
            }

            var uploadDir = Path.Combine(_storagePath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uploadDir);
            
            return Path.Combine(uploadDir, safeFileName + Path.GetExtension(originalName));
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