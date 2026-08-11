namespace TexCompiler.Services
{
    /// <summary>
    /// Определяет, архив ли перед нами, по содержимому файла.
    /// </summary>
    public static class ArchiveDetector
    {
        private static readonly byte[][] ZipSignatures =
        {
            new byte[] { 0x50, 0x4B, 0x03, 0x04 },
            new byte[] { 0x50, 0x4B, 0x05, 0x06 },
            new byte[] { 0x50, 0x4B, 0x07, 0x08 }
        };

        private const int SignatureLength = 4;

        public static bool IsZipArchive(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);

                return IsZipArchive(stream);
            }
            catch (IOException)
            {
                // Нечитаемый файл архивом на считаем: дальше по коду он всё равно
                // не откроется, но сообщение будет про компиляцию, а не про распаковку.
                return false;
            }
        }

        public static bool IsZipArchive(Stream stream)
        {
            var header = new byte[SignatureLength];
            var read = 0;

            while (read < SignatureLength)
            {
                var chunk = stream.Read(header, read, SignatureLength - read);
                if (chunk == 0)
                {
                    // Файл короче сигнатуры - архивом быть не может
                    return false;
                }

                read += chunk;
            }

            return ZipSignatures.Any(signature => header.SequenceEqual(signature));
        }

    }
}
