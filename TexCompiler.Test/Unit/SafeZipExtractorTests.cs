using System.IO.Compression;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class SafeZipExtractorTests : IDisposable
    {
        private readonly string _root;

        public SafeZipExtractorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"texcompiler-zip-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(_root);
        }

        [Fact]
        public void TryResolveEntryPath_PlainFile_IsAccepted()
        {
            var accepted = SafeZipExtractor.TryResolveEntryPath(_root, "main.tex", out var fullPath);
            
            Assert.True(accepted);
            Assert.Equal(Path.Combine(_root, "main.tex"), fullPath);
        }

        [Fact]
        public void TryResolveEntryPath_NestedFile_IsAccepted()
        {
            var accepted = SafeZipExtractor.TryResolveEntryPath(_root, "sections/intro.tex", out var fullPath);

            Assert.True(accepted);
            Assert.StartsWith(_root, fullPath);
        }

        [Theory]
        [InlineData("../evil.txt")]
        [InlineData("../../../etc/passwd")]
        [InlineData("sub/../../evil.txt")]
        public void TryResolveEntryPath_ParentTraversal_IsRejected(string entryName)
        {
            var accepted = SafeZipExtractor.TryResolveEntryPath(_root, entryName, out var fullPath);

            Assert.False(accepted);
            Assert.Equal(string.Empty, fullPath);
        }

        [Fact]
        public void TryResolveEntryPath_AbsolutePath_IsRejected()
        {
            var accepted = SafeZipExtractor.TryResolveEntryPath(_root, "/etc/passwd", out _);

            Assert.False(accepted);
        }

        [Fact]
        public void TryResolveEntryPath_SiblingDirectoryWithSharedPrefix_IsRejected()
        {
            // Ловушка проверки по префиксу: без завершающего разделителя путь
            // <root>-evil/x формально начинается с <root> и прошёл бы проверку
            var sibling = _root + "-evil";
            var entryName = Path.Combine("..", Path.GetFileName(sibling), "x.txt");

            var accepted = SafeZipExtractor.TryResolveEntryPath(_root, entryName, out _);
            
            Assert.False(accepted);
        }

        [Fact]
        public void TryResolveEntryPath_EmptyName_IsRejected()
        {
            Assert.False(SafeZipExtractor.TryResolveEntryPath(_root, string.Empty, out _));
            Assert.False(SafeZipExtractor.TryResolveEntryPath(_root, "  ", out _));
        }

        [Fact]
        public void ExtractTo_NormalArchive_ExtractAllEntries()
        {
            var zipPath = CreateArchive(("main.tex", "hello"), ("img/pic.asy", "size(1cm);"));
            var target = CreateTargetDirectory();

            SafeZipExtractor.ExtractTo(zipPath, target);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "main.tex")));
            Assert.True(File.Exists(Path.Combine(target, "img", "pic.asy")));
        }

        [Fact]
        public void ExtractTo_ArchiveWithTraversalEntry_ThrowsAndWriteNothingOutside()
        {
            var zipPath = CreateArchive(("../escaped.txt", "pwned"));
            var target = CreateTargetDirectory();
            var escaped = Path.Combine(_root, "escaped.txt");

            Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractTo(zipPath, target));

            // Главное утверждение теста: файл не появился за пределами целевого каталога
            Assert.False(File.Exists(escaped));
        }

        [Fact]
        public void ExtractTo_TooManyEntries_Throws()
        {
            var zipPath = CreateArchive(("a.tex", "a"), ("b.tex", "b"), ("c.tex", "c"));
            var target = CreateTargetDirectory();

            Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractTo(zipPath, target, maxEntryCount: 2));
        }

        [Fact]
        public void ExtractTo_TotalSizeOverLimit_Throws()
        {
            var zipPath = CreateArchive(("big.tex", new string('x', 4096)));
            var target = CreateTargetDirectory();

            Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractTo(zipPath, target, maxTotalBytes: 1024));
        }

        [Fact]
        public void ExtractTo_SizeWithinLimit_Success()
        {
            var zipPath = CreateArchive(("small.tex", new string('x', 100)));
            var target = CreateTargetDirectory();

            SafeZipExtractor.ExtractTo(zipPath, target, maxTotalBytes: 4096);
            Assert.True(File.Exists(Path.Combine(target, "small.tex")));
        }


        private string CreateTargetDirectory()
        {
            var target = Path.Combine(_root, $"target-{Guid.NewGuid():N}");
            Directory.CreateDirectory(target);
            return target;
        }

        private string CreateArchive(params (string Name, string Content)[] entries)
        {
            var zipPath = Path.Combine(_root, $"archive-{Guid.NewGuid():N}.zip");

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

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (IOException)
            {
                // Уборка тестов не должна ронять прогон
            }
        }
    }
}
