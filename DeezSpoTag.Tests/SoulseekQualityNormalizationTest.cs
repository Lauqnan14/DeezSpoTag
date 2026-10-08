using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Tests that Soulseek quality resolves into the existing <see cref="QualityCatalog"/> tiers.
/// </summary>
/// <remarks>
///     The behaviour under test is the one the design requires: Soulseek must not introduce a second
///     quality system, and an unknown quality must carry no tier and no rank so it can never be treated as
///     a high quality match.
/// </remarks>
public sealed class SoulseekQualityNormalizationTest
{
    [Fact]
    public void LosslessWithoutDepthOrRate_UsesTheContainerTier_ConsistentWithSiblingEngines()
    {
        // A FLAC with no depth reported is a plain FLAC, which is how Deezer's "9" and Amazon's "FLAC" are
        // already treated. It must not be assumed to be CD lossless or, worse, hi-res.
        var flac = SoulseekQuality.Normalize("@@u\\share\\Artist\\Track.flac", null, null);

        Assert.Equal("FLAC", flac.Code);
        Assert.Equal(QualityCatalog.Flac, flac.TierValue);
        Assert.Equal(QualityCatalog.GetLibraryFolderCanonicalRank(QualityCatalog.Flac), flac.CanonicalRank);
        Assert.True(flac.IsLossless);
    }

    [Fact]
    public void LosslessContainerWithNoMatchingTier_FallsBackToCdLossless()
    {
        var wav = SoulseekQuality.Normalize("track.wav", null, null);

        Assert.Equal("LOSSLESS", wav.Code);
        Assert.Equal(QualityCatalog.CdLossless, wav.TierValue);
    }

    [Theory]
    [InlineData(16, 44100, QualityCatalog.Flac)]
    [InlineData(24, 96000, QualityCatalog.HiRes96)]
    [InlineData(24, 192000, QualityCatalog.MaxHiRes192)]
    [InlineData(24, 48000, QualityCatalog.HiRes96)]
    [InlineData(24, 44100, QualityCatalog.Flac)]
    public void LosslessDepthAndRate_SelectTheMatchingExistingTier(int bitDepth, int sampleRate, string expectedTier)
    {
        var quality = SoulseekQuality.Normalize("track.flac", null, null, bitDepth, sampleRate);

        Assert.Equal(expectedTier, quality.TierValue);
        Assert.Equal(QualityCatalog.GetLibraryFolderCanonicalRank(expectedTier), quality.CanonicalRank);
    }

    [Fact]
    public void LosslessAt24BitWithUnreportedRate_AssumesHiResButNeverMaxHiRes()
    {
        var quality = SoulseekQuality.Normalize("track.flac", null, null, 24, null);

        Assert.Equal(QualityCatalog.HiRes96, quality.TierValue);
    }

    [Fact]
    public void AlacIsReportedAsGenericLossless()
    {
        // Soulseek has no ALAC code: an ALAC file is lossless, so it answers to LOSSLESS like WAV and APE do.
        // Its tier is CD lossless for the same reason - the shared alac tier belongs to Apple Music, and a
        // peer-supplied ALAC is just another 16-bit/44.1kHz lossless file here.
        var quality = SoulseekQuality.Normalize("track.m4a", null, null);

        Assert.Equal("LOSSLESS", quality.Code);
        Assert.Equal(QualityCatalog.CdLossless, quality.TierValue);
        Assert.Equal(QualityCatalog.GetLibraryFolderCanonicalRank(QualityCatalog.CdLossless), quality.CanonicalRank);
        Assert.True(quality.IsLossless);
    }

    [Fact]
    public void AlacIsNotAKnownCodeOfItsOwn()
    {
        Assert.DoesNotContain("ALAC", SoulseekQuality.KnownCodes);
        Assert.Equal(SoulseekQualityInfo.UnknownCode, SoulseekQuality.NormalizeCode("ALAC"));
        Assert.DoesNotContain("ALAC", QualityCatalog.GetEngineQualityOptions()["soulseek"].Select(option => option.Value));
    }

