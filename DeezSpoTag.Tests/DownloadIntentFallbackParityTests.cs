using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentFallbackParityTests
{
    [Fact]
    public void BuildFallbackPlanSources_UsesRequestedTargetQuality_ForAutoService()
    {
        var settings = CreateAutoSettings();

        var resolved = InvokeBuildFallbackPlanSources(
            new List<string> { "qobuz|6", "tidal|LOSSLESS", "apple|ALAC", "deezer|3" },
            settings,
            "qobuz",
            "3",
            availability: null);

        Assert.Equal("deezer|3", resolved[0]);
        Assert.Contains("deezer|3", resolved);
        Assert.Contains("deezer|1", resolved);
        Assert.DoesNotContain("qobuz|6", resolved);
    }

    [Fact]
    public void BuildFallbackPlanSources_PreservesCrossEngineOrder_WhenAvailabilityIsKnown()
    {
        var settings = CreateAutoSettings();
        var availability = new SongLinkResult
        {
            QobuzUrl = "https://play.qobuz.com/track/1",
            TidalUrl = "https://listen.tidal.com/track/1"
        };

        var resolved = InvokeBuildFallbackPlanSources(
            new List<string> { "qobuz|6", "tidal|LOSSLESS", "apple|ALAC", "deezer|3" },
            settings,
            "qobuz",
            requestedQuality: null,
            availability);

        Assert.Contains("qobuz|6", resolved);
        Assert.Contains("tidal|LOSSLESS", resolved);
        Assert.Contains("apple|ALAC", resolved);
        Assert.Contains("deezer|3", resolved);
    }

    [Fact]
    public void NormalizeEnqueueSettings_DoesNotForceFallbackBitrate_ForAutoService()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            FallbackBitrate = false
        };

        InvokeNormalizeEnqueueSettings(settings);

        Assert.False(settings.FallbackBitrate);
    }

    private static DeezSpoTagSettings CreateAutoSettings()
    {
        return new DeezSpoTagSettings
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
    }

    private static List<string> InvokeBuildFallbackPlanSources(
        IReadOnlyList<string> autoSources,
        DeezSpoTagSettings settings,
        string engine,
        string? requestedQuality,
        SongLinkResult? availability)
    {
        var method = typeof(DownloadIntentService).GetMethod(
            "BuildFallbackPlanSources",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { autoSources, settings, engine, requestedQuality, availability });
        Assert.NotNull(result);
        return Assert.IsAssignableFrom<List<string>>(result);
    }

    private static void InvokeNormalizeEnqueueSettings(DeezSpoTagSettings settings)
    {
        var method = typeof(DownloadIntentService).GetMethod(
            "NormalizeEnqueueSettings",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(null, new object[] { settings });
    }
}
