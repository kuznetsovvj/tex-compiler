using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    /// <summary>
    /// Регрессия на критическую уязвимость: pdflatex запускался с -shell-escape,
    /// и пользовательский документ через \write18 выполнял произвольные команды
    /// внутри контейнера.
    /// </summary>
    public class LatexArgumentsTests
    {
        [Fact]
        public void LatexArguments_ShellEscapeIsExplicitlyDisabled()
        {
            var args = CompilationService.BuildLatexArguments("main.tex");

            Assert.Contains("-no-shell-escape", args);

            // "-no-shell-escape" сам содержит подстроку "-shell-escape", поэтому
            // проверяем, что выключенный флаг - единственное вхождение shell-escape
            var withoutDisabledFlag = args.Replace("-no-shell-escape", string.Empty);
            Assert.DoesNotContain("shell-escape", withoutDisabledFlag);
        }

        [Fact]
        public void LatexArguments_ContainMainFileName()
        {
            var args = CompilationService.BuildLatexArguments("main.tex");

            Assert.Contains("main.tex", args);
        }
    }
}
