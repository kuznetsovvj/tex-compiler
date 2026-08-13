using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Runtime.CompilerServices;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class CleanupServiceTests : IDisposable
    {
        private readonly string _contentRoot;
        private readonly string _webRoot;
        private readonly string _pdfDir;
        private readonly string _storageDir;
        private readonly Mock<ITaskStorageService> _taskStorageService;
        private readonly CleanupService _service;

        public CleanupServiceTests()
        {
            _contentRoot = Path.Combine(Path.GetTempPath(), $"texcompiler-cleanup-test-{Guid.NewGuid()}");
            _webRoot = Path.Combine(_contentRoot, "wwwroot");
            _storageDir = Path.Combine(_contentRoot, "storage");

            var environmentMock = new Mock<IWebHostEnvironment>();
            environmentMock.Setup(e => e.WebRootPath).Returns(_webRoot);
            environmentMock.Setup(e => e.ContentRootPath).Returns(_contentRoot);

            _pdfDir = ArtifactPath.GetPdfDirectory(environmentMock.Object);
            Directory.CreateDirectory(_pdfDir);
            Directory.CreateDirectory(_storageDir);

            _taskStorageService = new Mock<ITaskStorageService>();
            _taskStorageService.Setup(t => t.GetAllTasks()).Returns(new List<CompilationTask>());

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CleanupSettings:PdfRetentionMinutes"] = "60",
                    ["CleanupSettings:SourceRetentionMinutes"] = "120",
                    ["CleanupSettings:TaskRetentionHours"] = "2",
                    ["CleanupSettings:TempRetentionMinutes"] = "15",
                })
                .Build();

            _service = new CleanupService(
                new Mock<ILogger<CleanupService>>().Object,
                environmentMock.Object,
                _taskStorageService.Object,
                configuration);
        }

        [Fact]
        public void PerformFullCleanup_OldPfdReferencedByTask_IsKept()
        {
            var pdfPath = CreateAgedPdf("referenced.pdf", TimeSpan.FromHours(3));
            GivenTasks(new CompilationTask(Path.Combine("storage", "avc", "doc.tex"))
            {
                PdfFilePath = pdfPath
            });

            _service.PerformFullCleanup();
            Assert.True(File.Exists(pdfPath), "PDF живой задачи не должен удаляться");
        }


        [Fact]
        public void PerformFullCleanup_OldOrphanedPdf_IsDeleted()
        {
            var pdfPath = CreateAgedPdf("orphan.pdf", TimeSpan.FromHours(3));
            
            _service.PerformFullCleanup();
            Assert.False(File.Exists(pdfPath), "PDF без задачи и старше срока должен удаляться");
        }

        [Fact]
        public void PerformFullCleanup_RecentOrphanedPdf_IsKept()
        {
            var pdfPath = CreateAgedPdf("fresh.pdf", TimeSpan.FromMinutes(5));

            _service.PerformFullCleanup();
            Assert.True(File.Exists(pdfPath), "PDF моложе срока хранения удалять рано");
        }

        [Fact]
        public void PerformFullCleanup_OldOrphanedUpload_IsDeleted()
        {
            var upload = CreateAgedUpload("diplom.tex", TimeSpan.FromHours(3));

            _service.PerformFullCleanup();

            Assert.False(File.Exists(upload), "исходник без задачи и старше срока хранения должен удаляться");
            Assert.False(Directory.Exists(Path.GetDirectoryName(upload)!), "каталог загрузки тоже должен удаляться");
        }

        [Theory]
        [InlineData(CompilationTaskStatus.Processing)]
        [InlineData(CompilationTaskStatus.Queued)]
        public void PerformFullCleanup_UploadOfLivingTask_IsKept(CompilationTaskStatus status)
        {
            // Задача может стоять в очереди дольше срока хранения - например, когда
            // очередь встала. Удалить ее исходник значит гарантированно уронить
            // компиляцию, до которой обработка еще не дошла
            var upload = CreateAgedUpload("diplom.tex", TimeSpan.FromHours(3));
            GivenTasks(new CompilationTask(upload) { TaskStatus = status });

            _service.PerformFullCleanup();

            Assert.True(File.Exists(upload), $"исходник задачи в статусе {status} удалять нельзя");
        }

        [Fact]
        public void PerformFullCleanup_RecentUpload_IsKept()
        {
            var upload = CreateAgedUpload("diploma.tex", TimeSpan.FromMicroseconds(10));
            _service.PerformFullCleanup();

            Assert.True(File.Exists(upload), "исходник моложе срока хранения удалять нельзя");
        }

        [Fact]
        public void PerformFullCleanup_LegacyFlatSourceFiles_OnlyOrphanedIsDeleted()
        {
            var orphan = CreateAgedLegacyFile("20260730_12000_orphan.tex", TimeSpan.FromHours(3));
            var referenced = CreateAgedLegacyFile("20260730_12000_referenced.tex", TimeSpan.FromHours(3));
            GivenTasks(new CompilationTask(referenced));

            _service.PerformFullCleanup();
            Assert.False(File.Exists(orphan), "файл прежней раскладки без задачи должен удалиться");
            Assert.True(File.Exists(referenced), "файл прежней раскладки с живой задачей - нет");
        }

        [Fact]
        public void ArtifactDirectories_AreOutsideWebroot()
        {
            var environment = new Mock<IWebHostEnvironment>();
            environment.Setup(e => e.WebRootPath).Returns(_webRoot);
            environment.Setup(e => e.ContentRootPath).Returns(_contentRoot);

            var webRootPrefix = _webRoot + Path.DirectorySeparatorChar;

            Assert.DoesNotContain(webRootPrefix, ArtifactPath.GetPdfDirectory(environment.Object));
            Assert.DoesNotContain(webRootPrefix, ArtifactPath.GetLogDirectory(environment.Object));
        }

        private void GivenTasks(params CompilationTask[] tasks)
        {
            _taskStorageService.Setup(t => t.GetAllTasks()).Returns(tasks.ToList());
        }

        private string CreateAgedPdf(string name, TimeSpan age)
        {
            var path = Path.Combine(_pdfDir, name);
            File.WriteAllText(path, "%PDF-1.4");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

            return path;
        }

        private string CreateAgedUpload(string name, TimeSpan age)
        {
            var uploadDir = Path.Combine(_storageDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uploadDir);

            var path = Path.Combine(uploadDir, name);
            File.WriteAllText(path, "\\documentclass{article}");

            // Каталог состарить после записи файла - запись обновляет его время
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            Directory.SetLastWriteTimeUtc(uploadDir, DateTime.UtcNow - age);

            return path;
        }

        private string CreateAgedLegacyFile(string name, TimeSpan age)
        {
            var path = Path.Combine(_storageDir, name);
            File.WriteAllText(path, "\\documentclass{article}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_webRoot))
                {
                    Directory.Delete(_webRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // Уборка тестов не должна ронять прогон
            }
        }

    }
}
