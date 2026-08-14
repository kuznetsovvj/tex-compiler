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

        /// <summary>
        /// Код возврата процесса
        /// null если процесс не удалось запустить
        /// Для pdflatex ненулевой код не означает непригодный pdf
        /// </summary>
        public int? ExitCode { get; set; }
    }
}
