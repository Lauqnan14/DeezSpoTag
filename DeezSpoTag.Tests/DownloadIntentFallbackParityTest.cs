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
using DeezSpoTag.Services.Download.Shared;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedPlan_ProviderSessionFailureContinuesToNextEnabledService(bool aggregateFailure)
    {
        var root = Path.Join(Path.GetTempPath(), "fallback-pre-resolution-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = (DownloadIntentService)RuntimeHelpers.GetUninitializedObject(typeof(DownloadIntentService));
            SetPrivateField(service, "_settingsService", new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance));
            SetPrivateField(service, "_trackIdentityResolver", new FailingAmazonIdentityResolver(aggregateFailure));
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
            Assert.NotEmpty(result.FallbackHistory!);
            Assert.Equal("step-0", result.FallbackHistory![0].StepId);
            Assert.Contains("amazon", result.FallbackHistory[0].Detail);
            Assert.Contains("ULTRA_HD_FLAC", result.FallbackHistory[0].Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavedPlan_EnforcesSnapshotEngineAndQualityBeforeResolution(bool engineEnabled, bool qualityEnabled)
    {
        var root = Path.Join(Path.GetTempPath(), "fallback-disabled-source-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var settings = new DeezSpoTagSettings { Service = "custom" };
            settings.DownloadEngineOrder.Enabled = true;
            var amazon = settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "amazon");
            amazon.Enabled = engineEnabled;
            amazon.Qualities.Single(quality => quality.Quality == "OPUS").Enabled = qualityEnabled;
            var payload = new JsonObject
            {
                ["AutoIndex"] = 0,
                ["SourceSettingsSnapshot"] = System.Text.Json.JsonSerializer.SerializeToNode(QueueSourceSettingsSnapshot.Capture(settings))
            };
            var resolver = new CountingFailingIdentityResolver();
            var service = CreateService(resolver);
            var plan = new List<FallbackPlanStep>
            {
                new("step-0", "amazon", "OPUS", [], "mapped_url"),
                new("step-1", "apple", "ALAC", [], "mapped_url")
            };

            var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem(), CreateUnresolvedIntent(), payload, plan, CancellationToken.None])!;

            Assert.Equal(engineEnabled && qualityEnabled ? 2 : 1, resolver.CallCount);
            Assert.Equal(plan, result.FallbackPlan);
            Assert.Equal(2, result.FallbackHistory!.Count);
            if (!engineEnabled || !qualityEnabled)
                Assert.Contains(result.FallbackHistory, attempt => attempt.StepId == "step-0"
                    && attempt.Status == "skipped" && attempt.ErrorClass == "source_disabled");
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
            Assert.Equal(2, result.FallbackHistory!.Count);
            Assert.All(result.FallbackHistory, attempt => Assert.Contains("Provider unavailable", attempt.Detail));
            var persisted = new JsonObject();
            QueuePreResolutionPayload.ApplyHistory(persisted, result);
            Assert.Equal(2, persisted["FallbackHistory"]!.AsArray().Count);
            Assert.Equal(plan, result.FallbackPlan);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Mary J. Blige", "Hate It Or Love It (Remix)", "FR8GV1829050", "deezer", "https://www.deezer.com/track/123", 236000)]
    [InlineData("Anne-Marie", "Cry", "GBAHS1800090", "apple", "https://music.apple.com/us/album/cry/123?i=456", 271000)]
    [InlineData("Anne-Marie", "Perfect To Me", "GBAHS1800795", "deezer", "https://www.deezer.com/track/789", 288000)]
    public async Task SavedPlan_UnresolvedSourceCannotBecomeApprovedQobuzIdentity(
        string artist, string title, string isrc, string source, string url, int duration)
    {
        var root = Path.Join(Path.GetTempPath(), "identity-handoff-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var resolver = new CountingFailingIdentityResolver();
            var service = CreateService(resolver);
            var intent = new DownloadIntent { Artist = artist, Title = title, Isrc = isrc, SourceService = source,
                SourceUrl = url, DurationMs = duration, WatchlistOrigin = "playlist" };
            var plan = new List<FallbackPlanStep> { new("step-0", "qobuz", "7", [], "mapped_url") };
            var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem(), intent, new JsonObject(), plan, CancellationToken.None])!;
            Assert.NotNull(result.Error);
            Assert.Null(result.SourceUrl);
            Assert.True(string.IsNullOrWhiteSpace(result.QobuzId));
            Assert.Equal(1, resolver.CallCount);
            Assert.Single(result.FallbackHistory!);
            Assert.Contains("Provider unavailable", result.FallbackHistory![0].Detail);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolutionHistorySurvivesExistingPayloadApplication(bool failed)
    {
        var previous = new FallbackAttempt("step-0", "failed", "provider_timeout", "previous request timed out");
        var current = new FallbackAttempt("step-1", "skipped", "identity_unresolved", "qobuz quality=7: missing identity");
        var payload = QueuePreResolutionPayload.ParseOrEmpty(System.Text.Json.JsonSerializer.Serialize(new { FallbackHistory = new[] { previous } }));
        var result = new QueuePreResolutionPayload.ResolutionResult("qobuz", "https://play.qobuz.com/track/74444082", "7", 1,
            null, failed ? "identity_unresolved" : null, FallbackHistory: new[] { current });
        QueuePreResolutionPayload.ApplyHistory(payload, result);
        if (failed) QueuePreResolutionPayload.ApplyFailed(payload, result.Error!, DateTimeOffset.UtcNow);
        else QueuePreResolutionPayload.ApplyResolved(payload, result, DateTimeOffset.UtcNow);
        QueuePreResolutionPayload.ApplyHistory(payload, result);
        Assert.Equal(2, payload["FallbackHistory"]!.AsArray().Count);
    }

    [Fact]
    public void WatchlistEnqueueRetainsPendingTargetWithoutRequiringAnApprovedIdentity()
    {
        var type = typeof(DownloadIntentService).GetNestedType("EnqueueRoutingState", BindingFlags.NonPublic)!;
        var routing = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object?[] { "qobuz", false, false, new List<string>(), "qobuz", "7", null, false }, null)!;
        var method = typeof(DownloadIntentService).GetMethod("BuildPendingEnqueueTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        var target = method.Invoke(null, new object[]
        {
            new DownloadIntent { SourceUrl = "https://www.deezer.com/track/789", WatchlistOrigin = "playlist", Isrc = "GBAHS1800795" },
            routing, new DeezSpoTagSettings { Service = "qobuz" }
        })!;
        Assert.Equal("qobuz", target.GetType().GetProperty("Engine")!.GetValue(target));
        var resolution = ((string Engine, string? SourceUrl, string Message, string MappingSource))target.GetType().GetProperty("Resolution")!.GetValue(target)!;
        Assert.Null(resolution.SourceUrl);
        Assert.Equal("queue-pending", resolution.MappingSource);
    }

    [Theory]
    [InlineData("tidal-timeout", "provider_timeout")]
    [InlineData("tidal-error", "catalog_lookup_failed")]
    [InlineData("duration_mismatch", "identity_unresolved")]
    public async Task SavedPlan_PreservesCentralResolverRejectionReason(string reason, string expectedClass)
    {
        var root = Path.Join(Path.GetTempPath(), "central-rejection-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = CreateService(new RejectingIdentityResolver(reason));
            var intent = new DownloadIntent { SourceService = "spotify", SourceUrl = "https://open.spotify.com/track/source",
                Title = "Big Boy", Artist = "The Jackson Five", DurationMs = 176000 };
            var plan = new List<FallbackPlanStep> { new("step-0", "tidal", "LOSSLESS", [], "mapped_url") };
            var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem(), intent, new JsonObject(), plan, CancellationToken.None])!;
            Assert.Contains(reason, result.Error);
            var failure = Assert.Single(result.FallbackHistory!);
            Assert.Equal("skipped", failure.Status);
            Assert.Equal(expectedClass, failure.ErrorClass);
            Assert.Contains(reason, failure.Detail);
            Assert.Null(result.SourceUrl);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RejectingIdentityResolver(string reason) : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken ct)
            => Task.FromResult(TrackIdentityResolution.Empty(request) with
            {
                Candidates = new[] { new PlatformIdentityCandidate("tidal", null, null, "tidal-native", false, reason) }
            });
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

    private sealed class FailingAmazonIdentityResolver(bool aggregateFailure) : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken cancellationToken)
            => Task.FromException<TrackIdentityResolution>(aggregateFailure
                ? new AggregateException("catalog lookup failed", new InvalidOperationException("Amazon Music metadata session could not be initialized."))
                : new InvalidOperationException("Amazon Music metadata session could not be initialized."));
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

    /// <summary>
    ///     An inactive Soulseek drops out of a resolved plan without reordering what remains.
    /// </summary>
    [Fact]
    public void AnIneligibleSoulseekIsOmittedWithoutReorderingTheRestOfThePlan()
    {
        var settings = CreateAutoSettings();
        var persisted = new List<string> { "qobuz|6", "tidal|LOSSLESS", "apple|ALAC", "soulseek|FLAC", "deezer|3" };

        var withSoulseek = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings, persisted, "qobuz", requestedQuality: null, strict: false, includeDeezer: true,
            soulseekEligible: true);

        var withoutSoulseek = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings, persisted, "qobuz", requestedQuality: null, strict: false, includeDeezer: true,
            soulseekEligible: false);

        Assert.DoesNotContain(withoutSoulseek, step => step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            withSoulseek.Where(step => !step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase)),
            withoutSoulseek);
    }

    /// <summary>
    ///     An inactive Soulseek contributes no step to an auto-mode plan, and takes nothing else with it.
    /// </summary>
    [Fact]
    public void AnIneligibleSoulseekIsAbsentFromAnAutoModePlan()
    {
        var settings = CreateAutoSettings();

        var eligible = DownloadSourceOrder.ResolveQualityAutoSources(
            settings, includeDeezer: true, targetQuality: null, soulseekEligible: true);
        var ineligible = DownloadSourceOrder.ResolveQualityAutoSources(
            settings, includeDeezer: true, targetQuality: null, soulseekEligible: false);

        Assert.Contains(eligible, step => step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ineligible, step => step.StartsWith("soulseek|", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(ineligible);
    }

    /// <summary>
    ///     An explicitly-selected Soulseek step produces no Soulseek sources when there is no verified login.
    /// </summary>
    [Fact]
    public void APinnedSoulseekStepYieldsNothingWhileInactive()
    {
        // `engine` is honoured only when the service is explicitly set, so force it here: this is the
        // reader who picked Soulseek from the picker, which is the path a pinned step can travel.
        var settings = CreateAutoSettings();
        settings.Service = "soulseek";

        var sources = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            new List<string> { "soulseek|FLAC" },
            "soulseek",
            requestedQuality: "FLAC",
            strict: true,
            includeDeezer: true,
            soulseekEligible: false);

        // The one engine the reader picked contributes nothing, and no substitute appears in its place: a
        // pinned request must never become work on a different engine.
        Assert.Empty(sources);
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
