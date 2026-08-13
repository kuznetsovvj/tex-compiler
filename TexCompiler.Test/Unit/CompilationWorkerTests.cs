using Microsoft.Extensions.Logging;
using Moq;
using System.Numerics;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class CompilationWorkerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly CompilationQueue _queue = new();
        private readonly Mock<ITaskStorageService> _taskStorageMock = new();
        private readonly Mock<ICompilationService> _compilationServiceMock = new();
        private readonly CompilationWorker _worker;

        public CompilationWorkerTests()
        {
            _worker = new CompilationWorker(
                _queue,
                _taskStorageMock.Object,
                _compilationServiceMock.Object,
                new Mock<ILogger<CompilationWorker>>().Object);
        }

        [Fact]
        public async Task EnqueuedTask_IsCompiled_WithoutExternalTrigger()
        {
            var compiled = new TaskCompletionSource<CompilationTask>();
            _compilationServiceMock.Setup(c => c.CompileAsync(It.IsAny<CompilationTask>()))
                .Returns((CompilationTask task) =>
                {
                    compiled.TrySetResult(task);
                    return Task.FromResult(new CompilationResult { IsSuccess = true });
                });

            await _worker.StartAsync(CancellationToken.None);
            try
            {
                var task = new CompilationTask("/tmp/storage/a/diploma.tex");
                await _queue.EnqueueAsync(task);

                var seen = await compiled.Task.WaitAsync(Timeout);

                Assert.Equal(task.TaskId, seen.TaskId);
                Assert.Equal(CompilationTaskStatus.Completed, task.TaskStatus);
            }
            finally
            {
                await _worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task TaskEnqueuedWhileAnotherIsRunning_IsPickedUpAfterIt()
        {
            var firstStarted = new TaskCompletionSource();
            var releaseFirst = new TaskCompletionSource();
            var secondCompiled = new TaskCompletionSource<CompilationTask>();
            var callCount = 0;

            _compilationServiceMock.Setup(c => c.CompileAsync(It.IsAny<CompilationTask>()))
                .Returns(async (CompilationTask task) =>
                {
                    if (Interlocked.Increment(ref callCount) == 1)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task;
                    }
                    else
                    {
                        secondCompiled.TrySetResult(task);
                    }

                    return new CompilationResult { IsSuccess = true };
                });

            await _worker.StartAsync(CancellationToken.None);

            try
            {
                await _queue.EnqueueAsync(new CompilationTask("/tmp/storage/a/first.tex"));
                await firstStarted.Task.WaitAsync(Timeout);

                var second = new CompilationTask("/tmp/storage/a/second.tex");
                await _queue.EnqueueAsync(second);

                // Пока первая на отпущена, вторая ждет: одна компиляция одновременно
                Assert.False(secondCompiled.Task.IsCompleted);

                releaseFirst.SetResult();

                var seen = await secondCompiled.Task.WaitAsync(Timeout);
                Assert.Equal(second.TaskId, seen.TaskId);
                
            }
            finally
            {
                releaseFirst.TrySetResult();
                await _worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Worker_CompilesOneTaskAtATime()
        {
            var running = 0;
            var maxParallel = 0;
            var allDone = new TaskCompletionSource();
            var completed = 0;

            _compilationServiceMock.Setup(c => c.CompileAsync(It.IsAny<CompilationTask>()))
                .Returns(async (CompilationTask _) =>
                {
                    var current = Interlocked.Increment(ref running);
                    InterlockedMax(ref maxParallel, current);

                    await Task.Delay(20);

                    Interlocked.Decrement(ref running);

                    if (Interlocked.Increment(ref completed) == 3)
                    {
                        allDone.TrySetResult();
                    }

                    return new CompilationResult { IsSuccess = true };
                });

            await _worker.StartAsync(CancellationToken.None);
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    await _queue.EnqueueAsync(new CompilationTask($"/tmp/storage/{i}/doc.tex"));
                }

                await allDone.Task.WaitAsync(Timeout);
                Assert.Equal(1, maxParallel);
            }
            finally
            {
                await _worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task FailingTask_DoesNotStopTheWorker()
        {
            // Ошибка компиляции не должна уносить единственного потребителя
            var secondCompiled = new TaskCompletionSource<CompilationTask>();
            var callCount = 0;

            _compilationServiceMock.Setup(c => c.CompileAsync(It.IsAny<CompilationTask>()))
                .Returns((CompilationTask task) =>
                {
                    if (Interlocked.Increment(ref callCount) == 1)
                    {
                        throw new IOException("disk full");
                    }

                    secondCompiled.TrySetResult(task);
                    return Task.FromResult(new CompilationResult { IsSuccess = true });
                });

            await _worker.StartAsync(CancellationToken.None);
            try
            {
                var failing = new CompilationTask("/tmp/storage/a/broken.tex");
                await _queue.EnqueueAsync(failing);

                var second = new CompilationTask("/tmp/storage/b/good.tex");
                await _queue.EnqueueAsync(second);

                var seen = await secondCompiled.Task.WaitAsync(Timeout);

                Assert.Equal(second.TaskId, seen.TaskId);
                Assert.Equal(CompilationTaskStatus.Failed, failing.TaskStatus);
            }
            finally
            {
                await _worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task TaskInterruptedByShutdown_IsMarkedFailedWithExplanation()
        {
            var started = new TaskCompletionSource();
            var release = new TaskCompletionSource();

            _compilationServiceMock.Setup(c => c.CompileAsync(It.IsAny<CompilationTask>()))
                .Returns(async (CompilationTask _) =>
                {
                    started.TrySetResult();
                    await release.Task;

                    return new CompilationResult { IsSuccess = true };
                });

            await _worker.StartAsync(CancellationToken.None);
            try
            {
                var task = new CompilationTask("/tmp/storage/a/doc.tex");
                await _queue.EnqueueAsync(task);
                await started.Task.WaitAsync(Timeout);

                await _worker.StopAsync(CancellationToken.None).WaitAsync(Timeout);

                Assert.Equal(CompilationTaskStatus.Failed, task.TaskStatus);
                Assert.Contains("перезапуск", task.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                release.TrySetResult();
            }

        }

        [Fact]
        public async Task Stop_DoesNotHangWhenQueueIsEmpty()
        {
            await _worker.StartAsync(CancellationToken.None);

            await _worker.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)))
                {
                    if (Interlocked.CompareExchange(ref target, value, current) == current)
                    {
                        return;
                    }
                }
        }
    }
}
