namespace TexCompiler.Models
{
	/// <summary>
	/// Задача на компиляцию
	/// </summary>
	public class CompilationTask
	{
		private string _sourceFile;

		public CompilationTask(string sourceFile)
		{
			_sourceFile = sourceFile;

            TaskId = Guid.NewGuid();
            CreatedAt = DateTime.Now;
			TaskStatus = CompilationTaskStatus.Queued;
		}

		public Guid TaskId { get; }

		public string SourceFile => _sourceFile;

		public DateTime CreatedAt { get; }

		public DateTime? StartedAt { get; set; }

		public DateTime? CompletedAt { get; set; }

		public string ErrorMessage { get; set; }

		public string PdfFilePath { get; set; }

		/// <summary>
		/// Путь к файлу с логами output компиляции
		/// </summary>
		public string LogFilePath { get; set; }

		public CompilationTaskStatus TaskStatus { get; set; }

		public TimeSpan? Duration
		{
			get
			{
				if (StartedAt.HasValue && CompletedAt.HasValue)
				{
					return CompletedAt.Value - StartedAt.Value;
				}

				if (StartedAt.HasValue)
				{
					return DateTime.UtcNow - StartedAt.Value;
				}

				return null;
			}
		}

		public CompilationTask SetProcessing()
		{
			TaskStatus = CompilationTaskStatus.Processing;
			StartedAt = DateTime.UtcNow;
			return this;
		}

        public CompilationTask SetCompleted(CompilationResult result)
        {
            TaskStatus = result.IsSuccess ?
                CompilationTaskStatus.Completed :
                CompilationTaskStatus.Failed;
            CompletedAt = DateTime.UtcNow;
            PdfFilePath = result.FilePath;
            LogFilePath = result.LogFilePath;
            ErrorMessage = result.ErrorMessage;
            return this;
        }

        public CompilationTask SetFailed(Exception ex)
        {
            TaskStatus = CompilationTaskStatus.Failed;
            CompletedAt = DateTime.UtcNow;
            ErrorMessage = $"Internal error: {ex.Message}";
            return this;
        }
    }
}
