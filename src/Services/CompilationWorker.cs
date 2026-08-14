using TexCompiler.Models;

namespace TexCompiler.Services
{
    /// <summary>
    /// Единственный потребитель очереди компиляции
    /// </summary>
    public class CompilationWorker : BackgroundService
    {
        private readonly CompilationQueue _queue;
        private readonly ITaskStorageService _taskStorage;
        private readonly ICompilationService _compilationService;
        private readonly ILogger<CompilationWorker> _logger;

        public CompilationWorker(
            CompilationQueue queue,
            ITaskStorageService taskStorage,
            ICompilationService compilationService,
            ILogger<CompilationWorker> logger)
        {
            _queue = queue;
            _taskStorage = taskStorage;
            _compilationService = compilationService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Compilation worker started");

            try
            {
                await foreach (var task in _queue.ReadAllAsync(cancellationToken))
                {
                    await ProcessTaskAsync(task, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Остановка приложения
            }

            _logger.LogInformation("Compilation worker stopped");
        }

        private async Task ProcessTaskAsync(CompilationTask task, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing task: {TaskId}", task.TaskId);

            try
            {
                var result = await _compilationService.CompileAsync(task).WaitAsync(cancellationToken);

                _taskStorage.UpdateTask(task.SetCompleted(result));
                _logger.LogInformation("Task {TaskId} completed with status: {Status}",
                    task.TaskId, task.TaskStatus);
            }
            catch (OperationCanceledException)
            {
                MarkInterrupted(task);
            }

            catch (Exception ex)
            {
                _taskStorage.UpdateTask(task.SetFailed(ex));
                _logger.LogError(ex, "Error processing task: {TaskId}", task.TaskId);
            }

        }

        private void MarkInterrupted(CompilationTask task)
        {
            _taskStorage.UpdateTask(task.SetCompleted(new CompilationResult
            {
                IsSuccess = false,
                ErrorMessage = "Обработка прервана перезапуском сервиса. Отправьте файл заново."
            }));

            _logger.LogWarning("Task {TaskId} interrupted by service shutdown", task.TaskId);
        }

    }
}
