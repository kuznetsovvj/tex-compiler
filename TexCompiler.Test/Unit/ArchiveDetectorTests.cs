using System.IO.Compression;
using System.Text;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class ArchiveDetectorTests
    {
        [Fact]
        public void RealArchive_IsRecognized_RegardlessOfExtension()
        {
            // Архив, переименованный в .tex, уходил в pdflatex как исходник.
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("main.tex");
            }

            archive.Position = 0;

            Assert.True(ArchiveDetector.IsZipArchive(archive));
        }

        [Fact]
        public void EmptyArchive_IsRecognized()
        {
            // Пустой архив начинается с сигнатуры central directory, а не записи.
            // Такой файл — всё-таки архив, и сообщение должно быть про отсутствие .tex.
            using var archive = new MemoryStream();
            using (var _ = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
            }

            archive.Position = 0;

            Assert.True(ArchiveDetector.IsZipArchive(archive));
        }

        [Fact]
        public void TexSource_IsNotAnArchive()
        {
            using var source = new MemoryStream(Encoding.UTF8.GetBytes("\\documentclass{article}"));

            Assert.False(ArchiveDetector.IsZipArchive(source));
        }

        [Fact]
        public void FileShorterThanSignature_IsNotAnArchive()
        {
            using var tiny = new MemoryStream(new byte[] { 0x50, 0x4B });

            Assert.False(ArchiveDetector.IsZipArchive(tiny));
        }

        [Fact]
        public void EmptyFile_IsNotAnArchive()
        {
            using var empty = new MemoryStream();

            Assert.False(ArchiveDetector.IsZipArchive(empty));
        }

        [Fact]
        public void MissingFile_IsNotAnArchive()
        {
            var path = Path.Combine(Path.GetTempPath(), $"texcompiler-missing-{Guid.NewGuid()}.zip");

            Assert.False(ArchiveDetector.IsZipArchive(path));
        }
    }
}
