namespace TexCompiler.Services
{
    /// <summary>
    /// Лимиты распаковки загруженных архивов - защита от zip-бомб. Значения по
    /// умолчанию рассчитаны на публичный сервис с загрузкой до 20 МБ: текстовые
    /// проекты с картинками распаковываются заметно больше сжатого объема, и эти
    /// пределы легитимный документ не задевает, а переполнение диска исключают.
    /// </summary>
    internal sealed class ExtractionLimits
    {
        /// <summary>Суммарный распакованный объем архива</summary>
        public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;

        /// <summary>Распакованный размер одной записи</summary>
        public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

        /// <summary>Число записей в архиве</summary>
        public int MaxEntries { get; init; } = DefaultMaxEntries;

        /// <summary>
        /// Максимум отношения "распакованный/сжатый" для одной записи. Проверяется
        /// только для записей от RatioFloorBytes: у крошечных файлов коэффициент
        /// формально огромный, но и вреда от них нет.
        /// </summary>
        public int MaxCompressionRatio { get; init; } = DefaultMaxCompressionRatio;

        /// <summary>Максимум сегментов пути записи (глубина вложенности)</summary>
        public int MaxNestingDepth { get; init; } = DefaultMaxNestingDepth;

        internal const long DefaultMaxTotalBytes = 500L * 1024 * 1024;
        internal const long DefaultMaxFileBytes = 100L * 1024 * 1024;
        internal const int DefaultMaxEntries = 10_000;
        internal const int DefaultMaxCompressionRatio = 100;
        internal const int DefaultMaxNestingDepth = 32;

        /// <summary>Записи мельче этого порога ratio-лимит не проверяют</summary>
        internal const long RatioFloorBytes = 1024 * 1024;

        public static ExtractionLimits Defaults => new ExtractionLimits();

        /// <summary>Читает лимиты из конфигурации; незаданные значения - по умолчанию</summary>
        public static ExtractionLimits FromConfiguration(IConfiguration configuration)
        {
            return new ExtractionLimits
            {
                MaxTotalBytes = configuration.GetValue("ExtractionLimits:MaxTotalBytes", DefaultMaxTotalBytes),
                MaxFileBytes = configuration.GetValue("ExtractionLimits:MaxFileBytes", DefaultMaxFileBytes),
                MaxEntries = configuration.GetValue("ExtractionLimits:MaxEntries", DefaultMaxEntries),
                MaxCompressionRatio = configuration.GetValue("ExtractionLimits:MaxCompressionRatio", DefaultMaxCompressionRatio),
                MaxNestingDepth = configuration.GetValue("ExtractionLimits:MaxNestingDepth", DefaultMaxNestingDepth),
            };
        }
    }
}
