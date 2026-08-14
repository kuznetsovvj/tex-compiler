using TexCompiler.Models;

namespace TexCompiler.UnitTests.Models
{
    public class CompilationTaskTests
    {
        [Fact]
        public void CreatedAt_IsUtc() 
        {
            // Регрессия: CreatedAt ставился через DateTime.Now
            var task = new CompilationTask("/tmp/storage/doc.tex");

            Assert.Equal(DateTimeKind.Utc, task.CreatedAt.Kind);
        }

        [Fact]
        public void SetProcessing_And_SetCompleted_UseUtc()
        {
            var task = new CompilationTask("/tmp/storage/doc.tex");

            task.SetProcessing();
            task.SetCompleted(new CompilationResult { IsSuccess = true });

            Assert.Equal(DateTimeKind.Utc, task.StartedAt.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, task.CompletedAt.Value.Kind);
        }

        [Fact]
        public void CreatedAt_ComparesWithUtcTreshold_LikeCleanupDoes()
        {
            var task = new CompilationTask("/tmp/storage/doc.tex");
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);

            Assert.True(task.CreatedAt > cutoff);
            Assert.True(task.CreatedAt <= DateTime.UtcNow);
        }

        [Fact]
        public void SetCompleted_FailedResultWithoutLogPath_KeepsLogPathSetByCompilationService()
        {
            // Регрессия: CompilationService сохраняет лог и проставляет путь напрямую в задачу,
            // а CompilationResult о нем не знает. Безусловное присваивание перетирало путь 
            // значением null, и скачивание лога не работало не при каких условиях.
            var task = new CompilationTask("/tmp/storage/doc.tex");
            task.LogFilePath = "/tmp/logs/abc.log";

            task.SetCompleted(new CompilationResult
            {
                IsSuccess = false,
                ErrorMessage = "LaTeX compilation failed"
            });

            Assert.Equal(CompilationTaskStatus.Failed, task.TaskStatus);
            Assert.Equal("/tmp/logs/abc.log", task.LogFilePath);
        }

        [Fact]
        public void SetCompleted_ResultWithLogPath_OverwriteLogPath()
        {
            var task = new CompilationTask("/tmp/storage/doc.tex");
            task.LogFilePath = "/tmp/logs/old.log";

            task.SetCompleted(new CompilationResult
            {
                IsSuccess = true,
                FilePath = "/tmp/pdfs/doc.pdf",
                LogFilePath = "/tmp/logs/new.log"
            });

            Assert.Equal(CompilationTaskStatus.Completed, task.TaskStatus);
            Assert.Equal("/tmp/logs/new.log", task.LogFilePath);
        }

        [Fact]
        public void SetCompleted_SuccessfulResult_SetsStatusAndPdfPath()
        {
            var task = new CompilationTask("/tmp/storage/doc.tex");

            task.SetCompleted(new CompilationResult
            {
                IsSuccess = true,
                FilePath = "/tmp/pdfs/doc.pdf",
            });

            Assert.Equal(CompilationTaskStatus.Completed, task.TaskStatus);
            Assert.Equal("/tmp/pdfs/doc.pdf", task.PdfFilePath);
            Assert.NotNull(task.CompletedAt);
        }
    }
}