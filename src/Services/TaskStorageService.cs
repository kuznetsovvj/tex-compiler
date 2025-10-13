using System.Collections.Concurrent;
using TexCompiler.Models;

namespace TexCompiler.Services
{
    public class TaskStorageService : ITaskStorageService
    {
        private readonly ConcurrentQueue<CompilationTask> _taskQueue;
        private readonly ConcurrentDictionary<Guid, CompilationTask> _taskDictionary;
        private readonly ILogger<TaskStorageService> _logger;

        public TaskStorageService(ILogger<TaskStorageService> logger)
        {
            _taskQueue = new ConcurrentQueue<CompilationTask>();
            _taskDictionary = new ConcurrentDictionary<Guid, CompilationTask>();
            _logger = logger;
        }

        public void AddTask(CompilationTask task)
        {
            _taskQueue.Enqueue(task);
            _taskDictionary[task.TaskId] = task;
        }

        /// <summary>
        /// Получает следующую задачу из очереди (удаляя ее)
        /// </summary>
        public CompilationTask? GetNextTask()
        {
            if (_taskQueue.TryDequeue(out var task))
            {
                _logger.LogDebug("Task retrieved from queue: {TaskId}", task.TaskId);
                return task;
            }
            return null;
        }

        /// <summary>
        /// Получает задачу по ID без удаления из очереди
        /// </summary>
        public CompilationTask? GetTask(Guid taskId)
        {
            _taskDictionary.TryGetValue(taskId, out var task);
            return task;
        }

        /// <summary>
        /// Обновляет задачу в словаре
        /// </summary>
        public void UpdateTask(CompilationTask task)
        {
            _taskDictionary[task.TaskId] = task;
            _logger.LogDebug("Task updated in storage: {TaskId}", task.TaskId);
        }

        /// <summary>
        /// Получает все задачи (для статистики)
        /// </summary>
        public List<CompilationTask> GetAllTasks()
        {
            return _taskDictionary.Values.ToList();
        }

        /// <summary>
        /// Очищает старые задачи (для FileCleanupService)
        /// </summary>
        public bool TryRemoveTask(Guid taskId)
        {
            return _taskDictionary.TryRemove(taskId, out _);
        }

        public int GetQueuePosition(Guid taskId)
        {
            var queuedTasks = _taskDictionary.Values
                .Where(t => t.TaskStatus == CompilationTaskStatus.Queued)
                .OrderBy(t => t.CreatedAt)
                .ToList();

            return queuedTasks.FindIndex(t => t.TaskId == taskId) + 1;

        }
    }
}