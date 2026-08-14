using Moq;
using Microsoft.AspNetCore.Http;
using TexCompiler.Models;
using TexCompiler.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace TexCompiler.UnitTests.Services
{
    public class CompilationManagerServiceTests
    {
        private readonly Mock<ITaskStorageService> _taskStorageMock;
        private readonly CompilationQueue _queue;
        private readonly Mock<IWebHostEnvironment> _environmentMock;
        private readonly Mock<ILogger<CompilationManagerService>> _loggerMock;
        private readonly CompilationManagerService _service;
        private readonly string _tempTestDir;

        public CompilationManagerServiceTests()
        {
            _taskStorageMock = new Mock<ITaskStorageService>();
            _queue = new CompilationQueue();
            _environmentMock = new Mock<IWebHostEnvironment>();
            _loggerMock = new Mock<ILogger<CompilationManagerService>>();

            // Создаем уникальную временную директорию для тестов
            _tempTestDir = Path.Combine(Path.GetTempPath(), $"texcompiler-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempTestDir);

            // Настраиваем environment использовать тестовую директорию
            _environmentMock.Setup(e => e.ContentRootPath).Returns(_tempTestDir);

            _service = new CompilationManagerService(
                _taskStorageMock.Object,
                _queue,


                _environmentMock.Object,
                _loggerMock.Object);
        }

        [Fact]
        public async Task SubmitTask_ValidFile_ReturnsTaskId()
        {
            // Arrange
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns("test.tex");
            fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

            CompilationTask? capturedTask = null;
            _taskStorageMock.Setup(t => t.AddTask(It.IsAny<CompilationTask>()))
                          .Callback<CompilationTask>(task => capturedTask = task);

            // Act
            var result = await _service.SubmitTaskAsync(fileMock.Object);

            // Assert
            Assert.NotEqual(Guid.Empty, result);
            Assert.NotNull(capturedTask);
            Assert.Equal(capturedTask.TaskId, result);

            // Проверяем что файл создался в тестовой директории
            var storageDir = Path.Combine(_tempTestDir, "storage");
            Assert.True(Directory.Exists(storageDir));

            _taskStorageMock.Verify(t => t.AddTask(It.IsAny<CompilationTask>()), Times.Once);
        }

        [Fact]
        public async Task SubmitTask_InvalidFile_ThrowsException()
        {
            // Arrange
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns("test.tex");
            fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new IOException("Disk full"));

            // Act & Assert
            await Assert.ThrowsAsync<IOException>(() => _service.SubmitTaskAsync(fileMock.Object));

            // Проверяем что задача не добавлена
            _taskStorageMock.Verify(t => t.AddTask(It.IsAny<CompilationTask>()), Times.Never);

            // Проверяем что директория storage создана (даже при ошибке)
            var storageDir = Path.Combine(_tempTestDir, "storage");
            Assert.True(Directory.Exists(storageDir));
        }

        [Fact]
        public async Task SubmitTask_PutsTaskInQueue_ForTheWorkerPickUp()
        {
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns("diploma.tex");
            fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var taskId = await _service.SubmitTaskAsync(fileMock.Object);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var queued = new List<CompilationTask>();

            await foreach (var task in _queue.ReadAllAsync(cts.Token))
            {
                queued.Add(task);
                break;
            }

            Assert.Equal(taskId, Assert.Single(queued).TaskId);
        }

        [Fact]
        public async Task SubmitTask_TwoFilesWhisSameName_ProduceDifferentPaths()
        {
            var capturedTasks = new List<CompilationTask>();
            _taskStorageMock.Setup(t => t.AddTask(It.IsAny<CompilationTask>()))
                .Callback<CompilationTask>(task => capturedTasks.Add(task));
            await _service.SubmitTaskAsync(CreateFile("diploma.tex"));
            await _service.SubmitTaskAsync(CreateFile("diploma.tex"));

            Assert.Equal(2, capturedTasks.Count);
            Assert.NotEqual(capturedTasks[0].SourceFile, capturedTasks[1].SourceFile);
            Assert.True(File.Exists(capturedTasks[0].SourceFile));
            Assert.True(File.Exists(capturedTasks[1].SourceFile));

            Assert.Equal("diploma.tex", Path.GetFileName(capturedTasks[0].SourceFile));
            Assert.Equal("diploma.tex", Path.GetFileName(capturedTasks[1].SourceFile));
        }

        [Theory]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData(".tex")]
        public async Task SubmitTask_DegenerateFileName_StillWritesFileInsideStorage(string fileName)
        {
            var capturedTasks = new List<CompilationTask>();
            _taskStorageMock.Setup(t => t.AddTask(It.IsAny<CompilationTask>()))
                .Callback<CompilationTask>(task => capturedTasks.Add(task));
            await _service.SubmitTaskAsync(CreateFile(fileName));

            var storageDir = Path.Combine(_tempTestDir, "storage");
            var sourceFile = Assert.Single(capturedTasks).SourceFile;

            Assert.True(File.Exists(sourceFile));
            Assert.StartsWith(storageDir + Path.DirectorySeparatorChar, Path.GetFullPath(sourceFile));
        }

        [Fact]
        public void GetTaskStatus_ExistingTask_ReturnsTask()
        {
            // Arrange
            // Используем путь внутри тестовой директории
            var testFilePath = Path.Combine(_tempTestDir, "storage", "test.tex");
            var expectedTask = new CompilationTask(testFilePath);

            _taskStorageMock.Setup(t => t.GetTask(expectedTask.TaskId))
                           .Returns(expectedTask);

            // Act
            var result = _service.GetTaskStatus(expectedTask.TaskId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(expectedTask.TaskId, result.TaskId);
            _taskStorageMock.Verify(t => t.GetTask(expectedTask.TaskId), Times.Once);

            // Проверяем что тестовая директория существует
            Assert.True(Directory.Exists(_tempTestDir));
        }

        [Fact]
        public void GetTaskStatus_NonExistentTask_ReturnsNull()
        {
            // Arrange
            var nonExistentTaskId = Guid.NewGuid();
            _taskStorageMock.Setup(t => t.GetTask(nonExistentTaskId))
                           .Returns((CompilationTask?)null);

            // Act
            var result = _service.GetTaskStatus(nonExistentTaskId);

            // Assert
            Assert.Null(result);
            _taskStorageMock.Verify(t => t.GetTask(nonExistentTaskId), Times.Once);

            // Проверяем что тестовая директория не была затронута этим тестом
            Assert.True(Directory.Exists(_tempTestDir));
        }

        private static IFormFile CreateFile(string fileName)
        {
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns(fileName);
            fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            return fileMock.Object;
        }

        internal void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempTestDir))
                {
                    Directory.Delete(_tempTestDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not delete test directory: {ex.Message}");
            }
        }
    }
}