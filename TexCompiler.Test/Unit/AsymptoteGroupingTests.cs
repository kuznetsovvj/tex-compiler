namespace TexCompiler.UnitTests.Services
{
    /// <summary>
    /// Группировка .asy по каталогам. Проверяется именно она, потому что рабочий каталог
    /// вызова asy определяет, где окажется картинка: asy пишет результат в свой рабочий
    /// каталог, а \includegraphics ищет файл рядом с tex-исходником.
    /// </summary>
    public class AsymptoteGroupingTests
    {
        private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "tex_compile_sample");

        [Fact]
        public void FilesInRoot_FormSingleGroupWithRootAsWorkingDirectory()
        {
            var files = new[]
            {
                Path.Combine(TempDir, "main-1.asy"),
                Path.Combine(TempDir, "main-2.asy")
            };

            var groups = CompilationService.GroupAsymptoteFilesByDirectory(files, TempDir);

            var group = Assert.Single(groups);
            Assert.Equal(Path.GetFullPath(TempDir), group.Directory);
            Assert.Equal(new[] { "main-1.asy", "main-2.asy" }, group.FileNames);
        }

        [Fact]
        public void FilesInSubdirectories_AreGroupedByThierOwnDirectory()
        {
            var figures = Path.Combine(TempDir, "figures");
            var images = Path.Combine(TempDir, "images", "extra");

            var files = new[]
            {
                Path.Combine(figures, "plot.asy"),
                Path.Combine(figures, "graph.asy"),
                Path.Combine(images, "scheme.asy")
            };

            var groups = CompilationService.GroupAsymptoteFilesByDirectory(files, TempDir);

            Assert.Equal(2, groups.Count);

            var figuresGroup = groups.Single(g => g.Directory == Path.GetFullPath(figures));
            Assert.Equal(new[] { "graph.asy", "plot.asy" }, figuresGroup.FileNames);

            var imagesGroup = groups.Single(g => g.Directory == Path.GetFullPath(images));
            Assert.Equal(new[] { "scheme.asy" }, imagesGroup.FileNames);
        }

        [Fact]
        public void FilesInSubdirectories_AreSubdirectoriesSeparate()
        {
            var figures = Path.Combine(TempDir, "figures");

            var files = new[]
            {
                Path.Combine(figures, "plot.asy"),
                Path.Combine(TempDir, "main-1.asy"),
            };

            var groups = CompilationService.GroupAsymptoteFilesByDirectory(files, TempDir);

            Assert.Equal(2, groups.Count);

            var figuresGroup = groups.Single(g => g.Directory == Path.GetFullPath(figures));
            Assert.Equal(new[] { "plot.asy" }, figuresGroup.FileNames);

            var imagesGroup = groups.Single(g => g.Directory == Path.GetFullPath(TempDir));
            Assert.Equal(new[] { "main-1.asy" }, imagesGroup.FileNames);
        }

        [Fact]
        public void GroupDirectory_IsAlwaysTheDirectoryOfItsFiles()
        {
            var files = new[]
            {
                Path.Combine(TempDir, "root.asy"),
                Path.Combine(TempDir, "a", "one.asy"),
                Path.Combine(TempDir, "a", "b", "two.asy")
            };

            var groups = CompilationService.GroupAsymptoteFilesByDirectory(files, TempDir);

            foreach (var (directory, fileNames) in groups)
            {
                foreach (var name in fileNames)
                {
                    var reconstructed = Path.Combine(directory, name);
                    Assert.Contains(reconstructed, files.Select(Path.GetFullPath));
                }
            }
        }
    }
}
