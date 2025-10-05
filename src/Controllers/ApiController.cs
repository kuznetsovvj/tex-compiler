using Microsoft.AspNetCore.Mvc;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.Controllers
{
	[ApiController]
	public class ApiController : ControllerBase
	{
		private readonly CompilationManagerService _compilationManagerService;
		private readonly ITaskStorageService _taskStorageService;
		private readonly IWebHostEnvironment _environment;
		private readonly ILogger<ApiController> _logger;

		private const int _fileMaxSizeMb = 20;

		public ApiController (
			CompilationManagerService compilationManagerService,
			IWebHostEnvironment environment,
			ITaskStorageService taskStorageService,
			ILogger<ApiController> logger)
		{
			_compilationManagerService = compilationManagerService;
			_environment = environment;
			_taskStorageService = taskStorageService;
			_logger = logger;
		}

		[HttpPost]
		[Route("api/upload")]
		public async Task<ActionResult<ApiResponse<UploadResponse>>> UploadFile([FromForm] UploadRequest request)
		{

            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList();

                _logger.LogWarning("Model validation failed: {Errors}", string.Join(", ", errors));

                return BadRequest(new ApiResponse<UploadResponse>
                {
                    Success = false,
                    Error = $"Ошибка валидации: {string.Join(", ", errors)}"
                });
            }

            try
			{
				if (request.TexFile == null || request.TexFile.Length == 0)
				{
					return BadRequest(new ApiResponse<UploadResponse>
					{
						Success = false,
						Error = "Файл не был загружен"
					});
				}

                if (request.TexFile.Length > _fileMaxSizeMb * 1024 * 1024)
				{
					return BadRequest(new ApiResponse<UploadResponse>
					{
						Success = false,
						Error = $"Размер файла не должен превышать {_fileMaxSizeMb} Мб"
					});
				}

				var taskId = await _compilationManagerService.SubmitTaskAsync(request.TexFile);

				_logger.LogInformation("File upload successfully. TaskId: {TaskId}, FileName: {FileName}", taskId, request.TexFile.FileName);

				return Ok(new ApiResponse<UploadResponse>
				{
					Success = true,
					Data = new UploadResponse
					{
						TaskId = taskId,
						Message = "Файл успешно загружен и поставлен в очередь на компиляцию"
					}
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error uploading file {FileName}", request.TexFile?.FileName);

				return StatusCode(500, new ApiResponse<UploadResponse>
				{
					Success = false,
					Error = "Произошла внутренняя ошибка при загрузке файла"
				});
			}
		}

		/// <summary>
		/// Получение статуса задачи компиляции
		/// </summary>
		[HttpGet("api/status/{taskId}")]
		public ActionResult<ApiResponse<TaskStatusResponse>> GetStatus (Guid taskId)
		{
			try
			{
				var task = _taskStorageService.GetTask(taskId);

				if (task == null)
				{
					return NotFound(new ApiResponse<TaskStatusResponse>
					{
						Success = false,
						Error = "Задача не найдена"
					});
				}

				var response = new TaskStatusResponse
				{
					TaskId = task.TaskId,
					Status = task.TaskStatus,
					CreatedAt = task.CreatedAt,
					StartedAt = task.StartedAt,
					CompletedAt = task.CompletedAt,
					Duration = task.Duration.HasValue ? (long)task.Duration.Value.TotalMilliseconds : null,
					ErrorMessage = task.ErrorMessage
				};

				if (task.TaskStatus == CompilationTaskStatus.Completed && !string.IsNullOrEmpty(task.PdfFilePath))
				{
					response.DownloadUrl = Url.Action("Download", "Api", new { taskId = task.TaskId }, Request.Scheme);
				}

				return Ok(new ApiResponse<TaskStatusResponse>
				{
					Success = true,
					Data = response
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error getting status for task: {TaskId}", taskId);
				return StatusCode(500, new ApiResponse<TaskStatusResponse>
				{
					Success = false,
					Error = "Произошла внутренняя ошибка при получении статуса"
				});
			}
		}

		[HttpGet("api/download/{taskId}")]
		public IActionResult Download (Guid taskId)
		{
			try
			{
				var task = _compilationManagerService.GetTaskStatus(taskId);

				if (task == null)
				{
					return NotFound("Задача не найдена");
				}

				if (task.TaskStatus != CompilationTaskStatus.Completed)
				{
					return BadRequest("Компиляция еще не завершена");
				}

				if (string.IsNullOrEmpty(task.PdfFilePath) || !System.IO.File.Exists(task.PdfFilePath))
				{
					_logger.LogError("PDF file not found for task: {TaskId}, Path: {Path}", taskId, task.PdfFilePath);
					return NotFound("PDF файл не найден");
				}

				var fileBytes = System.IO.File.ReadAllBytes(task.PdfFilePath);
                var fileName = Path.GetFileNameWithoutExtension(task.FileName) + ".pdf";

                return File(fileBytes, "application/pdf", fileName);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error downloading PDF for task: {TaskId}", taskId);
				return StatusCode(500, "Произошла внутренняя ошибка при скачивании файла");
			}
		}
	}
}
