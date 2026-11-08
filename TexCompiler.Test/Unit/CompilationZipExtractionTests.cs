using System.IO.Compression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    /// <summary>
    /// Интеграционная регрессия Zip Slip именно через CompileAsync: unit-тесты
    /// SafeZipExtractor проверяют отдельный класс, который реальный код не вызывал.
    /// Временный каталог CompileAsync живет в Path.GetTempPath(), поэтому запись
    /// "../escaped.txt" означала бы файл в корне системного temp.
    /// </summary>
    public class CompilationZipExtractionTests : IDisposable
    {
        private readonly string _workDir;

        public CompilationZipExtractionTests()
        {
            _workDir = Path.Combine(Path.GetTempPath(), $"texcompiler-zipslip-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(_workDir);
        }

        [Fact]
        public async Task CompileAsync_ZipWithParentTraversalEntry_WritesNothingOutsideTempDir()
        {
            var escapedName = $"escaped-{Guid.NewGuid():N}.txt";
            var zipPath = CreateArchive(("../" + escapedName, "pwned"), ("main.tex", "\\documentclass{article}\\begin{document}x\\end{document}"));

            var result = await CreateService().CompileAsync(new CompilationTask(zipPath));

            Assert.False(result.IsSuccess);
            Assert.Contains("распаковать", result.ErrorMessage);

            // Главное утверждение: файл не появился вне временного каталога задачи
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), escapedName)),
                "запись ../ в архиве записала файл за пределы временного каталога");
        }

        [Fact]
        public async Task CompileAsync_ZipWithAbsolutePathEntry_WritesNothingOutsideTempDir()
        {
            var escapedPath = Path.Combine(Path.GetTempPath(), $"escaped-abs-{Guid.NewGuid():N}.txt");
            var zipPath = CreateArchive((escapedPath, "pwned"), ("main.tex", "\\documentclass{article}\\begin{document}x\\end{document}"));

            var result = await CreateService().CompileAsync(new CompilationTask(zipPath));

            Assert.False(result.IsSuccess);
            Assert.Contains("распаковать", result.ErrorMessage);
            Assert.False(File.Exists(escapedPath),
                "запись с абсолютным путем записала файл за пределы временного каталога");
        }

        [Fact]
        public async Task CompileAsync_NormalZip_PassesExtraction()
        {
            // Контроль с другой стороны: обычный архив проходит распаковку.
            // pdflatex в тестовом окружении нет, поэтому успеха компиляции не ждем,
            // но отказ обязан быть не про архив
            var zipPath = CreateArchive(("main.tex", "\\documentclass{article}\\begin{document}x\\end{document}"));

            var result = await CreateService().CompileAsync(new CompilationTask(zipPath));

            Assert.DoesNotContain("распаковать", result.ErrorMessage ?? string.Empty);
        }

        private string CreateArchive(params (string Name, string Content)[] entries)
        {
            var zipPath = Path.Combine(_workDir, $"archive-{Guid.NewGuid():N}.zip");

            using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }

            return zipPath;
        }

        private static CompilationService CreateService()
        {
            var webRoot = Path.Combine(Path.GetTempPath(), $"texcompiler-zip-webroot-{Guid.NewGuid()}");
            Directory.CreateDirectory(webRoot);

            var environmentMock = new Mock<IWebHostEnvironment>();
            // ArtifactPath строит каталоги результатов от ContentRootPath, без него
            // конструктор CompilationService падает на Path.Combine
            environmentMock.Setup(e => e.ContentRootPath).Returns(webRoot);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CompilationSettings:ProcessTimeoutSeconds"] = "30"
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
