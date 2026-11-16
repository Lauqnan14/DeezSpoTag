using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The monitoring re-check lifecycle: the manual_unavailable_track record must survive a requeue
/// (so a failed re-attempt keeps being re-checked), an active row must not be reset by the
/// re-check, a completed row retires the record without requeueing, and a row that still exists
/// never falls through to a duplicate fresh enqueue.
/// </summary>
[Collection("Settings Config Isolation")]
public sealed class ManualUnavailableRetryLifecycleTest
{
    [Fact]
    public async Task Recheck_CompletedRow_RetiresTheRecordWithoutRequeueing()
    {
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-completed");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(dto.QueueUuid, "completed"), CancellationToken.None);

        var handled = await RecheckAsync(context, dto);

        Assert.True(handled);
        var row = await context.QueueRepository.GetByUuidAsync(dto.QueueUuid, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("completed", row!.Status, ignoreCase: true);
        Assert.False(await context.LibraryRepository.IsManualUnavailableTrackMonitoredAsync(dto.QueueUuid, CancellationToken.None));
    }

    [Fact]
    public async Task Recheck_ActiveRow_ReschedulesInsteadOfRequeueing()
    {
        // The row is already queued: resetting it in place would discard the live attempt's
        // progress and could double-process it. The re-check waits for the outcome instead.
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-active");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(dto.QueueUuid, "queued"), CancellationToken.None);

        var handled = await RecheckAsync(context, dto);

        Assert.True(handled);
        var row = await context.QueueRepository.GetByUuidAsync(dto.QueueUuid, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("queued", row!.Status, ignoreCase: true);
        var record = await GetRecordAsync(context, dto.QueueUuid);
        Assert.NotNull(record);
        Assert.True(
            record!.NextRetryAtUtc > DateTimeOffset.UtcNow.AddMinutes(5),
            $"The record must be rescheduled, got next retry at {record.NextRetryAtUtc:O}");
    }

    [Fact]
    public async Task Recheck_RequeuedFailedRow_KeepsTheRecordForTheNextRecheck()
    {
        // Deleting the record immediately after queueing a retry abandons monitoring: if the
        // re-attempt fails again, nothing re-checks the track any more. The record must be
        // rescheduled and only retire on a terminal conclusion (obtained or cancelled).
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-requeued");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(dto.QueueUuid, "failed"), CancellationToken.None);

        var handled = await RecheckAsync(context, dto);

        Assert.True(handled);
        var row = await context.QueueRepository.GetByUuidAsync(dto.QueueUuid, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("queued", row!.Status, ignoreCase: true);
        var record = await GetRecordAsync(context, dto.QueueUuid);
        Assert.NotNull(record);
        Assert.True(
            record!.NextRetryAtUtc > DateTimeOffset.UtcNow.AddMinutes(5),
            $"The record must stay scheduled for the next re-check, got {record.NextRetryAtUtc:O}");
    }

    [Fact]
    public async Task Recheck_CancelledRow_RetiresTheRecordWithoutRequeueing()
    {
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-cancelled");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(dto.QueueUuid, "canceled"), CancellationToken.None);

        var handled = await RecheckAsync(context, dto);

        Assert.True(handled);
        var row = await context.QueueRepository.GetByUuidAsync(dto.QueueUuid, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("canceled", row!.Status, ignoreCase: true);
        Assert.False(await context.LibraryRepository.IsManualUnavailableTrackMonitoredAsync(dto.QueueUuid, CancellationToken.None));
    }

    [Fact]
    public async Task FreshEnqueueForMissingRow_RebindsMonitoringToTheNewQueueRow()
    {
        // The original queue row is gone and a fresh intent was enqueued as a NEW row. Keeping
        // the record on the deleted uuid would never observe the new row's outcome, so the
        // record must be rebound to the new queue uuid.
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-missing-row");
        var service = new ManualUnavailableRetryService(
            context.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ManualUnavailableRetryService>.Instance);
        var method = typeof(ManualUnavailableRetryService).GetMethod(
            "RecordFreshEnqueueOutcomeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var queued = new DownloadIntentResult
        {
            Success = true,
            Engine = "qobuz",
            Queued = ["fresh-row-1"],
            Message = "Queued 1 item(s)."
        };
        await (Task)method!.Invoke(service, [context.LibraryRepository, dto, queued, CancellationToken.None])!;

        var rebound = await GetRecordAsync(context, "fresh-row-1");
        Assert.NotNull(rebound);
        Assert.Equal("fresh-row-1", rebound!.QueueUuid);
        Assert.True(
            rebound.NextRetryAtUtc > DateTimeOffset.UtcNow.AddMinutes(5),
            $"The rebound record must stay scheduled for the next re-check, got {rebound.NextRetryAtUtc:O}");
        Assert.False(
            await context.LibraryRepository.IsManualUnavailableTrackMonitoredAsync(dto.QueueUuid, CancellationToken.None),
            "The record must not stay on the deleted original queue uuid.");
    }

    [Fact]
    public async Task ReboundMonitoring_WatchesTheNewRowUntilItConcludes()
    {
        // The rebound record's re-check must observe the NEW row's actual state: an active row
        // is waited on, not reset and not requeued.
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-rebound-active");
        var service = new ManualUnavailableRetryService(
            context.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ManualUnavailableRetryService>.Instance);
        var method = typeof(ManualUnavailableRetryService).GetMethod(
            "RecordFreshEnqueueOutcomeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var queued = new DownloadIntentResult
        {
            Success = true,
            Engine = "qobuz",
            Queued = ["fresh-row-2"],
            Message = "Queued 1 item(s)."
        };
        await (Task)method!.Invoke(service, [context.LibraryRepository, dto, queued, CancellationToken.None])!;
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("fresh-row-2", "queued"), CancellationToken.None);

        var rebound = await GetRecordAsync(context, "fresh-row-2");
        Assert.NotNull(rebound);

        var handled = await RecheckAsync(context, rebound!);

        Assert.True(handled);
        var row = await context.QueueRepository.GetByUuidAsync("fresh-row-2", CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("queued", row!.Status, ignoreCase: true);
        var stillMonitored = await GetRecordAsync(context, "fresh-row-2");
        Assert.NotNull(stillMonitored);
    }

    [Fact]
    public async Task ReboundMonitoring_RetiresWhenTheNewRowCompletes()
    {
        // The full loop on the rebound record: once the NEW row completes, the next re-check
        // observes it and retires the record - proving monitoring actually follows the new row.
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-rebound-completed");
        var service = new ManualUnavailableRetryService(
            context.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ManualUnavailableRetryService>.Instance);
        var method = typeof(ManualUnavailableRetryService).GetMethod(
            "RecordFreshEnqueueOutcomeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var queued = new DownloadIntentResult
        {
            Success = true,
            Engine = "qobuz",
            Queued = ["fresh-row-3"],
            Message = "Queued 1 item(s)."
        };
        await (Task)method!.Invoke(service, [context.LibraryRepository, dto, queued, CancellationToken.None])!;
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("fresh-row-3", "completed"), CancellationToken.None);

        var rebound = await GetRecordAsync(context, "fresh-row-3");
        Assert.NotNull(rebound);

        var handled = await RecheckAsync(context, rebound!);

        Assert.True(handled);
        Assert.False(
            await context.LibraryRepository.IsManualUnavailableTrackMonitoredAsync("fresh-row-3", CancellationToken.None),
            "The rebound record must retire once the new row completes.");
    }

    [Fact]
    public async Task FreshEnqueueRejection_KeepsMonitoringWithTheSkipReason()
    {
        // A rejected fresh enqueue (duplicate/unavailable) must also keep the record scheduled,
        // carrying the rejection reason for the next re-check.
        await using var context = await CreateContextAsync();
        var dto = await UpsertTrackAsync(context, "lifecycle-rejected");
        var service = new ManualUnavailableRetryService(
            context.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ManualUnavailableRetryService>.Instance);
        var method = typeof(ManualUnavailableRetryService).GetMethod(
            "RecordFreshEnqueueOutcomeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var rejected = new DownloadIntentResult
        {
            Success = false,
            Engine = "qobuz",
            Skipped = 1,
            Message = "Duplicate of an existing download.",
            SkipReasonCodes = ["queue_duplicate"],
            SkipReasons = ["Duplicate of an existing download."]
        };
        await (Task)method!.Invoke(service, [context.LibraryRepository, dto, rejected, CancellationToken.None])!;

        var record = await GetRecordAsync(context, dto.QueueUuid);
        Assert.NotNull(record);
        Assert.Contains("Duplicate of an existing download.", record!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ManualUnavailableTrackDto?> GetRecordAsync(TestContext context, string queueUuid)
    {
        var records = await context.LibraryRepository.GetDueManualUnavailableTracksAsync(
            DateTimeOffset.UtcNow.AddDays(30),
            50,
            CancellationToken.None);
        foreach (var record in records)
        {
            if (string.Equals(record.QueueUuid, queueUuid, StringComparison.Ordinal))
            {
                return record;
            }
        }

        return null;
    }

    private static async Task<bool> RecheckAsync(TestContext context, ManualUnavailableTrackDto dto)
    {
        var cancellationRegistry = new DownloadCancellationRegistry();
        var listener = new DeezSpoTagListener(null);
        var retryScheduler = new DownloadRetryScheduler(
            context.QueueRepository,
            context.SettingsService,
            new NullActivityLogWriter(),
            listener,
            NullLogger<DownloadRetryScheduler>.Instance,
            cancellationRegistry);
        var app = new DeezSpoTagApp(
            NullLogger<DeezSpoTagApp>.Instance,
            new DeezSpoTagApp.Dependencies(
                context.SettingsService,
                listener,
                retryScheduler,
                context.QueueRepository,
                cancellationRegistry,
                new AlwaysClosedExecutionGate()),
            context.ServiceProvider);
        var service = new ManualUnavailableRetryService(
            context.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ManualUnavailableRetryService>.Instance);

        var method = typeof(ManualUnavailableRetryService).GetMethod(
            "TryRequeueExistingRowAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return await (Task<bool>)method!.Invoke(service, [context.LibraryRepository, app, dto, CancellationToken.None])!;
    }

    private static async Task<ManualUnavailableTrackDto> UpsertTrackAsync(TestContext context, string queueUuid)
    {
        var dto = await context.LibraryRepository.UpsertManualUnavailableTrackAsync(
            new ManualUnavailableTrackUpsertInput(
                QueueUuid: queueUuid,
                Title: "Shared Track",
                Artist: "Shared Artist",
                Album: "Shared Album",
                AlbumArtist: "Shared Artist",
                CoverUrl: null,
                DurationMs: null,
                TrackNumber: null,
                TrackTotal: null,
                DiscNumber: null,
                DiscTotal: null,
                ReleaseDate: null,
                Explicit: null,
                Isrc: null,
                Engine: "qobuz",
                SourceService: "qobuz",
                SourceUrl: "https://play.qobuz.com/track/123",
                DeezerId: null,
                SpotifyId: null,
                AppleId: null,
                QobuzId: "123",
                TidalId: null,
                AmazonId: null,
                DestinationFolderId: null,
                ExpectedFinalPath: null,
                Quality: "27",
                ContentType: "music",
                Reason: "Track unavailable among the enabled services",
                PayloadJson: null),
            CancellationToken.None);
        Assert.NotNull(dto);
        return dto!;
    }

    private static DownloadQueueItem CreateQueueItem(string queueUuid, string status)
        => new(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
            ArtistName: "Shared Artist",
            TrackTitle: "Shared Track",
            Isrc: null,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: null,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "music",
            Status: status,
            PayloadJson: JsonSerializer.Serialize(new QobuzQueueItem
            {
                Id = queueUuid,
                Title = "Shared Track",
                Artist = "Shared Artist",
                Album = "Shared Album",
                Quality = "27",
                SourceUrl = "https://play.qobuz.com/track/123",
                QobuzId = "123",
                ContentType = "music",
                FallbackPlan = [new("step-0", "qobuz", "27", [], "direct_url")]
            }),
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static async Task<TestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-manual-retry-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        var queueDb = Path.Join(tempRoot, "queue.db");
        var queueConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDb}"
            })
            .Build();
        var queueRepository = new DownloadQueueRepository(queueConfiguration, NullLogger<DownloadQueueRepository>.Instance);

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

        var previousConfigDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", Path.Join(tempRoot, "config"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.DownloadLocation = Path.Join(tempRoot, "library");
        settingsService.SaveSettings(settings);

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        return new TestContext(tempRoot, queueRepository, libraryRepository, settingsService, serviceProvider, previousConfigDir);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            string tempRoot,
            DownloadQueueRepository queueRepository,
            LibraryRepository libraryRepository,
            DeezSpoTagSettingsService settingsService,
            IServiceProvider serviceProvider,
            string? previousConfigDir)
        {
            TempRoot = tempRoot;
            QueueRepository = queueRepository;
            LibraryRepository = libraryRepository;
            SettingsService = settingsService;
            ServiceProvider = serviceProvider;
            PreviousConfigDir = previousConfigDir;
        }

        public string TempRoot { get; }
        public DownloadQueueRepository QueueRepository { get; }
        public LibraryRepository LibraryRepository { get; }
        public DeezSpoTagSettingsService SettingsService { get; }
        public IServiceProvider ServiceProvider { get; }
        private string? PreviousConfigDir { get; }

        public ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", PreviousConfigDir);
            try
            {
                Directory.Delete(TempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temporary test artifacts.
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlwaysClosedExecutionGate : IDownloadQueueExecutionGate
    {
        public Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DownloadQueueExecutionDecision(false, "closed", "test harness"));
    }
}
