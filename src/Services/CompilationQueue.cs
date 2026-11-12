using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using TexCompiler.Models;

namespace TexCompiler.Services
{
    /// <summary>
    /// Транспорт очереди компиляции. Писатель - SubmitTaskAsync, читатель -
    /// единственный CompilationWorker. Канал ограничен: заполнение означает, что
    /// worker не справляется с потоком загрузок, и HTTP-путь обязан отказать
    /// клиенту вместо неограниченного роста памяти и storage.
    /// </summary>
    public class CompilationQueue
    {
        private const int DefaultCapacity = 50;

        private readonly Channel<CompilationTask> _channel;
        private readonly int _capacity;

        public CompilationQueue(IConfiguration? configuration = null)
        {
            _capacity = configuration?.GetValue("CompilationQueue:Capacity", DefaultCapacity) ?? DefaultCapacity;

            _channel = Channel.CreateBounded<CompilationTask>(new BoundedChannelOptions(_capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                // Вместо отбрасывания ждем: HTTP-путь ждет ограниченное время
                // (TryEnqueueAsync) и сам решает, когда отказать
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public int Capacity => _capacity;

        public ValueTask EnqueueAsync(CompilationTask task, CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(task, cancellationToken);
        }

        /// <summary>
        /// Ставит задачу в очередь, ожидая освобождения места не дольше waitTimeout.
        /// Возвращает false, если очередь не освободилась за отведенное время -
        /// задача НЕ принята, вызывающий обязан ответить клиенту отказом.
        /// </summary>
        public async Task<bool> TryEnqueueAsync(CompilationTask task, TimeSpan waitTimeout)
        {
            using var cts = new CancellationTokenSource(waitTimeout);

            try
            {
                if (await _channel.Writer.WaitToWriteAsync(cts.Token).ConfigureAwait(false))
                {
                    return _channel.Writer.TryWrite(task);
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public IAsyncEnumerable<CompilationTask> ReadAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
