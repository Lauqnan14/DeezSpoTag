using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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
