using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DownloadIntentFallbackParityTest
{
    [Fact]
    public async Task SavedPlan_ProviderSessionFailureContinuesToNextEnabledService()
    {
        var root = Path.Join(Path.GetTempPath(), "fallback-pre-resolution-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = (DownloadIntentService)RuntimeHelpers.GetUninitializedObject(typeof(DownloadIntentService));
            SetPrivateField(service, "_settingsService", new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance));
            SetPrivateField(service, "_trackIdentityResolver", new FailingAmazonIdentityResolver());
            SetPrivateField(service, "_logger", NullLogger<DownloadIntentService>.Instance);
            var item = CreateQueueItem();
            var intent = new DownloadIntent
            {
                SourceService = "deezer", SourceUrl = "https://www.deezer.com/track/456",
                DeezerId = "456", Title = "Track", Artist = "Artist", Album = "Album",
                Isrc = "USAAA1234567", Cover = "https://example.invalid/cover.jpg", DurationMs = 180000
            };
            var plan = new List<FallbackPlanStep>
            {
                new("step-0", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
                new("step-1", "deezer", "9", [], "direct_url")
            };
            var method = typeof(DownloadIntentService).GetMethod("ResolveQueuedPayloadFromSavedPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var task = (Task<QueuePreResolutionPayload.ResolutionResult>)method.Invoke(service,
                [item, intent, new JsonObject { ["AutoIndex"] = 0 }, plan, CancellationToken.None])!;
            var result = await task;

            Assert.Null(result.Error);
            Assert.Equal("deezer", result.Engine);
            Assert.Equal(1, result.AutoIndex);
            Assert.Equal("9", result.Quality);
            Assert.Equal(plan, result.FallbackPlan);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavedPlan_CallerCancellationPropagates()
    {
        var root = Path.Join(Path.GetTempPath(), "fallback-pre-resolution-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = CreateService(new CancelingIdentityResolver());
            var method = GetSavedPlanMethod();
            var task = (Task<QueuePreResolutionPayload.ResolutionResult>)method.Invoke(service,
                [CreateQueueItem(), CreateUnresolvedIntent(), new JsonObject { ["AutoIndex"] = 0 },
                    new List<FallbackPlanStep> { new("step-0", "amazon", "ULTRA_HD_FLAC", [], "mapped_url") }, cancellation.Token])!;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavedPlan_WhenEveryProviderFailsReturnsUnavailableAfterVisitingEveryStep()
    {
        var root = Path.Join(Path.GetTempPath(), "fallback-pre-resolution-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var resolver = new CountingFailingIdentityResolver();
            var service = CreateService(resolver);
            var plan = new List<FallbackPlanStep>
            {
                new("step-0", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
                new("step-1", "apple", "ALAC", [], "mapped_url")
            };
            var task = (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem(), CreateUnresolvedIntent(), new JsonObject { ["AutoIndex"] = 0 }, plan, CancellationToken.None])!;

            var result = await task;

            Assert.NotNull(result.Error);
            Assert.Equal(2, resolver.CallCount);
            Assert.Equal(plan, result.FallbackPlan);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DownloadIntentService CreateService(ITrackIdentityResolver resolver)
    {
        var service = (DownloadIntentService)RuntimeHelpers.GetUninitializedObject(typeof(DownloadIntentService));
        SetPrivateField(service, "_settingsService", new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance));
        SetPrivateField(service, "_trackIdentityResolver", resolver);
        SetPrivateField(service, "_logger", NullLogger<DownloadIntentService>.Instance);
        return service;
    }

    private static MethodInfo GetSavedPlanMethod()
        => typeof(DownloadIntentService).GetMethod("ResolveQueuedPayloadFromSavedPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static DownloadIntent CreateUnresolvedIntent() => new()
    {
        SourceService = "spotify", SourceUrl = string.Empty, Title = "Track", Artist = "Artist",
        Album = "Album", Isrc = "USAAA1234567", DurationMs = 180000
    };

    private static DownloadQueueItem CreateQueueItem() => new(
        Id: 0, QueueUuid: "saved-plan-test", Engine: "amazon", ArtistName: "Artist",
        TrackTitle: "Track", Isrc: "USAAA1234567", DeezerTrackId: "456",
        DeezerAlbumId: null, DeezerArtistId: null, SpotifyTrackId: null,
        SpotifyAlbumId: null, SpotifyArtistId: null, AppleTrackId: null,
        AppleAlbumId: null, AppleArtistId: null, DurationMs: 180000,
        DestinationFolderId: null, QualityRank: null, QueueOrder: null,
        Status: "queued", PayloadJson: null, Progress: 0, Downloaded: 0,
        Failed: 0, Error: null, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private static void SetPrivateField(object instance, string name, object value)
        => typeof(DownloadIntentService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class FailingAmazonIdentityResolver : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken cancellationToken)
            => Task.FromException<TrackIdentityResolution>(new InvalidOperationException("Amazon Music metadata session could not be initialized."));
    }

    private sealed class CancelingIdentityResolver : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken cancellationToken)
            => Task.FromCanceled<TrackIdentityResolution>(cancellationToken);
    }

    private sealed class CountingFailingIdentityResolver : ITrackIdentityResolver
    {
        public int CallCount { get; private set; }

        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromException<TrackIdentityResolution>(new HttpRequestException("Provider unavailable"));
        }
    }

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
