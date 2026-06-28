using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
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

    [Fact]
    public async Task DequeueNextAsync_DoesNotSkipEarlierQueuedItemForAnotherEngine()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-deezer-head", engine: "deezer"), CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-qobuz-later", engine: "qobuz"), CancellationToken.None);

        var qobuzBeforeHead = await context.QueueRepository.DequeueNextAsync(
            "qobuz",
            newestFirst: false,
            CancellationToken.None);
        var globalHead = await context.QueueRepository.DequeueNextAnyAsync(
            newestFirst: false,
            CancellationToken.None);
        var qobuzAfterHead = await context.QueueRepository.DequeueNextAsync(
            "qobuz",
            newestFirst: false,
            CancellationToken.None);

        Assert.Null(qobuzBeforeHead);
        Assert.NotNull(globalHead);
        Assert.Equal("queue-deezer-head", globalHead!.QueueUuid);
        Assert.NotNull(qobuzAfterHead);
        Assert.Equal("queue-qobuz-later", qobuzAfterHead!.QueueUuid);
    }

    [Fact]
    public async Task DequeueNextAnyAsync_SkipsEarlierResolvingItem()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-resolving-head", payloadJson: "{}"), CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-later"), CancellationToken.None);
        var claimed = await context.QueueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
            "queue-resolving-head",
            "{}",
            "{}",
            status: "resolving",
            cancellationToken: CancellationToken.None);

        var next = await context.QueueRepository.DequeueNextAnyAsync(
            newestFirst: false,
            CancellationToken.None);

        Assert.True(claimed);
        Assert.NotNull(next);
        Assert.Equal("queue-later", next!.QueueUuid);
    }

    [Fact]
    public async Task DequeueNextAnyAsync_DoesNotSkipQueuedItemWithFailedPreResolutionPayload()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                "queue-failed-resolution-head",
                payloadJson: "{\"ResolutionStatus\":\"failed\",\"resolutionStatus\":\"failed\"}"),
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-later"), CancellationToken.None);

        var next = await context.QueueRepository.DequeueNextAnyAsync(
            newestFirst: false,
            CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal("queue-failed-resolution-head", next!.QueueUuid);
    }

    [Fact]
    public async Task HasRunnableDownloadsAsync_TreatsQueuedFailedPreResolutionPayloadAsRunnable()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                "queue-failed-resolution-runnable",
                payloadJson: "{\"ResolutionStatus\":\"failed\",\"resolutionStatus\":\"failed\"}"),
            CancellationToken.None);

        var hasRunnable = await context.QueueRepository.HasRunnableDownloadsAsync(CancellationToken.None);

        Assert.True(hasRunnable);
    }

    [Fact]
    public async Task RecoverInterruptedPreResolutionAsync_RequeuesResolvingItemForDownload()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-resolving", status: "resolving"), CancellationToken.None);

        var recovered = await context.QueueRepository.RecoverInterruptedPreResolutionAsync(CancellationToken.None);
        var next = await context.QueueRepository.DequeueNextAnyAsync(newestFirst: false, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.NotNull(next);
        Assert.Equal("queue-resolving", next!.QueueUuid);
        Assert.Equal("running", next.Status);
    }

    [Fact]
    public async Task GetQueuedCountAsync_CountsResolvingItemSoQueueProcessorWakes()
    {
        await using var context = CreateContext();
        await context.QueueRepository.EnqueueAsync(CreateQueueItem("queue-resolving", status: "resolving"), CancellationToken.None);

        var count = await context.QueueRepository.GetQueuedCountAsync(CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EnqueueAsync_PublishesWakeAfterSuccessfulCommit()
    {
        var wakeSignal = new DownloadQueueWakeSignal();
        await using var context = CreateContext(wakeSignal);

        var inserted = await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("queue-wake"),
            CancellationToken.None);

        Assert.NotNull(inserted);
        var startedAt = DateTimeOffset.UtcNow;
        await wakeSignal.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(1));
    }

    private static TestContext CreateContext(DownloadQueueWakeSignal? wakeSignal = null)
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

        var queueRepository = new DownloadQueueRepository(
            config,
            NullLogger<DownloadQueueRepository>.Instance,
            queueWakeSignal: wakeSignal);
        return new TestContext(tempRoot, queueRepository);
    }

    private static DownloadQueueItem CreateQueueItem(
        string queueUuid,
        string status = "queued",
        string engine = "deezer",
        string? payloadJson = null)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: engine,
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
            Status: status,
            PayloadJson: payloadJson,
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
