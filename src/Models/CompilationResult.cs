namespace TexCompiler.Models
{
    public class CompilationResult
    {
        public bool IsSuccess { get; set; }

        public string Output { get; set; }

        public string ErrorMessage { get; set; }

        /// <summary>
        /// Путь к файлу PDF
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Путь к файлу TXT для логирования output компиляции
        /// </summary>
        public string LogFilePath { get; set; }

        public TimeSpan? Duration { get; set; }
    }
}
