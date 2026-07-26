using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadSourceOrderFallbackParityTests
{
    private static readonly string[] ExpectedTargetQualityFallback = { "deezer|3", "deezer|1", "tidal|LOW" };
    private static readonly string[] ExpectedDeezerQualityFallback = { "deezer|3", "deezer|1" };
    private static readonly string[] ExpectedQobuzStrictQuality = { "qobuz|6" };
    private static readonly string[] ExpectedCustomQualityOrder = { "apple|ALAC", "qobuz|6", "tidal|LOSSLESS" };
    private static readonly string[] ExpectedCustomQobuzTidalFilteredQualityOrder =
    {
        "qobuz|27",
        "qobuz|7",
        "tidal|HI_RES",
        "qobuz|6",
        "tidal|LOSSLESS"
    };
    private static readonly string[] ExpectedAppleOnlyOrder = { "apple|ALAC", "apple|AAC" };
    private static readonly string[] ExpectedQobuzLosslessOrder = { "qobuz|6" };
    private static readonly string[] ExpectedDirectAppleOrder = { "apple|ALAC", "qobuz|6" };
    private static readonly string[] ExpectedDefaultOrder =
    {
        "qobuz|27",
        "tidal|HI_RES_LOSSLESS",
        "qobuz|7",
        "tidal|HI_RES",
        "amazon|ULTRA_HD_FLAC",
        "apple|ALAC",
        "qobuz|6",
        "tidal|LOSSLESS",
        "amazon|HD_FLAC",
        "deezer|9",
        "apple|AAC",
        "qobuz|5",
        "tidal|HIGH",
        "amazon|OPUS",
        "deezer|3",
        "deezer|1",
        "tidal|LOW"
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
        Assert.True(sources.IndexOf("amazon|ULTRA_HD_FLAC") < sources.IndexOf("apple|ALAC"));
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
    public void LibraryFolderQualityOptions_UseCanonicalMergedQualityTiers()
    {
        var options = QualityCatalog.GetLibraryFolderQualityOptions().ToList();

        var expected = new (string Value, string Label)[]
        {
                ("max_hires_192", "Max Hi-Res (24-bit/192kHz)"),
                ("hires_96", "Hi-Res (24-bit/96kHz)"),
                ("alac", "ALAC"),
                ("cd_lossless", "CD Lossless (16-bit/44.1kHz)"),
                ("flac", "FLAC"),
                ("aac_lc", "AAC-LC"),
                ("mp3_320", "MP3 320 kbps"),
                ("mp3_128", "MP3 128 kbps"),
                ("mp3_96", "MP3 96 kbps")
        };

        Assert.Equal(expected, options.Select(option => (option.Value, option.Label)).ToArray());
        Assert.Equal(options.Count, options.Select(option => option.Label).Distinct().Count());
    }

    [Fact]
    public void LibraryFolderQualityTiers_NormalizeLegacyEngineValuesAndResolveEngineQuality()
    {
        Assert.Equal("max_hires_192", QualityCatalog.NormalizeLibraryFolderQualityValue("27"));
        Assert.Equal("max_hires_192", QualityCatalog.NormalizeLibraryFolderQualityValue("HI_RES_LOSSLESS"));
        Assert.Equal("hires_96", QualityCatalog.NormalizeLibraryFolderQualityValue("HI_RES"));
        Assert.Equal("cd_lossless", QualityCatalog.NormalizeLibraryFolderQualityValue("LOSSLESS"));
        Assert.Equal("flac", QualityCatalog.NormalizeLibraryFolderQualityValue("9"));
        Assert.Equal("aac_lc", QualityCatalog.NormalizeLibraryFolderQualityValue("AAC"));
        Assert.Equal("mp3_320", QualityCatalog.NormalizeLibraryFolderQualityValue("3"));
        Assert.Equal("mp3_128", QualityCatalog.NormalizeLibraryFolderQualityValue("1"));
        Assert.Equal("mp3_96", QualityCatalog.NormalizeLibraryFolderQualityValue("LOW"));

        Assert.Equal("5", QualityCatalog.ResolveEngineQualityForLibraryFolderTier("mp3_320", "qobuz"));
        Assert.Equal("HIGH", QualityCatalog.ResolveEngineQualityForLibraryFolderTier("mp3_320", "tidal"));
        Assert.Equal("3", QualityCatalog.ResolveEngineQualityForLibraryFolderTier("mp3_320", "deezer"));
        Assert.Equal("FLAC", QualityCatalog.ResolveEngineQualityForLibraryFolderTier("flac", "amazon"));
        Assert.Equal("9", QualityCatalog.ResolveEngineQualityForLibraryFolderTier("flac", "deezer"));
        Assert.Null(QualityCatalog.ResolveEngineQualityForLibraryFolderTier("mp3_96", "deezer"));
    }

    [Fact]
    public void ResolveQualityAutoSources_CustomOrderEnabled_FiltersCanonicalQualityOrder()
    {
        var settings = CreateCustomOrderSettings(
            ("apple", true, new[] { ("ALAC", true), ("AAC", false) }),
            ("qobuz", true, new[] { ("6", true), ("5", false), ("7", false), ("27", false) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }),
            ("tidal", true, new[] { ("LOSSLESS", true), ("HIGH", false), ("LOW", false), ("HI_RES", false), ("HI_RES_LOSSLESS", false) }),
            ("amazon", false, new[] { ("HD_FLAC", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(ExpectedCustomQualityOrder, sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_CustomOrderEnabled_InterleavesEnabledQobuzAndTidalByQualityOrder()
    {
        var settings = CreateCustomOrderSettings(
            ("tidal", true, new[] { ("LOSSLESS", true), ("HIGH", false), ("LOW", false), ("HI_RES", true), ("HI_RES_LOSSLESS", false) }),
            ("qobuz", true, new[] { ("6", true), ("5", false), ("7", true), ("27", true) }),
            ("apple", false, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", false, new[] { ("HD_FLAC", true) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(ExpectedCustomQobuzTidalFilteredQualityOrder, sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_CustomOrderEnabled_KeepsDeezer128BeforeTidal96()
    {
        var settings = CreateCustomOrderSettings(
            ("tidal", true, new[] { ("HI_RES_LOSSLESS", false), ("HI_RES", false), ("LOSSLESS", false), ("HIGH", false), ("LOW", true) }),
            ("deezer", true, new[] { ("9", false), ("3", false), ("1", true) }),
            ("qobuz", false, new[] { ("27", true), ("7", true), ("6", true), ("5", true) }),
            ("apple", false, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", false, new[] { ("HD_FLAC", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(new[] { "deezer|1", "tidal|LOW" }, sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_CustomOrderEnabled_IncludesAppleMusicQualities()
    {
        var settings = CreateCustomOrderSettings(
            ("apple", true, new[] { ("ALAC", true), ("AAC", true) }),
            ("qobuz", false, new[] { ("27", true), ("7", true), ("6", true), ("5", true) }),
            ("tidal", false, new[] { ("HI_RES_LOSSLESS", true), ("HI_RES", true), ("LOSSLESS", true), ("HIGH", true), ("LOW", true) }),
            ("amazon", false, new[] { ("HD_FLAC", true) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(ExpectedAppleOnlyOrder, sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_CustomOrderEnabled_DoesNotReaddOmittedDefaultEngines()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("27", true), ("7", false), ("6", false), ("5", false) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(new[] { "qobuz|27" }, sources);
        Assert.DoesNotContain(sources, source => source.StartsWith("apple|", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source => source.StartsWith("tidal|", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source => source.StartsWith("amazon|", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source => source.StartsWith("deezer|", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveEngineQualitySources_CustomOrderEnabled_RespectsDisabledQualities_ForForcedEngine()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("27", false), ("7", false), ("6", true), ("5", false) }),
            ("tidal", true, new[] { ("HI_RES_LOSSLESS", true), ("HI_RES", true), ("LOSSLESS", true), ("HIGH", true), ("LOW", true) }),
            ("apple", true, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", true, new[] { ("HD_FLAC", true) }),
            ("deezer", true, new[] { ("9", true), ("3", true), ("1", true) }));

        var sources = DownloadSourceOrder.ResolveEngineQualitySources(settings, "qobuz", "27", strict: false);

        Assert.Equal(ExpectedQobuzLosslessOrder, sources);
    }

    [Fact]
    public void ResolveAutoSourceState_DirectApiUsesCustomOrder()
    {
        var settings = CreateCustomOrderSettings(
            ("apple", true, new[] { ("ALAC", true), ("AAC", false) }),
            ("qobuz", true, new[] { ("27", false), ("7", false), ("6", true), ("5", false) }),
            ("tidal", false, new[] { ("HI_RES_LOSSLESS", true), ("HI_RES", true), ("LOSSLESS", true), ("HIGH", true), ("LOW", true) }),
            ("amazon", false, new[] { ("HD_FLAC", true) }),
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
            ("qobuz", true, new[] { ("27", false), ("7", false), ("6", false), ("5", false) }),
            ("tidal", false, new[] { ("HI_RES_LOSSLESS", true), ("HI_RES", true), ("LOSSLESS", true), ("HIGH", true), ("LOW", true) }),
            ("apple", false, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", false, new[] { ("HD_FLAC", true) }),
            ("deezer", false, new[] { ("9", true), ("3", true), ("1", true) }));

        var result = DownloadSourceOrder.ValidateDownloadEngineOrderSettings(settings.DownloadEngineOrder);

        Assert.False(result.IsValid);
        Assert.Contains("Qobuz", result.Error);
    }

    [Fact]
    public void ValidateDownloadEngineOrderSettings_RejectsDuplicateEngines()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("27", true), ("7", true), ("6", true), ("5", true) }),
            ("qobuz", true, new[] { ("27", true), ("7", true), ("6", true), ("5", true) }),
            ("tidal", true, new[] { ("HI_RES_LOSSLESS", true), ("HI_RES", true), ("LOSSLESS", true), ("HIGH", true), ("LOW", true) }),
            ("apple", true, new[] { ("ALAC", true), ("AAC", true) }),
            ("amazon", true, new[] { ("HD_FLAC", true) }),
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
        Assert.Equal(ExpectedTargetQualityFallback, sources);
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

    [Fact]
    public void ResolveQualityAutoSources_StereoRequestExcludesEnabledAtmosProfiles()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("7", true), ("6", true) }),
            ("tidal", true, new[] { ("HI_RES", true), ("LOSSLESS", true), ("DOLBY_ATMOS", true) }),
            ("amazon", true, new[] { ("HD_FLAC", true), ("DOLBY_ATMOS", true) }),
            ("apple", true, new[] { ("ALAC", true), ("ATMOS", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: true,
            targetQuality: "HI_RES");

        Assert.NotEmpty(sources);
        Assert.DoesNotContain(sources, source => source.Contains("ATMOS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveQualityAutoSources_AtmosRequestIncludesOnlyEnabledAtmosProfiles()
    {
        var settings = CreateCustomOrderSettings(
            ("qobuz", true, new[] { ("7", true), ("6", true) }),
            ("tidal", true, new[] { ("HI_RES", true), ("DOLBY_ATMOS", true) }),
            ("amazon", true, new[] { ("HD_FLAC", true), ("DOLBY_ATMOS", true) }),
            ("apple", true, new[] { ("ALAC", true), ("ATMOS", true) }));

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: true,
            targetQuality: "ATMOS");

        Assert.Equal(
            ["apple|ATMOS", "tidal|DOLBY_ATMOS", "amazon|DOLBY_ATMOS"],
            sources);
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
