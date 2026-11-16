using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Services.Download.Deezer;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DownloadQueueEnqueueHelperTest
{
    [Fact]
    public async Task EnqueueWithDedupAsync_ReturnsQueueDuplicate_WhenMatchingItemIsQueued()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("queued-1");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_DoesNotAutoRequeueCancelledDuplicate()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("cancelled-dup-1");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            payload.Id,
            "canceled",
            cancellationToken: CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);

        var persisted = await context.QueueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("canceled", persisted!.Status, ignoreCase: true);
    }

    [Fact]
    public async Task RetryDownloadAsync_MonitoringOrigin_CannotRequeueCancelledRow()
    {
        await using var context = await CreateContextAsync();
        var (app, registry) = CreateApp(context);
        var payload = CreatePayload("monitoring-cancelled-retry");
        payload.AutoIndex = 1;
        payload.FallbackPlan = [new("step-0", "qobuz", "27", [], "direct_url")];
        payload.FallbackHistory = [new("step-0", "skipped", "provider_timeout", "qobuz timed out")];
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await app.CancelDownloadAsync(payload.Id);
        Assert.True(registry.WasUserCanceled(payload.Id));

        // Monitoring is an automatic origin: it must lose to the durable cancellation
        // and must not rewrite the cancelled row's payload, engine or cancel mark.
        var accepted = await app.RetryDownloadAsync(payload.Id, origin: QueueRequeueOrigin.Monitoring);

        Assert.False(accepted);
        var persisted = await context.QueueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("canceled", persisted!.Status, ignoreCase: true);
        var restoredPayload = System.Text.Json.JsonSerializer.Deserialize<QobuzQueueItem>(persisted.PayloadJson!);
        Assert.NotNull(restoredPayload);
        Assert.Equal(1, restoredPayload!.AutoIndex);
        Assert.Single(restoredPayload.FallbackHistory);
        Assert.Equal("qobuz", persisted.Engine, ignoreCase: true);
        Assert.True(registry.WasUserCanceled(payload.Id));
    }

    [Fact]
    public async Task RetryDownloadAsync_ClearsStaleResolutionFieldsFromThePreviousFailure()
    {
        // A direct retry must not carry the previous failure's resolution record: the stale
        // ResolutionError would be preferred over a fresh failure's message when the next
        // attempt fails (EngineAudioPostDownloadHelper prefers payload.ResolutionError), and
        // a stale ResolutionStatus makes the row look still-failed while it is queued again.
        await using var context = await CreateContextAsync();
        var (app, _) = CreateApp(context);
        var payload = CreatePayload("stale-resolution-retry");
        payload.AutoIndex = 1;
        payload.ResolutionStatus = "failed";
        payload.ResolutionError = "qobuz catalog lookup failed: 403 (Forbidden)";
        payload.ResolvedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4);
        payload.FallbackPlan = [new("step-0", "qobuz", "27", [], "direct_url")];
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "failed"), CancellationToken.None);

        var accepted = await app.RetryDownloadAsync(payload.Id, origin: QueueRequeueOrigin.Monitoring);

        Assert.True(accepted);
        var persisted = await context.QueueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        var retriedPayload = JsonSerializer.Deserialize<QobuzQueueItem>(persisted!.PayloadJson!);
        Assert.NotNull(retriedPayload);
        Assert.True(string.IsNullOrWhiteSpace(retriedPayload!.ResolutionError), $"ResolutionError survived the retry: {retriedPayload.ResolutionError}");
        Assert.True(string.IsNullOrWhiteSpace(retriedPayload.ResolutionStatus), $"ResolutionStatus survived the retry: {retriedPayload.ResolutionStatus}");
        Assert.Null(retriedPayload.ResolvedAtUtc);
    }

    [Fact]
    public async Task RetryDownloadAsync_WakesTheProcessorOnlyAfterThePreparedStateIsPersisted()
    {
        // The requeue flips the row to queued and pulses the queue wake signal. A processor
        // waking on that signal must never observe the pre-retry payload (stale engine,
        // stale AutoIndex, stale fallback history) or the pre-retry artifacts.
        await using var context = await CreateContextAsync();
        var (app, _) = CreateApp(context);
        var payload = CreatePayload("wake-race-retry");
        payload.AutoIndex = 2;
        payload.FallbackPlan =
        [
            new("step-0", "qobuz", "27", [], "direct_url"),
            new("step-1", "amazon", "HD_FLAC", [], "mapped_url"),
            new("step-2", "deezer", "9", [], "direct_url")
        ];
        payload.FallbackHistory = [new("step-2", "skipped", "provider_timeout", "deezer timed out")];
        payload.ResolutionError = "deezer mapping timed out";
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "failed"), CancellationToken.None);
        await context.QueueRepository.UpdateEngineAsync(payload.Id, "deezer", CancellationToken.None);

        var observedStale = new List<string>();
        void OnQueueStateChanged(DownloadQueueRepository.QueueStateChangedEvent change)
        {
            if (!string.Equals(change.QueueUuid, payload.Id, StringComparison.Ordinal))
            {
                return;
            }

            var row = context.QueueRepository.GetByUuidAsync(payload.Id).GetAwaiter().GetResult();
            if (row is null || !string.Equals(row.Status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var wakePayload = JsonSerializer.Deserialize<QobuzQueueItem>(row.PayloadJson!)!;
            if (wakePayload.AutoIndex != 0
                || wakePayload.FallbackHistory.Count != 0
                || !string.IsNullOrWhiteSpace(wakePayload.ResolutionError))
            {
                observedStale.Add($"payload (autoIndex={wakePayload.AutoIndex}, history={wakePayload.FallbackHistory.Count}, resolutionError={wakePayload.ResolutionError})");
            }

            if (!string.Equals(row.Engine, "qobuz", StringComparison.OrdinalIgnoreCase))
            {
                observedStale.Add($"engine ({row.Engine})");
            }
        }

        DownloadQueueRepository.QueueStateChanged += OnQueueStateChanged;
        try
        {
            var accepted = await app.RetryDownloadAsync(payload.Id, origin: QueueRequeueOrigin.Monitoring);
            Assert.True(accepted);
        }
        finally
        {
            DownloadQueueRepository.QueueStateChanged -= OnQueueStateChanged;
        }

        Assert.True(observedStale.Count == 0, $"The processor was woken with stale state: {string.Join("; ", observedStale)}");
    }

    [Fact]
    public async Task RetryDownloadAsync_ClearsTheStaleRetryScheduleBeforeWakingTheProcessor()
    {
        // The stale-schedule cleanup must land BEFORE the queued notification: a processor
        // woken by the signal may fail and schedule a fresh retry before the retry method
        // finishes, and a cleanup that runs afterwards would erase that fresh schedule and
        // strand the row in retry_waiting without a due time.
        await using var context = await CreateContextAsync();
        var (app, _) = CreateApp(context);
        var payload = CreatePayload("wake-retry-clear");
        payload.AutoIndex = 0;
        payload.FallbackPlan = [new("step-0", "qobuz", "27", [], "direct_url")];
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "failed"), CancellationToken.None);
        // An earlier failure left a stale retry schedule pending on the row.
        Assert.True(await context.QueueRepository.ScheduleRetryAsync(payload.Id, "qobuz", "earlier failure", 3));

        void OnQueueStateChanged(DownloadQueueRepository.QueueStateChangedEvent change)
        {
            if (!string.Equals(change.QueueUuid, payload.Id, StringComparison.Ordinal)
                || !string.Equals(change.Status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Simulate a processor that wakes on the signal, runs, fails and schedules a new
            // retry before the retry method continues past the requeue.
            var wokenScheduler = new DownloadRetryScheduler(
                context.QueueRepository,
                context.SettingsService,
                new NullActivityLogWriter(),
                new DeezSpoTagListener(),
                NullLogger<DownloadRetryScheduler>.Instance,
                new DownloadCancellationRegistry());
            wokenScheduler.ScheduleRetryAsync(payload.Id, "qobuz", "fresh failure after the wake")
                .GetAwaiter().GetResult();
        }

        DownloadQueueRepository.QueueStateChanged += OnQueueStateChanged;
        try
        {
            var accepted = await app.RetryDownloadAsync(payload.Id, origin: QueueRequeueOrigin.Monitoring);
            Assert.True(accepted);
        }
        finally
        {
            DownloadQueueRepository.QueueStateChanged -= OnQueueStateChanged;
        }

        var row = await context.QueueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(row);
        await using (var db = new SqliteConnection($"Data Source={Path.Join(context.TempRoot, "queue.db")}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT retry_next_at, status FROM download_task WHERE queue_uuid = $uuid";
            command.Parameters.AddWithValue("$uuid", payload.Id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.IsDBNull(0),
                "The fresh retry schedule scheduled after the wake must survive the retry's own cleanup.");
            Assert.Equal("retry_waiting", reader.GetString(1), ignoreCase: true);
        }
    }

    [Fact]
    public async Task RetryDownloadAsync_MonitoringOrigin_RequeuesFailedItem()
    {
        await using var context = await CreateContextAsync();
        var (app, registry) = CreateApp(context);
        var payload = CreatePayload("monitoring-failed-retry");
        payload.AutoIndex = 1;
        payload.FallbackPlan = [new("step-0", "qobuz", "27", [], "direct_url")];
        payload.FallbackHistory = [new("step-0", "skipped", "provider_timeout", "qobuz timed out")];
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "failed"), CancellationToken.None);

        var accepted = await app.RetryDownloadAsync(payload.Id, origin: QueueRequeueOrigin.Monitoring);

        Assert.True(accepted);
        var persisted = await context.QueueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("queued", persisted!.Status, ignoreCase: true);
        Assert.Equal("qobuz", persisted.Engine, ignoreCase: true);
        Assert.False(registry.WasUserCanceled(payload.Id));
    }

    [Fact]
    public async Task RetryDownloadAsync_ManualRequeueOfCancelledItem_ClearsCancellation()
    {
        await using var context = await CreateContextAsync();
        var (app, registry) = CreateApp(context);
        var payload = CreatePayload("manual-cancelled-retry");
        payload.AutoIndex = 1;
        payload.FallbackPlan = [new("step-0", "qobuz", "27", [], "direct_url")];
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await app.CancelDownloadAsync(payload.Id);
        Assert.True(registry.WasUserCanceled(payload.Id));

        // Manual retry is the only origin allowed to leave the cancelled state.
        var accepted = await app.RetryDownloadAsync(payload.Id);

        Assert.True(accepted);
        var persisted = await context.QueueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("queued", persisted!.Status, ignoreCase: true);
        Assert.False(registry.WasUserCanceled(payload.Id));
    }

    private static (DeezSpoTagApp App, DownloadCancellationRegistry Registry) CreateApp(TestContext context)
    {
        var cancellationRegistry = new DownloadCancellationRegistry();
        var listener = new DeezSpoTagListener(null);
        var retryScheduler = new DownloadRetryScheduler(
            context.QueueRepository,
            context.SettingsService,
            new NullActivityLogWriter(),
            listener,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadRetryScheduler>.Instance,
            cancellationRegistry);
        var app = new DeezSpoTagApp(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTagApp>.Instance,
            new DeezSpoTagApp.Dependencies(
                context.SettingsService,
                listener,
                retryScheduler,
                context.QueueRepository,
                cancellationRegistry,
                new AlwaysClosedExecutionGate()),
            context.ServiceProvider);
        return (app, cancellationRegistry);
    }

    private sealed class AlwaysOpenExecutionGate : IDownloadQueueExecutionGate
    {
        public Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DownloadQueueExecutionDecision(true, "open", string.Empty));
    }

    /// <summary>
    /// Keeps the queue processor from dequeuing so these tests observe the persisted
    /// requeue state instead of racing the live download worker.
    /// </summary>
    private sealed class AlwaysClosedExecutionGate : IDownloadQueueExecutionGate
    {
        public Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DownloadQueueExecutionDecision(false, "closed", "test harness"));
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_DoesNotAutoRehydrateFailedDuplicate()
    {
        await using var context = await CreateContextAsync();
        var existingPayload = CreatePayload("failed-stale-output-1");
        existingPayload.Isrc = "USAT20900265";
        existingPayload.FilePath = Path.Join(context.TempRoot, "downs", "Smash", "Mega Freestyle Box", "Smash - Crazy for Love.flac");
        existingPayload.Files = new List<Dictionary<string, object>>
        {
            new()
            {
                ["path"] = existingPayload.FilePath,
                ["albumPath"] = Path.GetDirectoryName(existingPayload.FilePath)!,
                ["artistPath"] = Path.GetDirectoryName(Path.GetDirectoryName(existingPayload.FilePath)!)!
            }
        };

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(existingPayload, "failed"), CancellationToken.None);
        await context.QueueRepository.UpdateFinalDestinationsAsync(
            existingPayload.Id,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [existingPayload.FilePath] = Path.Join(context.TempRoot, "library", "Smash", "Mega Freestyle Box", "Smash - Crazy for Love.flac")
            }),
            JsonSerializer.Serialize(existingPayload),
            CancellationToken.None);

        var replacementPayload = CreatePayload("new-request-id");
        replacementPayload.Title = "Day Dreaming (feat. Akon, Snoop Dogg & T.I.)";
        replacementPayload.Artist = "DJ Drama";
        replacementPayload.Album = "Gangsta Grillz: The Album Vol. 2";
        replacementPayload.Isrc = existingPayload.Isrc;

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            replacementPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
        Assert.Equal(existingPayload.Id, outcome.QueueUuid);

        var persisted = await context.QueueRepository.GetByUuidAsync(existingPayload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("failed", persisted!.Status, ignoreCase: true);
        Assert.Equal("Shared Artist", persisted.ArtistName);
        Assert.Equal("Shared Track", persisted.TrackTitle);
        Assert.NotNull(persisted.FinalDestinationsJson);

        Assert.False(string.IsNullOrWhiteSpace(persisted.PayloadJson));
        var payload = JsonSerializer.Deserialize<QobuzQueueItem>(persisted.PayloadJson!);
        Assert.NotNull(payload);
        Assert.Equal(existingPayload.Id, payload!.Id);
        Assert.Equal("Shared Artist", payload.Artist);
        Assert.Equal("Shared Track", payload.Title);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_ReturnsRecentlyDownloaded_WhenMatchingItemCompleted()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("completed-1");
        payload.FilePath = Path.Join(context.TempRoot, "downloads", "Shared Artist", "Shared Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(payload.FilePath)!);
        await File.WriteAllTextAsync(payload.FilePath, "audio", CancellationToken.None);

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            payload.Id,
            "completed",
            cancellationToken: CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_BlocksCompletedDuplicateWhenPayloadFileWasDeleted()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("completed-deleted-1");
        payload.FilePath = Path.Join(context.TempRoot, "downloads", "Shared Artist", "Shared Track.flac");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            payload.Id,
            "completed",
            cancellationToken: CancellationToken.None);

        var retryPayload = CreatePayload("completed-deleted-2");
        retryPayload.FilePath = payload.FilePath;
        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            retryPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
        Assert.Equal(payload.Id, outcome.QueueUuid);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_AllowsSameTrackInDifferentDestinationFolder()
    {
        await using var context = await CreateContextAsync();
        var firstPayload = CreatePayload("dest-queued-1", destinationFolderId: 101);
        var secondPayload = CreatePayload("dest-queued-2", destinationFolderId: 202);

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(firstPayload, "queued"), CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            secondPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(outcome.AlreadyQueued);

        var queuedItems = await context.QueueRepository.GetTasksAsync(firstPayload.Engine, CancellationToken.None);
        Assert.Equal(2, queuedItems.Count);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_PersistsDestinationFolderIdAcrossRepositoryInstances()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("dest-persist-1", destinationFolderId: 909);

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);

        var restartedRepository = BuildRepository(context.TempRoot, context.QueueDbPath);
        var persisted = await restartedRepository.GetByUuidAsync(payload.Id, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(payload.DestinationFolderId, persisted!.DestinationFolderId);
    }

    [Fact]
    public async Task EnqueueAsync_ReturnsNull_WhenInsertIsIgnoredByQueueUuidConstraint()
    {
        await using var context = await CreateContextAsync();
        var existingPayload = CreatePayload("insert-ignore-1");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(existingPayload, "queued"), CancellationToken.None);

        var conflictingPayload = CreatePayload("insert-ignore-1");
        conflictingPayload.Artist = "Different Artist";
        conflictingPayload.Title = "Different Track";

        var result = await context.QueueRepository.EnqueueAsync(CreateQueueItem(conflictingPayload, "queued"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_ReturnsQueueDuplicate_WhenInsertIsIgnored()
    {
        await using var context = await CreateContextAsync();
        var existingPayload = CreatePayload("helper-ignore-1");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(existingPayload, "queued"), CancellationToken.None);

        var conflictingPayload = CreatePayload("helper-ignore-1");
        conflictingPayload.Artist = "Different Artist";
        conflictingPayload.Title = "Different Track";

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            conflictingPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_AllowsDifferentTracksWithSharedAlbumAndArtistIds()
    {
        await using var context = await CreateContextAsync();
        var firstPayload = CreateDeezerPayload("deezer-shared-1", "First Track", "dz-track-1");
        var secondPayload = CreateDeezerPayload("deezer-shared-2", "Second Track", "dz-track-2");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(firstPayload, "queued"), CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            secondPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(outcome.AlreadyQueued);

        var queuedItems = await context.QueueRepository.GetTasksAsync(firstPayload.Engine, CancellationToken.None);
        Assert.Equal(2, queuedItems.Count);
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-download-queue-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        var queueDb = Path.Join(tempRoot, "queue.db");
        var queueRepository = BuildRepository(tempRoot, queueDb);
        var libraryDb = Path.Join(tempRoot, "library.db");
        var libraryConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={libraryDb}"
            })
            .Build();
        var libraryDbService = new LibraryDbService(libraryConfiguration, NullLogger<LibraryDbService>.Instance);
        await libraryDbService.EnsureSchemaAsync(CancellationToken.None);
        var libraryRepository = new LibraryRepository(libraryConfiguration, NullLogger<LibraryRepository>.Instance);
        var dedupeService = new DownloadDedupeService(
            queueRepository,
            libraryRepository,
            NullLogger<DownloadDedupeService>.Instance,
            new PassthroughLocalTrackAmbiguityResolver());
        var previousConfigDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", Path.Join(tempRoot, "config"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.DownloadLocation = Path.Join(tempRoot, "library");
        settings.TracknameTemplate = "%artist% - %title%";
        settingsService.SaveSettings(settings);
        var serviceProvider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance))
            .AddSingleton<IDownloadTagSettingsResolver>(new TestDownloadTagSettingsResolver())
            .BuildServiceProvider();
        return new TestContext(tempRoot, queueDb, queueRepository, dedupeService, settingsService, serviceProvider, previousConfigDir);
    }

    private static DownloadQueueRepository BuildRepository(string tempRoot, string queueDbPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = tempRoot
            })
            .Build();

        return new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
    }

    private static QobuzQueueItem CreatePayload(string queueUuid, long? destinationFolderId = null)
    {
        return new QobuzQueueItem
        {
            Id = queueUuid,
            Title = "Shared Track",
            Artist = "Shared Artist",
            Album = "Shared Album",
            AlbumArtist = "Shared Artist",
            Cover = "",
            Quality = "27",
            SourceUrl = "https://play.qobuz.com/track/123",
            QobuzId = "123",
            DestinationFolderId = destinationFolderId,
            ContentType = string.Empty,
            DurationSeconds = 0
        };
    }

    private static DownloadQueueItem CreateQueueItem(QobuzQueueItem payload, string status)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: payload.Id,
            Engine: payload.Engine,
            ArtistName: payload.Artist,
            TrackTitle: payload.Title,
            Isrc: payload.Isrc,
            DeezerTrackId: payload.DeezerId,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: payload.SpotifyId,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: payload.AppleId,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: payload.DestinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: payload.ContentType,
            Status: status,
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static DeezerQueueItem CreateDeezerPayload(string queueUuid, string title, string deezerTrackId)
    {
        return new DeezerQueueItem
        {
            Id = queueUuid,
            Title = title,
            Artist = "Shared Artist",
            Album = "Shared Album",
            AlbumArtist = "Shared Artist",
            DeezerId = deezerTrackId,
            DeezerAlbumId = "dz-album-1",
            DeezerArtistId = "dz-artist-1",
            SourceUrl = "https://www.deezer.com/track/123",
            DestinationFolderId = 303,
            ContentType = "stereo",
            DurationSeconds = 0
        };
    }

    private static DownloadQueueItem CreateQueueItem(DeezerQueueItem payload, string status)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: payload.Id,
            Engine: payload.Engine,
            ArtistName: payload.Artist,
            TrackTitle: payload.Title,
            Isrc: payload.Isrc,
            DeezerTrackId: payload.DeezerId,
            DeezerAlbumId: payload.DeezerAlbumId,
            DeezerArtistId: payload.DeezerArtistId,
            SpotifyTrackId: payload.SpotifyId,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: payload.AppleId,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: payload.DestinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: payload.ContentType,
            Status: status,
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            string tempRoot,
            string queueDbPath,
            DownloadQueueRepository queueRepository,
            DownloadDedupeService dedupeService,
            DeezSpoTagSettingsService settingsService,
            ServiceProvider serviceProvider,
            string? previousConfigDir)
        {
            TempRoot = tempRoot;
            QueueDbPath = queueDbPath;
            QueueRepository = queueRepository;
            DedupeService = dedupeService;
            SettingsService = settingsService;
            ServiceProvider = serviceProvider;
            PreviousConfigDir = previousConfigDir;
        }

        public string TempRoot { get; }
        public string QueueDbPath { get; }
        public DownloadQueueRepository QueueRepository { get; }
        public DownloadDedupeService DedupeService { get; }
        public DeezSpoTagSettingsService SettingsService { get; }
        public ServiceProvider ServiceProvider { get; }
        private string? PreviousConfigDir { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                ServiceProvider.Dispose();
                Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", PreviousConfigDir);
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDownloadTagSettingsResolver : IDownloadTagSettingsResolver
    {
        private static readonly DownloadTagProfileSettings Profile = new(
            new TagSettings(),
            "follow-download-engine",
            new FolderStructureSettings(),
            null,
            new DownloadProfileRuntimeOverrides(
                TracknameTemplate: "%artist% - %title%",
                SaveArtwork: null,
                SaveAnimatedArtwork: null,
                AnimatedArtworkFormats: null,
                DlAlbumcoverForPlaylist: null,
                SaveArtworkArtist: null,
                CoverImageTemplate: null,
                ArtistImageTemplate: null,
                LocalArtworkFormat: null,
                EmbedMaxQualityCover: null,
                JpegImageQuality: null));

        public Task<TagSettings?> ResolveAsync(long? destinationFolderId, CancellationToken cancellationToken)
            => Task.FromResult<TagSettings?>(Profile.TagSettings);

        public Task<DownloadTagProfileSettings?> ResolveProfileAsync(long? destinationFolderId, CancellationToken cancellationToken)
            => Task.FromResult<DownloadTagProfileSettings?>(Profile);
    }
}
