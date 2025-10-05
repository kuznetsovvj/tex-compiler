namespace TexCompiler.Models
{
	public class ApiResponse<T>
	{
		public bool Success { get; set; }

		public T Data { get; set; }

		public string? Error { get; set; }
	}

	public class TaskStatusResponse
	{
		public Guid TaskId { get; set; }

		public CompilationTaskStatus Status { get; set; }

		public DateTime CreatedAt { get; set; }

		public DateTime? StartedAt { get; set; }

		public DateTime? CompletedAt { get; set; }

		/// <summary>
		/// Длительность обработки запроса в миллисекундах
		/// </summary>
		public long? Duration { get; set; }

		public string? ErrorMessage { get; set; }

		public string? DownloadUrl { get; set; }
	}

	public class UploadResponse
	{
		public Guid TaskId { get; set; }

		public string? Message { get; set; }
	}

}
