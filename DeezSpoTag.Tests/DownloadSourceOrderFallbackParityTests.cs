using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadSourceOrderFallbackParityTests
{
    private static readonly string[] ExpectedDeezerQualityFallback = { "deezer|3", "deezer|1" };
    private static readonly string[] ExpectedQobuzStrictQuality = { "qobuz|6" };
    private static readonly string[] ExpectedCustomQualityOrder = { "apple|ALAC", "qobuz|6", "tidal|LOSSLESS" };
    private static readonly string[] ExpectedAppleOnlyOrder = { "apple|ALAC", "apple|AAC" };
    private static readonly string[] ExpectedQobuzLosslessOrder = { "qobuz|6" };
    private static readonly string[] ExpectedDirectAppleOrder = { "apple|ALAC", "qobuz|6" };
    private static readonly string[] ExpectedDefaultOrder =
    {
        "qobuz|27",
        "tidal|HI_RES_LOSSLESS",
        "apple|ALAC",
        "qobuz|7",
        "qobuz|6",
        "tidal|LOSSLESS",
        "amazon|FLAC",
        "deezer|9",
        "apple|AAC",
        "deezer|3",
        "deezer|1"
    };

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
    public void ResolveQualityAutoSources_CustomOrderDisabled_KeepsCanonicalQualityOrder()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = false;
        settings.DownloadEngineOrder.Engines.Reverse();

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(ExpectedDefaultOrder, sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_CustomOrderEnabled_RespectsEngineAndQualityOrder()
    {
        var settings = CreateCustomOrderSettings(
            ("apple", true, new[] { ("ALAC", true), ("AAC", false) }),
            ("qobuz", true, new[] { ("6", true), ("7", false), ("27", false) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }),
            ("tidal", true, new[] { ("LOSSLESS", true), ("HI_RES_LOSSLESS", false) }),
            ("amazon", false, new[] { ("FLAC", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(ExpectedCustomQualityOrder, sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_CustomOrderEnabled_IncludesAppleMusicQualities()
    {
        var settings = CreateCustomOrderSettings(
            ("apple", true, new[] { ("ALAC", true), ("AAC", true) }),
            ("qobuz", false, new[] { ("27", true), ("7", true), ("6", true) }),
            ("tidal", false, new[] { ("HI_RES_LOSSLESS", true), ("LOSSLESS", true) }),
            ("amazon", false, new[] { ("FLAC", true) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(ExpectedAppleOnlyOrder, sources);
    }

    [Fact]
    public void ResolveEngineQualitySources_CustomOrderEnabled_RespectsDisabledQualities_ForForcedEngine()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("27", false), ("7", false), ("6", true) }),
            ("tidal", true, new[] { ("HI_RES_LOSSLESS", true), ("LOSSLESS", true) }),
            ("apple", true, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", true, new[] { ("FLAC", true) }),
            ("deezer", true, new[] { ("9", true), ("3", true), ("1", true) }));

        var sources = DownloadSourceOrder.ResolveEngineQualitySources(settings, "qobuz", "27", strict: false);

        Assert.Equal(ExpectedQobuzLosslessOrder, sources);
    }

    [Fact]
    public void ResolveAutoSourceState_DirectApiUsesCustomOrder()
    {
        var settings = CreateCustomOrderSettings(
            ("apple", true, new[] { ("ALAC", true), ("AAC", false) }),
            ("qobuz", true, new[] { ("27", false), ("7", false), ("6", true) }),
            ("tidal", false, new[] { ("HI_RES_LOSSLESS", true), ("LOSSLESS", true) }),
            ("amazon", false, new[] { ("FLAC", true) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }));

        var state = EngineDownloadControllerCommon.ResolveAutoSourceState(settings, includeDeezer: true, "apple", "ALAC");

        Assert.Equal(ExpectedDirectAppleOrder, state.AutoSources);
        Assert.Equal(0, state.AutoIndex);
        Assert.Equal("ALAC", state.ResolvedQuality);
    }

    [Fact]
    public void ValidateDownloadEngineOrderSettings_RejectsEnabledConfigWithoutEnabledQualities()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("27", false), ("7", false), ("6", false) }),
            ("tidal", false, new[] { ("HI_RES_LOSSLESS", true), ("LOSSLESS", true) }),
            ("apple", false, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", false, new[] { ("FLAC", true) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }));

        var result = DownloadSourceOrder.ValidateDownloadEngineOrderSettings(settings.DownloadEngineOrder);

        Assert.False(result.IsValid);
        Assert.Contains("Qobuz", result.Error);
    }

    [Fact]
    public void ValidateDownloadEngineOrderSettings_RejectsDuplicateEngines()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("27", true), ("7", true), ("6", true) }),
            ("qobuz", true, new[] { ("27", true), ("7", true), ("6", true) }),
            ("tidal", true, new[] { ("HI_RES_LOSSLESS", true), ("LOSSLESS", true) }),
            ("apple", true, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", true, new[] { ("FLAC", true) }),
            ("deezer", true, new[] { ("9", true), ("3", true), ("1", true) }));

        var result = DownloadSourceOrder.ValidateDownloadEngineOrderSettings(settings.DownloadEngineOrder);

        Assert.False(result.IsValid);
        Assert.Contains("duplicate Qobuz", result.Error);
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

    private static DeezSpoTagSettings CreateCustomOrderSettings(
        params (string Engine, bool Enabled, (string Quality, bool Enabled)[] Qualities)[] engines)
    {
        return new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = new DownloadEngineOrderSettings
            {
                Enabled = true,
                Engines = engines
                    .Select(engine => new DownloadEngineOrderItem
                    {
                        Engine = engine.Engine,
                        Enabled = engine.Enabled,
                        Qualities = engine.Qualities
                            .Select(quality => new DownloadEngineQualityItem
                            {
                                Quality = quality.Quality,
                                Enabled = quality.Enabled
                            })
                            .ToList()
                    })
                    .ToList()
            }
        };
    }
}
