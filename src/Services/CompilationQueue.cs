using System.Threading.Channels;
using TexCompiler.Models;

namespace TexCompiler.Services
{
    /// <summary>
    /// Транспорт очереди компиляции. Писатель - SubmitTaskAsync, читатель - 
    /// единственный CompilationWorker
    /// </summary>
    public class CompilationQueue
    {
        private readonly Channel<CompilationTask> _channel;

        public CompilationQueue()
        {
            // Читать ровно один, поэтому SingleReader. Очередь неограниченная:
            // отказывать в приеме уже загруженного файла сервис не умеет, а размер
            // самой загрузки ограничен в контроллере
            _channel = Channel.CreateUnbounded<CompilationTask>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        public ValueTask EnqueueAsync(CompilationTask task, CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(task, cancellationToken);
        }

        public IAsyncEnumerable<CompilationTask> ReadAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
