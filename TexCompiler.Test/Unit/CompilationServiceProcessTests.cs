using Castle.Core.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    /// <summary>
    /// Проверка чтения потоков внешнего процесса. Запускают реальный /bin/sh: поведение pipe 
    /// с заполненным буфером иначе не воспроизвести - моки здесь ничего не доказывают.
    /// </summary>
    public class CompilationServiceProcessTests : IDisposable
    {
        private const string ShellPath = "bin/sh";

        private readonly string _workDir;

        public CompilationServiceProcessTests()
        {
            _workDir = Path.Combine(Path.GetTempPath(), $"texcompiler-process-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(_workDir);
        }

        [Fact]
        public async Task ProcessWritingMoreToStderrThanBuffer_Completes()
        {
            // Регрессия: stderr был перенаправлен, но не читался. Буфер pipe на Linux
            // порядка 64 Кб, и на этом объеме процесс блокировался на записи навсегда - 
            // родитель ждал завершения, процесс ждал места в буфере. Пишем заведомо больше буфера
            // и ждем нормального завершения
            if (!File.Exists(ShellPath))
            {
                return; // не Unix - вопроизвести нечем
            }

            var script = WriteScript("noisy.sh", """
                line=$(printf 'E%.0s' $(seq 1 200))
                i=0
                while [ $i -lt 1000 ]; do
                    echo "$line" 1>&2
                    i=$((i+1))
                done
                echo finished
                exit 3
                """);

            var service = CreateService(timeoutSeconds: 60);

            var result = await service.RunProcessAsync(ShellPath, script, _workDir);

            Assert.False(result.TimedOut, "Процесс должен завершиться сам, а не по таймауту");
            Assert.False(result.Success, "Код возврата 3 - неуспех");
            Assert.True(result.Error.Length > 64 * 1024, $"Ожидали больше 64 Кб в stderr, получили {result.Error.Length} байт");
            Assert.Contains("finished", result.Output);
        }

        [Fact]
        public async Task BothStreamsAreCaptured()
        {
            if (!File.Exists(ShellPath))
            {
                return; 
            }

            var script = WriteScript("both.sh", """
                echo to-stdout
                echo to-srderr 1<&2
                """);

            var service = CreateService(timeoutSeconds: 60);

            var result = await service.RunProcessAsync(ShellPath, script, _workDir);

            Assert.True(result.Success);
            Assert.Contains("to-stdout", result.Output);
            Assert.Contains("to-stderr", result.Error);
        }

        [Fact]
        public async Task TimedOutProcess_ReturnsInsteadOfHangling()
        {
            if (!File.Exists(ShellPath))
            {
                return;
            }

            var script = WriteScript("slow.sh", """
                sleep 30
                """);

            var service = CreateService(timeoutSeconds: 1);

            var result = await service.RunProcessAsync(ShellPath, script, _workDir)
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(result.TimedOut);
            Assert.False(result.Success);
        }

        private string WriteScript(string name, string body)
        {
            var path = Path.Combine(_workDir, name);
            File.WriteAllText(path, body);

            return name;
        }

        private static CompilationService CreateService(int timeoutSeconds)
        {
            var webRoot = Path.Combine(Path.GetTempPath(), $"texcompiler-process-webroot-{Guid.NewGuid()}");
            Directory.CreateDirectory(webRoot);

            var environmentMock = new Mock<IWebHostEnvironment>();
            environmentMock.Setup(e => e.WebRootPath).Returns(webRoot);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CompilationSettings:ProcessTimeoutSeconds"] = timeoutSeconds.ToString()
                })
                .Build();

            return new CompilationService
                (environmentMock.Object,
                new Mock<ILogger<CompilationService>>().Object,
                configuration);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_workDir))
                {
                    Directory.Delete(_workDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Уборка тестов не должна ронять прогон
            }

        }
    }
}
