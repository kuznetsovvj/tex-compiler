using System.Threading.Tasks;
using TexCompiler.Models;

namespace TexCompiler.Services
{
	/// <summary>
	/// Хранилище заявок в оперативной памяти
	/// </summary>
	public interface ITaskStorageService
	{
		void AddTask(CompilationTask task);

		CompilationTask GetTask(Guid taskId);

		CompilationTask GetNextTask();

		void UpdateTask(CompilationTask task);

		List<CompilationTask> GetAllTasks();

        bool TryRemoveTask(Guid taskId);

    }
}
