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
    }
}