    [Theory]
    // Bands are lower-bound "320 class" style, matching DeliveredAudioQualityGuard's existing
    // "5"/"HIGH"/"3" => BitrateKbps >= 256 rule, so Soulseek and the other engines agree on what
    // counts as 320 kbps.
    [InlineData(320, "MP3_320")]
    [InlineData(300, "MP3_320")]
    [InlineData(256, "MP3_320")]
    [InlineData(255, "MP3_256")]
    [InlineData(192, "MP3_256")]
    [InlineData(191, "MP3_192")]
    [InlineData(160, "MP3_192")]
    [InlineData(159, "MP3_128")]
    [InlineData(128, "MP3_128")]
    public void Mp3BitrateSelectsTheMatchingCode(int bitrate, string expectedCode)
    {
        var quality = SoulseekQuality.Normalize("track.mp3", null, bitrate);

        Assert.Equal(expectedCode, quality.Code);
        Assert.False(quality.IsLossless);
        Assert.False(quality.IsUnknown);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(96)]
    [InlineData(64)]
    public void Mp3Below128Kbps_IsUnknownRatherThanRoundedUpToThe128Tier(int bitrate)
    {
        // The design names 320/256/192/128 only. Rounding a 96 kbps file up to the 128 tier would let it
        // satisfy a 128 kbps request in DeliveredAudioQualityGuard, so it must carry no rank at all.
        var quality = SoulseekQuality.Normalize("track.mp3", null, bitrate);

        Assert.True(quality.IsUnknown);
        Assert.Null(quality.TierValue);
        Assert.Null(quality.CanonicalRank);
    }

