using System.Text.Json.Serialization;

namespace TexCompiler.Models
{
	/// <summary>
	/// Статусы задачи на компиляцию
	/// </summary>
	[JsonConverter(typeof(JsonStringEnumConverter))]	 
	public enum CompilationTaskStatus
	{
		/// <summary>
		/// В очереди
		/// </summary>
		Queued,

		/// <summary>
		/// Компилируется
		/// </summary>
		Processing,

		/// <summary>
		/// Завершена с ошибкой
		/// </summary>
		Failed,

		/// <summary>
		/// Завершена с успехом
		/// </summary>
		Completed
	}
}
