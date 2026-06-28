using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentFallbackParityTests
{
    [Fact]
    public void ResolveFallbackPlanSources_UsesRequestedTargetQuality_ForAutoService()
    {
        var settings = CreateAutoSettings();

        var resolved = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            new List<string> { "qobuz|6", "tidal|LOSSLESS", "apple|ALAC", "deezer|3" },
            "qobuz",
            "3",
            strict: false,
            includeDeezer: true);

        Assert.Equal("deezer|3", resolved[0]);
        Assert.Contains("deezer|3", resolved);
        Assert.Contains("deezer|1", resolved);
        Assert.DoesNotContain("qobuz|6", resolved);
    }

    [Fact]
    public void ResolveFallbackPlanSources_PreservesCrossEngineOrder_WhenAvailabilityIsKnown()
    {
        var settings = CreateAutoSettings();
        var resolved = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            new List<string> { "qobuz|6", "tidal|LOSSLESS", "apple|ALAC", "deezer|3" },
            "qobuz",
            requestedQuality: null,
            strict: false,
            includeDeezer: true);

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

    [Fact]
    public void PrioritizeFallbackSourcesByHealth_KeepsCanonicalOrder_ForAutoService()
    {
        var settings = CreateAutoSettings();
        var tracker = new DownloadApiHealthTracker();
        tracker.ReportSuccess("qobuz");
        var sources = new List<string>
        {
            "qobuz|27",
            "tidal|HI_RES_LOSSLESS",
            "apple|ALAC",
            "qobuz|7",
            "qobuz|6"
        };

        var resolved = InvokePrioritizeFallbackSourcesByHealth(
            sources,
            settings,
            allowCrossEngineFallback: true,
            engine: "qobuz",
            tracker);

        Assert.Equal(sources, resolved);
    }

    [Fact]
    public void ResolveVisibleQueueEngine_UsesCustomOrderBeforeRecommendationSeedSource()
    {
        var settings = CreateCustomQobuzOnlySettings();
        var intent = new DownloadIntent
        {
            PreferredEngine = "auto",
            SourceService = "deezer",
            SourceUrl = "https://www.deezer.com/track/123",
            DeezerId = "123",
            Title = "Seed Track",
            Artist = "Seed Artist"
        };

        var engine = InvokeResolveVisibleQueueEngine(intent, settings, isPodcastIntent: false);

        Assert.Equal("qobuz", engine);
    }

    [Fact]
    public void ResolvePreferredQuality_UsesFirstEnabledCustomQuality()
    {
        var settings = CreateCustomQobuzOnlySettings();

        var quality = InvokeResolvePreferredQuality(settings, "qobuz");

        Assert.Equal("7", quality);
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

    private static DeezSpoTagSettings CreateCustomQobuzOnlySettings()
    {
        var settings = CreateAutoSettings();
        settings.QobuzQuality = "27";
        settings.DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault();
        settings.DownloadEngineOrder.Enabled = true;

        var qobuz = settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "qobuz");
        foreach (var quality in qobuz.Qualities)
        {
            quality.Enabled = quality.Quality == "7" || quality.Quality == "6";
        }

        foreach (var engine in settings.DownloadEngineOrder.Engines.Where(engine => engine.Engine != "qobuz"))
        {
            engine.Enabled = false;
        }

        return settings;
    }

    private static void InvokeNormalizeEnqueueSettings(DeezSpoTagSettings settings)
    {
        var method = typeof(DownloadIntentService).GetMethod(
            "NormalizeEnqueueSettings",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(null, new object[] { settings });
    }

    private static List<string> InvokePrioritizeFallbackSourcesByHealth(
        List<string> sources,
        DeezSpoTagSettings settings,
        bool allowCrossEngineFallback,
        string engine,
        IDownloadApiHealthTracker tracker)
    {
        var method = typeof(DownloadIntentService).GetMethod(
            "PrioritizeFallbackSourcesByHealth",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { sources, settings, allowCrossEngineFallback, engine, tracker });
        Assert.NotNull(result);
        return Assert.IsAssignableFrom<List<string>>(result);
    }

    private static string InvokeResolveVisibleQueueEngine(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        bool isPodcastIntent)
    {
        var method = typeof(DownloadIntentService).GetMethod(
            "ResolveVisibleQueueEngine",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { intent, settings, isPodcastIntent });
        return Assert.IsType<string>(result);
    }

    private static string? InvokeResolvePreferredQuality(DeezSpoTagSettings settings, string engine)
    {
        var method = typeof(DownloadIntentService).GetMethod(
            "ResolvePreferredQuality",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { settings, engine });
        return Assert.IsType<string>(result);
    }
}
