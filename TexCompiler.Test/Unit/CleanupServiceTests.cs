using Castle.Components.DictionaryAdapter.Xml;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class CleanupServiceTests : IDisposable
    {
        private readonly string _webRoot;
        private readonly string _pdfDir;
        private readonly Mock<ITaskStorageService> _taskStorageService;
        private readonly CleanupService _service;

        public CleanupServiceTests()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), $"texcompiler-cleanup-test-{Guid.NewGuid()}");
            _pdfDir = Path.Combine(_webRoot, "pdfs");
            Directory.CreateDirectory(_pdfDir);

            var environmentMock = new Mock<IWebHostEnvironment>();
            environmentMock.Setup(e => e.WebRootPath).Returns(_webRoot);

            _taskStorageService = new Mock<ITaskStorageService>();
            _taskStorageService.Setup(t => t.GetAllTasks()).Returns(new List<CompilationTask>());

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CleanupSettings:PdfRetentionMinutes"] = "60",
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
