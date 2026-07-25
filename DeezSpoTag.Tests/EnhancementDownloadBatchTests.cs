using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EnhancementDownloadBatchTests
{
    [Fact]
    public void CompleteAlbum_IsVerifiedOnlyWhenEveryIndexedTrackHasIdentityAndPosition()
    {
        var tracks = Enumerable.Range(1, 12)
            .Select(index => CreateTrack(index, albumId: 7, trackNumber: index, trackTotal: 12))
            .ToList();

        Assert.True(QualityScannerService.IsVerifiedCompleteAlbum(tracks));
        Assert.False(QualityScannerService.IsVerifiedCompleteAlbum(tracks.Take(11).ToList()));
        Assert.False(QualityScannerService.IsVerifiedCompleteAlbum(
            tracks.Select((track, index) => index == 4 ? track with { AudioFilePath = string.Empty } : track).ToList()));
    }

    [Fact]
    public void AlbumGroups_PreserveDiscTrackOrderAndDoNotMixLibraries()
    {
        var tracks = new[]
        {
            CreateTrack(3, 20, 3, 3, destinationFolderId: 1),
            CreateTrack(1, 20, 1, 3, destinationFolderId: 1),
            CreateTrack(2, 20, 2, 3, destinationFolderId: 1),
            CreateTrack(4, 20, 1, 1, destinationFolderId: 2)
        };

        var groups = QualityScannerService.BuildAlbumGroups(tracks);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, groups[0].Select(track => track.TrackId));
        Assert.Single(groups[1]);
        Assert.Equal(2, groups[1][0].DestinationFolderId);
    }

    [Fact]
    public async Task HeldBatch_IsInvisibleToWorkersUntilAtomicallyReleased()
    {
        await using var context = CreateContext();
        var batchId = Guid.NewGuid().ToString("N");
        var payload = $$"""{"enhancementBatchId":"{{batchId}}"}""";
        await context.Repository.EnqueueAsync(CreateQueueItem("held-1", "enhancement_held", payload));

        Assert.False(await context.Repository.HasRunnableDownloadsAsync());
        Assert.Equal(1, await context.Repository.ReleaseEnhancementBatchAsync(batchId));
        Assert.True(await context.Repository.HasRunnableDownloadsAsync());
    }

    [Fact]
    public async Task InterruptedHeldBatch_IsCanceledInsteadOfPartiallyReleasedAfterRestart()
    {
        await using var context = CreateContext();
        await context.Repository.EnqueueAsync(CreateQueueItem(
            "orphaned-held",
            "enhancement_held",
            """{"enhancementBatchId":"interrupted"}"""));

        Assert.Equal(1, await context.Repository.CancelOrphanedEnhancementBatchesAsync());
        Assert.Equal(0, await context.Repository.ReleaseEnhancementBatchAsync("interrupted"));
        Assert.False(await context.Repository.HasRunnableDownloadsAsync());
    }

    private static QualityScanTrackDto CreateTrack(
        long id,
        long albumId,
        int trackNumber,
        int trackTotal,
        long? destinationFolderId = 1)
        => new(
            id,
            $"Track {id}",
            "Artist",
            "Album",
            string.Empty,
            180000,
            2,
            3,
            "hi_res",
            destinationFolderId,
            2,
            "lossless",
            "flac",
            ".flac",
            1000,
            16,
            44100,
            albumId,
            10,
            id + 100,
            $"/library/artist/album/{id}.flac",
            1,
            trackNumber,
            trackTotal);

    private static TestContext CreateContext()
    {
        var root = Path.Join(Path.GetTempPath(), $"enhancement-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(root, "queue.db")}",
                ["DataDirectory"] = root
            })
            .Build();
        return new TestContext(
            root,
            new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance));
    }

    private static DownloadQueueItem CreateQueueItem(string uuid, string status, string payload)
        => new(
            0,
            uuid,
            "qobuz",
            "Artist",
            "Track",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            180000,
            1,
            5,
            null,
            "stereo",
            status,
            payload,
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private sealed class TestContext(string root, DownloadQueueRepository repository) : IAsyncDisposable
    {
        public DownloadQueueRepository Repository { get; } = repository;

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
            return ValueTask.CompletedTask;
        }
    }
}
