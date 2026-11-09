using Microsoft.Extensions.Configuration;
using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    public class ExtractionLimitsTests
    {
        [Fact]
        public void FromConfiguration_EmptyConfig_UsesDefaults()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            var limits = ExtractionLimits.FromConfiguration(config);

            Assert.Equal(ExtractionLimits.Defaults.MaxTotalBytes, limits.MaxTotalBytes);
            Assert.Equal(ExtractionLimits.Defaults.MaxFileBytes, limits.MaxFileBytes);
            Assert.Equal(ExtractionLimits.Defaults.MaxEntries, limits.MaxEntries);
            Assert.Equal(ExtractionLimits.Defaults.MaxCompressionRatio, limits.MaxCompressionRatio);
            Assert.Equal(ExtractionLimits.Defaults.MaxNestingDepth, limits.MaxNestingDepth);
        }

        [Fact]
        public void FromConfiguration_OverridesApplied()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ExtractionLimits:MaxFileBytes"] = "2048",
                    ["ExtractionLimits:MaxEntries"] = "5",
                })
                .Build();

            var limits = ExtractionLimits.FromConfiguration(config);

            Assert.Equal(2048, limits.MaxFileBytes);
            Assert.Equal(5, limits.MaxEntries);
            // не заданные значения остаются дефолтными
            Assert.Equal(ExtractionLimits.Defaults.MaxTotalBytes, limits.MaxTotalBytes);
            Assert.Equal(ExtractionLimits.Defaults.MaxCompressionRatio, limits.MaxCompressionRatio);
        }
    }
}
