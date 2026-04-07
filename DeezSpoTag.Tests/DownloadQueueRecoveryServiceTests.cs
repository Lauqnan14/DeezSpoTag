using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DownloadQueueRecoveryServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly string _queueDbPath;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DownloadQueueRecoveryService _recoveryService;
    private readonly DeezSpoTagSettingsService _settingsService;

    public DownloadQueueRecoveryServiceTests()
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
        _settingsService.SaveSettings(settings);

        var retryScheduler = new DownloadRetryScheduler(
            _queueRepository,
            _settingsService,
            new NullActivityLogWriter(),
            new DeezSpoTagListener(),
            NullLogger<DownloadRetryScheduler>.Instance,
            _cancellationRegistry);
        var fallbackCoordinator = new EngineFallbackCoordinator(
            _queueRepository,
            _settingsService,
            new SongLinkResolver(
                new StubHttpClientFactory(),
                qobuzMetadataService: null,
                qobuzTrackResolver: null,
                qobuzOptions: null,
                NullLogger<SongLinkResolver>.Instance),
            new DeezerIsrcResolver(
                deezerApi: null!,
                NullLogger<DeezerIsrcResolver>.Instance),
            new NullActivityLogWriter());

        _recoveryService = new DownloadQueueRecoveryService(
            _queueRepository,
            _cancellationRegistry,
            retryScheduler,
            fallbackCoordinator,
            _settingsService,
            new NullActivityLogWriter(),
            new DeezSpoTagListener(),
            NullLogger<DownloadQueueRecoveryService>.Instance);
    }

    [Fact]
    public async Task RecoverStaleRunningTasksAsync_AdvancesOrphanedCrossEngineItem()
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
            AutoSources = new List<string> { "qobuz|27", "deezer|9" },
            AutoIndex = 0,
            FallbackPlan = new List<FallbackPlanStep>
            {
                new("qobuz-27", "qobuz", "27", new[] { "source_url" }, "direct_url"),
                new("deezer-9", "deezer", "9", new[] { "deezer_id" }, "deezer_track_id")
            }
        };

        await EnqueueRunningItemAsync(queueUuid, payload);
        await AgeQueueItemAsync(queueUuid, DownloadQueueRecoveryPolicy.RunningStallThreshold + TimeSpan.FromMinutes(1));

        await _recoveryService.RecoverStaleRunningTasksAsync(CancellationToken.None);

        var recovered = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("queued", recovered!.Status);
        Assert.Equal("deezer", recovered.Engine);

        var recoveredPayload = JsonSerializer.Deserialize<QobuzQueueItem>(recovered.PayloadJson!);
        Assert.NotNull(recoveredPayload);
        Assert.Equal("deezer", recoveredPayload!.Engine);
        Assert.Equal("deezer", recoveredPayload.SourceService);
        Assert.Equal(1, recoveredPayload.AutoIndex);
        Assert.Equal("https://www.deezer.com/track/3094483121", recoveredPayload.SourceUrl);
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
            AutoSources = new List<string> { "qobuz|27" }
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

    private async Task EnqueueRunningItemAsync(string queueUuid, QobuzQueueItem payload)
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
            DestinationFolderId: null,
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
        public HttpClient CreateClient(string name) => new();
    }
}
