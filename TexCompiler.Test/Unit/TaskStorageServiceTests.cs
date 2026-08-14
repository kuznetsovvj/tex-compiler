using Microsoft.Extensions.Logging;
using Moq;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class TaskStorageServiceTests
    {
        private readonly TaskStorageService _storage =
            new(new Mock<ILogger<TaskStorageService>>().Object);

        [Fact]
        public void GetQueuePosition_CountsOnlyQueuedTask()
        {
            // Позиция считается по словарю статусов и на зависит от того, как
            // реализован транспорт очереди.
            var processing = new CompilationTask("/tmp/storage/first.tex");
            processing.SetProcessing();

            var queued = new CompilationTask("/tmp/storage/second.tex");

            _storage.AddTask(processing);
            _storage.AddTask(queued);

            Assert.Equal(1, _storage.GetQueuePosition(queued.TaskId));
        }

        [Fact]  
        public void GetQueuePosition_UnknownTask_ReturnsZero()
        {
            Assert.Equal(0, _storage.GetQueuePosition(Guid.NewGuid()));
        }

        [Fact]
        public void AddedTask_IsAvailableByIdAndInAllTasks()
        {
            var task = new CompilationTask("/tmp/storage/a/doc.tex");

            _storage.AddTask(task);
            Assert.Same(task, _storage.GetTask(task.TaskId));
            Assert.Contains(task, _storage.GetAllTasks());
        }

        [Fact]
        public void RemovedTask_IsNoLongerFound()
        {
            var task = new CompilationTask("/tmp/storage/a/doc.tex");
            _storage.AddTask(task);

            Assert.True(_storage.TryRemoveTask(task.TaskId));
            Assert.Null(_storage.GetTask(task.TaskId));
            Assert.False(_storage.TryRemoveTask(task.TaskId));
        }
    }
}
