using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using TexCompiler.Models;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class UploadValidationTests
    {
        private readonly AllowedFileExtensionsAttribute _attribute = new(".tex", ".zip");

        [Theory]
        [InlineData("diplom.tex")]
        [InlineData("diplom.zip")]
        [InlineData("DIPLOM.TEX")]
        [InlineData("архив.Zip")]
        public void AllowedExtensions_KnownExtension_IsAccepted(string fileName)
        {
            Assert.True(_attribute.IsValid(FileNamed(fileName)));
        }

        [Theory]
        [InlineData("payload.sh")]
        [InlineData("diplom.pdf")]
        [InlineData("noextension")]
        [InlineData("diplom.tex.exe")]
        public void AllowedExtensions_UnknownExtension_IsRejected(string fileName)
        {
            // Проверка жила только в браузере, то есть обходилась прямым HTTP-запросом.
            Assert.False(_attribute.IsValid(FileNamed(fileName)));
        }

        [Fact]
        public void AllowedExtensions_NoFile_IsNotThisChecksProblem()
        {
            // Пустую загрузку разбирает отдельная проверка в контроллере, и её сообщение
            // точнее — эта не должна перехватывать случай на себя.
            Assert.True(_attribute.IsValid(null));
        }

        [Fact]
        public void AllowedExtensions_ErrorMessage_ListsAllowedTypes()
        {
            // Текст уходит пользователю как есть, через сборку сообщений ModelState.
            Assert.Equal("Разрешены только файлы .tex и .zip", _attribute.FormatErrorMessage("TexFile"));
        }

        [Fact]
        public void MaxFileSize_NotConfigured_FallsBackToTwentyMegabytes()
        {
            var configuration = ConfigurationWith(null);

            Assert.Equal(20, UploadLimits.GetMaxFileSizeMegabytes(configuration));
            Assert.Equal(20L * 1024 * 1024, UploadLimits.GetMaxFileSizeBytes(configuration));
        }

        [Fact]
        public void MaxFileSize_Configured_IsUsedByEveryLimit()
        {
            // Суть правки: значение задаётся в одном месте, и барьер Kestrel едет за ним.
            var configuration = ConfigurationWith("5");

            Assert.Equal(5, UploadLimits.GetMaxFileSizeMegabytes(configuration));
            Assert.Equal(5L * 1024 * 1024, UploadLimits.GetMaxFileSizeBytes(configuration));
            Assert.Equal(
                5L * 1024 * 1024 + UploadLimits.MultipartOverheadBytes,
                UploadLimits.GetMaxRequestBodySizeBytes(configuration));
        }

        [Fact]
        public void MaxRequestBodySize_IsAboveFileLimit()
        {
            // Без запаса на служебные части multipart файл ровно в лимит обрывался бы на
            // последних байтах, и вместо внятного сообщения пользователь получал бы 413.
            var configuration = ConfigurationWith(null);

            Assert.True(
                UploadLimits.GetMaxRequestBodySizeBytes(configuration) > UploadLimits.GetMaxFileSizeBytes(configuration),
                "барьер Kestrel должен быть выше точной проверки, иначе она недостижима");
        }

        private static IConfiguration ConfigurationWith(string? megabytes) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [UploadLimits.MaxFileSizeKey] = megabytes
                })
                .Build();

        private static IFormFile FileNamed(string fileName)
        {
            var file = new Mock<IFormFile>();
            file.Setup(f => f.FileName).Returns(fileName);

            return file.Object;
        }
    }
}
