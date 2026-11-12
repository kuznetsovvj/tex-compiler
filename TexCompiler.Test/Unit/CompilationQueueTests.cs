using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class CompilationQueueTests
    {
        private static CompilationQueue CreateQueue(int capacity)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CompilationQueue:Capacity"] = capacity.ToString()
                })
                .Build();

            return new CompilationQueue(config);
        }

        [Fact]
        public async Task TryEnqueue_WithinCapacity_Accepts()
        {
            var queue = CreateQueue(2);

            Assert.True(await queue.TryEnqueueAsync(new CompilationTask("/tmp/a.tex"), TimeSpan.FromSeconds(1)));
            Assert.True(await queue.TryEnqueueAsync(new CompilationTask("/tmp/b.tex"), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task TryEnqueue_WhenFull_WaitsThenRejects()
        {
            var queue = CreateQueue(1);
            Assert.True(await queue.TryEnqueueAsync(new CompilationTask("/tmp/a.tex"), TimeSpan.FromSeconds(1)));

            var sw = Stopwatch.StartNew();
            var accepted = await queue.TryEnqueueAsync(
                new CompilationTask("/tmp/b.tex"), TimeSpan.FromMilliseconds(300));
            sw.Stop();

            Assert.False(accepted);
            Assert.True(sw.ElapsedMilliseconds >= 250,
                $"ожидали, что запрос дождется конца таймаута, факт: {sw.ElapsedMilliseconds} мс");
        }

        [Fact]
        public async Task TryEnqueue_AfterDequeue_AcceptsAgain()
        {
            var queue = CreateQueue(1);
            Assert.True(await queue.TryEnqueueAsync(new CompilationTask("/tmp/a.tex"), TimeSpan.FromSeconds(1)));

            // Забираем задачу - место освобождается
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var _ in queue.ReadAllAsync(cts.Token))
            {
                break;
            }

            Assert.True(await queue.TryEnqueueAsync(new CompilationTask("/tmp/b.tex"), TimeSpan.FromSeconds(1)));
        }
    }
}
