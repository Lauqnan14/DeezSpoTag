using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Strict contract for the download fallback quality order.
/// </summary>
/// <remarks>
///     The fallback ladder is walked top to bottom, so the quality a step demands must never increase as
///     the walk descends. A step that demands a higher tier than a step above it means the ladder can
///     settle on a worse file than one it already passed over, which is exactly the "quality order is not
///     respected" failure. <see cref="RequiredTier"/> is the single place that maps an engine quality code
///     onto that ladder, and these tests assert the ladder and the delivered-audio guard both agree with it.
/// </remarks>
public sealed class DownloadQualityOrderMonotonicityTest
{
    /// <summary>
    ///     Position on the shared quality ladder. Higher is better. Only relative order matters, so the
    ///     numbers are spaced out to make an off-by-one inversion obvious in a failure message.
    /// </summary>
    private static readonly Dictionary<string, int> RequiredTier = new(StringComparer.OrdinalIgnoreCase)
    {
        // Lossless, resolution-carrying tiers.
        ["27"] = 60,
        ["HI_RES_LOSSLESS"] = 60,
        ["7"] = 50,
        ["HI_RES"] = 50,
        ["ULTRA_HD_FLAC"] = 50,

        // Lossless, CD or better.
        ["ALAC"] = 40,
        ["LOSSLESS"] = 40,
        ["HD_FLAC"] = 40,
        ["6"] = 40,
        ["9"] = 40,

        // Lossy, high.
        ["AAC"] = 30,
        ["5"] = 30,
        ["HIGH"] = 30,
        ["3"] = 30,

        // Lossy, low.
        ["OPUS"] = 20,
        ["1"] = 10,
        ["LOW"] = 0,

        // Soulseek reports per-file facts rather than tier codes, but its selectable codes still sit on
        // the same ladder so the order can be verified the same way. Its three FLAC bands reuse Tidal's tiers.
        ["FLAC_HI_RES_LOSSLESS"] = 60,
        ["FLAC_HI_RES"] = 50,
        ["FLAC"] = 40,
        ["MP3_320"] = 30,
        ["MP3_256"] = 25,
        ["MP3_192"] = 20,
        ["MP3_128"] = 10,
        ["UNKNOWN"] = 0,

        // SoundCloud advertises three lossy tiers by bitrate: HQ is the 256kbps Go+ stream, SQ is the 128kbps standard
        // stream, and LQ is 64kbps - worse than every rung above it, which is why it is the only step placed
        // below UNKNOWN rather than tiered alongside LOW.
        ["HQ"] = 30,
        ["SQ"] = 10,
        ["LQ"] = -1
    };

    /// <summary>
    ///     Soulseek steps are woven into the ladder at their per-tier position, immediately after the catalogue
    ///     steps of the same quality. That means the ladder is now monotonic across the board - including the
    ///     Soulseek steps - so no engine needs an exemption from the check.
    /// </summary>

    private static DeezSpoTagSettings AutoSettings() => new()
    {
        Service = "auto",
        QobuzQuality = "6",
        TidalQuality = "LOSSLESS",
        MaxBitrate = 3
    };

    private static DownloadEngineOrderSettings EveryQualityEnabled()
    {
        var order = DownloadEngineOrderSettings.CreateDefault();
        order.Enabled = true;
        foreach (var engine in order.Engines)
        {
            engine.Enabled = true;
            foreach (var quality in engine.Qualities)
            {
                quality.Enabled = true;
            }
        }

        return order;
    }

    [Fact]
    public void CanonicalAutoLadder_NeverStepsBackUpInQuality()
    {
        AssertMonotonic(ResolveLadder(AutoSettings(), targetQuality: null));
    }

    [Fact]
    public void CustomEngineLadder_NeverStepsBackUpInQuality()
    {
        var settings = AutoSettings();
        settings.Service = DownloadSourceCatalog.Custom;
        settings.DownloadEngineOrder = EveryQualityEnabled();

        AssertMonotonic(ResolveLadder(settings, targetQuality: null));
    }

    [Fact]
    public void TargetQualityLadders_NeverStepBackUpInQuality()
    {
        var settings = AutoSettings();
        foreach (var target in new[] { "27", "7", "6", "5", "3", "1" })
        {
            AssertMonotonic(ResolveLadder(settings, target));
        }
    }

