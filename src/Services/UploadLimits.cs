namespace TexCompiler.Services
{
    /// <summary>
    /// Единственный источник истины про размер загружаемого файла.
    /// 
    /// Лимитов раньше было три и они друг о друге не знали: константа 20 МБ в контроллере,
    /// умолчание Kestrel на размер тела запроса (~30 МБ) и MultipartBodyLengthLimit (128 МБ).
    /// Пользователь на 25 МБ получал внятный отказ, но только после того, как все 25 МБ уже
    /// приняты и записаны во временный файл, а пользователь на 100 МБ - сырой HTTP 413.
    /// 
    /// Значение читается из конфигурации по образу CleanupSettings в CleanupService.
    /// 
    /// </summary>
    public static class UploadLimits
    {
        public const string MaxFileSizeKey = "UploadSettings:MaxFileSizeMegabytes";

        public const int DefaultMaxFileSizeMegabytes = 20;

        /// <summary>
        /// Запас поверх размера файла на служебные части multipart-запроса: границы,
        /// заголовки частей, имя файла. Ограничение Kestrel считает тело целиком, поэтому
        /// без запаса файл ровно в лимит обрывался бы на последний байтах - и вместо внятного
        /// сообщения из контроллера пользователь получал бы 413.
        /// </summary>
        public const long MultipartOverheadBytes = 1L * 1024 * 1024;

        public static int GetMaxFileSizeMegabytes(IConfiguration configuration) =>
            configuration.GetValue(MaxFileSizeKey, DefaultMaxFileSizeMegabytes);

        public static long GetMaxFileSizeBytes(IConfiguration configuration) =>
            (long)GetMaxFileSizeMegabytes(configuration) * 1024 * 1024;

        public static long GetMaxRequestBodySizeBytes(IConfiguration configuration) =>
            GetMaxFileSizeBytes(configuration) + MultipartOverheadBytes;
    }
}
