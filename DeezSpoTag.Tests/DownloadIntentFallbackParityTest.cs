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
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
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
    [Fact]
    public async Task SavedPlanReusesRejectedMatrixAndEmptyLinksCannotEraseTheProviderTimeout()
    {
        var root = Path.Join(Path.GetTempPath(), "identity-rejection-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var resolver = new CountingRejectingIdentityResolver();
            var service = CreateService(resolver);
            var plan = new List<FallbackPlanStep> {
                new("step-0", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
                new("step-1", "amazon", "HD_FLAC", [], "mapped_url") };
            var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem(), CreateUnresolvedIntent(), new JsonObject(), plan, CancellationToken.None])!;
            Assert.Contains("amazon-timeout", result.Error);
            Assert.Equal(1, resolver.CallCount);
            Assert.Null(result.SourceUrl);
            Assert.All(result.FallbackHistory!, attempt => Assert.Equal("provider_timeout", attempt.ErrorClass));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CountingRejectingIdentityResolver : ITrackIdentityResolver
    {
        public int CallCount { get; private set; }
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken token)
        {
            CallCount++;
            return Task.FromResult(TrackIdentityResolution.Empty(request) with {
                Candidates = [new PlatformIdentityCandidate("amazon", null, null, "amazon-native", false, "amazon-timeout")] });
        }
    }

    [Fact]
    public async Task SavedPlanDoesNotRepeatTheSameFailedIdentityMatrixForEveryQuality()
    {
        var root = Path.Join(Path.GetTempPath(), "identity-attempt-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var resolver = new CountingFailingIdentityResolver();
            var service = CreateService(resolver);
            var plan = new List<FallbackPlanStep> {
                new("step-0", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
                new("step-1", "amazon", "HD_FLAC", [], "mapped_url") };
            for (var pass = 1; pass <= 2; pass++)
            {
                var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                    [CreateQueueItem(), CreateUnresolvedIntent(), new JsonObject(), plan, CancellationToken.None])!;
                Assert.NotNull(result.Error);
                Assert.Equal(2, result.FallbackHistory!.Count);
                Assert.All(result.FallbackHistory, attempt => Assert.Contains("Provider unavailable", attempt.Detail));
                Assert.Equal(pass, resolver.CallCount); // Retry is fresh; qualities reuse this attempt's matrix.
                Assert.Null(result.SourceUrl);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SavedPlanLastDisabledStepCannotEraseEarlierProviderFailure()
    {
        var root = Path.Join(Path.GetTempPath(), "identity-root-error-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = CreateService(new CountingFailingIdentityResolver());
            var plan = new List<FallbackPlanStep> {
                new("step-0", "amazon", "HD_FLAC", [], "mapped_url"),
                new("step-1", "deezer", "1", [], "direct_url") };
            var settings = new DeezSpoTagSettings();
            settings.DownloadEngineOrder!.Enabled = true;
            settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "amazon").Enabled = true;
            settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "deezer").Enabled = false;
            var payload = new JsonObject { ["SourceSettingsSnapshot"] =
                System.Text.Json.JsonSerializer.SerializeToNode(QueueSourceSettingsSnapshot.Capture(settings)) };
            var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem(), CreateUnresolvedIntent(), payload, plan, CancellationToken.None])!;
            Assert.Contains("Provider unavailable", result.Error);
            Assert.Equal(2, result.FallbackHistory!.Count);
            Assert.Equal("source_disabled", result.FallbackHistory[1].ErrorClass);
            Assert.Null(result.SourceUrl);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VisibleYouTubePreparationDoesNotMatchBeforeQueueInsertion()
    {
        var root = Path.Join(Path.GetTempPath(), "visible-youtube-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var resolver = new CountingFailingIdentityResolver();
            var service = CreateService(resolver);
            var intent = new DownloadIntent
            {
                SourceService = "ytmusic", SourceUrl = "https://music.youtube.com/watch?v=dQw4w9WgXcQ",
                Title = "Song", Artist = "Artist", DestinationFolderId = 17,
                DeezerId = "untrusted", Cover = "https://example.invalid/video-still.jpg"
            };
            var method = typeof(DownloadIntentService).GetMethod("PrepareEnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Contains(method.GetParameters(), parameter => parameter.Name == "deferRecordingMatch");
            await (Task)method.Invoke(service, [intent, true, CancellationToken.None, null, true])!;
            Assert.Equal(0, resolver.CallCount);
            Assert.Equal("ytmusic", intent.SourceService);
            Assert.Equal("https://music.youtube.com/watch?v=dQw4w9WgXcQ", intent.SourceUrl);
            Assert.True(string.IsNullOrEmpty(intent.DeezerId));
            Assert.True(string.IsNullOrEmpty(intent.Cover));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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

            Assert.Equal(1, resolver.CallCount); // All enabled quality steps share one recording lookup.
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
            Assert.Equal(1, resolver.CallCount);
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

    [Fact]
    public async Task ResolverError_ClassifiesFromSavedEvidenceInsteadOfStampingUnavailable()
    {
        // A queued item whose saved-plan resolution failed with a mapping timeout is a
        // recoverable failure, not a confirmed catalogue miss: the resolver must leave
        // the row failed (the shared evidence rule decides "unavailable"), so the
        // scheduler keeps retrying it.
        var root = Path.Join(Path.GetTempPath(), "resolver-evidence-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = CreateService(new RejectingIdentityResolver("tidal-timeout"));
            var resolver = new DownloadIntentQueuedPayloadResolver(service);
            var item = CreateQueueItem() with
            {
                Engine = "tidal",
                PayloadJson = """
                    {
                      "FallbackPlan": [
                        {"StepId": "step-0", "Engine": "tidal", "Quality": "LOSSLESS", "RequiredInputs": [], "ResolutionStrategy": "mapped_url"}
                      ]
                    }
                    """
            };

            var resolution = await resolver.ResolveAsync(item, CancellationToken.None);

            Assert.Equal("failed", resolution.Item.Status);
            Assert.NotNull(resolution.Error);
            Assert.Contains("tidal-timeout", resolution.Error, StringComparison.Ordinal);
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

    [Fact]
    public async Task SavedPlan_MixedProviderOutcomesStayRecoverableAndKeepEveryCause()
    {
        // Replays the NAS row's six saved outcomes (catalogue 403 / mapping timeout /
        // missing approved URL, two qualities each) through the real saved-plan resolver.
        // None of these classes proves recording absence, so the row must stay recoverable
        // and the joined error must retain every cause instead of the last one only.
        var root = Path.Join(Path.GetTempPath(), "nas-six-outcome-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var resolver = new MixedProviderRejectionIdentityResolver();
            var service = CreateService(resolver);
            var settings = new DeezSpoTagSettings();
            settings.DownloadEngineOrder.Enabled = true;
            settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "qobuz").Enabled = true;
            settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "tidal").Enabled = true;
            settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "amazon").Enabled = true;
            settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "apple").Enabled = false;
            settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "deezer").Enabled = false;
            var payload = new JsonObject
            {
                ["AutoIndex"] = 0,
                ["Quality"] = "7",
                ["SourceSettingsSnapshot"] = System.Text.Json.JsonSerializer.SerializeToNode(QueueSourceSettingsSnapshot.Capture(settings))
            };
            var plan = new List<FallbackPlanStep>
            {
                new("step-0", "qobuz", "7", [], "isrc"),
                new("step-1", "tidal", "HI_RES", [], "isrc"),
                new("step-2", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
                new("step-3", "qobuz", "6", [], "isrc"),
                new("step-4", "tidal", "LOSSLESS", [], "isrc"),
                new("step-5", "amazon", "HD_FLAC", [], "mapped_url")
            };

            var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem() with { Engine = "qobuz" }, CreateUnresolvedIntent(), payload, plan, CancellationToken.None])!;

            Assert.Equal(
                new[]
                {
                    "catalog_lookup_failed", "provider_timeout", "identity_unresolved",
                    "catalog_lookup_failed", "provider_timeout", "identity_unresolved"
                },
                result.FallbackHistory.Select(attempt => attempt.ErrorClass).ToArray());
            Assert.Equal(6, result.FallbackHistory!.Count);
            // A rejected anonymous catalogue request must never be recorded as a
            // signed-session/authentication verdict for the step.
            Assert.DoesNotContain(result.FallbackHistory, attempt => attempt.ErrorClass == "authentication_required");
            // No cause is masked by the last failure (NAS showed only missing_url).
            Assert.Contains("catalog lookup failed", result.Error, StringComparison.Ordinal);
            Assert.Contains("tidal provider timed out", result.Error, StringComparison.Ordinal);
            Assert.Contains("amazon", result.Error, StringComparison.Ordinal);
            // One shared recording lookup for the whole attempt (the identity matrix is
            // recording-level work, independent of the quality steps).
            Assert.Equal(1, resolver.CallCount);
            Assert.Null(result.SourceUrl);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SavedPlan_Catalogue403FailureKeepsRetryEligibilityAndRecovery()
    {
        var root = Path.Join(Path.GetTempPath(), "nas-catalog-403-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = CreateService(new ThrowingCatalogue403IdentityResolver());
            var plan = new List<FallbackPlanStep>
            {
                new("step-0", "qobuz", "7", [], "isrc"),
                new("step-1", "qobuz", "6", [], "isrc")
            };
            var result = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(service,
                [CreateQueueItem(), CreateUnresolvedIntent(), new JsonObject { ["AutoIndex"] = 0 }, plan, CancellationToken.None])!;

            // The anonymous catalogue 403 aggregate classifies as an operational catalogue
            // failure, not as authentication_required, and every step records the cause.
            Assert.Equal(2, result.FallbackHistory!.Count);
            Assert.All(result.FallbackHistory, attempt =>
            {
                Assert.Equal("catalog_lookup_failed", attempt.ErrorClass);
                Assert.Contains("Qobuz catalog lookup failed", attempt.Detail, StringComparison.Ordinal);
                Assert.Contains("403", attempt.Detail, StringComparison.Ordinal);
            });

            // The whole shape stays retry-eligible: the reconstructed failure history is a
            // temporary provider failure, so recovery continues beyond the ordinary attempt
            // budget, and the mixed outcomes do not prove whole-plan unavailability.
            var payload = new JsonObject();
            QueuePreResolutionPayload.ApplyHistory(payload, result);
            var persistedItem = CreateQueueItem() with
            {
                PayloadJson = payload.ToJsonString(),
                Error = result.FallbackHistory[0].Detail
            };
            var retryFailure = (AggregateException)typeof(DeezSpoTagApp)
                .GetMethod("ResolveRetryFailure", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [persistedItem])!;
            Assert.True(FallbackFailureClassifier.IsTemporaryProviderFailure(retryFailure));
            Assert.False(FallbackFailureClassifier.IsPlanUnavailable(plan, result.FallbackHistory));

            // A subsequent attempt whose catalogue lookup succeeds resolves the same
            // saved plan without bypassing the identity safeguards.
            var recoveredService = CreateService(new AcceptingQobuzIdentityResolver());
            SetPrivateField(recoveredService, "_qobuzTrackResolver", CreateValidatedQobuzResolver());
            SetPrivateField(recoveredService, "_activityLog", new NullActivityLogWriter());
            var recovered = await (Task<QueuePreResolutionPayload.ResolutionResult>)GetSavedPlanMethod().Invoke(recoveredService,
                [CreateQueueItem() with { Engine = "qobuz" }, CreateUnresolvedIntent(), new JsonObject { ["AutoIndex"] = 0 }, plan, CancellationToken.None])!;
            Assert.Null(recovered.Error);
            Assert.Equal("qobuz", recovered.Engine);
            Assert.Equal("7", recovered.Quality);
            Assert.Equal(0, recovered.AutoIndex);
            Assert.False(string.IsNullOrWhiteSpace(recovered.SourceUrl));
            Assert.False(string.IsNullOrWhiteSpace(recovered.QobuzId));
        }
        finally { Directory.Delete(root, true); }
    }

    private static QobuzTrackResolver CreateValidatedQobuzResolver()
    {
        var track = new DeezSpoTag.Core.Models.Qobuz.QobuzTrack
        {
            Id = 74444082, Title = "Track", ISRC = "USAAA1234567", Duration = 180,
            Performer = new DeezSpoTag.Core.Models.Qobuz.QobuzArtist { Name = "Artist" },
            Album = new DeezSpoTag.Core.Models.Qobuz.QobuzAlbum { Title = "Album" }
        };
        return new QobuzTrackResolver(new ValidatedQobuzMetadataService(track),
            Microsoft.Extensions.Options.Options.Create(new QobuzApiConfig
            {
                DefaultStore = "us-en",
                PreferredStores = new List<string> { "us-en" }
            }),
            NullLogger<QobuzTrackResolver>.Instance);
    }

    private sealed class ValidatedQobuzMetadataService(DeezSpoTag.Core.Models.Qobuz.QobuzTrack track) : IQobuzMetadataService
    {
        public Task<QobuzTrack?> FindTrackByISRC(string isrc, CancellationToken ct) => Task.FromResult<QobuzTrack?>(track);
        public Task<QobuzAlbum?> FindAlbumByUPC(string upc, CancellationToken ct) => Task.FromResult<QobuzAlbum?>(null);
        public Task<QobuzArtist?> FindArtistByName(string name, CancellationToken ct) => Task.FromResult<QobuzArtist?>(null);
        public Task<List<QobuzTrack>> SearchTracks(string query, CancellationToken ct) => Task.FromResult(new List<QobuzTrack> { track });
        public Task<List<QobuzTrack>> SearchAlbumTracks(string query, CancellationToken ct) => Task.FromResult(new List<QobuzTrack>());
        public Task<List<QobuzTrack>> SearchTracksAutosuggest(string query, string? store, CancellationToken ct) => Task.FromResult(new List<QobuzTrack>());
        public Task<List<QobuzAlbum>> SearchAlbums(string query, CancellationToken ct) => Task.FromResult(new List<QobuzAlbum>());
        public Task<List<QobuzArtist>> SearchArtists(string query, CancellationToken ct) => Task.FromResult(new List<QobuzArtist>());
        public Task<QobuzArtist?> GetArtistDiscography(int artistId, string store, CancellationToken ct) => Task.FromResult<QobuzArtist?>(null);
        public Task<List<QobuzAlbum>> GetArtistAlbums(int artistId, string store, CancellationToken ct) => Task.FromResult(new List<QobuzAlbum>());
        public Task<QobuzTrack?> GetTrack(int trackId, CancellationToken ct) => Task.FromResult<QobuzTrack?>(track);
        public Task<List<QobuzTrack>> GetAlbumPageTracks(string url, CancellationToken ct) => Task.FromResult(new List<QobuzTrack>());
    }

    private sealed class MixedProviderRejectionIdentityResolver : ITrackIdentityResolver
    {
        public int CallCount { get; private set; }

        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken token)
        {
            CallCount++;
            return Task.FromResult(TrackIdentityResolution.Empty(request) with
            {
                Candidates =
                [
                    new PlatformIdentityCandidate("qobuz", null, null, "qobuz-native", false, "qobuz-error"),
                    new PlatformIdentityCandidate("tidal", null, null, "tidal-native", false, "tidal-timeout"),
                    new PlatformIdentityCandidate("amazon", null, null, "amazon-native", false, "amazon-unresolved")
                ]
            });
        }
    }

    private sealed class ThrowingCatalogue403IdentityResolver : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken token)
            => Task.FromException<TrackIdentityResolution>(new AggregateException(
                "Qobuz catalog lookup failed",
                new HttpRequestException("Response status code does not indicate success: 403 (Forbidden).",
                    null, System.Net.HttpStatusCode.Forbidden)));
    }

    private sealed class AcceptingQobuzIdentityResolver : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken token)
            => Task.FromResult(TrackIdentityResolution.Empty(request) with
            {
                Isrc = request.Isrc,
                QobuzUrl = "https://play.qobuz.com/track/74444082",
                Candidates =
                [
                    new PlatformIdentityCandidate("qobuz", "74444082", "https://play.qobuz.com/track/74444082", "qobuz-native", true, null)
                ]
            });
    }

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
