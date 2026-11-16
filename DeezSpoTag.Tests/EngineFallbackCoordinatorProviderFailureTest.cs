using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class EngineFallbackCoordinatorProviderFailureTest : IDisposable
{
    [Theory]
    [InlineData(" track_unavailable ", FallbackFailureClassifier.Unavailable)]
    [InlineData(" quality_unavailable ", FallbackFailureClassifier.QualityBelowRequested)]
    public void ProviderContract_NormalizationSurvivesFailureClassification(string code, string expected)
    {
        var body = JsonSerializer.Serialize(new { origin = " Provider ", code, retryable = false, retry_mode = " NONE " });
        var failure = new HttpRequestException("Provider HTTP 404", null, HttpStatusCode.NotFound);
        FallbackFailureClassifier.AttachProviderErrorContract(failure, body);
        Assert.Equal(expected, FallbackFailureClassifier.Classify(failure));
    }

    private readonly string _root = Path.Join(Path.GetTempPath(), "fallback-provider-" + Path.GetRandomFileName());
    private readonly TestConfigRootScope _configScope;
    private readonly DownloadQueueRepository _repository;
    private readonly DeezSpoTagSettingsService _settings;

    public EngineFallbackCoordinatorProviderFailureTest()
    {
        Directory.CreateDirectory(_root);
        _configScope = new TestConfigRootScope(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_root, "queue.db")}",
            ["DataDirectory"] = _root
        }).Build();
        _repository = new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance);
        _settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    [Fact]
    public async Task TemporaryProviderFailure_DoesNotBecomePermanentWhenNormalRetriesAreExhausted()
    {
        var settings = _settings.LoadSettings(); settings.MaxRetries = 1; _settings.SaveSettings(settings);
        var payload = BuildPayload(); payload.FallbackPlan = [new("step-0", "qobuz", "7", [], "isrc")]; payload.Quality = "7";
        await EnqueueAsync(payload);
        using var services = new ServiceCollection().BuildServiceProvider();
        var listener = new DeezSpoTagListener();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), listener,
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());
        var context = new EngineAudioPostDownloadHelper.FailureHandlingContext<QobuzQueueItem>(
            _repository, new NullActivityLogWriter(), listener, retry, services,
            (_, _, _, _) => Task.FromResult(false), p => p.ToQueuePayload(), "qobuz", NullLogger.Instance);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await EngineAudioPostDownloadHelper.HandleFailureAsync(new HttpRequestException("Provider HTTP 503", null, HttpStatusCode.ServiceUnavailable),
                payload.Id, payload, context, CancellationToken.None);
            Assert.Equal("retry_waiting", (await _repository.GetByUuidAsync(payload.Id))!.Status);
            await using (var db = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "UPDATE download_task SET retry_next_at = '2000-01-01T00:00:00Z' WHERE queue_uuid = $uuid";
                command.Parameters.AddWithValue("$uuid", payload.Id);
                await command.ExecuteNonQueryAsync();
            }
            Assert.True(await retry.RunRetrySweepAsync());
            Assert.Equal("queued", (await _repository.GetByUuidAsync(payload.Id))!.Status);
        }
    }

    [Fact]
    public async Task NasSixOutcomeFailureKeepsProviderRecoveryBeyondAttemptExhaustion()
    {
        // Mirrors the NAS stranded row: six skipped outcomes (catalogue 403 / mapping
        // timeout / missing URL, two qualities each) with no step proving absence. The
        // reconstructed failure history is a temporary provider failure, so provider
        // recovery must keep scheduling retries even after the ordinary attempt budget
        // is exhausted, and the retry sweep must restart the saved plan from step-0.
        var payload = BuildPayload();
        payload.Engine = "amazon";
        payload.SourceService = "amazon";
        payload.Quality = "HD_FLAC";
        payload.AutoIndex = 5;
        payload.FallbackPlan =
        [
            new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc"),
            new("step-2", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
            new("step-3", "qobuz", "6", [], "isrc"),
            new("step-4", "tidal", "LOSSLESS", [], "isrc"),
            new("step-5", "amazon", "HD_FLAC", [], "mapped_url")
        ];
        payload.FallbackHistory =
        [
            new("step-0", "skipped", "catalog_lookup_failed", "qobuz quality=7: qobuz catalog lookup failed: 403 (Forbidden)"),
            new("step-1", "skipped", "provider_timeout", "tidal quality=HI_RES: Unable to resolve mapping for requested engine. tidal provider timed out: tidal-timeout"),
            new("step-2", "skipped", "identity_unresolved", "amazon quality=ULTRA_HD_FLAC: missing_url"),
            new("step-3", "skipped", "catalog_lookup_failed", "qobuz quality=6: qobuz catalog lookup failed: 403 (Forbidden)"),
            new("step-4", "skipped", "provider_timeout", "tidal quality=LOSSLESS: Unable to resolve mapping for requested engine. tidal provider timed out: tidal-timeout"),
            new("step-5", "skipped", "identity_unresolved", "amazon quality=HD_FLAC: missing_url")
        ];
        await EnqueueAsync(payload);
        await _repository.UpdateStatusAsync(payload.Id, "failed", "amazon quality=ULTRA_HD_FLAC: missing_url");

        var item = await _repository.GetByUuidAsync(payload.Id);
        Assert.NotNull(item);
        var retryFailure = (AggregateException)typeof(DeezSpoTagApp)
            .GetMethod("ResolveRetryFailure", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [item])!;
        Assert.True(FallbackFailureClassifier.IsTemporaryProviderFailure(retryFailure));
        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(payload.FallbackPlan, payload.FallbackHistory));

        // Exhaust the ordinary attempt budget: without provider recovery the item stays
        // terminal instead of silently looping.
        await using (var db = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "UPDATE download_task SET retry_attempt_count = 99 WHERE queue_uuid = $uuid";
            command.Parameters.AddWithValue("$uuid", payload.Id);
            await command.ExecuteNonQueryAsync();
        }
        Assert.False(await _repository.ScheduleRetryAsync(payload.Id, "amazon", item.Error ?? "missing_url", 3));

        // Provider recovery continues past the budget...
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(),
            new DeezSpoTagListener(), NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());
        Assert.True(await retry.ScheduleRetryAsync(payload.Id, "amazon", item.Error ?? "missing_url", default,
            minimumDelay: null, continueProviderRecovery: true));
        Assert.Equal("retry_waiting", (await _repository.GetByUuidAsync(payload.Id))!.Status);

        // ...and the due sweep requeues the same UUID and restarts the saved plan from
        // its first enabled step, preserving the saved plan rather than current settings.
        await using (var db = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "UPDATE download_task SET retry_next_at = '2000-01-01T00:00:00Z' WHERE queue_uuid = $uuid";
            command.Parameters.AddWithValue("$uuid", payload.Id);
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(await retry.RunRetrySweepAsync());
        var requeued = await _repository.GetByUuidAsync(payload.Id);
        Assert.Equal("queued", requeued!.Status);
        Assert.Equal("qobuz", requeued.Engine);
        var restarted = System.Text.Json.JsonSerializer.Deserialize<QobuzQueueItem>(requeued.PayloadJson!);
        Assert.NotNull(restarted);
        Assert.Equal(0, restarted!.AutoIndex);
        Assert.Equal("qobuz", restarted.Engine);
        Assert.Empty(restarted.FallbackHistory);
    }

    [Fact]
    public async Task ExhaustedDownload_KeepsEveryAttemptInActivityAndDockerLogs()
    {
        var payload = BuildPayload();
        payload.AutoIndex = 2;
        payload.FallbackHistory =
        [
            new("step-0", "failed", "provider_transient", "first diagnostic"),
            new("step-1", "skipped", "authentication_required", "second diagnostic")
        ];
        await EnqueueAsync(payload);
        var capture = new CapturingDownloadLog();
        var catalog = new AppleMusicCatalogService(new NotFoundHttpClientFactory(), _settings,
            NullLogger<AppleMusicCatalogService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        var search = new EngineFallbackSearchService(catalog, NullLogger<EngineFallbackSearchService>.Instance,
            amazonFallbackTrackResolver: new ThrowingAmazonResolver(false));
        var coordinator = new EngineFallbackCoordinator(_repository, _settings,
            new DeezerIsrcResolver(null!, NullLogger<DeezerIsrcResolver>.Instance), search, capture, logger: capture);
        Assert.False(await coordinator.TryAdvanceAsync(payload.Id, "deezer", payload, CancellationToken.None));
        foreach (var detail in new[] { "first diagnostic", "second diagnostic" })
        {
            Assert.Contains(capture.Activity, line => line.Contains(detail, StringComparison.Ordinal));
            Assert.Contains(capture.Docker, line => line.Contains(detail, StringComparison.Ordinal));
        }
    }

    private sealed class CapturingDownloadLog : IActivityLogWriter, Microsoft.Extensions.Logging.ILogger<EngineFallbackCoordinator>
    {
        public List<string> Activity { get; } = [];
        public List<string> Docker { get; } = [];
        public void Info(string message) => Activity.Add(message);
        public void Warn(string message) => Activity.Add(message);
        public void Error(string message) => Activity.Add(message);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Docker.Add(formatter(state, exception));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task PreDownloadResolution_UsesSavedOutcomesAndPreservesProviderBackoff(bool rateLimited, bool includeHistory)
    {
        var settings = _settings.LoadSettings(); settings.MaxRetries = 3; _settings.SaveSettings(settings);
        var payload = BuildPayload(); payload.ResolutionStatus = "pending";
        payload.FallbackHistory = payload.FallbackPlan.ConvertAll(step => new FallbackAttempt(step.StepId, "skipped", "unavailable", "Confirmed unavailable"));
        if (rateLimited) payload.FallbackHistory[0] = new("step-0", "skipped", "provider_rate_limited", "HTTP 429 retry_after=600");
        if (!includeHistory) payload.FallbackHistory.Clear();
        await EnqueueAsync(payload);
        using var services = new ServiceCollection().AddSingleton<IQueuedDownloadPayloadResolver>(
            new RecordedPayloadResolver(JsonSerializer.Serialize(payload), rateLimited ? (includeHistory ? "Qobuz rate-limited the request." : "Qobuz HTTP 429") : "All selected sources had no copy."))
            .BuildServiceProvider();
        var listener = new DeezSpoTagListener();
        var cancellation = new DownloadCancellationRegistry();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), listener,
            NullLogger<DownloadRetryScheduler>.Instance, cancellation);
        var app = new DeezSpoTagApp(NullLogger<DeezSpoTagApp>.Instance,
            new DeezSpoTagApp.Dependencies(_settings, listener, retry, _repository, cancellation, null!), services);
        var before = DateTimeOffset.UtcNow;
        await app.ProcessQueueItemAsync((await _repository.GetByUuidAsync(payload.Id))!);
        Assert.Equal(rateLimited ? "retry_waiting" : "unavailable", (await _repository.GetByUuidAsync(payload.Id))!.Status);
        if (rateLimited)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}");
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT retry_next_at FROM download_task WHERE queue_uuid = $uuid";
            command.Parameters.AddWithValue("$uuid", payload.Id);
            Assert.True(DateTimeOffset.Parse((string)(await command.ExecuteScalarAsync())!) >= before.AddSeconds(includeHistory ? 599 : 899));
        }
    }

    private sealed class RecordedPayloadResolver(string payload, string error) : IQueuedDownloadPayloadResolver
    {
        public Task<QueuedDownloadPayloadResolution> ResolveAsync(DownloadQueueItem item, CancellationToken token)
            => Task.FromResult(new QueuedDownloadPayloadResolution(item with { PayloadJson = payload, Status = "unavailable" }, error));
    }

    [Theory]
    [InlineData(false, true, "unavailable", true)]
    [InlineData(true, true, "retry_waiting", true)]
    [InlineData(false, false, "retry_waiting", true)]
    [InlineData(false, false, "retry_waiting", false)]
    public async Task FinalStatus_RequiresConfirmedOutcomesForEverySavedEngineQuality(bool transient, bool allStepsKnown, string expected, bool savedSelection)
    {
        var settings = _settings.LoadSettings(); settings.MaxRetries = 3; _settings.SaveSettings(settings);
        var payload = BuildPayload();
        payload.FallbackPlan = [new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "LOSSLESS", [], "isrc"), new("step-2", "amazon", "HD_FLAC", [], "isrc")];
        payload.Quality = "7";
        payload.ResolutionError = transient ? "Track not found." : "Qobuz (7), Tidal (LOSSLESS), Amazon (HD_FLAC) had no copy.";
        if (allStepsKnown) payload.FallbackHistory = [new("step-1", "failed", "unavailable", "Confirmed missing recording"),
            new("step-2", "failed", "quality_below_requested", "Actual audio was below requested quality")];
        if (!savedSelection) { payload.FallbackPlan.Clear(); payload.ResolutionError = "Track not found."; }
        await EnqueueAsync(payload);
        using var services = new ServiceCollection().BuildServiceProvider();
        var listener = new DeezSpoTagListener();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), listener,
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());
        var context = new EngineAudioPostDownloadHelper.FailureHandlingContext<QobuzQueueItem>(
            _repository, new NullActivityLogWriter(), listener, retry, services,
            (_, _, _, _) => Task.FromResult(false), p => p.ToQueuePayload(), "qobuz", NullLogger.Instance);
        var message = transient ? "Provider HTTP 503" : "HTTP 404: {\"code\":\"TRACK_UNAVAILABLE\",\"retryable\":false,\"retry_mode\":\"none\"}";
        await EngineAudioPostDownloadHelper.HandleFailureAsync(new HttpRequestException(message, null,
            transient ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound), payload.Id, payload, context, CancellationToken.None);
        Assert.Equal(expected, (await _repository.GetByUuidAsync(payload.Id))!.Status);
        Assert.Equal(expected == "retry_waiting", await retry.HasPendingRetriesAsync());
    }

    [Fact]
    public async Task FinalStatus_DisabledStepTimeout_DoesNotHideConfirmedAbsence()
    {
        // The saved selection enabled qobuz 27 + tidal HI_RES; deezer 9 was disabled
        // afterwards. Both enabled steps conclusively missed; the disabled step's last
        // outcome is a timeout. The saved-selection evidence rule must mark the item
        // unavailable (no retry), matching what the monitor button and the failure
        // message then show.
        var settings = _settings.LoadSettings(); settings.MaxRetries = 3; _settings.SaveSettings(settings);
        var payload = BuildPayload();
        payload.FallbackPlan =
        [
            new("step-0", "qobuz", "27", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc"),
            new("step-2", "deezer", "9", [], "direct_url")
        ];
        payload.FallbackHistory =
        [
            new("step-0", "skipped", "unavailable", "Confirmed missing recording"),
            new("step-1", "skipped", "unavailable", "Confirmed missing recording"),
            new("step-2", "skipped", "provider_timeout", "deezer provider timed out")
        ];
        payload.SourceSettingsSnapshot = new QueueSourceSettingsSnapshot
        {
            DownloadEngineOrder = new DownloadEngineOrderSettings
            {
                Enabled = true,
                Engines =
                [
                    new DownloadEngineOrderItem { Engine = "qobuz", Qualities = [new DownloadEngineQualityItem { Quality = "27" }] },
                    new DownloadEngineOrderItem { Engine = "tidal", Qualities = [new DownloadEngineQualityItem { Quality = "HI_RES" }] }
                ]
            }
        };
        payload.ResolutionError = "tidal quality=HI_RES: confirmed unavailable";
        await EnqueueAsync(payload);
        using var services = new ServiceCollection().BuildServiceProvider();
        var listener = new DeezSpoTagListener();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), listener,
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());
        var context = new EngineAudioPostDownloadHelper.FailureHandlingContext<QobuzQueueItem>(
            _repository, new NullActivityLogWriter(), listener, retry, services,
            (_, _, _, _) => Task.FromResult(false), p => p.ToQueuePayload(), "qobuz", NullLogger.Instance);
        await EngineAudioPostDownloadHelper.HandleFailureAsync(
            new HttpRequestException(
                "HTTP 404: {\"code\":\"TRACK_UNAVAILABLE\",\"retryable\":false,\"retry_mode\":\"none\"}",
                null, HttpStatusCode.NotFound), payload.Id, payload, context, CancellationToken.None);
        Assert.Equal("unavailable", (await _repository.GetByUuidAsync(payload.Id))!.Status);
        Assert.False(await retry.HasPendingRetriesAsync());
    }

    [Fact]
    public async Task FinalStatus_MixedProviderOutcomes_StayFailedWithProviderRecovery()
    {
        // Pin: the NAS six-outcome shape (two catalogue 403s, two mapping timeouts, two
        // unresolved identities) is not evidence of absence - the item stays failed and
        // provider recovery keeps retry eligibility.
        var settings = _settings.LoadSettings(); settings.MaxRetries = 3; _settings.SaveSettings(settings);
        var payload = BuildPayload();
        payload.FallbackPlan =
        [
            new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc"),
            new("step-2", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
            new("step-3", "qobuz", "6", [], "isrc"),
            new("step-4", "tidal", "LOSSLESS", [], "isrc"),
            new("step-5", "amazon", "HD_FLAC", [], "mapped_url")
        ];
        payload.FallbackHistory =
        [
            new("step-0", "skipped", "catalog_lookup_failed", "qobuz catalog lookup failed: 403 (Forbidden)"),
            new("step-1", "skipped", "provider_timeout", "tidal provider timed out"),
            new("step-2", "skipped", "identity_unresolved", "missing_url"),
            new("step-3", "skipped", "catalog_lookup_failed", "qobuz catalog lookup failed: 403 (Forbidden)"),
            new("step-4", "skipped", "provider_timeout", "tidal provider timed out"),
            new("step-5", "skipped", "identity_unresolved", "missing_url")
        ];
        payload.ResolutionError = "amazon quality=ULTRA_HD_FLAC: missing_url";
        await EnqueueAsync(payload);
        using var services = new ServiceCollection().BuildServiceProvider();
        var listener = new DeezSpoTagListener();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), listener,
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());
        var context = new EngineAudioPostDownloadHelper.FailureHandlingContext<QobuzQueueItem>(
            _repository, new NullActivityLogWriter(), listener, retry, services,
            (_, _, _, _) => Task.FromResult(false), p => p.ToQueuePayload(), "amazon", NullLogger.Instance);
        await EngineAudioPostDownloadHelper.HandleFailureAsync(
            new HttpRequestException("HTTP 404", null, HttpStatusCode.NotFound), payload.Id, payload, context,
            CancellationToken.None);
        // The scheduler immediately requeues a failed row (retry_waiting), so the durable
        // agreement to check is: never unavailable, and a retry is pending.
        Assert.NotEqual("unavailable", (await _repository.GetByUuidAsync(payload.Id))!.Status);
        Assert.True(await retry.HasPendingRetriesAsync());
    }

    [Fact]
    public void ExhaustionMessage_UnresolvedSearchIsNotDescribedAsHavingNoCopy()
    {
        // "unresolved" is a failed peer search, not a confirmed catalogue absence; the
        // sentence must not claim the provider had no copy of the track.
        var payload = BuildPayload();
        payload.FallbackPlan =
        [
            new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc")
        ];
        payload.FallbackHistory =
        [
            new("step-0", "skipped", "unresolved", "No peer with a copy was found"),
            new("step-1", "skipped", "provider_timeout", "tidal provider timed out")
        ];

        var message = (string)typeof(EngineFallbackCoordinator)
            .GetMethod("BuildExhaustionMessage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [payload, "tidal"])!;

        Assert.DoesNotContain("had no copy", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Qobuz", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tidal", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExhaustionMessage_KeepsEveryEnabledProviderDiagnostic()
    {
        var payload = BuildPayload();
        payload.SourceSettingsSnapshot = new QueueSourceSettingsSnapshot();
        payload.FallbackPlan =
        [
            new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc"),
            new("step-2", "amazon", "ULTRA_HD_FLAC", [], "mapped_url")
        ];
        payload.FallbackHistory =
        [
            new("step-0", "skipped", "catalog_lookup_failed", "qobuz catalog lookup failed: 403 (Forbidden)"),
            new("step-1", "skipped", "provider_timeout", "tidal provider timed out"),
            new("step-2", "skipped", "identity_unresolved", "amazon missing_url")
        ];

        var message = (string)typeof(EngineFallbackCoordinator)
            .GetMethod("BuildExhaustionMessage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [payload, "amazon"])!;

        Assert.Contains("could not access its catalog", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not approve a matching track identity", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QobuzTemporaryOutage_BeyondTheAttemptBudget_KeepsProviderRecoveryScheduled()
    {
        // A temporary outage that exhausts the ordinary retry budget must still schedule a
        // retry (provider recovery continues past the budget), matching the shared helper's
        // failure path - otherwise the downloaded-pending state strands after the budget.
        var settings = _settings.LoadSettings(); settings.MaxRetries = 3; _settings.SaveSettings(settings);
        var payload = BuildPayload();
        await EnqueueAsync(payload);
        await using (var db = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "UPDATE download_task SET retry_attempt_count = 99 WHERE queue_uuid = $uuid";
            command.Parameters.AddWithValue("$uuid", payload.Id);
            await command.ExecuteNonQueryAsync();
        }

        var processor = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(QobuzEngineProcessor));
        SetProcessorField(processor, "_queueRepository", _repository);
        SetProcessorField(processor, "_retryScheduler",
            new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), new DeezSpoTagListener(),
                NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry()));
        SetProcessorField(processor, "_activityLog", new NullActivityLogWriter());
        SetProcessorField(processor, "_serviceProvider", new ServiceCollection().BuildServiceProvider());
        SetProcessorField(processor, "_logger", NullLogger<QobuzEngineProcessor>.Instance);
        SetProcessorField(processor, "_deezspotagListener", new DeezSpoTagListener());

        var failure = new HttpRequestException("Qobuz download stream failed: HTTP 503 (Service Unavailable).",
            null, HttpStatusCode.ServiceUnavailable);
        typeof(QobuzEngineProcessor)
            .GetMethod("MarkFinalFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(processor, [payload.Id, payload, failure]);

        Assert.True(await ((DownloadRetryScheduler)GetProcessorField(processor, "_retryScheduler")!).HasPendingRetriesAsync());
        Assert.Equal("retry_waiting", (await _repository.GetByUuidAsync(payload.Id))!.Status);
    }

    private static void SetProcessorField(object processor, string name, object? value)
        => typeof(QobuzEngineProcessor)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(processor, value);

    private static object? GetProcessorField(object processor, string name)
        => typeof(QobuzEngineProcessor)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(processor);

    [Fact]
    public async Task RecoveryFlow_FailedItemCompletesOnTheSameRowOnceRecordingBecomesObtainable()
    {
        // End-to-end proof on one row: a mixed-outcome failure (no step proved absence) keeps
        // provider recovery scheduling retries; the due sweep requeues the SAME queue row and
        // restarts the saved plan; the real processor pipeline then downloads actual audio (a
        // TagLib-parseable FLAC delivered by the fake provider) and the same row completes with
        // its artifacts, and no retry stays pending. The test never writes the completed status
        // itself: completion comes out of QobuzEngineProcessor.ProcessQueueItemAsync, the same
        // download → quality-guard → tag-writing → completion path production runs.
        var settings = _settings.LoadSettings();
        settings.MaxRetries = 3;
        settings.DownloadLocation = Path.Join(_root, "library");
        settings.SaveLyrics = false;
        settings.SyncedLyrics = false;
        settings.SaveArtwork = false;
        settings.SaveAnimatedArtwork = false;
        settings.SaveArtworkArtist = false;
        settings.Tags.Cover = false;
        _settings.SaveSettings(settings);
        Directory.CreateDirectory(settings.DownloadLocation);

        var payload = BuildPayload();
        payload.Engine = "amazon";
        payload.SourceService = "amazon";
        payload.Quality = "HD_FLAC";
        payload.AutoIndex = 2;
        payload.FallbackPlan =
        [
            new("step-0", "qobuz", "6", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc"),
            new("step-2", "amazon", "ULTRA_HD_FLAC", [], "mapped_url")
        ];
        payload.FallbackHistory =
        [
            new("step-0", "skipped", "catalog_lookup_failed", "qobuz catalog lookup failed: 403 (Forbidden)"),
            new("step-1", "skipped", "provider_timeout", "tidal provider timed out"),
            new("step-2", "skipped", "identity_unresolved", "amazon missing_url")
        ];
        await EnqueueAsync(payload);
        await _repository.UpdateStatusAsync(payload.Id, "failed", "amazon quality=ULTRA_HD_FLAC: missing_url");

        var listener = new DeezSpoTagListener();
        var registry = new DownloadCancellationRegistry();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), listener,
            NullLogger<DownloadRetryScheduler>.Instance, registry);
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new EngineAudioPostDownloadHelper.FailureHandlingContext<QobuzQueueItem>(
            _repository, new NullActivityLogWriter(), listener, retry, services,
            (_, _, _, _) => Task.FromResult(false), p => p.ToQueuePayload(), "amazon", NullLogger.Instance);
        await EngineAudioPostDownloadHelper.HandleFailureAsync(
            new HttpRequestException("HTTP 404", null, HttpStatusCode.NotFound), payload.Id, payload, context,
            CancellationToken.None);
        Assert.True(await retry.HasPendingRetriesAsync());

        // The due sweep requeues the same row and restarts the saved plan from step-0.
        await MakeRetryDueAsync(payload.Id);
        Assert.True(await retry.RunRetrySweepAsync());
        var requeued = await _repository.GetByUuidAsync(payload.Id);
        Assert.Equal("queued", requeued!.Status);
        Assert.Equal(payload.Id, requeued.QueueUuid);
        Assert.Equal("qobuz", requeued.Engine);

        // The recording becomes obtainable: drive the REAL processor with a provider that
        // delivers actual audio - a real encoder-produced FLAC containing audio frames, not
        // just metadata blocks. The row must complete through the actual pipeline.
        var fakeDownloader = new FakeQobuzDownloadService(_root, RealEncodedFlacBytes());
        using var scopeServices = BuildProcessorScope(listener);
        var processor = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(QobuzEngineProcessor));
        SetProcessorField(processor, "_queueRepository", _repository);
        SetProcessorField(processor, "_cancellationRegistry", registry);
        SetProcessorField(processor, "_settingsService", _settings);
        SetProcessorField(processor, "_deezspotagListener", listener);
        SetProcessorField(processor, "_retryScheduler", retry);
        SetProcessorField(processor, "_qobuzDownloader", fakeDownloader);
        SetProcessorField(processor, "_serviceProvider", scopeServices);
        SetProcessorField(processor, "_fallbackCoordinator", null);
        SetProcessorField(processor, "_activityLog", new NullActivityLogWriter());
        SetProcessorField(processor, "_lyricsService", null);
        SetProcessorField(processor, "_postDownloadTaskScheduler", null);
        SetProcessorField(processor, "_folderConversionSettingsOverlay", new StubFolderConversionOverlay());
        SetProcessorField(processor, "_tagSettingsResolver", new StubDownloadTagSettingsResolver());
        SetProcessorField(processor, "_logger", NullLogger<QobuzEngineProcessor>.Instance);

        await ((QobuzEngineProcessor)processor).ProcessQueueItemAsync(requeued, CancellationToken.None);

        // The same queue row completed on its own: the provider actually delivered audio, the
        // row reports the download, and no retry remains pending.
        var afterProcessing = await _repository.GetByUuidAsync(payload.Id);
        Assert.True(fakeDownloader.DownloadCount == 1,
            $"The processor never downloaded: row status={afterProcessing?.Status}, error={afterProcessing?.Error}");
        var completed = await _repository.GetByUuidAsync(payload.Id);
        Assert.NotNull(completed);
        Assert.Equal(payload.Id, completed!.QueueUuid);
        Assert.True(string.Equals("completed", completed.Status, StringComparison.OrdinalIgnoreCase),
            $"The row did not complete: status={completed.Status}, error={completed.Error}");
        Assert.Equal(100, completed.Progress);
        Assert.Equal(1, completed.Downloaded);
        Assert.False(await retry.HasPendingRetriesAsync());

        // The delivered audio is real and survived the finalization (tag writing) on disk: the
        // file exists, keeps the FLAC magic, still parses via TagLib with the delivered
        // CD-quality properties, and contains actual audio frames beyond the metadata blocks.
        Assert.NotNull(fakeDownloader.LastWrittenPath);
        Assert.True(File.Exists(fakeDownloader.LastWrittenPath));
        var finalBytes = await File.ReadAllBytesAsync(fakeDownloader.LastWrittenPath!);
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(finalBytes, 0, 4));
        using var parsed = TagLib.File.Create(fakeDownloader.LastWrittenPath!);
        Assert.Equal(44100, parsed.Properties.AudioSampleRate);
        Assert.Equal(16, parsed.Properties.BitsPerSample);
        Assert.True(parsed.Properties.Duration > TimeSpan.Zero);
        Assert.True(ContainsFlacAudioFrame(finalBytes),
            "The delivered FLAC must contain actual audio frames, not only metadata blocks.");
    }

    /// <summary>
    /// A genuine FLAC-encoded 0.1 s stereo tone (44.1 kHz, 16-bit), produced by FFmpeg's FLAC
    /// encoder. Unlike a hand-built metadata-only placeholder, it carries a real audio frame
    /// (frame sync 0xFF 0xF8 at its audio section), so delivery proves playable audio, and the
    /// production tag writer exercises the same TagLib read/write path it does in production.
    /// </summary>
    private static byte[] RealEncodedFlacBytes()
        => Convert.FromBase64String(RealEncodedFlacBase64);

    private static bool ContainsFlacAudioFrame(byte[] flac)
    {
        // FLAC frame headers begin with the 14-bit sync code 11111111 111111xx; the fixed/
        // variable blocking-strategy bit yields 0xF8 (fixed) or 0xF9 (variable) as the second
        // byte. Search past the mandatory STREAMINFO block (bytes 0-7).
        for (var index = 8; index < flac.Length - 1; index++)
        {
            if (flac[index] == 0xFF && flac[index + 1] is 0xF8 or 0xF9)
            {
                return true;
            }
        }

        return false;
    }

        private const string RealEncodedFlacBase64 =
            """
            ZkxhQwAAACISABIAAATQAATQCsRC8AAAETqn4Skya1vFrImnvuJ36HQkBAAALg0AAABMYXZmNjAuMTYuMTAwAQAAABUAAABlbmNv
            ZGVyPUxhdmY2MC4xNi4xMDCBACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/+HmIABE5vU4AAAC1AWkCHQLOA3wEJwTO5qPnHm3+07W3U17o
            QDNElYAczMmSEyZOZmcyTQk4eZJknmS5DchmUk5OTOyZM5CeTkkmSZJYZMmZMmZkh8JfM/Pk85CXJJZJJSTw5Q5k5JMmTJksySzk
            uZNMh5OfISpkzlMlMyzMzJlMMM0kkzCUkzCeGUpPJCykpOTzhNkOySeScyckhPhPJyZMzmZOZJ5JyfJMlJYZyTknJkpJMzCZmE5M
            yZIc5JzJKc5Jck58zz8k8pnk5OknOSSSknMyckzMklOScmcNyE6ZPw8yGsJzJklKEMmZOZJzPJJnJkzJ0yfk50k+Zpk0yZzJKFJk
            smQnMkh5JKSdCTmZOcpCfMnyVJnnDLDJ0hNw5nycOZJnJMkoTw8mGz4Z5mc7JOZMzkmwlkh5JJphPhOQmkkyeTJMmcyySznkyzk7
            JmfknkyZpk5mSTyQnOGkyZJlKYT5CfJLOHOQsyachyZLCbJDmZJJPOTMzknJMycuT4Zh0klk/NJ8kynITNIZySk4SeThPhmSchwp
            MnM/J2E+STTMMzJZJMnJMwmeQyTJmZkhPSHklJk55Oc8mc5ySkk4ZsmGUyTmcnJkyTDMyyTJz5LJ/Dnk+GcnCaZOQ8MzISWSTJyf
            ITM5knM5k5OYTpmbkkmmTkmeSk4TmSWTMnhySdJA9MmTwyyQ7MmpIbJM8kyc8mZkyGScycnJCZM8meGcnmSS8k8ymZ5JlOTzJMnk
            zJDOZJ5DySeEzySch8Oc5yfkOc88JmfJMMykkyck8nmYc5zkszPMnOZmcyT0JMJkzknCcyeYaQzISckOyTOTOTzmkzOZNKczyczM
            mSEyZOZmcyTQk4eZJknmS5DchmUk5OTOyZKEyfJySTJMksMmTMmTMyQ+EvmfnyechLkkskkpJ4cocyckmTJkyWZJZyXMmmQ8nPkJ
            UyZymSmZZmZkymGGaSSZhKSZhPDKUnkhZSUnJ5wmyHZJPJOZOSQnwnk5MmZzMnMM8J5PkmSksM5JyTkyUkmZhMzCcmZMkOck5klO
            ckuSSnJM8/JPKZ5OTpJzkkkpJzMnJMzJJTknJnDchOmT8PMhrCcyZJShDJmTmSczySZyZMydMn5OdJPmaZNMmcyShSZLJkJzJIeS
            SknQk5mTnKQnzJ8lSZ5wywydITcOZ8nDmSZyTJKE8PJhs+GeZnOyTmTM5JsJZIeSSaYT4TkJpJMnkyTJnMsks55Ms5OyZn5J5Mma
            ZOZkk8kJzhpMmSZSmE+QnySzhzkLMmnIcmSwmyQ5mSSTzkzM5JyTMnLk+GYdJJZPzSfJMpyEzSGckpOEnk4T4ZknIcKTJzPydhPk
            k0zDMyWSTJyTMJnkMkyZmZIT0h5JSZOeTnPJnOckpJOGbJhlMk5nJyZMkwzMskyc+Syfw55PhnJwmmTkPDMyElkkycnyEzOZJzOZ
            OTmE6Zm5JJpk5JnkpOE5klkzJ4cknSQPTJk8MskOzJqSGyTPJMnPJmZMhknMnJyQmTPJnhnJ5kkvJPMpmeSZTk8yTJ5MyQzmSeEu
            STwmeSTkPhznOT8hznnhMz5JhmUkmTknk8zDnOclmZ5k5zMzmSehJhMmck4TmTzDSGZCTkh2SZyZyec0mZzJpTAAAAAmxQ==
            """;

    private ServiceProvider BuildProcessorScope(DeezSpoTagListener listener)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_repository);
        services.AddSingleton<IDeezSpoTagListener>(listener);
        services.AddSingleton(new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance));
        services.AddSingleton(new AudioTagger(NullLogger<AudioTagger>.Instance, _settings));
        return services.BuildServiceProvider();
    }

    private sealed class FakeQobuzDownloadService(string rootPath, byte[] audioBytes) : IQobuzDownloadService
    {
        public int DownloadCount { get; private set; }
        public string? LastWrittenPath { get; private set; }

        public Task<bool> HasPublicDownloadSessionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> PeekPublicDownloadSessionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<string?> BeginPublicDownloadVerificationAsync(
            CancellationToken cancellationToken, string? publicAppBaseUrl = null)
            => Task.FromResult<string?>(null);
        public Task CompletePublicDownloadVerificationAsync(string grant, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<QobuzQualityResolution?> ResolveQualityAsync(
            long trackId, string qualityCode, CancellationToken cancellationToken)
            => Task.FromResult<QobuzQualityResolution?>(
                new QobuzQualityResolution(trackId, qualityCode, qualityCode, "https://fake.invalid/audio.flac", 16, 44.1, "qobuz"));

        public async Task<string> DownloadByUrlAsync(QobuzDownloadRequest request, CancellationToken cancellationToken)
        {
            DownloadCount++;
            var outputDir = string.IsNullOrWhiteSpace(request.OutputDir) ? Path.Join(rootPath, "staging") : request.OutputDir;
            Directory.CreateDirectory(outputDir);
            var path = Path.Join(outputDir, "proof.flac");
            await File.WriteAllBytesAsync(path, audioBytes, cancellationToken);
            LastWrittenPath = path;
            return path;
        }
    }

    private sealed class StubFolderConversionOverlay : IFolderConversionSettingsOverlay
    {
        public Task ApplyAsync(DeezSpoTagSettings settings, long? destinationFolderId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Production always resolves a download profile for the destination folder; an empty
    /// profile stands in for the default one the app would resolve.
    /// </summary>
    private sealed class StubDownloadTagSettingsResolver : IDownloadTagSettingsResolver
    {
        public Task<TagSettings?> ResolveAsync(long? destinationFolderId, CancellationToken cancellationToken)
            => Task.FromResult<TagSettings?>(null);

        public Task<DownloadTagProfileSettings?> ResolveProfileAsync(long? destinationFolderId, CancellationToken cancellationToken)
            => Task.FromResult<DownloadTagProfileSettings?>(
                new DownloadTagProfileSettings(new TagSettings { Cover = false }, null, null, null));
    }

    [Fact]
    public async Task RecoveryFlow_CancellationDuringProviderRecoveryStaysFinalUntilManualRequeue()
    {
        // The user cancels while the item waits for provider recovery: the sweep must not
        // revive it, and only the manual origin may leave the cancelled state (proven at the
        // app level by RetryDownloadAsync_ManualRequeueOfCancelledItem_ClearsCancellation).
        var settings = _settings.LoadSettings(); settings.MaxRetries = 3; _settings.SaveSettings(settings);
        var payload = BuildPayload();
        payload.FallbackPlan = [new("step-0", "qobuz", "7", [], "isrc")];
        payload.FallbackHistory = [];
        await EnqueueAsync(payload);
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new DownloadCancellationRegistry();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(),
            new DeezSpoTagListener(), NullLogger<DownloadRetryScheduler>.Instance, registry);
        var context = new EngineAudioPostDownloadHelper.FailureHandlingContext<QobuzQueueItem>(
            _repository, new NullActivityLogWriter(), new DeezSpoTagListener(), retry, services,
            (_, _, _, _) => Task.FromResult(false), p => p.ToQueuePayload(), "qobuz", NullLogger.Instance);
        await EngineAudioPostDownloadHelper.HandleFailureAsync(
            new HttpRequestException("Provider HTTP 503", null, HttpStatusCode.ServiceUnavailable),
            payload.Id, payload, context, CancellationToken.None);
        Assert.Equal("retry_waiting", (await _repository.GetByUuidAsync(payload.Id))!.Status);

        // Durable cancellation, mirroring CancelDownloadAsync's write-first ordering.
        registry.MarkUserCanceled(payload.Id);
        Assert.True(await _repository.UpdateStatusAsync(payload.Id, "canceled"));

        await MakeRetryDueAsync(payload.Id);
        await retry.RunRetrySweepAsync();
        var cancelled = await _repository.GetByUuidAsync(payload.Id);
        Assert.Equal("canceled", cancelled!.Status);
        Assert.True(registry.WasUserCanceled(payload.Id));
    }

    private async Task MakeRetryDueAsync(string queueUuid)
    {
        await using var db = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "UPDATE download_task SET retry_next_at = '2000-01-01T00:00:00Z' WHERE queue_uuid = $uuid";
        command.Parameters.AddWithValue("$uuid", queueUuid);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RateLimitedDownload_IsDurablyRequeuedAfterItsAdvertisedDelay()
    {
        var settings = _settings.LoadSettings(); settings.MaxRetries = 3; _settings.SaveSettings(settings);
        var payload = BuildPayload(); payload.FallbackPlan = [new("step-0", "qobuz", "7", [], "isrc")]; payload.Quality = "7";
        await EnqueueAsync(payload);
        using var services = new ServiceCollection().BuildServiceProvider();
        var listener = new DeezSpoTagListener();
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), listener,
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());
        var context = new EngineAudioPostDownloadHelper.FailureHandlingContext<QobuzQueueItem>(
            _repository, new NullActivityLogWriter(), listener, retry, services,
            (_, _, _, _) => Task.FromResult(false), p => p.ToQueuePayload(), "qobuz", NullLogger.Instance);
        var before = DateTimeOffset.UtcNow;
        await EngineAudioPostDownloadHelper.HandleFailureAsync(new ZarzSessionRateLimitException("Qobuz rate limited", 600),
            payload.Id, payload, context, CancellationToken.None);
        Assert.Equal("retry_waiting", (await _repository.GetByUuidAsync(payload.Id))!.Status);
        await using (var db = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT retry_next_at FROM download_task WHERE queue_uuid = $uuid";
            command.Parameters.AddWithValue("$uuid", payload.Id);
            Assert.True(DateTimeOffset.Parse((string)(await command.ExecuteScalarAsync())!) >= before.AddSeconds(599));
            command.CommandText = "UPDATE download_task SET retry_next_at = '2000-01-01T00:00:00Z' WHERE queue_uuid = $uuid";
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(await retry.RunRetrySweepAsync());
        Assert.Equal("queued", (await _repository.GetByUuidAsync(payload.Id))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRetryRestartsSavedPlanUnlessAudioWasAlreadyAcquired(bool acquired)
    {
        var payload = BuildPayload();
        payload.Engine = "amazon"; payload.Quality = "ULTRA_HD_FLAC"; payload.AutoIndex = 1;
        payload.ResolutionStatus = "resolved"; payload.ResolvedEngine = "amazon"; payload.ResolvedAutoIndex = 1;
        payload.FallbackHistory = [new("step-0", "skipped", "provider_timeout", "temporary catalog timeout")];
        if (acquired)
        {
            var audio = Path.Join(_root, "acquired.flac");
            await File.WriteAllBytesAsync(audio, new byte[] { 1, 2, 3 });
            payload.AudioAcquired = true; payload.AcquiredAudioPath = audio;
        }
        await EnqueueAsync(payload);
        Assert.True(await _repository.ScheduleRetryAsync(payload.Id, "amazon", "upstream API failed", 3));
        await using (var connection = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
        {
            await connection.OpenAsync();
            await using var due = connection.CreateCommand();
            due.CommandText = "UPDATE download_task SET retry_next_at = '2000-01-01T00:00:00Z' WHERE queue_uuid = $uuid";
            due.Parameters.AddWithValue("$uuid", payload.Id);
            await due.ExecuteNonQueryAsync();
        }
        var retry = new DownloadRetryScheduler(_repository, _settings, new NullActivityLogWriter(), new DeezSpoTagListener(),
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());
        Assert.True(await retry.RunRetrySweepAsync());
        var item = await _repository.GetByUuidAsync(payload.Id);
        var saved = JsonSerializer.Deserialize<QobuzQueueItem>(item!.PayloadJson!)!;
        Assert.Equal(acquired ? "amazon" : "qobuz", item.Engine);
        Assert.Equal(acquired ? 1 : 0, saved.AutoIndex);
        Assert.Equal(acquired ? "resolved" : "pending", saved.ResolutionStatus);
        Assert.Equal(acquired ? 1 : 0, saved.FallbackHistory.Count);
        Assert.Equal(3, saved.FallbackPlan.Count);
        Assert.Equal("stereo", saved.ContentType);
        if (acquired) Assert.Equal(payload.AcquiredAudioPath, saved.AcquiredAudioPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedProviderLookupAdvancesToLaterEnabledService(bool httpFailure)
    {
        var payload = BuildPayload();
        await EnqueueAsync(payload);
        var coordinator = BuildCoordinator(new ThrowingAmazonResolver(httpFailure));

        var advanced = await coordinator.TryAdvanceAsync(payload.Id, "qobuz", payload, CancellationToken.None);

        Assert.True(advanced);
        Assert.Equal("deezer", payload.Engine);
        Assert.Equal("9", payload.Quality);
        Assert.Equal(2, payload.AutoIndex);
        Assert.Contains(payload.FallbackHistory, attempt => attempt.StepId == "step-1" && attempt.Status == "skipped");
        var providerFailure = Assert.Single(payload.FallbackHistory, attempt => attempt.StepId == "step-1");
        Assert.Contains(httpFailure ? "Amazon unavailable" : "metadata session", providerFailure.Detail, StringComparison.OrdinalIgnoreCase);
        var saved = await _repository.GetByUuidAsync(payload.Id);
        Assert.NotNull(saved);
        Assert.Equal("deezer", saved.Engine);
        Assert.Equal("queued", saved.Status);
        var persisted = JsonSerializer.Deserialize<QobuzQueueItem>(saved.PayloadJson!);
        Assert.NotNull(persisted);
        Assert.Contains(persisted.FallbackHistory, attempt => attempt.StepId == "step-1" && attempt.Status == "skipped");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavedPlanEnforcesSnapshotEngineAndQualityBeforeProviderLookup(bool engineEnabled, bool qualityEnabled)
    {
        var settings = new DeezSpoTagSettings { Service = "custom", FallbackBitrate = true };
        settings.DownloadEngineOrder.Enabled = true;
        var amazon = settings.DownloadEngineOrder.Engines.Find(engine => engine.Engine == "amazon")!;
        amazon.Enabled = engineEnabled;
        amazon.Qualities.Find(quality => quality.Quality == "OPUS")!.Enabled = qualityEnabled;
        var payload = BuildPayload();
        payload.FallbackPlan[1] = new("step-1", "amazon", "OPUS", [], "mapped_url");
        payload.SourceSettingsSnapshot = QueueSourceSettingsSnapshot.Capture(settings);
        await EnqueueAsync(payload);
        var resolver = new ThrowingAmazonResolver(false);

        Assert.True(await BuildCoordinator(resolver).TryAdvanceAsync(payload.Id, "qobuz", payload, CancellationToken.None));

        var enabled = engineEnabled && qualityEnabled;
        Assert.Equal(enabled ? 1 : 0, resolver.CallCount);
        Assert.Equal("deezer", payload.Engine);
        Assert.Equal(2, payload.AutoIndex);
        Assert.Equal("step-2", payload.FallbackPlan[2].StepId);
        if (!enabled)
            Assert.Contains(payload.FallbackHistory, attempt => attempt.StepId == "step-1"
                && attempt.Status == "skipped" && attempt.ErrorClass == "source_disabled");
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var payload = BuildPayload();
        await EnqueueAsync(payload);
        using var cancellation = new CancellationTokenSource();
        var coordinator = BuildCoordinator(new CancelingAmazonResolver(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.TryAdvanceAsync(payload.Id, "qobuz", payload, cancellation.Token));
    }

    [Fact]
    public async Task RepositoryFailurePropagates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Queue"] = "Data Source=/dev/null/queue.db",
            ["DataDirectory"] = _root
        }).Build();
        var repository = new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance);
        var payload = BuildPayload();
        payload.AutoIndex = 1;
        var coordinator = BuildCoordinator(new ThrowingAmazonResolver(false), repository);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.TryAdvanceAsync(payload.Id, "amazon", payload, CancellationToken.None));
    }

    [Fact]
    public async Task PersistedTidalId_DoesNotResolveWithoutTheTidalVariantCheck()
    {
        // A Tidal id sitting in the database is not proof of anything on its own. On Tidal an
        // Atmos master is a different track with a different id from its stereo version, and it
        // carries the same quality tags, so an id that was correct yesterday can hand back Atmos
        // audio for a stereo request today. Building the link straight from the saved id would
        // skip the only check that can catch that, and the mistake would surface at download time
        // with the file already written.
        //
        // So a persisted id may only ever be used as a hint to ask Tidal, never as the answer.
        // This service is built with no Tidal service wired in, which is exactly the situation in
        // which there is nobody to ask: the correct outcome is unresolved, and the run moves on to
        // the next fallback source instead of downloading the wrong variant.
        var service = BuildFallbackSearchService();

        var result = await service.ResolveAsync(
            BuildFallbackSearchRequest("tidal", tidalId: "125064025"),
            CancellationToken.None);

        Assert.Null(result.ResolvedUrl);
        Assert.Equal("unresolved", result.ResolutionSource);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("999999999999999999999999999999")]
    public async Task MissingOrInvalidTidalIdRetainsMetadataResolutionPath(string tidalId)
    {
        var service = BuildFallbackSearchService();

        var result = await service.ResolveAsync(
            BuildFallbackSearchRequest("tidal", tidalId),
            CancellationToken.None);

        Assert.Null(result.ResolvedUrl);
        Assert.Equal("unresolved", result.ResolutionSource);
    }

    [Fact]
    public async Task ManualSoulseekPeerSelectionCannotAdvanceEvenWithAnOldCrossEnginePlan()
    {
        var payload = new SoulseekQueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceService = "soulseek",
            SourceUrl = "https://www.deezer.com/track/123",
            Artist = "Selected Artist",
            Title = "Selected Track",
            Quality = "FLAC",
            SoulseekUsername = "selected-peer",
            SoulseekRemotePath = "Album/Selected Track.flac",
            SoulseekQualityCode = "FLAC",
            AutoIndex = 0,
            FallbackPlan =
            [
                new("step-0", "soulseek", "FLAC", [], "mapped_url"),
                new("step-1", "deezer", "9", [], "direct_url")
            ]
        };

        var advanced = await BuildCoordinator(new ThrowingAmazonResolver(false))
            .TryAdvanceAsync(payload.Id, "soulseek", payload, CancellationToken.None);

        Assert.False(advanced);
        Assert.Equal("soulseek", payload.Engine);
        Assert.Equal("FLAC", payload.Quality);
        Assert.Empty(payload.FallbackHistory);
    }

    [Fact]
    public async Task FailedManualSoulseekPeerSelectionIsNotAutomaticallyRetried()
    {
        var payload = new SoulseekQueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceService = "soulseek",
            Artist = "Selected Artist",
            Title = "Selected Track",
            Quality = "FLAC",
            SoulseekUsername = "selected-peer",
            SoulseekRemotePath = "Album/Selected Track.flac",
            SoulseekQualityCode = "FLAC"
        };
        await _repository.EnqueueAsync(new DownloadQueueItem(
            Id: 0, QueueUuid: payload.Id, Engine: payload.Engine, ArtistName: payload.Artist,
            TrackTitle: payload.Title, Isrc: null, DeezerTrackId: null,
            DeezerAlbumId: null, DeezerArtistId: null, SpotifyTrackId: null,
            SpotifyAlbumId: null, SpotifyArtistId: null, AppleTrackId: null,
            AppleAlbumId: null, AppleArtistId: null, DurationMs: null,
            DestinationFolderId: null, QualityRank: null, QueueOrder: null,
            Status: "failed", PayloadJson: JsonSerializer.Serialize(payload), Progress: 0,
            Downloaded: 0, Failed: 1, Error: "selected file failed", CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));
        // Recreate the repository as a restarted process would: the selected peer, path and quality must
        // come back from durable queue JSON, not from an object retained in memory.
        var reopened = new DownloadQueueRepository(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_root, "queue.db")}",
                ["DataDirectory"] = _root
            })
            .Build(), NullLogger<DownloadQueueRepository>.Instance);
        var saved = await reopened.GetByUuidAsync(payload.Id);
        Assert.NotNull(saved);
        var restored = JsonSerializer.Deserialize<SoulseekQueueItem>(saved.PayloadJson!);
        Assert.NotNull(restored);
        Assert.Equal("selected-peer", restored.SoulseekUsername);
        Assert.Equal("Album/Selected Track.flac", restored.SoulseekRemotePath);
        Assert.Equal("FLAC", restored.SoulseekQualityCode);
        Assert.True(SoulseekPinnedCandidatePolicy.IsManualSelection(restored));

        var retry = new DownloadRetryScheduler(
            reopened, _settings, new NullActivityLogWriter(), new DeezSpoTagListener(),
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());

        var scheduled = await retry.ScheduleRetryAsync(payload.Id, "soulseek", "selected file failed");

        Assert.False(scheduled);
        Assert.False(await _repository.HasScheduledRetriesAsync());

        // Older queued selections may already have a retry waiting when this rule is installed.
        Assert.True(await _repository.ScheduleRetryAsync(payload.Id, "soulseek", "selected file failed", 3));
        await using (var connection = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
        {
            await connection.OpenAsync();
            await using var due = connection.CreateCommand();
            due.CommandText = "UPDATE download_task SET retry_next_at = '2000-01-01T00:00:00Z' WHERE queue_uuid = $uuid";
            due.Parameters.AddWithValue("$uuid", payload.Id);
            await due.ExecuteNonQueryAsync();
        }

        Assert.False(await retry.RunRetrySweepAsync());
        Assert.Equal("failed", (await _repository.GetByUuidAsync(payload.Id))!.Status);
        Assert.False(await _repository.HasScheduledRetriesAsync());
    }

    private EngineFallbackCoordinator BuildCoordinator(
        IAmazonFallbackTrackResolver amazon,
        DownloadQueueRepository? repository = null)
    {
        var catalog = new AppleMusicCatalogService(new NotFoundHttpClientFactory(), _settings,
            NullLogger<AppleMusicCatalogService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        var search = new EngineFallbackSearchService(catalog, NullLogger<EngineFallbackSearchService>.Instance,
            amazonFallbackTrackResolver: amazon);
        return new EngineFallbackCoordinator(repository ?? _repository, _settings,
            new DeezerIsrcResolver(null!, NullLogger<DeezerIsrcResolver>.Instance),
            search, new NullActivityLogWriter());
    }

    private EngineFallbackSearchService BuildFallbackSearchService()
    {
        var catalog = new AppleMusicCatalogService(new NotFoundHttpClientFactory(), _settings,
            NullLogger<AppleMusicCatalogService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        return new EngineFallbackSearchService(catalog, NullLogger<EngineFallbackSearchService>.Instance);
    }

    private static EngineFallbackSearchRequest BuildFallbackSearchRequest(string engine, string tidalId) => new(
        Engine: engine,
        SourceUrl: "https://www.deezer.com/track/4249878541",
        SpotifyId: string.Empty,
        AppleId: string.Empty,
        QobuzId: string.Empty,
        TidalId: tidalId,
        AmazonId: string.Empty,
        Isrc: "KEUM71900021",
        Title: "Pandana",
        Artist: "Ethic Entertainment",
        Album: "Pandana",
        DurationMs: 223000,
        DeezerId: "4249878541",
        Quality: "HI_RES",
        ContentType: "stereo",
        Storefront: "us",
        Language: "en-US",
        MediaUserToken: null,
        UserCountry: "US",
        FallbackSearchEnabled: false);

    private static QobuzQueueItem BuildPayload() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Engine = "qobuz", SourceService = "qobuz",
        SourceUrl = "https://play.qobuz.com/track/123", Title = "Track", Artist = "Artist",
        Album = "Album", Isrc = "USAAA1234567", DeezerId = "456", Quality = "27",
        ContentType = "stereo", AutoIndex = 0,
        FallbackPlan =
        [
            new("step-0", "qobuz", "27", [], "isrc"),
            new("step-1", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
            new("step-2", "deezer", "9", [], "direct_url")
        ]
    };

    private Task<long?> EnqueueAsync(QobuzQueueItem payload) => _repository.EnqueueAsync(new DownloadQueueItem(
        Id: 0, QueueUuid: payload.Id, Engine: payload.Engine, ArtistName: payload.Artist,
        TrackTitle: payload.Title, Isrc: payload.Isrc, DeezerTrackId: payload.DeezerId,
        DeezerAlbumId: null, DeezerArtistId: null, SpotifyTrackId: null,
        SpotifyAlbumId: null, SpotifyArtistId: null, AppleTrackId: null,
        AppleAlbumId: null, AppleArtistId: null, DurationMs: null,
        DestinationFolderId: null, QualityRank: null, QueueOrder: null,
        Status: "running", PayloadJson: JsonSerializer.Serialize(payload), Progress: 0,
        Downloaded: 0, Failed: 0, Error: null, CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow));

    public void Dispose()
    {
        _configScope.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class ThrowingAmazonResolver(bool httpFailure) : IAmazonFallbackTrackResolver
    {
        public int CallCount { get; private set; }
        public Task<AmazonFallbackTrackResolution?> ResolveAmazonFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, CancellationToken cancellationToken)
        {
            CallCount++;
            return httpFailure
                ? Task.FromException<AmazonFallbackTrackResolution?>(new HttpRequestException("Amazon unavailable"))
                : Task.FromException<AmazonFallbackTrackResolution?>(new InvalidOperationException("Amazon Music metadata session could not be initialized."));
        }

        public Task<AmazonFallbackTrackResolution?> ResolveAmazonAtmosFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, string? amazonId, CancellationToken cancellationToken)
            => ResolveAmazonFallbackTrackAsync(title, artist, album, durationMs, isrc, cancellationToken);
    }

    private sealed class CancelingAmazonResolver(CancellationTokenSource cancellation) : IAmazonFallbackTrackResolver
    {
        public Task<AmazonFallbackTrackResolution?> ResolveAmazonFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<AmazonFallbackTrackResolution?>(cancellationToken);
        }

        public Task<AmazonFallbackTrackResolution?> ResolveAmazonAtmosFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, string? amazonId, CancellationToken cancellationToken)
            => ResolveAmazonFallbackTrackAsync(title, artist, album, durationMs, isrc, cancellationToken);
    }

    private sealed class NotFoundHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());
        private sealed class NotFoundHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
