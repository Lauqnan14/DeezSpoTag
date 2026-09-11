using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Apple;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DownloadQueueRecoveryServiceTest : IDisposable
{
    private static readonly string[] QobuzSourceUrlInput = ["source_url"];
    private static readonly string[] DeezerIdInput = ["deezer_id"];

    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly string _queueDbPath;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DownloadQueueRecoveryService _recoveryService;
    private readonly DeezSpoTagSettingsService _settingsService;

    public DownloadQueueRecoveryServiceTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-queue-recovery-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
        _queueDbPath = Path.Join(_tempRoot, "queue.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={_queueDbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();

        _queueRepository = new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
        _cancellationRegistry = new DownloadCancellationRegistry();
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = _settingsService.LoadSettings();
        settings.MaxRetries = 0;
        settings.DownloadLocation = _tempRoot;
        _settingsService.SaveSettings(settings);

        var retryScheduler = new DownloadRetryScheduler(
            _queueRepository,
            _settingsService,
            new NullActivityLogWriter(),
            new DeezSpoTagListener(),
            NullLogger<DownloadRetryScheduler>.Instance,
            _cancellationRegistry);
        var appleCatalogService = new AppleMusicCatalogService(
            new StubHttpClientFactory(),
            _settingsService,
            NullLogger<AppleMusicCatalogService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        var fallbackSearchService = new EngineFallbackSearchService(
            appleCatalogService,
            NullLogger<EngineFallbackSearchService>.Instance);
        var fallbackCoordinator = new EngineFallbackCoordinator(
            _queueRepository,
            _settingsService,
            new DeezerIsrcResolver(
                deezerApi: null!,
                NullLogger<DeezerIsrcResolver>.Instance),
            fallbackSearchService,
            new NullActivityLogWriter());

        var runtime = new DownloadQueueRecoveryRuntime(
            retryScheduler,
            new NullActivityLogWriter(),
            new DeezSpoTagListener());
        _recoveryService = new DownloadQueueRecoveryService(
            _queueRepository,
            _cancellationRegistry,
            runtime,
            _settingsService,
            NullLogger<DownloadQueueRecoveryService>.Instance);
    }

    [Fact]
    public async Task RecoverStaleRunningTasksAsync_RetriesPersistedEngineWithoutCrossEngineResolution()
    {
        var queueUuid = "recovery-qobuz-to-deezer";
        var payload = new QobuzQueueItem
        {
            Id = queueUuid,
            Engine = "qobuz",
            SourceService = "qobuz",
            SourceUrl = "https://play.qobuz.com/track/301435615",
            Title = "Nairobi",
            Artist = "Marioo",
            Album = "The Godson",
            Isrc = "ZA56E2420399",
            DeezerId = "3094483121",
            Quality = "27",
            AutoIndex = 0,
            FallbackPlan = new List<FallbackPlanStep>
            {
                new("qobuz-27", "qobuz", "27", QobuzSourceUrlInput, "direct_url"),
                new("deezer-9", "deezer", "9", DeezerIdInput, "deezer_track_id")
            }
        };

        await EnqueueRunningItemAsync(queueUuid, payload);
        await AgeQueueItemAsync(queueUuid, DownloadQueueRecoveryPolicy.RunningStallThreshold + TimeSpan.FromMinutes(1));

        await _recoveryService.RecoverStaleRunningTasksAsync(CancellationToken.None);

        var recovered = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("failed", recovered!.Status);
        Assert.Equal("qobuz", recovered.Engine);

        var recoveredPayload = JsonSerializer.Deserialize<QobuzQueueItem>(recovered.PayloadJson!);
        Assert.NotNull(recoveredPayload);
        Assert.Equal("qobuz", recoveredPayload!.Engine);
        Assert.Equal("qobuz", recoveredPayload.SourceService);
        Assert.Equal(0, recoveredPayload.AutoIndex);
        Assert.Equal("https://play.qobuz.com/track/301435615", recoveredPayload.SourceUrl);
    }

    [Fact]
    public async Task RecoverStaleRunningTasksAsync_CancelsActiveStalledItemWithTimeoutReason()
    {
        var queueUuid = "recovery-active-timeout";
        var payload = new QobuzQueueItem
        {
            Id = queueUuid,
            Engine = "qobuz",
            SourceService = "qobuz",
            Title = "Timeout Track",
            Artist = "Timeout Artist",
            Quality = "27",
            FallbackPlan = new List<FallbackPlanStep>
            {
                new("qobuz-27", "qobuz", "27", QobuzSourceUrlInput, "direct_url")
            }
        };

        await EnqueueRunningItemAsync(queueUuid, payload);
        await AgeQueueItemAsync(queueUuid, DownloadQueueRecoveryPolicy.RunningStallThreshold + TimeSpan.FromMinutes(1));

        using var cts = new CancellationTokenSource();
        _cancellationRegistry.Register(queueUuid, cts);

        await _recoveryService.RecoverStaleRunningTasksAsync(CancellationToken.None);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(_cancellationRegistry.WasTimedOut(queueUuid));

        var persisted = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("running", persisted!.Status);
    }

    [Fact]
    public async Task RecoverStaleRunningTasksAsync_PromotesAcquiredAudioWithDestinationInsteadOfRetrying()
    {
        var queueUuid = "recovery-acquired-audio";
        var audioPath = Path.Join(_tempRoot, "Artist", "Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await File.WriteAllTextAsync(audioPath, "audio");
        var payload = new QobuzQueueItem
        {
            Id = queueUuid,
            Engine = "qobuz",
            SourceService = "qobuz",
            Title = "Recovered Track",
            Artist = "Recovered Artist",
            Quality = "27",
            FilePath = audioPath,
            AudioAcquired = true,
            AcquiredAudioPath = audioPath,
            DestinationFolderId = 9,
            FallbackPlan = new List<FallbackPlanStep>
            {
                new("qobuz-27", "qobuz", "27", QobuzSourceUrlInput, "direct_url")
            }
        };

        await EnqueueRunningItemAsync(queueUuid, payload, destinationFolderId: 9);
        await AgeQueueItemAsync(queueUuid, DownloadQueueRecoveryPolicy.OrphanedRunningThreshold + TimeSpan.FromSeconds(5));

        await _recoveryService.RecoverStaleRunningTasksAsync(CancellationToken.None);

        var recovered = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("completed", recovered!.Status);
        Assert.Equal("pending", recovered.EnrichmentStatus);
        Assert.Equal("pending", recovered.FinalizationStatus);
        Assert.Equal(9, recovered.DestinationFolderId);
        Assert.True(File.Exists(audioPath));
    }

    [Fact]
    public async Task RecoverStaleRunningTasksAsync_DoesNotPromoteRunningItemWithoutAcquiredAudio()
    {
        var queueUuid = "recovery-no-audio";
        var payload = new QobuzQueueItem
        {
            Id = queueUuid,
            Engine = "qobuz",
            SourceService = "qobuz",
            Title = "Incomplete Track",
            Artist = "Incomplete Artist",
            Quality = "27",
            DestinationFolderId = 9,
            FallbackPlan = new List<FallbackPlanStep>
            {
                new("qobuz-27", "qobuz", "27", QobuzSourceUrlInput, "direct_url")
            }
        };

        await EnqueueRunningItemAsync(queueUuid, payload, destinationFolderId: 9);
        await AgeQueueItemAsync(queueUuid, DownloadQueueRecoveryPolicy.RunningStallThreshold + TimeSpan.FromMinutes(1));

        await _recoveryService.RecoverStaleRunningTasksAsync(CancellationToken.None);

        var recovered = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("failed", recovered!.Status);
    }

    [Fact]
    public async Task RecoverPendingPostDownloadWorkAsync_ReopensFailedItemWithAcquiredStagingFile()
    {
        var queueUuid = "recovery-failed-acquired";
        var audioPath = Path.Join(_tempRoot, "Artist", "Failed Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await File.WriteAllTextAsync(audioPath, "audio");
        var payload = new QobuzQueueItem
        {
            Id = queueUuid,
            Engine = "qobuz",
            Title = "Failed Track",
            Artist = "Artist",
            FilePath = audioPath,
            AudioAcquired = true,
            AcquiredAudioPath = audioPath,
            DestinationFolderId = 12
        };
        var queueItem = new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
            ArtistName: payload.Artist,
            TrackTitle: payload.Title,
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
            DestinationFolderId: 12,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "stereo",
            FinalizationStatus: "failed",
            EnrichmentStatus: "not_required",
            Status: "failed",
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 100,
            Downloaded: 1,
            Failed: 1,
            Error: "recovered after restart",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
        await _queueRepository.EnqueueAsync(queueItem, CancellationToken.None);

        await _recoveryService.RecoverPendingPostDownloadWorkAsync(CancellationToken.None);

        var recovered = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("completed", recovered!.Status);
        Assert.Equal("not_required", recovered.EnrichmentStatus);
        Assert.Equal("pending", recovered.FinalizationStatus);
        Assert.Equal(12, recovered.DestinationFolderId);
    }

    [Fact]
    public async Task RecoverPendingPostDownloadWorkAsync_LeavesCompletedPendingRowsUnchanged()
    {
        var queueUuid = "recovery-completed-pending";
        var audioPath = Path.Join(_tempRoot, "Artist", "Pending Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await File.WriteAllTextAsync(audioPath, "audio");
        await EnqueueCompletedItemAsync(
            queueUuid,
            audioPath,
            destinationFolderId: 4,
            enrichmentStatus: "pending",
            finalizationStatus: "pending");

        await _recoveryService.RecoverPendingPostDownloadWorkAsync(CancellationToken.None);

        var recovered = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("completed", recovered!.Status);
        Assert.Equal("pending", recovered.EnrichmentStatus);
        Assert.Equal("pending", recovered.FinalizationStatus);
        Assert.Equal(4, recovered.DestinationFolderId);
        Assert.True(File.Exists(audioPath));
        var candidates = await _queueRepository.GetPostDownloadRecoveryCandidatesAsync(CancellationToken.None);
        Assert.Contains(candidates, item => item.QueueUuid == queueUuid);
    }

    [Fact]
    public async Task RecoverPendingPostDownloadWorkAsync_ReopensBlockedRowWhenStagingFileExists()
    {
        var queueUuid = "recovery-blocked-staging";
        var audioPath = Path.Join(_tempRoot, "Artist", "Blocked Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await File.WriteAllTextAsync(audioPath, "audio");
        await EnqueueCompletedItemAsync(
            queueUuid,
            audioPath,
            destinationFolderId: 8,
            enrichmentStatus: "interrupted",
            finalizationStatus: "blocked");

        await _recoveryService.RecoverPendingPostDownloadWorkAsync(CancellationToken.None);

        var recovered = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("completed", recovered!.Status);
        Assert.Equal("pending", recovered.EnrichmentStatus);
        Assert.Equal("pending", recovered.FinalizationStatus);
        Assert.Equal(8, recovered.DestinationFolderId);
        Assert.True(File.Exists(audioPath));
    }

    public void Dispose()
    {
        _configScope.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private async Task EnqueueCompletedItemAsync(
        string queueUuid,
        string audioPath,
        long destinationFolderId,
        string enrichmentStatus,
        string finalizationStatus)
    {
        var payload = new QobuzQueueItem
        {
            Id = queueUuid,
            Engine = "qobuz",
            Title = "Track",
            Artist = "Artist",
            FilePath = audioPath,
            AudioAcquired = true,
            AcquiredAudioPath = audioPath,
            DestinationFolderId = destinationFolderId
        };
        var queueItem = new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
            ArtistName: payload.Artist,
            TrackTitle: payload.Title,
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
            DestinationFolderId: destinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "stereo",
            FinalizationStatus: finalizationStatus,
            EnrichmentStatus: enrichmentStatus,
            Status: "completed",
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 100,
            Downloaded: 1,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
        await _queueRepository.EnqueueAsync(queueItem, CancellationToken.None);
    }

    private async Task EnqueueRunningItemAsync(
        string queueUuid,
        QobuzQueueItem payload,
        long? destinationFolderId = null)
    {
        var queueItem = new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
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
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : null,
            DestinationFolderId: destinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            Status: "running",
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(payload),
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        await _queueRepository.EnqueueAsync(queueItem, CancellationToken.None);
    }

    private async Task AgeQueueItemAsync(string queueUuid, TimeSpan age)
    {
        await using var connection = new SqliteConnection($"Data Source={_queueDbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE download_task
SET updated_at = datetime('now', '-' || $ageSeconds || ' seconds')
WHERE queue_uuid = $queueUuid;";
        command.Parameters.AddWithValue("$queueUuid", queueUuid);
        command.Parameters.AddWithValue("$ageSeconds", Math.Max(1, (int)Math.Ceiling(age.TotalSeconds)));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler? handler = null)
        {
            _handler = handler ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private sealed class StubSpotifyIdResolver : ISpotifyIdResolver
    {
        private readonly string _spotifyId;

        public StubSpotifyIdResolver(string spotifyId)
        {
            _spotifyId = spotifyId;
        }

        public Task<string?> ResolveTrackIdAsync(
            string title,
            string artist,
            string? album,
            string? isrc,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(_spotifyId);
        }
    }
}