    /// <summary>
    ///     A ladder is a descending preference list, so each step must demand a tier no better than the
    ///     step before it. Any increase is an inversion: the plan would prefer the worse file.
    /// </summary>
    private static void AssertMonotonic(IReadOnlyList<string> encodedLadder)
    {
        Assert.NotEmpty(encodedLadder);

        var tiers = new List<(string Encoded, string Engine, string Quality, int Tier)>(encodedLadder.Count);
        foreach (var encoded in encodedLadder)
        {
            var step = DownloadSourceOrder.DecodeAutoSource(encoded);
            Assert.True(
                RequiredTier.TryGetValue(step.Quality ?? string.Empty, out var tier),
                $"Ladder step '{encoded}' has no entry in RequiredTier, so the quality order cannot be verified for it.");

            tiers.Add((encoded, step.Source, step.Quality ?? string.Empty, tier));
        }

        for (var index = 1; index < tiers.Count; index++)
        {
            var previous = tiers[index - 1];
            var current = tiers[index];

            Assert.True(
                current.Tier <= previous.Tier,
                $"Quality order inversion: step {index} '{current.Encoded}' requires tier {current.Tier}, "
                + $"which is better than step {index - 1} '{previous.Encoded}' at tier {previous.Tier}. "
                + "The fallback ladder must never step back up in quality, otherwise the plan prefers the "
                + "worse file and satisfies the download before reaching the better step.");
        }
    }

    /// <summary>
    ///     The guard is what actually stops a step from being satisfied by a worse file. Every quality code
    ///     on the ladder must reject a file from the tier directly below it.
    /// </summary>
    [Theory]
    [InlineData("qobuz", "27")]
    [InlineData("qobuz", "7")]
    [InlineData("qobuz", "6")]
    [InlineData("qobuz", "5")]
    [InlineData("tidal", "HI_RES_LOSSLESS")]
    [InlineData("tidal", "HI_RES")]
    [InlineData("tidal", "LOSSLESS")]
    [InlineData("tidal", "HIGH")]
    [InlineData("tidal", "LOW")]
    [InlineData("deezer", "9")]
    [InlineData("deezer", "3")]
    [InlineData("deezer", "1")]
    [InlineData("apple", "ALAC")]
    [InlineData("apple", "AAC")]
    [InlineData("amazon", "ULTRA_HD_FLAC")]
    [InlineData("amazon", "HD_FLAC")]
    [InlineData("amazon", "OPUS")]
    [InlineData("soulseek", "FLAC_HI_RES_LOSSLESS")]
    [InlineData("soulseek", "FLAC_HI_RES")]
    [InlineData("soulseek", "FLAC")]
    [InlineData("soundcloud", "HQ")]
    [InlineData("soundcloud", "SQ")]
    [InlineData("soundcloud", "LQ")]
    public void Guard_RejectsFileOneTierBelowTheStep(string engine, string quality)
    {
        var required = RequiredTier[quality];
        var (label, bits, rate, kbps, lossless) = BuildFileOneTierBelow(required);

        Assert.False(
            IsDeliveredQualityAccepted(engine, quality, label, bits, rate, kbps, lossless),
            $"Step {engine}|{quality} requires tier {required} but accepted a tier-{required - 10} file "
            + $"({label}). The step can be satisfied by a worse file than the ladder promises.");
    }

    private static (string Label, int Bits, int Rate, int Kbps, bool Lossless) BuildFileOneTierBelow(int requiredTier)
    {
        if (requiredTier >= 60)
        {
            // Max Hi-Res step must not accept 24-bit/96kHz.
            return ("Hi-Res (24-bit/96kHz)", 24, 96000, 0, true);
        }

        if (requiredTier >= 50)
        {
            // Hi-Res step must not accept CD lossless.
            return ("CD Lossless (16-bit/44.1kHz)", 16, 44100, 0, true);
        }

        if (requiredTier >= 40)
        {
            // Lossless step must not accept lossy audio.
            return ("MP3 320 kbps", 0, 44100, 320, false);
        }

        if (requiredTier >= 30)
        {
            // High lossy step must not accept a low lossy file.
            return ("MP3 96 kbps", 0, 44100, 96, false);
        }

        if (requiredTier >= 20)
        {
            // Low lossy step must not accept a sub-128kbps file.
            return ("MP3 64 kbps", 0, 44100, 64, false);
        }

        // The bottom step still must not accept silence-free nothing; a 32kbps file is below every tier.
        return ("MP3 32 kbps", 0, 22050, 32, false);
    }

    private static List<string> ResolveLadder(DeezSpoTagSettings settings, string? targetQuality)
        => DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: true,
            targetQuality: targetQuality);

    private static bool IsDeliveredQualityAccepted(
        string engine,
        string requestedQuality,
        string label,
        int bitsPerSample,
        int sampleRate,
        int bitrateKbps,
        bool isLossless)
    {
        var assembly = typeof(QualityCatalog).Assembly;
        var guardType = assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.DeliveredAudioQualityGuard",
            throwOnError: true)!;
        var actualType = assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.ActualAudioQuality",
            throwOnError: true)!;
        var actual = Activator.CreateInstance(
            actualType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { label, bitsPerSample, sampleRate, bitrateKbps, isLossless },
            culture: null)!;
        var method = guardType.GetMethod(
            "IsDeliveredQualityAccepted",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, new object[] { engine, requestedQuality, actual })!;
    }
}
