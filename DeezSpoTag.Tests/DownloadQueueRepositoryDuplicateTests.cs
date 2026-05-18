using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueRepositoryDuplicateTests
{
    [Fact]
    public async Task ExistsDuplicateAsync_DoesNotTreatSharedAlbumOrArtistIdsAsTrackDuplicates()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "existing-queue-item",
                artist: "Shared Artist",
                title: "First Track",
                destinationFolderId: 7,
                deezerTrackId: "dz-track-1",
                deezerAlbumId: "dz-album-1",
                deezerArtistId: "dz-artist-1",
                spotifyTrackId: "sp-track-1",
                spotifyAlbumId: "sp-album-1",
                spotifyArtistId: "sp-artist-1",
                appleTrackId: "ap-track-1",
                appleAlbumId: "ap-album-1",
                appleArtistId: "ap-artist-1"),
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Shared Artist",
                TrackTitle = "Second Track",
                DestinationFolderId = 7,
                ContentType = "stereo",
                DeezerTrackId = "dz-track-2",
                DeezerAlbumId = "dz-album-1",
                DeezerArtistId = "dz-artist-1",
                SpotifyTrackId = "sp-track-2",
                SpotifyAlbumId = "sp-album-1",
                SpotifyArtistId = "sp-artist-1",
                AppleTrackId = "ap-track-2",
                AppleAlbumId = "ap-album-1",
                AppleArtistId = "ap-artist-1"
            },
            CancellationToken.None);

        Assert.False(exists);
    }

    [Fact]
    public async Task ExistsDuplicateAsync_MatchesTrackLevelIdentifiers()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "existing-track-id",
                artist: "Shared Artist",
                title: "Original Title",
                destinationFolderId: 9,
                deezerTrackId: "dz-track-match"),
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Different Artist Name",
                TrackTitle = "Different Track Name",
                DestinationFolderId = 9,
                ContentType = "stereo",
                DeezerTrackId = "dz-track-match"
            },
            CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task GetActiveDownloadCountAsync_CountsQueuedRunningAndPausedOnly()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-queued", "Artist", "Queued", 1) with { Status = "queued" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-running", "Artist", "Running", 1) with { Status = "running" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-paused", "Artist", "Paused", 1) with { Status = "paused" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("inactive-completed", "Artist", "Completed", 1) with { Status = "completed" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("inactive-failed", "Artist", "Failed", 1) with { Status = "failed" },
            CancellationToken.None);

        var activeCount = await context.QueueRepository.GetActiveDownloadCountAsync(CancellationToken.None);

        Assert.Equal(3, activeCount);
    }

    [Fact]
    public async Task DeleteClearableByStatusAsync_PreservesCompletedDestinationUntilMoveSucceeded()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("completed-pending-move", "Artist", "Track", 44),
            CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            "completed-pending-move",
            "completed",
            downloaded: 1,
            progress: 100,
            cancellationToken: CancellationToken.None);

        var blockedDeleteCount = await context.QueueRepository.DeleteClearableByStatusAsync("completed", CancellationToken.None);

        Assert.Equal(0, blockedDeleteCount);
        Assert.NotNull(await context.QueueRepository.GetByUuidAsync("completed-pending-move", CancellationToken.None));

        await context.QueueRepository.MarkMoveSucceededAsync("completed-pending-move", CancellationToken.None);
        var deletedCount = await context.QueueRepository.DeleteClearableByStatusAsync("completed", CancellationToken.None);

        Assert.Equal(1, deletedCount);
        Assert.Null(await context.QueueRepository.GetByUuidAsync("completed-pending-move", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteClearableAllAsync_PreservesAnyDestinationRowUntilMoveSucceeded()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-with-destination", "Artist", "Failed", 55) with { Status = "failed" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("completed-without-destination", "Artist", "No Destination", null) with { Status = "completed" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("completed-moved", "Artist", "Moved", 66) with { Status = "completed" },
            CancellationToken.None);
        await context.QueueRepository.MarkMoveSucceededAsync("completed-moved", CancellationToken.None);

        var deletedCount = await context.QueueRepository.DeleteClearableAllAsync(CancellationToken.None);

        Assert.Equal(2, deletedCount);
        Assert.NotNull(await context.QueueRepository.GetByUuidAsync("failed-with-destination", CancellationToken.None));
        Assert.Null(await context.QueueRepository.GetByUuidAsync("completed-without-destination", CancellationToken.None));
        Assert.Null(await context.QueueRepository.GetByUuidAsync("completed-moved", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateQueueMetadataAsync_ProtectsCompletedRowWhenDestinationIsRecovered()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("completed-recovered-destination", "Artist", "Recovered", null) with { Status = "completed" },
            CancellationToken.None);

        await context.QueueRepository.UpdateQueueMetadataAsync(
            "completed-recovered-destination",
            qualityRank: null,
            contentType: "stereo",
            destinationFolderId: 77,
            cancellationToken: CancellationToken.None);

        var blockedDeleteCount = await context.QueueRepository.DeleteClearableByStatusAsync("completed", CancellationToken.None);

        Assert.Equal(0, blockedDeleteCount);
        Assert.NotNull(await context.QueueRepository.GetByUuidAsync("completed-recovered-destination", CancellationToken.None));
    }

    private static Task<TestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-queue-duplicate-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        var queueDbPath = Path.Join(tempRoot, "queue.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = tempRoot
            })
            .Build();

        var queueRepository = new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
        return Task.FromResult(new TestContext(tempRoot, queueRepository));
    }

    private static DownloadQueueItem CreateQueueItem(
        string queueUuid,
        string artist,
        string title,
        long? destinationFolderId,
        string? deezerTrackId = null,
        string? deezerAlbumId = null,
        string? deezerArtistId = null,
        string? spotifyTrackId = null,
        string? spotifyAlbumId = null,
        string? spotifyArtistId = null,
        string? appleTrackId = null,
        string? appleAlbumId = null,
        string? appleArtistId = null)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "deezer",
            ArtistName: artist,
            TrackTitle: title,
            Isrc: null,
            DeezerTrackId: deezerTrackId,
            DeezerAlbumId: deezerAlbumId,
            DeezerArtistId: deezerArtistId,
            SpotifyTrackId: spotifyTrackId,
            SpotifyAlbumId: spotifyAlbumId,
            SpotifyArtistId: spotifyArtistId,
            AppleTrackId: appleTrackId,
            AppleAlbumId: appleAlbumId,
            AppleArtistId: appleArtistId,
            DurationMs: null,
            DestinationFolderId: destinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "stereo",
            Status: "queued",
            PayloadJson: null,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(string tempRoot, DownloadQueueRepository queueRepository)
        {
            TempRoot = tempRoot;
            QueueRepository = queueRepository;
        }

        public string TempRoot { get; }
        public DownloadQueueRepository QueueRepository { get; }

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
                // Best-effort cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }
}
