using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Services.Download.Deezer;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueEnqueueHelperTests
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
            context.Listener,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_ReturnsRecentlyDownloaded_WhenMatchingItemCompleted()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("completed-1");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            payload.Id,
            "completed",
            cancellationToken: CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.Listener,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_recently_downloaded", outcome.ReasonCode);
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
            context.Listener,
            NullLogger.Instance,
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
            context.Listener,
            NullLogger.Instance,
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
            context.Listener,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(outcome.AlreadyQueued);

        var queuedItems = await context.QueueRepository.GetTasksAsync(firstPayload.Engine, CancellationToken.None);
        Assert.Equal(2, queuedItems.Count);
    }

    private static Task<TestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-download-queue-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        var queueDb = Path.Join(tempRoot, "queue.db");
        var queueRepository = BuildRepository(tempRoot, queueDb);
        var listener = new DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagListener();
        return Task.FromResult(new TestContext(tempRoot, queueDb, queueRepository, listener));
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
        public TestContext(string tempRoot, string queueDbPath, DownloadQueueRepository queueRepository, DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagListener listener)
        {
            TempRoot = tempRoot;
            QueueDbPath = queueDbPath;
            QueueRepository = queueRepository;
            Listener = listener;
        }

        public string TempRoot { get; }
        public string QueueDbPath { get; }
        public DownloadQueueRepository QueueRepository { get; }
        public DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagListener Listener { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
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
}
