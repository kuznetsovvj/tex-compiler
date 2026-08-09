using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.IO.Compression;

namespace TexCompiler.Services
{
    /// <summary>
    /// Распаковка загруженных архивов с двумя защитами: от выхода за пределы целевого
    /// каталога (Zip Slip) и от несоразмерного распакованного объема (zip-бомба).
    /// 
    /// Вынесено из CompilationService отдельным типом намеренно: логику нужно покрывать
    /// тестами.
    /// </summary>
    internal static class SafeZipExtractor
    {
        /// <summary>
        /// Предел суммарного объема распакованных данных
        /// </summary>
        internal const long DefaultMaxTotalBytes = 500L * 1024 * 1024;

        /// <summary>
        /// Предел числа файлов в архиве
        /// </summary>
        internal const int DefaultMaxEntryCount = 10_000;

        internal static void ExtractTo(
            string ZipPath,
            string destinationRoot,
            long maxTotalBytes = DefaultMaxTotalBytes,
            int maxEntryCount = DefaultMaxEntryCount)
        {
            using var archive = ZipFile.OpenRead(ZipPath);

            if (archive.Entries.Count > maxEntryCount)
            {
                throw new InvalidDataException(
                    $"В архиве слишком много файлов: {archive.Entries.Count}, допустимо не более {maxEntryCount}");
            }

            long declaredTotal = 0;
            foreach (var declared in archive.Entries)
            {
                declaredTotal += declared.Length;

                if (declaredTotal > maxTotalBytes)
                {
                    throw new InvalidDataException(BuildSizeLimitMessage(maxTotalBytes));
                }
            }

            long writtenTotal = 0;

            foreach (var entry in archive.Entries)
            {
                if (!TryResolveEntryPath(destinationRoot, entry.FullName, out var fullPath))
                {
                    throw new InvalidDataException($"архив содержит недопустимый путь: {entry.FullName}");
                }

                if (IsDirectoryEntry(entry))
                {
                    Directory.CreateDirectory(fullPath);
                    continue;
                }

                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var source = entry.Open();
                using var destination = new FileStream(fullPath, FileMode.Create, FileAccess.Write);

                writtenTotal += CopyWithLimit(source, destination, maxTotalBytes - writtenTotal, maxTotalBytes);
            }

        }

        /// <summary>
        /// Приводит имя записи к полному пути и проверяет, что он остался внутри 
        /// целевого каталога. Возвращает false для любой записи, уводящей наружу.
        /// </summary>
        internal static bool TryResolveEntryPath(string destinationRoot, string entryName, out string fullPath)
        {
            fullPath = string.Empty;

            if (string.IsNullOrWhiteSpace(entryName))
            {
                return false;
            }

            // Path.Combine отбрасывает первый аргумент, если второй - абсолютный путь.
            // Поэтому запись с именем вида /etc/passwd уводит за пределы каталога
            // вообще без единого ".." и одной проверкой на ".." тут не обойтись
            var candidate = Path.GetFullPath(Path.Combine(destinationRoot, entryName));

            var root = Path.GetFullPath(destinationRoot);

            // Завершающий разделитель обязателен
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            if (!candidate.StartsWith(root, StringComparison.Ordinal))
            {
                return false;
            }
            
            fullPath = candidate;
            return true;
        }

        private static bool IsDirectoryEntry(ZipArchiveEntry entry)
        {
            return entry.FullName.EndsWith('/') || entry.FullName.EndsWith("\\");
        }

        private static long CopyWithLimit(Stream source, Stream destination, long remaining, long maxTotalBytes)
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;

            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                copied += read;

                if (copied > remaining)
                {
                    // Частично записанный файл останется, но временный каталог
                    // целиком удаляется в CompilationService после компиляции
                    throw new InvalidDataException(BuildSizeLimitMessage(remaining));
                }

                destination.Write(buffer, 0, read);
            }

            return copied;
        }

        private static string BuildSizeLimitMessage(long maxTotalBytes)
        {
            return $"распакованный размер архивы превышает допустимые {maxTotalBytes} / {1024 * 1024} МБ";
        }
    }
}
