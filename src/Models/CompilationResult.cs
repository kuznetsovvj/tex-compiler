namespace TexCompiler.Models
{
    public class CompilationResult
    {
        public bool IsSuccess { get; set; }

        public string Output { get; set; }

        public string ErrorMessage { get; set; }

        public string FilePath { get; set; }

        public TimeSpan? Duration { get; set; }
    }
}
