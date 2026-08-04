namespace TexCompiler.UnitTests.Services
{
    public class MainTexFileTests : IDisposable
    {
        private readonly string _root;

        public MainTexFileTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"texcompiler-maintex-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(_root);
        }

        [Fact]
        public void MainTexInSubdirectory_IsFoundWithItsOwnDirectory()
        {
            // Типичный архив: внутри одна папка проекта, а в ней все остальное
            var projectDir = Path.Combine(_root, "project");
            CreateTex(Path.Combine(projectDir, "main.tex"));
            CreateTex(Path.Combine(projectDir, "sections", "intro.tex"));

            var found = CompilationService.FindMainTexFile(_root);

            Assert.Equal("main.tex", Path.GetFileName(found));
            Assert.Equal(projectDir, Path.GetDirectoryName(found));
        }

        [Fact]
        public void TexInRoot_KeepsRootAsWorkingDirectory()
        {
            CreateTex(Path.Combine(_root, "diploma.tex"));

            var found = CompilationService.FindMainTexFile(_root);

            Assert.Equal(_root, Path.GetDirectoryName(found));
        }

        [Fact]
        public void FileWinMainName_WinsOverOtherTexFiles()
        {
            CreateTex(Path.Combine(_root, "diploma.tex"));
            CreateTex(Path.Combine(_root, "book", "main.tex"));

            var found = CompilationService.FindMainTexFile(_root);

            Assert.Equal("main.tex", Path.GetFileName(found));
            Assert.Equal(Path.Combine(_root, "book"), Path.GetDirectoryName(found));
        }           

        [Fact]
        public void NoTexFiles_ReturnsEmpty()
        {
            Directory.CreateDirectory(Path.Combine(_root, "images"));
            File.WriteAllText(Path.Combine(_root, "images", "plot.png"), "not a tex");

            Assert.Equal(string.Empty, CompilationService.FindMainTexFile(_root));
        }

        private static void CreateTex(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "\\documentsclass{article}\\being{document}x\\end{document}");
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
                // Уборка не должна ронять прогон
            }
        }
    }
}
