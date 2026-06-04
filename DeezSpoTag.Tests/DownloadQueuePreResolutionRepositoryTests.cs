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

public sealed class DownloadQueuePreResolutionRepositoryTests
{
    [Fact]
    public async Task TryUpdateQueuedPayloadIfCurrentAsync_UpdatesPayloadAndEngineWhenPayloadMatches()
    {
        await using var context = CreateContext();
        var item = CreateQueueItem("queue-1", "deezer", "{\"SourceUrl\":\"old\"}");
        await context.QueueRepository.EnqueueAsync(item, skipDuplicateCheck: true, CancellationToken.None);

        var updated = await context.QueueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
            item.QueueUuid,
            item.PayloadJson,
            "{\"SourceUrl\":\"https://play.qobuz.com/track/123\"}",
            "qobuz",
            cancellationToken: CancellationToken.None);
        var stored = await context.QueueRepository.GetByUuidAsync(item.QueueUuid, CancellationToken.None);

        Assert.True(updated);
        Assert.NotNull(stored);
        Assert.Equal("qobuz", stored!.Engine);
        Assert.Contains("qobuz", stored.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryUpdateQueuedPayloadIfCurrentAsync_RejectsStalePayload()
    {
        await using var context = CreateContext();
        var item = CreateQueueItem("queue-2", "deezer", "{\"SourceUrl\":\"old\"}");
        await context.QueueRepository.EnqueueAsync(item, skipDuplicateCheck: true, CancellationToken.None);

        var updated = await context.QueueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
            item.QueueUuid,
            "{\"SourceUrl\":\"different\"}",
            "{\"SourceUrl\":\"new\"}",
            "qobuz",
            cancellationToken: CancellationToken.None);
        var stored = await context.QueueRepository.GetByUuidAsync(item.QueueUuid, CancellationToken.None);

        Assert.False(updated);
        Assert.NotNull(stored);
        Assert.Equal("deezer", stored!.Engine);
        Assert.Equal(item.PayloadJson, stored.PayloadJson);
    }

    [Fact]
    public async Task TryUpdateQueuedPayloadIfCurrentAsync_RejectsRunningItem()
    {
        await using var context = CreateContext();
        var item = CreateQueueItem("queue-3", "deezer", "{\"SourceUrl\":\"old\"}");
        await context.QueueRepository.EnqueueAsync(item, skipDuplicateCheck: true, CancellationToken.None);
        _ = await context.QueueRepository.DequeueNextAnyAsync(newestFirst: false, CancellationToken.None);

        var updated = await context.QueueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
            item.QueueUuid,
            item.PayloadJson,
            "{\"SourceUrl\":\"new\"}",
            "qobuz",
            cancellationToken: CancellationToken.None);
        var stored = await context.QueueRepository.GetByUuidAsync(item.QueueUuid, CancellationToken.None);

        Assert.False(updated);
        Assert.NotNull(stored);
        Assert.Equal("running", stored!.Status);
        Assert.Equal("deezer", stored.Engine);
    }

    [Fact]
    public async Task TryUpdateQueuedIdentityIfCurrentAsync_PersistsResolvedIdentityColumns()
    {
        await using var context = CreateContext();
        var item = CreateQueueItem("queue-identity", "qobuz", "{\"ResolutionStatus\":\"resolving\"}");
        await context.QueueRepository.EnqueueAsync(item, skipDuplicateCheck: true, CancellationToken.None);
        var resolvedPayload = """
            {
              "Engine":"qobuz",
              "Title":"Sahani",
              "Artist":"Davy Waweru, Muthoka",
              "Isrc":"QT3F22565438",
              "DeezerId":"359542303",
              "SpotifyId":"spotify-track",
              "AppleId":"apple-track",
              "DurationSeconds":205,
              "DestinationFolderId":1,
              "ContentType":"stereo"
            }
            """;
        var identity = item with
        {
            Engine = "qobuz",
            TrackTitle = "Sahani",
            ArtistName = "Davy Waweru, Muthoka",
            Isrc = "QT3F22565438",
            DeezerTrackId = "359542303",
            SpotifyTrackId = "spotify-track",
            AppleTrackId = "apple-track",
            DurationMs = 205000,
            DestinationFolderId = 1,
            ContentType = "stereo",
            PayloadJson = resolvedPayload
        };

        var updated = await context.QueueRepository.TryUpdateQueuedIdentityIfCurrentAsync(
            identity,
            item.PayloadJson,
            status: "queued",
            error: null,
            cancellationToken: CancellationToken.None);
        var stored = await context.QueueRepository.GetByUuidAsync(item.QueueUuid, CancellationToken.None);

        Assert.True(updated);
        Assert.NotNull(stored);
        Assert.Equal("QT3F22565438", stored!.Isrc);
        Assert.Equal("359542303", stored.DeezerTrackId);
        Assert.Equal("spotify-track", stored.SpotifyTrackId);
        Assert.Equal("apple-track", stored.AppleTrackId);
        Assert.Equal(205000, stored.DurationMs);
        Assert.Equal(1, stored.DestinationFolderId);
    }

    [Fact]
    public async Task EnsureSchemaAsync_BackfillsMissingIdentityColumnsFromPayload()
    {
        await using var context = CreateContext();
        var payloadJson = """
            {
              "Title":"Sahani",
              "Artist":"Davy Waweru, Muthoka",
              "Isrc":"QT3F22565438",
              "DeezerId":"359542303",
              "DurationSeconds":205,
              "DestinationFolderId":1,
              "ContentType":"stereo"
            }
            """;
        var item = CreateQueueItem("queue-backfill", "qobuz", payloadJson) with
        {
            TrackTitle = "Sahani",
            ArtistName = "Davy Waweru, Muthoka",
            DurationMs = null,
            DestinationFolderId = null,
            ContentType = null
        };
        await context.QueueRepository.EnqueueAsync(item, skipDuplicateCheck: true, CancellationToken.None);

        var freshRepository = BuildRepository(context.TempRoot, context.QueueDbPath);
        var stored = await freshRepository.GetByUuidAsync(item.QueueUuid, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("QT3F22565438", stored!.Isrc);
        Assert.Equal("359542303", stored.DeezerTrackId);
        Assert.Equal(205000, stored.DurationMs);
        Assert.Equal(1, stored.DestinationFolderId);
        Assert.Equal("stereo", stored.ContentType);
    }

    private static TestContext CreateContext()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-preresolution-repository-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var queueDbPath = Path.Join(tempRoot, "queue.db");

        var queueRepository = BuildRepository(tempRoot, queueDbPath);
        return new TestContext(tempRoot, queueDbPath, queueRepository);
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

    private static DownloadQueueItem CreateQueueItem(string queueUuid, string engine, string payloadJson)
        => new(
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
            Status: "queued",
            PayloadJson: payloadJson,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(string tempRoot, string queueDbPath, DownloadQueueRepository queueRepository)
        {
            TempRoot = tempRoot;
            QueueDbPath = queueDbPath;
            QueueRepository = queueRepository;
        }

        public string TempRoot { get; }
        public string QueueDbPath { get; }
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
