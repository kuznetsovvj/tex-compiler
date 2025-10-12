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
        private readonly Mock<ICompilationService> _compilationServiceMock;
        private readonly Mock<IWebHostEnvironment> _environmentMock;
        private readonly Mock<ILogger<CompilationManagerService>> _loggerMock;
        private readonly CompilationManagerService _service;

        public CompilationManagerServiceTests()
        {
            _taskStorageMock = new Mock<ITaskStorageService>();
            _compilationServiceMock = new Mock<ICompilationService>();
            _environmentMock = new Mock<IWebHostEnvironment>();
            _loggerMock = new Mock<ILogger<CompilationManagerService>>();

            _environmentMock.Setup(e => e.ContentRootPath).Returns("/test/path");

            _service = new CompilationManagerService(
                _taskStorageMock.Object,
                _compilationServiceMock.Object,
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
            Assert.Equal(capturedTask?.TaskId, result);
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
            _taskStorageMock.Verify(t => t.AddTask(It.IsAny<CompilationTask>()), Times.Never);
        }

        [Fact]
        public void GetTaskStatus_ExistingTask_ReturnsTask()
        {
            // Arrange
            var expectedTask = new CompilationTask("path/to/file/test.tex");
            _taskStorageMock.Setup(t => t.GetTask(expectedTask.TaskId))
                           .Returns(expectedTask);

            // Act
            var result = _service.GetTaskStatus(expectedTask.TaskId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(expectedTask.TaskId, result.TaskId);
            Assert.Equal(expectedTask.SourceFile, result.SourceFile);
            _taskStorageMock.Verify(t => t.GetTask(expectedTask.TaskId), Times.Once);
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
        }      
    }
}