using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    /// <summary>
    /// Регрессия на критическую уязвимость: pdflatex запускался с -shell-escape,
    /// и пользовательский документ через \write18 выполнял произвольные команды.
    /// Аргументы списком: каждый уходит в ProcessStartInfo.ArgumentList отдельно,
    /// ручного quoting строкой нет.
    /// </summary>
    public class LatexArgumentsTests
    {
        [Fact]
        public void LatexArguments_ShellEscapeIsExplicitlyDisabled()
        {
            var args = CompilationService.BuildLatexArguments("main.tex");

            Assert.Contains("-no-shell-escape", args);
            // Сравнение по элементам, а не подстрокам: "-no-shell-escape" сам
            // содержит "-shell-escape"
            Assert.DoesNotContain("-shell-escape", args);
        }

        [Fact]
        public void LatexArguments_ContainMainFileName()
        {
            var args = CompilationService.BuildLatexArguments("main.tex");

            Assert.Equal("main.tex", args.Last());
        }
    }
}
