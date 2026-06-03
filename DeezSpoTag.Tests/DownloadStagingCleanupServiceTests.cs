using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadStagingCleanupServiceTests
{
    [Fact]
    public async Task CleanupAsync_DeletesOwnedRemnantFolder_WhenNoAudioRemains()
    {
        await using var context = CreateContext();
        var albumFolder = Path.Join(context.DownloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var expectedAudioPath = Path.Join(albumFolder, "Song.flac");
        var partPath = expectedAudioPath + ".part";
        var coverPath = Path.Join(albumFolder, "cover.jpg");
        File.WriteAllText(partPath, "partial");
        File.WriteAllText(coverPath, "cover");

        var result = await context.CleanupService.CleanupAsync(
            "queue-1",
            BuildPayload(expectedAudioPath),
            CancellationToken.None);

        Assert.Equal(DownloadStagingCleanupService.CompletedStatus, result.Status);
        Assert.False(Directory.Exists(albumFolder));
        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(coverPath));
    }

    [Fact]
    public async Task CleanupAsync_PreservesSharedAlbumFolder_WhenAnotherAudioFileRemains()
    {
        await using var context = CreateContext();
        var albumFolder = Path.Join(context.DownloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var failedAudioPath = Path.Join(albumFolder, "Failed.flac");
        var partPath = failedAudioPath + ".part";
        var remainingAudioPath = Path.Join(albumFolder, "Completed.flac");
        var coverPath = Path.Join(albumFolder, "cover.jpg");
        File.WriteAllText(partPath, "partial");
        File.WriteAllText(remainingAudioPath, "audio");
        File.WriteAllText(coverPath, "cover");

        var result = await context.CleanupService.CleanupAsync(
            "queue-2",
            BuildPayload(failedAudioPath),
            CancellationToken.None);

        Assert.Equal(DownloadStagingCleanupService.CompletedStatus, result.Status);
        Assert.True(Directory.Exists(albumFolder));
        Assert.False(File.Exists(partPath));
        Assert.True(File.Exists(remainingAudioPath));
        Assert.True(File.Exists(coverPath));
    }

    [Fact]
    public async Task CleanupAsync_DeletesExtensionedPartialFile_WhenPayloadPathHasNoExtension()
    {
        await using var context = CreateContext();
        var albumFolder = Path.Join(context.DownloadRoot, "Benzema", "101%");
        Directory.CreateDirectory(albumFolder);
        var extensionlessPath = Path.Join(albumFolder, "Benzema - 101%");
        var partialAudioPath = extensionlessPath + ".flac.part";
        var coverPath = Path.Join(albumFolder, "cover.jpg");
        File.WriteAllText(partialAudioPath, "partial");
        File.WriteAllText(coverPath, "cover");

        var result = await context.CleanupService.CleanupAsync(
            "queue-extensionless",
            BuildPayload(extensionlessPath),
            CancellationToken.None);

        Assert.Equal(DownloadStagingCleanupService.CompletedStatus, result.Status);
        Assert.False(Directory.Exists(albumFolder));
        Assert.False(File.Exists(partialAudioPath));
        Assert.False(File.Exists(coverPath));
    }

    [Fact]
    public async Task CleanupAsync_DeletesAnimatedArtworkSidecars_WhenNoPrimaryMediaRemains()
    {
        await using var context = CreateContext();
        var albumFolder = Path.Join(context.DownloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var expectedAudioPath = Path.Join(albumFolder, "Song.flac");
        var partialPath = expectedAudioPath + ".part";
        var animatedArtworkPath = Path.Join(albumFolder, "cover - square_animated_artwork.mp4");
        File.WriteAllText(partialPath, "partial");
        File.WriteAllText(animatedArtworkPath, "artwork");

        var result = await context.CleanupService.CleanupAsync(
            "queue-artwork",
            BuildPayload(expectedAudioPath),
            CancellationToken.None);

        Assert.Equal(DownloadStagingCleanupService.CompletedStatus, result.Status);
        Assert.False(Directory.Exists(albumFolder));
        Assert.False(File.Exists(animatedArtworkPath));
    }

    [Fact]
    public async Task CleanupAsync_DeletesArtistAndAlbumSidecarFolders_FromQueueFileObjectDirectories()
    {
        await using var context = CreateContext();
        var artistFolder = Path.Join(context.DownloadRoot, "Benzema");
        var albumFolder = Path.Join(artistFolder, "101%");
        Directory.CreateDirectory(albumFolder);
        var extensionlessPath = Path.Join(albumFolder, "Benzema - 101%");
        var albumCoverPath = Path.Join(albumFolder, "cover.jpg");
        var artistCoverPath = Path.Join(artistFolder, "folder.jpg");
        File.WriteAllText(albumCoverPath, "album-cover");
        File.WriteAllText(artistCoverPath, "artist-cover");

        var result = await context.CleanupService.CleanupAsync(
            "queue-live-payload",
            BuildPayload(extensionlessPath, albumFolder, artistFolder),
            CancellationToken.None);

        Assert.Equal(DownloadStagingCleanupService.CompletedStatus, result.Status);
        Assert.False(Directory.Exists(albumFolder));
        Assert.False(Directory.Exists(artistFolder));
    }

    [Fact]
    public async Task CleanupAsync_RejectsPathsOutsideDownloadRoot()
    {
        await using var context = CreateContext();
        var outsideRoot = Path.Join(Path.GetTempPath(), "deezspotag-staging-cleanup-outside-" + Path.GetRandomFileName());
        Directory.CreateDirectory(outsideRoot);
        var outsideFile = Path.Join(outsideRoot, "Song.flac.part");
        File.WriteAllText(outsideFile, "outside");

        try
        {
            var result = await context.CleanupService.CleanupAsync(
                "queue-3",
                BuildPayload(Path.ChangeExtension(outsideFile, ".flac")),
                CancellationToken.None);

            Assert.Equal(DownloadStagingCleanupService.FailedStatus, result.Status);
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupAsync_PreservesFolderWithProtectedActivePath()
    {
        await using var context = CreateContext();
        var albumFolder = Path.Join(context.DownloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var failedAudioPath = Path.Join(albumFolder, "Failed.flac");
        var partPath = failedAudioPath + ".part";
        var protectedFuturePath = Path.Join(albumFolder, "Future.flac");
        var coverPath = Path.Join(albumFolder, "cover.jpg");
        File.WriteAllText(partPath, "partial");
        File.WriteAllText(coverPath, "cover");

        var result = await context.CleanupService.CleanupAsync(
            "queue-4",
            BuildPayload(failedAudioPath),
            [protectedFuturePath],
            CancellationToken.None);

        Assert.Equal(DownloadStagingCleanupService.CompletedStatus, result.Status);
        Assert.True(Directory.Exists(albumFolder));
        Assert.False(File.Exists(partPath));
        Assert.True(File.Exists(coverPath));
    }

    [Fact]
    public async Task RepositoryCleanup_ProtectsOtherActiveQueueItemsInSameFolder()
    {
        await using var context = CreateContext();
        var albumFolder = Path.Join(context.DownloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var failedAudioPath = Path.Join(albumFolder, "Failed.flac");
        var failedPartPath = failedAudioPath + ".part";
        var activeAudioPath = Path.Join(albumFolder, "Active.flac");
        var coverPath = Path.Join(albumFolder, "cover.jpg");
        File.WriteAllText(failedPartPath, "partial");
        File.WriteAllText(coverPath, "cover");

        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-item", "queued", BuildPayload(failedAudioPath)),
            skipDuplicateCheck: true,
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-item", "queued", BuildPayload(activeAudioPath)),
            skipDuplicateCheck: true,
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "failed-item",
            "failed",
            "test failure",
            0,
            1,
            0,
            CancellationToken.None);

        var cleanupStatus = await ReadStagingCleanupStatusAsync(context.QueueDbPath, "failed-item");
        Assert.Equal(DownloadStagingCleanupService.CompletedStatus, cleanupStatus);
        Assert.True(Directory.Exists(albumFolder));
        Assert.False(File.Exists(failedPartPath));
        Assert.True(File.Exists(coverPath));
    }

    [Fact]
    public async Task DeleteClearableByUuidAsync_RerunsCleanupBeforeRemovingCanceledRow()
    {
        await using var context = CreateContext();
        var artistFolder = Path.Join(context.DownloadRoot, "Benzema");
        var albumFolder = Path.Join(artistFolder, "101%");
        Directory.CreateDirectory(albumFolder);
        var extensionlessPath = Path.Join(albumFolder, "Benzema - 101%");

        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("clear-canceled-item", "queued", BuildPayload(extensionlessPath, albumFolder, artistFolder)),
            skipDuplicateCheck: true,
            CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            "clear-canceled-item",
            "canceled",
            cancellationToken: CancellationToken.None);

        Directory.CreateDirectory(albumFolder);
        File.WriteAllText(Path.Join(albumFolder, "cover.jpg"), "album-cover-after-cleanup");
        File.WriteAllText(Path.Join(artistFolder, "folder.jpg"), "artist-cover-after-cleanup");

        var deleted = await context.QueueRepository.DeleteClearableByUuidAsync(
            "clear-canceled-item",
            CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(albumFolder));
        Assert.False(Directory.Exists(artistFolder));
    }

    [Fact]
    public async Task DeleteClearableByStatusAsync_RemovesOrphanSidecarFolderAfterRowsAreGone()
    {
        await using var context = CreateContext();
        var orphanArtistFolder = Path.Join(context.DownloadRoot, "Benzema");
        Directory.CreateDirectory(orphanArtistFolder);
        File.WriteAllText(Path.Join(orphanArtistFolder, "folder.jpg"), "orphan-artist-cover");

        var activeFolder = Path.Join(context.DownloadRoot, "Active Artist", "Active Album");
        Directory.CreateDirectory(activeFolder);
        File.WriteAllText(Path.Join(activeFolder, "cover.jpg"), "active-cover");
        var activePath = Path.Join(activeFolder, "Active Song.flac");
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-item", "queued", BuildPayload(activePath)),
            skipDuplicateCheck: true,
            CancellationToken.None);

        var deleted = await context.QueueRepository.DeleteClearableByStatusAsync(
            "canceled",
            CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.False(Directory.Exists(orphanArtistFolder));
        Assert.True(Directory.Exists(activeFolder));
    }

    private static TestContext CreateContext()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-staging-cleanup-tests-" + Path.GetRandomFileName());
        var downloadRoot = Path.Join(tempRoot, "downloads");
        Directory.CreateDirectory(downloadRoot);
        var queueDbPath = Path.Join(tempRoot, "queue.db");

        var cleanupService = new DownloadStagingCleanupService(
            NullLogger<DownloadStagingCleanupService>.Instance,
            downloadRootOverride: downloadRoot);
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
            cleanupService);

        return new TestContext(tempRoot, downloadRoot, queueDbPath, cleanupService, queueRepository);
    }

    private static async Task<string?> ReadStagingCleanupStatusAsync(string queueDbPath, string queueUuid)
    {
        await using var connection = new SqliteConnection($"Data Source={queueDbPath}");
        await connection.OpenAsync();
        const string sql = "SELECT staging_cleanup_status FROM download_task WHERE queue_uuid = @queueUuid LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        return await command.ExecuteScalarAsync() as string;
    }

    private static string BuildPayload(string filePath)
        => $$"""
           {"filePath":"{{JsonEscape(filePath)}}","files":[{"path":"{{JsonEscape(filePath)}}"}]}
           """;

    private static string BuildPayload(string filePath, string albumPath, string artistPath)
        => $$"""
           {"FilePath":"{{JsonEscape(filePath)}}","Files":[{"path":"{{JsonEscape(filePath)}}","albumPath":"{{JsonEscape(albumPath)}}","artistPath":"{{JsonEscape(artistPath)}}"}]}
           """;

    private static string JsonEscape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static DownloadQueueItem CreateQueueItem(string queueUuid, string status, string payloadJson)
        => new(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
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

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            string tempRoot,
            string downloadRoot,
            string queueDbPath,
            DownloadStagingCleanupService cleanupService,
            DownloadQueueRepository queueRepository)
        {
            TempRoot = tempRoot;
            DownloadRoot = downloadRoot;
            QueueDbPath = queueDbPath;
            CleanupService = cleanupService;
            QueueRepository = queueRepository;
        }

        public string TempRoot { get; }
        public string DownloadRoot { get; }
        public string QueueDbPath { get; }
        public DownloadStagingCleanupService CleanupService { get; }
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