    [Fact]
    public void Mp3_256AndMp3_192RankInsideTheExisting320Tier_WithoutAddingASharedTier()
    {
        // The shared folder-quality selector must gain no new entries, so the 256 and 192 codes borrow the
        // existing 320 tier for ranking while keeping their own labels.
        var tier320 = QualityCatalog.GetLibraryFolderCanonicalRank(QualityCatalog.Mp3_320);
        var tier128 = QualityCatalog.GetLibraryFolderCanonicalRank(QualityCatalog.Mp3_128);

        var at192 = SoulseekQuality.Normalize("track.mp3", null, 192);
        var at160 = SoulseekQuality.Normalize("track.mp3", null, 160);
        var at128 = SoulseekQuality.Normalize("track.mp3", null, 128);

        Assert.Equal("MP3_256", at192.Code);
        Assert.Equal(QualityCatalog.Mp3_320, at192.TierValue);
        Assert.Equal(tier320, at192.CanonicalRank);
        Assert.Equal("MP3 192 kbps", at192.Label);

        Assert.Equal("MP3_192", at160.Code);
        Assert.Equal(QualityCatalog.Mp3_320, at160.TierValue);
        Assert.Equal(tier320, at160.CanonicalRank);
        Assert.Equal("MP3 160 kbps", at160.Label);

        Assert.Equal(QualityCatalog.Mp3_128, at128.TierValue);
        Assert.Equal(tier128, at128.CanonicalRank);

        Assert.True(at192.CanonicalRank > at128.CanonicalRank);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void UnknownQuality_HasNoTierAndNoRank_SoItCanNeverCountAsAHighMatch(int? bitrate)
    {
        var quality = SoulseekQuality.Normalize("track.mp3", null, bitrate);

        Assert.Equal(SoulseekQualityInfo.UnknownCode, quality.Code);
        Assert.Null(quality.TierValue);
        Assert.Null(quality.CanonicalRank);
        Assert.True(quality.IsUnknown);
    }

    [Fact]
    public void UnsupportedAudioExtension_IsUnknownRatherThanBeingTreatedAsLossless()
    {
        var quality = SoulseekQuality.Normalize("track.opus", null, 128);

        Assert.True(quality.IsUnknown);
        Assert.Null(quality.TierValue);
    }

    [Fact]
    public void ExtensionIsDerivedFromTheRemotePathWhenSlskdDidNotReportOne()
    {
        var quality = SoulseekQuality.Normalize("@@listener\\Music\\Artist (2020)\\01 - Song.FLAC", null, null, 24, 96000);

        Assert.Equal("FLAC_HI_RES", quality.Code);
        Assert.Equal(QualityCatalog.HiRes96, quality.TierValue);
    }

    [Fact]
    public void ReportedExtensionWins_AndLeadingDotIsOptional()
    {
        Assert.Equal("FLAC", SoulseekQuality.Normalize("track.bin", "flac", null, 16, 44100).Code);
        Assert.Equal("FLAC", SoulseekQuality.Normalize("track.bin", ".FLAC", null, 16, 44100).Code);
    }

    [Theory]
    [InlineData(24, 192000, "FLAC_HI_RES_LOSSLESS", QualityCatalog.MaxHiRes192)]
    [InlineData(24, 96000, "FLAC_HI_RES", QualityCatalog.HiRes96)]
    [InlineData(16, 44100, "FLAC", QualityCatalog.Flac)]
    [InlineData(24, 44100, "FLAC", QualityCatalog.Flac)]
    public void FlacIsSplitIntoTheSameThreeBandsQobuzAndTidalUse(
        int bitDepth,
        int sampleRate,
        string expectedCode,
        string expectedTier)
    {
        var quality = SoulseekQuality.Normalize("track.flac", null, null, bitDepth, sampleRate);

        Assert.Equal(expectedCode, quality.Code);
        Assert.Equal(expectedTier, quality.TierValue);
        Assert.True(quality.IsLossless);
    }

    [Fact]
    public void TheThreeFlacBandsAreSelectableCodesThatRankLikeTidals()
    {
        Assert.Equal(QualityCatalog.MaxHiRes192, SoulseekQuality.NormalizeCodeWithFacts("FLAC_HI_RES_LOSSLESS").TierValue);
        Assert.Equal(QualityCatalog.HiRes96, SoulseekQuality.NormalizeCodeWithFacts("FLAC_HI_RES").TierValue);
        Assert.Equal(QualityCatalog.Flac, SoulseekQuality.NormalizeCodeWithFacts("FLAC").TierValue);

        Assert.Equal("FLAC_HI_RES_LOSSLESS", SoulseekQuality.NormalizeCode("flac_hi_res_lossless"));
        Assert.Equal("FLAC_HI_RES", SoulseekQuality.NormalizeCode(" flac_hi_res "));

        foreach (var code in new[] { "FLAC_HI_RES_LOSSLESS", "FLAC_HI_RES" })
        {
            Assert.Contains(code, SoulseekQuality.KnownCodes);
            Assert.Contains(code, QualityCatalog.GetEngineQualityOptions()["soulseek"].Select(option => option.Value));
            Assert.True(SoulseekQuality.IsLosslessCode(code), $"{code} should be recognised as lossless.");
        }
    }

    [Fact]
    public void PathWithNoExtension_IsUnknown()
    {
        var quality = SoulseekQuality.Normalize("@@listener\\Music\\Discography", null, null, 24, 96000);

        Assert.True(quality.IsUnknown);
    }

    [Fact]
    public void NormalizeCode_FallsBackToUnknown_AndUppercases()
    {
        Assert.Equal("MP3_320", SoulseekQuality.NormalizeCode("mp3_320"));
        Assert.Equal("FLAC", SoulseekQuality.NormalizeCode(" flac "));
        Assert.Equal(SoulseekQualityInfo.UnknownCode, SoulseekQuality.NormalizeCode("dsd"));
        Assert.Equal(SoulseekQualityInfo.UnknownCode, SoulseekQuality.NormalizeCode(null));
    }

    [Fact]
    public void NormalizeCodeWithFacts_ReusesTheSameTierMapping()
    {
        Assert.Equal(QualityCatalog.MaxHiRes192, SoulseekQuality.NormalizeCodeWithFacts("LOSSLESS", 24, 192000).TierValue);
        Assert.Equal(QualityCatalog.HiRes96, SoulseekQuality.NormalizeCodeWithFacts("LOSSLESS", 24, 96000).TierValue);
        Assert.Equal(QualityCatalog.Flac, SoulseekQuality.NormalizeCodeWithFacts("LOSSLESS", 16, 44100).TierValue);
        Assert.Equal(QualityCatalog.Flac, SoulseekQuality.NormalizeCodeWithFacts("FLAC").TierValue);
        Assert.Equal(QualityCatalog.Mp3_320, SoulseekQuality.NormalizeCodeWithFacts("MP3_256").TierValue);
        Assert.Equal(QualityCatalog.Mp3_128, SoulseekQuality.NormalizeCodeWithFacts("MP3_128").TierValue);
        Assert.True(SoulseekQuality.NormalizeCodeWithFacts("nonsense").IsUnknown);
    }

    [Theory]
    [InlineData(QualityCatalog.MaxHiRes192, "FLAC_HI_RES_LOSSLESS")]
    [InlineData(QualityCatalog.HiRes96, "FLAC_HI_RES")]
    [InlineData(QualityCatalog.CdLossless, "FLAC")]
    [InlineData(QualityCatalog.Flac, "FLAC")]
    [InlineData(QualityCatalog.Alac, "LOSSLESS")]
    [InlineData(QualityCatalog.Mp3_320, "MP3_320")]
    [InlineData(QualityCatalog.Mp3_128, "MP3_128")]
    public void ResolveCodeForTier_TranslatesAFolderQualityIntoASoulseekCode(string tier, string expectedCode)
        => Assert.Equal(expectedCode, SoulseekQuality.ResolveCodeForTier(tier));

    [Fact]
    public void ResolveCodeForTier_ReturnsNullForNonAudioTiers()
    {
        Assert.Null(SoulseekQuality.ResolveCodeForTier("atmos"));
        Assert.Null(SoulseekQuality.ResolveCodeForTier(null));
    }

    [Fact]
    public void LosslessCodeDetection_CoversEveryLosslessCode()
    {
        Assert.True(SoulseekQuality.IsLosslessCode("LOSSLESS"));
        Assert.True(SoulseekQuality.IsLosslessCode("flac"));
        Assert.True(SoulseekQuality.IsLosslessCode("flac_hi_res"));
        Assert.True(SoulseekQuality.IsLosslessCode("flac_hi_res_lossless"));
        Assert.False(SoulseekQuality.IsLosslessCode("MP3_320"));
        Assert.False(SoulseekQuality.IsLosslessCode("UNKNOWN"));
    }

    [Fact]
    public void EveryKnownCodeResolvesToATierExceptUnknown()
    {
        foreach (var code in SoulseekQuality.KnownCodes.Where(code => code != SoulseekQualityInfo.UnknownCode))
        {
            var quality = SoulseekQuality.NormalizeCodeWithFacts(code, 16, 44100);
            Assert.True(quality.CanonicalRank.HasValue, $"{code} should resolve to a canonical rank.");
        }

        var unknown = SoulseekQuality.NormalizeCodeWithFacts(SoulseekQualityInfo.UnknownCode);
        Assert.Null(unknown.CanonicalRank);
    }

    [Fact]
    public void QualityCatalog_ExposesSoulseekOptionsWithoutTouchingTheSharedTiers()
    {
        var options = QualityCatalog.GetEngineQualityOptions();

        Assert.True(options.ContainsKey("soulseek"));
        Assert.Contains(options["soulseek"], option => option.Value == "MP3_320");

        // Soulseek codes must NOT be linked into the shared tier table: a single "LOSSLESS" code spans CD,
        // hi-res and max hi-res, which FindLibraryFolderQualityTier cannot disambiguate.
        var tiers = QualityCatalog.GetLibraryFolderQualityTiers();
        Assert.All(tiers, tier => Assert.DoesNotContain(tier.EngineValues, link => link.Engine == "soulseek"));
    }

    /// <summary>
    ///     Ranks a Soulseek code by the shared canonical quality rank, highest first. UNKNOWN carries no rank
    ///     and sorts last, which is what keeps it at the foot of the ladder.
    /// </summary>
    private static int rankOf(string code) => SoulseekQuality.NormalizeCodeWithFacts(code).CanonicalRank ?? -1;

    /// <summary>
    ///     The search tab groups results by the qualities the user has enabled, and it must read the same
    ///     answer the downloader reads. This is the single source both use, so the tab can never offer a
    ///     quality the download would refuse.
    /// </summary>
    [Fact]
    public void EnabledSoulseekQualitiesFollowTheConfiguredSource()
    {
        // Soulseek selected outright: its own qualities, in the engine's catalog order.
        var direct = new Core.Models.Settings.DeezSpoTagSettings { Service = "soulseek" };
        var directCodes = DownloadSourceOrder.ResolveEnabledSoulseekQualities(direct);

        Assert.NotEmpty(directCodes);
        Assert.All(directCodes, code => Assert.Contains(code, SoulseekQuality.KnownCodes));
        // Best quality first, which is the ladder's descending order rather than the catalog's declaration order.
        Assert.Equal("FLAC_HI_RES_LOSSLESS", directCodes[0]);
        Assert.Equal(directCodes.OrderByDescending(rankOf), directCodes);

        // Auto: the Soulseek steps of the ladder, which is the tail the interleave produces.
        var auto = new Core.Models.Settings.DeezSpoTagSettings { Service = "auto" };
        var autoCodes = DownloadSourceOrder.ResolveEnabledSoulseekQualities(auto);

        Assert.NotEmpty(autoCodes);
        Assert.Equal("FLAC_HI_RES_LOSSLESS", autoCodes[0]);
        Assert.All(autoCodes, code => Assert.Contains(code, SoulseekQuality.KnownCodes));

        // A Custom order that ticks no Soulseek quality resolves to nothing, so the tab shows no group.
        var custom = new Core.Models.Settings.DeezSpoTagSettings { Service = "custom" };
        custom.DownloadEngineOrder = Core.Models.Settings.DownloadEngineOrderSettings.CreateDefault();
        custom.DownloadEngineOrder.Enabled = true;
        foreach (var engine in custom.DownloadEngineOrder.Engines)
        {
            engine.Enabled = engine.Engine != "soulseek";
        }

        Assert.Empty(DownloadSourceOrder.ResolveEnabledSoulseekQualities(custom));
    }

    /// <summary>
    ///     An empty Source selection disables Soulseek qualities; it must not expand to the whole ladder.
    /// </summary>
    [Fact]
    public void DisplayGroupsAreAllOrNoneOtherwiseTheLadderSubset()
    {
        // Nothing ticked means Soulseek has no enabled rung.
        var empty = DownloadSourceOrder.ResolveDisplayQualityGroups([]);
        Assert.Empty(empty);

        // Everything ticked renders the known ladder.
        var complete = DownloadSourceOrder.ResolveDisplayQualityGroups(SoulseekQuality.KnownCodes);
        Assert.Equal(8, complete.Count);
        Assert.Equal("FLAC_HI_RES_LOSSLESS", complete[0]);
        Assert.Equal("MP3_128", complete[^1]);

        // A partial selection shows only what is ticked, in ladder order rather than in tick order.
        Assert.Equal(
            new[] { "FLAC", "MP3_320" },
            DownloadSourceOrder.ResolveDisplayQualityGroups(new[] { "MP3_320", "FLAC" }));

        // Unknown quality alone is not a tier selection: it is the rung the ladder falls back to, governed by
        // its own switch rather than by the ticked tiers.
        Assert.Empty(DownloadSourceOrder.ResolveDisplayQualityGroups(new[] { "UNKNOWN" }));
    }

    /// <summary>
    ///     The undetermined rung is the ladder's last step, so allowing it adds the last group and refusing it
    ///     removes it entirely.
    /// </summary>
    /// <remarks>
    ///     This is the whole point of the switch: a group of files the download would refuse, counted in the
    ///     All Results total, is a step the ladder is not willing to take and the tab should not offer.
    /// </remarks>
    [Fact]
    public void TheUndeterminedRungFollowsTheUnknownQualitySwitch()
    {
        // Off: no trailing group, whatever the ticked tiers are.
        Assert.DoesNotContain(
            "UNKNOWN",
            DownloadSourceOrder.ResolveDisplayQualityGroups(SoulseekQuality.KnownCodes, allowUnknownQuality: false),
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "UNKNOWN",
            DownloadSourceOrder.ResolveDisplayQualityGroups([], allowUnknownQuality: false),
            StringComparer.OrdinalIgnoreCase);

        // On: the whole ladder gains it as its last rung, with nothing else moved. KnownCodes is a set in
        // descending-preference order with the undetermined code first, so it is the ladder order that is
        // being asserted here, not that list.
        var refused = DownloadSourceOrder.ResolveDisplayQualityGroups(SoulseekQuality.KnownCodes, allowUnknownQuality: false);
        var allowed = DownloadSourceOrder.ResolveDisplayQualityGroups(SoulseekQuality.KnownCodes, allowUnknownQuality: true);
        Assert.Equal(9, allowed.Count);
        Assert.Equal("UNKNOWN", allowed[^1]);
        Assert.Equal(refused, allowed.Take(refused.Count));

        Assert.Empty(DownloadSourceOrder.ResolveDisplayQualityGroups([], allowUnknownQuality: true));
        Assert.Equal(
            new[] { "FLAC" },
            DownloadSourceOrder.ResolveDisplayQualityGroups(["FLAC"], allowUnknownQuality: true));

        // UNKNOWN must be enabled in Source as well as allowed by its safety switch.
        Assert.Equal(
            new[] { "FLAC", "UNKNOWN" },
            DownloadSourceOrder.ResolveDisplayQualityGroups(new[] { "FLAC", "UNKNOWN" }, allowUnknownQuality: true));
    }

    /// <summary>
    ///     A quality the user asked for and did not get is a failed attempt, not a missing track. The two
    ///     are genuinely different: <c>unavailable</c> is terminal, is not retried, and offers a Monitor
    ///     button because the track may appear later, while <c>failed</c> is retried and advances the
    ///     fallback ladder. Filing a quality rejection as <c>unavailable</c> would stop the ladder walking.
    /// </summary>
    [Theory]
    [InlineData("Requested FLAC (16-bit/44.1kHz) but the provider delivered MP3 128 kbps.")]
    [InlineData("Requested Max Hi-Res (24-bit/192kHz) but the provider delivered CD Lossless (16-bit/44.1kHz).")]
    [InlineData("Requested Hi-Res (24-bit/96kHz) but the provider delivered FLAC (16-bit/44.1kHz).")]
    [InlineData("Requested MP3 320 kbps but the provider delivered MP3 128 kbps.")]
    public void ARejectedQualityIsAFailedAttemptNotAnUnavailableTrack(string message)
    {
        Assert.False(
            EngineAudioPostDownloadHelper.IsTrackUnavailableFailure(message),
            $"'{message}' is a rejected quality, so it must be retryable and must advance the ladder, which "
            + "means it must not be classified as an unavailable track.");
    }

    /// <summary>
    ///     A Soulseek peer network that cannot serve the request is also a failed attempt, never a missing
    ///     track. The track is not known not to exist; the sources simply could not deliver it right now.
    /// </summary>
    [Theory]
    [InlineData("Soulseek is not configured.")]
    [InlineData("slskd is unavailable.")]
    [InlineData("No Soulseek candidate passed matching for \"Boards of Canada - Roygbiv\" (37 seen, 12 peers responded). Closest rejections: below_requested_quality (22).")]
    public void ASoulseekFailureIsAFailedAttemptNotAnUnavailableTrack(string message)
    {
        Assert.False(
            EngineAudioPostDownloadHelper.IsTrackUnavailableFailure(message),
            $"'{message}' must stay retryable so the ladder can move on to the next quality.");
    }

    [Fact]
    public void TheNewFlacBandsCarryTheSameCanonicalRanksAsTidals()
    {
        // Soulseek reuses Tidal's ranks rather than inventing a scale, so ParseRequestedQualityRank resolves
        // them. Without these entries the codes would carry no rank at all.
        var intentSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            "DeezSpoTag.Web",
            "Services",
            "DownloadIntentService.cs"));

