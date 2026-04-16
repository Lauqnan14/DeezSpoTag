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

public sealed class DownloadQueueOrderingTests
{
    [Fact]
    public async Task DequeueNextAnyAsync_UsesOldestFirst_WhenNewestFirstIsFalse()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-oldest"), CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-middle"), CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-newest"), CancellationToken.None);

        var next = await context.QueueRepository.DequeueNextAnyAsync(newestFirst: false, CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal("queue-oldest", next!.QueueUuid);
    }

    [Fact]
    public async Task DequeueNextAnyAsync_UsesNewestFirst_WhenNewestFirstIsTrue()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-oldest"), CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-middle"), CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-newest"), CancellationToken.None);

        var next = await context.QueueRepository.DequeueNextAnyAsync(newestFirst: true, CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal("queue-newest", next!.QueueUuid);
    }

    private static TestContext CreateContext()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-queue-order-tests-" + Path.GetRandomFileName());
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
        return new TestContext(tempRoot, queueRepository);
    }

    private static DownloadQueueItem CreateQueueItem(string queueUuid)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "deezer",
            ArtistName: "Artist",
            TrackTitle: queueUuid,
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
            DurationMs: 180000,
            DestinationFolderId: null,
            QualityRank: 50,
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
                // Best effort cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }
}
