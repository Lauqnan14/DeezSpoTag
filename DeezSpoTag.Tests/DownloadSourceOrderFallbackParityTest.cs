using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadSourceOrderFallbackParityTest
{
    // A seek to a catalogue step keeps the Soulseek step of the same tier with it, because a peer is the last
    // resort for that quality rather than a separate block walked at the very end.
    private static readonly string[] ExpectedTargetQualityFallback =
    [
        "deezer|3",
        "soundcloud|HQ",
        "soulseek|MP3_320",
        "soulseek|MP3_256",
        "amazon|OPUS",
        "soulseek|MP3_192",
        "deezer|1",
        "soundcloud|SQ",
        "soulseek|MP3_128",
        "tidal|LOW",
        "soulseek|UNKNOWN",
        "soundcloud|LQ",
    ];
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

    /// <summary>
    ///     The catalogue steps in ladder order, ignoring Soulseek. This is the order that shipped before
    ///     Soulseek existed, so it is what proves the interleave changed nothing for the catalogue engines.
    /// </summary>
    private static readonly string[] ExpectedCatalogueOrder =
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
        "deezer|3",
        "deezer|1",
        "tidal|LOW"
    };

    /// <summary>
    ///     Soulseek is the last resort <em>for each tier</em>: every Soulseek step sits directly after the
    ///     catalogue steps of the same quality, so a peer search is only attempted once the services that
    ///     could deliver that tier have been tried. Removing the Soulseek steps leaves
    ///     <see cref="ExpectedCatalogueOrder"/> unchanged, which is what makes the interleave additive.
    /// </summary>
    private static readonly string[] ExpectedDefaultOrder =
    [
        "qobuz|27",
        "tidal|HI_RES_LOSSLESS",
        "soulseek|FLAC_HI_RES_LOSSLESS",
        "qobuz|7",
        "tidal|HI_RES",
        "amazon|ULTRA_HD_FLAC",
        "soulseek|FLAC_HI_RES",
        "apple|ALAC",
        "qobuz|6",
        "tidal|LOSSLESS",
        "amazon|HD_FLAC",
        "deezer|9",
        "soulseek|FLAC",
        "soulseek|LOSSLESS",
        "apple|AAC",
        "qobuz|5",
        "tidal|HIGH",
        "deezer|3",
        "soundcloud|HQ",
        "soulseek|MP3_320",
        "soulseek|MP3_256",
        "amazon|OPUS",
        "soulseek|MP3_192",
        "deezer|1",
        "soundcloud|SQ",
        "soulseek|MP3_128",
        "tidal|LOW",
        "soulseek|UNKNOWN",
        "soundcloud|LQ",
    ];

    /// <summary>
    ///     An inactive Soulseek contributes no step to a plan, and takes nothing else with it.
    /// </summary>
    /// <remarks>
    ///     The point is not only that Soulseek is absent. A plan that loses one engine must otherwise come out
    ///     identical, because an inactive source that quietly reordered the ladder or shifted another engine's
    ///     rung would change which file every other track downloads from.
    /// </remarks>
    [Fact]
    public void AnIneligibleSoulseekIsOmittedWithoutDisturbingTheRestOfThePlan()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            QobuzQuality = "6",
            TidalQuality = "LOSSLESS",
            MaxBitrate = 3
        };

        var eligible = DownloadSourceOrder.ResolveQualityAutoSources(
            settings, includeDeezer: true, targetQuality: null, soulseekEligible: true);
        var ineligible = DownloadSourceOrder.ResolveQualityAutoSources(
            settings, includeDeezer: true, targetQuality: null, soulseekEligible: false);

        Assert.Contains(eligible, step => step == "soulseek|MP3_320");
        Assert.DoesNotContain(ineligible, step => step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));

        // Everything else is exactly where it was.
        Assert.Equal(
            eligible.Where(step => !step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase)),
            ineligible);
    }

    /// <summary>
    ///     Soulseek being inactive must not remove another engine or invent one.
    /// </summary>
    [Fact]
    public void AnIneligibleSoulseekNeverReplacesTheStepWithADifferentEngine()
    {
        var settings = new DeezSpoTagSettings { Service = "auto", MaxBitrate = 3 };

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(
            settings, includeDeezer: true, targetQuality: null, soulseekEligible: false);

        // The reader selected Soulseek and got nothing. Silently fetching from Deezer instead would hand back a
        // file they did not choose, which is the substitution the pin exists to prevent.
        Assert.DoesNotContain(sources, step => step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(sources);
    }

    [Fact]
    public void AskingForAnIneligibleSoulseekProducesNoStepsAtAll()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = new DownloadEngineOrderSettings
            {
                Enabled = true,
                Engines =
                {
                    new DownloadEngineOrderItem
                    {
                        Engine = "soulseek",
                        Qualities =
                        {
                            new DownloadEngineQualityItem { Quality = "FLAC" },
                            new DownloadEngineQualityItem { Quality = "MP3_320" }
                        }
                    }
                }
            }
        };

        var blocked = DownloadSourceOrder.ResolveEngineQualitySources(
            settings, "soulseek", requestedQuality: "FLAC", strict: true, soulseekEligible: false);
        var allowed = DownloadSourceOrder.ResolveEngineQualitySources(
            settings, "soulseek", requestedQuality: "FLAC", strict: true, soulseekEligible: true);

        Assert.Empty(blocked);
        Assert.NotEmpty(allowed);

        // Another engine asked for by name is untouched, so the gate is specific to Soulseek.
        Assert.NotEmpty(DownloadSourceOrder.ResolveEngineQualitySources(
            settings, "deezer", requestedQuality: null, strict: false, soulseekEligible: false));
    }

    /// <summary>
    ///     A plan persisted while Soulseek was logged in must not resurrect it after logout.
    /// </summary>
    [Fact]
    public void APersistedPlanIsReCheckedRatherThanTrusted()
    {
        var settings = new DeezSpoTagSettings { Service = "custom", MaxBitrate = 3 };
        var persisted = new List<string> { "soulseek|FLAC", "deezer|9", "tidal|LOSSLESS" };

        var afterLogout = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            persisted,
            engine: string.Empty,
            requestedQuality: null,
            strict: false,
            includeDeezer: true,
            soulseekEligible: false);

        Assert.DoesNotContain(afterLogout, step => step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));

        // The other engines are still there, in their saved order.
        Assert.Contains(afterLogout, step => step == "deezer|9");
        Assert.Contains(afterLogout, step => step == "tidal|LOSSLESS");
    }

    /// <summary>
    ///     Filtering Soulseek out must not be undone by the wholesale-restore fallback.
    /// </summary>
    [Fact]
    public void AnEmptyFilteredPlanIsNotRefilledWithTheCallersStoredList()
    {
        var settings = new DeezSpoTagSettings { Service = "deezer", MaxBitrate = 3 };
        var onlySoulseek = new List<string> { "soulseek|FLAC" };

        var sources = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            onlySoulseek,
            engine: "soulseek",
            requestedQuality: "FLAC",
            strict: true,
            includeDeezer: true,
            soulseekEligible: false);

        Assert.Empty(sources);
    }

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

    [Theory]
    [InlineData("flac")]
    [InlineData("FLAC")]
    [InlineData("mp3_320")]
    [InlineData("mp3_128")]
    [InlineData("unknown")]
    [InlineData("flac_hires")]
    [InlineData("flac_hi_res")]
    [InlineData("flac_hi_res_lossless")]
    public void ResolveQualityAutoSources_TargetQualityNeverStartsOnASoulseekStep(string targetQuality)
    {
        // Soulseek's codes are deliberately not linked into the shared tier table, so none of them may act as a
        // seek target. Without this guard a request for "flac" would land on soulseek|FLAC at the tail of the
        // ladder and silently skip every catalogue engine above it.
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = false;

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: targetQuality);

        Assert.Equal(ExpectedDefaultOrder, sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_SoulseekIsLastForEachTierAndNeverAheadOfACatalogueStep()
    {
        // The invariant behind the interleave: for every Soulseek step, no catalogue step of the same tier may
        // come after it, and no catalogue step of a worse tier may come before it.
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = false;

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        var tiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["27"] = 60, ["HI_RES_LOSSLESS"] = 60, ["FLAC_HI_RES_LOSSLESS"] = 60,
            ["7"] = 50, ["HI_RES"] = 50, ["ULTRA_HD_FLAC"] = 50, ["FLAC_HI_RES"] = 50,
            ["6"] = 40, ["LOSSLESS"] = 40, ["ALAC"] = 40, ["HD_FLAC"] = 40, ["9"] = 40, ["FLAC"] = 40,
            ["AAC"] = 30, ["5"] = 30, ["HIGH"] = 30, ["3"] = 30, ["MP3_320"] = 30, ["HQ"] = 30, ["MP3_256"] = 25,
            ["OPUS"] = 20, ["MP3_192"] = 20,
            ["1"] = 10, ["MP3_128"] = 10, ["SQ"] = 10,
            ["LOW"] = 0, ["UNKNOWN"] = 0, ["LQ"] = -1
        };

        for (var i = 0; i < sources.Count; i++)
        {
            var step = DownloadSourceOrder.DecodeAutoSource(sources[i]);
            if (!string.Equals(step.Source, "soulseek", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tier = tiers[step.Quality!];

            for (var j = i + 1; j < sources.Count; j++)
            {
                var later = DownloadSourceOrder.DecodeAutoSource(sources[j]);
                if (string.Equals(later.Source, "soulseek", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Assert.True(
                    tiers[later.Quality!] < tier,
                    $"Soulseek step '{sources[i]}' (tier {tier}) is followed by catalogue step '{sources[j]}' "
                    + $"(tier {tiers[later.Quality!]}). A catalogue step of the same or better tier must be "
                    + "tried before falling back to a peer.");
            }
        }
    }

    [Fact]
    public void ResolveQualityAutoSources_TargetQualityStillSeeksToACatalogueStep()
    {
        // The Soulseek skip must not disable the existing seek. "lossless" belongs to Tidal, so the ladder
        // still has to start at Tidal's step rather than at the top.
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = false;

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: "LOSSLESS");

        Assert.Equal("tidal|LOSSLESS", sources[0]);
        Assert.DoesNotContain("qobuz|27", sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_SoulseekFollowsTheCatalogueStepsOfItsOwnTier()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = false;

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        Assert.Equal(ExpectedDefaultOrder, sources);

        // The catalogue order is untouched, which is what makes the interleave purely additive.
        Assert.Equal(
            ExpectedCatalogueOrder,
            sources.Where(source => !source.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase)).ToArray());

        // Each Soulseek step comes after every catalogue step of its own tier, and before the next tier down.
        foreach (var (soulseekStep, precedingCatalogueSteps) in new[]
                 {
                    (Step: "soulseek|FLAC_HI_RES_LOSSLESS", Preceding: new[] { "qobuz|27", "tidal|HI_RES_LOSSLESS" }),
                    (Step: "soulseek|FLAC_HI_RES", Preceding: new[] { "qobuz|7", "tidal|HI_RES", "amazon|ULTRA_HD_FLAC" }),
                    (Step: "soulseek|FLAC", Preceding: new[] { "qobuz|6", "tidal|LOSSLESS", "apple|ALAC", "amazon|HD_FLAC", "deezer|9" }),
                    (Step: "soulseek|MP3_320", Preceding: new[] { "apple|AAC", "qobuz|5", "tidal|HIGH", "deezer|3" })
                })
        {
            var soulseekIndex = sources.IndexOf(soulseekStep);
            Assert.True(soulseekIndex > 0, $"'{soulseekStep}' should be on the ladder.");

            foreach (var catalogueStep in precedingCatalogueSteps)
            {
                Assert.True(
                    sources.IndexOf(catalogueStep) < soulseekIndex,
                    $"'{catalogueStep}' must be tried before '{soulseekStep}'.");
            }
        }
    }

    [Fact]
    public void ResolveQualityAutoSources_ANoLossCatalogueRequestNeverReachesAPeer()
    {
        // The failure this interleave prevents: a 320kbps request used to fall through to Soulseek's peer
        // search only after 16 worse catalogue steps had already been tried and failed.
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = false;

        var sources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: "5");

        Assert.Equal(
            new[] { "qobuz|5", "tidal|HIGH", "deezer|3", "soundcloud|HQ", "soulseek|MP3_320", "soulseek|MP3_256", "amazon|OPUS", "soulseek|MP3_192", "deezer|1", "soundcloud|SQ", "soulseek|MP3_128", "tidal|LOW", "soulseek|UNKNOWN", "soundcloud|LQ" },
            sources);
    }

    [Fact]
    public void ResolveQualityAutoSources_AtmosRequestIsUnaffectedBySoulseek()
    {
        // Atmos is a separate ladder. Soulseek is stereo and lossy-capable only, so it must not appear there.
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = false;

        var atmosSources = DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: true,
            targetQuality: "DOLBY_ATMOS");

        Assert.NotEmpty(atmosSources);
        Assert.DoesNotContain(atmosSources, source => source.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));
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
