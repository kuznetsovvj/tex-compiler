namespace TexCompiler.Models
{
    /// <summary>
    /// Результат запуска команды pdflatex или asy
    /// </summary>
    public class ProcessResult
    {
        public bool Success { get; set;  }
        
        public string Output { get; set; }

        public string Error { get; set; }

        /// <summary>
        /// Процесс не уложился в установленное время и был принудительно завершен.
        /// Отличает превышение лимита от обычной неудачной компиляции
        /// </summary>
        public bool TimedOut { get; set; }
    }
}
