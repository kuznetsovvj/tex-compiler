using TexCompiler.Models;

namespace TexCompiler.Services
{
    public interface ICompilationService
    {
        Task<CompilationResult> CompileAsync(CompilationTask task);
    }
}
