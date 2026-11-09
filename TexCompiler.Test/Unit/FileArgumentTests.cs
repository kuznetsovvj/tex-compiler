using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    /// <summary>
    /// Регрессия на инъекцию аргументов через имя файла. Имена попадают в argv
    /// pdflatex/asy без shell-разбора (ProcessStartInfo.ArgumentList), но сами
    /// инструменты разбирают leading '-' как опцию: файл "-shell-escape" вернул бы
    /// shell escape после нашего запрета, "-output-directory=/x" - вывод.
    /// </summary>
    public class FileArgumentTests
    {
        [Theory]
        [InlineData("-shell-escape")]
        [InlineData("-output-directory=/tmp")]
        [InlineData("--interaction=batchmode")]
        [InlineData("-")]
        public void FileArgument_StartingWithDash_GetsPathPrefix(string fileName)
        {
            var sanitized = CompilationService.SanitizeFileArgument(fileName);

            Assert.False(sanitized.StartsWith("-"), $"аргумент '{sanitized}' всё ещё похож на опцию");
            Assert.Equal("./" + fileName, sanitized);
        }

        [Theory]
        [InlineData("main.tex")]
        [InlineData("my document v2.tex")]
        [InlineData("say \"hi\".tex")]
        [InlineData("semi;colon&amp|pipe.tex")]
        public void FileArgument_RegularNames_Unchanged(string fileName)
        {
            // Пробелы, кавычки и shell-метасимволы не трогаем: ArgumentList передает
            // аргумент как есть, без shell-разбора
            Assert.Equal(fileName, CompilationService.SanitizeFileArgument(fileName));
        }

        [Fact]
        public void LatexArguments_FileNameAlwaysLastAndSanitized()
        {
            var args = CompilationService.BuildLatexArguments("-shell-escape");

            Assert.Equal(
                new[] { "-interaction=nonstopmode", "-no-shell-escape", "./-shell-escape" },
                args);
        }

        [Fact]
        public void AsymptoteArguments_ContainOnlySanitizedFileNames()
        {
            // Регрессия: слово "asy" само по себе в аргументах не допускается -
            // команду подставляет транспорт, иначе argv = 'asy asy <файлы>' и asy
            // пытается обработать файл по имени "asy"
            var args = CompilationService.BuildAsymptoteArguments(new[] { "pic.asy", "-render=4.asy" });

            Assert.Equal(new[] { "pic.asy", "./-render=4.asy" }, args);
        }

        [Fact]
        public void FindMainTexFile_OptionLikeName_ArgumentsStaySafe()
        {
            // Сквозной случай: архив, где единственный .tex назван как опция.
            // Имя проходит через FindMainTexFile и должно выйти санитизированным.
            var dir = Path.Combine(Path.GetTempPath(), $"texcompiler-argtest-{Guid.NewGuid()}");
            Directory.CreateDirectory(dir);

            try
            {
                File.WriteAllText(
                    Path.Combine(dir, "-shell-escape.tex"),
                    "\\documentclass{article}\\begin{document}x\\end{document}");

                var mainFile = CompilationService.FindMainTexFile(dir);
                var args = CompilationService.BuildLatexArguments(Path.GetFileName(mainFile));

                Assert.False(args.Last().StartsWith("-"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