        Assert.Contains("[\"FLAC_HI_RES_LOSSLESS\"] = 115", intentSource, StringComparison.Ordinal);
        Assert.Contains("[\"FLAC_HI_RES\"] = 95", intentSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeliveredFileGuardChecksTheResolutionOfTheFlacBands()
    {
        // The guard is the one place quality is enforced for every engine, and it is where the split has to
        // land: a 24/192 request has to reject a delivered 16/44.1 file.
        var guardSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeliveredAudioQualityGuard.cs"));

        var start = guardSource.IndexOf("if (IsSoulseekEngine(engine))", StringComparison.Ordinal);
        Assert.True(start > 0, "The Soulseek arm of the guard was not found.");
        var end = guardSource.IndexOf("};", start, StringComparison.Ordinal);
        var soulseekArm = guardSource.Substring(start, end - start);

        Assert.Contains("\"FLAC_HI_RES_LOSSLESS\"", soulseekArm, StringComparison.Ordinal);
        Assert.Contains("\"FLAC_HI_RES\"", soulseekArm, StringComparison.Ordinal);

        // The two bands carry the same depth and sample-rate floors Qobuz's "27" and "7" already use.
        Assert.Contains("actual.BitsPerSample >= 24", soulseekArm, StringComparison.Ordinal);
        Assert.Contains("actual.SampleRate >= 192000", soulseekArm, StringComparison.Ordinal);

        // ALAC is no longer a code of its own, so the arm must not still accept one.
        Assert.DoesNotContain("\"ALAC\"", soulseekArm, StringComparison.Ordinal);
    }

    [Fact]
    public void Soulseek_IsASelectableSourceAndIsLastInTheCanonicalAutoOrder()
    {
        var sources = DownloadSourceCatalog.GetSettingsSourceOptions();
        Assert.Contains(sources, option => option.Value == "soulseek");

        var watchlist = DownloadSourceCatalog.GetWatchlistSourceOptions();
        Assert.Contains(watchlist, option => option.Value == "soulseek");

        // Soulseek is reachable from Auto mode, but only after every catalogue engine.
        var autoSources = DownloadSourceOrder.ResolveQualityAutoSources(
            new Core.Models.Settings.DeezSpoTagSettings { Service = "auto" },
            includeDeezer: true,
            targetQuality: null);

        var firstSoulseek = autoSources.FindIndex(source => source.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));
        Assert.True(firstSoulseek > 0, "Soulseek must appear in the auto ladder.");

        var catalogue = autoSources.Take(firstSoulseek);
        Assert.All(catalogue, source => Assert.False(source.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("qobuz|27", autoSources[0]);
    }
}
