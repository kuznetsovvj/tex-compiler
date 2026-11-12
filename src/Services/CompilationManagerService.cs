using TexCompiler.Models;

namespace TexCompiler.Services
{
    public class CompilationManagerService
    {
        private readonly ITaskStorageService _taskStorage;
        private readonly CompilationQueue _queue;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<CompilationManagerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _storagePath;

        // Сколько HTTP-запрос готов ждать свободное место в очереди, прежде чем
        // ответить отказом. Долго держать соединение нет смысла: при переполнении
        // задача все равно будет ждать в очереди долго.
        private TimeSpan EnqueueWaitTimeout => TimeSpan.FromSeconds(
            _configuration.GetValue<int>("CompilationQueue:EnqueueTimeoutSeconds", 2));

        public CompilationManagerService(
            ITaskStorageService taskStorage,
            CompilationQueue queue,
            IWebHostEnvironment environment,
            ILogger<CompilationManagerService> logger,
            IConfiguration configuration)
        {
            _taskStorage = taskStorage;
            _queue = queue;
            _environment = environment;
            _logger = logger;
            _configuration = configuration;
            _storagePath = Path.Combine(_environment.ContentRootPath, "storage");

            Directory.CreateDirectory(_storagePath);
        }

        /// <summary>
        /// Сохраняет загруженный файл и передает задачу потребителю очереди.
        /// Возвращает null, если очередь заполнена и задача не принята: вызывающий
        /// обязан ответить клиенту отказом. Уже сохраненный файл при отказе
        /// удаляется - иначе отклоненные загрузки копились бы в storage без задачи.
        /// </summary>
        public async Task<Guid?> SubmitTaskAsync(IFormFile file)
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

                var accepted = await _queue.TryEnqueueAsync(task, EnqueueWaitTimeout);
                if (!accepted)
                {
                    _taskStorage.TryRemoveTask(task.TaskId);
                    TryDeleteUploadedFile(filePath);
                    _logger.LogWarning(
                        "Compilation queue is full (capacity {Capacity}), upload rejected: {FileName}",
                        _queue.Capacity, file.FileName);
                    return null;
                }

                return task.TaskId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting task for file: {FileName}", file.FileName);
                throw;
            }
        }

        private static void TryDeleteUploadedFile(string filePath)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception)
            {
                // Чистка не должна маскировать основной отказ: остаток доберет CleanupService
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