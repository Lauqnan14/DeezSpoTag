using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalStereoQualitySeparationTests
{
    [Theory]
    [InlineData("LOW", "LOW")]
    [InlineData("HIGH", "HIGH")]
    [InlineData("LOSSLESS", "LOSSLESS")]
    [InlineData("HI_RES", "HI_RES")]
    [InlineData("HI_RES_LOSSLESS", "HI_RES_LOSSLESS")]
    [InlineData("MAX_HI_RES", "HI_RES_LOSSLESS")]
    [InlineData("ATMOS", "DOLBY_ATMOS")]
    [InlineData("DOLBY_ATMOS", "DOLBY_ATMOS")]
    public void TidalRequestBuilder_PreservesDistinctFallbackTier(string inputQuality, string expectedQueueQuality)
    {
        var item = new TidalQueueItem { Quality = inputQuality };
        var settings = new DeezSpoTagSettings { TidalQuality = "LOSSLESS" };

        var request = TidalRequestBuilder.BuildRequest(item, settings);

        Assert.Equal(expectedQueueQuality, request.Quality);
    }

    [Fact]
    public void TidalRequestBuilder_UsesConfiguredTidalQualityWhenPayloadHasNoQuality()
    {
        var item = new TidalQueueItem();
        var settings = new DeezSpoTagSettings { TidalQuality = "HI_RES" };

        var request = TidalRequestBuilder.BuildRequest(item, settings);

        Assert.Equal("HI_RES", request.Quality);
    }
}
