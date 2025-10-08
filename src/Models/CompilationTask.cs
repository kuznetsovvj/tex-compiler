namespace TexCompiler.Models
{
	/// <summary>
	/// Задача на компиляцию
	/// </summary>
	public class CompilationTask
	{
		public CompilationTask(string fileName, string fullFilePath)
		{
			TaskId = Guid.NewGuid();
			FileName = fileName;
			SourceFileFullPath = fullFilePath;
			CreatedAt = DateTime.Now;
			TaskStatus = CompilationTaskStatus.Queued;
		}

		public Guid TaskId { get; }

		public string FileName { get; set; }

		public DateTime CreatedAt { get; }

		public DateTime? StartedAt { get; set; }

		public DateTime? CompletedAt { get; set; }

		public string ErrorMessage { get; set; }

		public string SourceFileFullPath { get; set; }
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
	}
}
