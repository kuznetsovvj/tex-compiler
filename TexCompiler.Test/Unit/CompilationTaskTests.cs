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
    }
}