using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

    private static Task<TestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-download-queue-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        var queueDb = Path.Join(tempRoot, "queue.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDb}",
                ["DataDirectory"] = tempRoot
            })
            .Build();

        var queueRepository = new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
        var listener = new DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagListener();
        return Task.FromResult(new TestContext(tempRoot, queueRepository, listener));
    }

    private static QobuzQueueItem CreatePayload(string queueUuid)
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

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(string tempRoot, DownloadQueueRepository queueRepository, DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagListener listener)
        {
            TempRoot = tempRoot;
            QueueRepository = queueRepository;
            Listener = listener;
        }

        public string TempRoot { get; }
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
