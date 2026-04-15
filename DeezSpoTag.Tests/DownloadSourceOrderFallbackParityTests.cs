using System.Collections.Generic;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadSourceOrderFallbackParityTests
{
    private static readonly string[] ExpectedDeezerQualityFallback = { "deezer|3", "deezer|1" };
    private static readonly string[] ExpectedQobuzStrictQuality = { "qobuz|6" };

    [Fact]
    public void ResolveQualityAutoSources_UsesCanonicalQualityOrder_WhenServiceIsAuto()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            QobuzQuality = "6",
            TidalQuality = "LOSSLESS",
            MaxBitrate = 3,
            AppleMusic = new AppleMusicSettings
            {
                PreferredAudioProfile = "ALAC"
            }
        };

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal("qobuz|27", sources[0]);
        Assert.Equal("tidal|HI_RES_LOSSLESS", sources[1]);
        Assert.Equal("apple|ALAC", sources[2]);
        Assert.Contains("qobuz|6", sources);
        Assert.Contains("tidal|LOSSLESS", sources);
        Assert.Contains("deezer|9", sources);
        Assert.Contains("deezer|3", sources);
        Assert.Contains("deezer|1", sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_HonorsRequestedTargetQuality_WhenServiceIsAuto()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            QobuzQuality = "6",
            TidalQuality = "LOSSLESS",
            MaxBitrate = 3,
            AppleMusic = new AppleMusicSettings
            {
                PreferredAudioProfile = "ALAC"
            }
        };

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: "3");

        Assert.Equal("deezer|3", sources[0]);
        Assert.DoesNotContain("qobuz|6", sources);
        Assert.DoesNotContain("tidal|LOSSLESS", sources);
        Assert.Equal(ExpectedDeezerQualityFallback, sources);
    }

    [Fact]
    public void ResolveEngineQualitySources_StrictFalse_ReturnsEngineOnlyFromRequestedQualityDownward()
    {
        var sources = DownloadSourceOrder.ResolveEngineQualitySources("deezer", "3", strict: false);

        Assert.Equal(ExpectedDeezerQualityFallback, sources);
    }

    [Fact]
    public void ResolveEngineQualitySources_StrictTrue_ReturnsSingleRequestedQualityStep()
    {
        var sources = DownloadSourceOrder.ResolveEngineQualitySources("qobuz", "6", strict: true);

        Assert.Equal(ExpectedQobuzStrictQuality, sources);
    }

    [Fact]
    public void ResolveInitialAutoStep_PrefersExactEngineAndQualityMatch()
    {
        var autoSources = new List<string>
        {
            "qobuz|27",
            "tidal|HI_RES_LOSSLESS",
            "deezer|9",
            "deezer|3"
        };

        var resolved = DownloadSourceOrder.ResolveInitialAutoStep(autoSources, "deezer", "3");

        Assert.Equal(3, resolved.Index);
        Assert.Equal("3", resolved.Quality);
    }

    [Fact]
    public void ResolveInitialAutoStep_FallsBackToFirstEngineStep_WhenExactQualityMissing()
    {
        var autoSources = new List<string>
        {
            "qobuz|27",
            "deezer|9",
            "deezer|3"
        };

        var resolved = DownloadSourceOrder.ResolveInitialAutoStep(autoSources, "deezer", "1");

        Assert.Equal(1, resolved.Index);
        Assert.Equal("9", resolved.Quality);
    }
}
