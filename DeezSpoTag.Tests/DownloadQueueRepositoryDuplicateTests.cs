using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueRepositoryDuplicateTests
{
    private const string WatchlistPayloadJson = """
        {"WatchlistOrigin":"playlist","WatchlistSource":"spotify","WatchlistPlaylistId":"playlist-1","WatchlistTrackId":"track-1"}
        """;

    [Fact]
    public void WatchlistClaimOwnership_ExpiresCompletedPendingWorkButProtectsRunningWork()
    {
        var now = DateTimeOffset.UtcNow;
        var completed = CreateQueueItem("claim-item", "Artist", "Track", 1) with
        {
            Status = "completed",
            EnrichmentStatus = "pending",
            FinalizationStatus = "pending",
            UpdatedAt = now - DownloadQueueRecoveryPolicy.PostDownloadPendingLease - TimeSpan.FromMinutes(1)
        };

        Assert.False(DownloadQueueRecoveryPolicy.IsWatchlistClaimOwnedByQueue(completed, now));
        Assert.True(DownloadQueueRecoveryPolicy.IsWatchlistClaimOwnedByQueue(
            completed with { EnrichmentStatus = "running" },
            now));
        Assert.False(DownloadQueueRecoveryPolicy.IsWatchlistClaimOwnedByQueue(
            completed with { EnrichmentStatus = null, FinalizationStatus = null, UpdatedAt = now },
            now));
    }

    [Fact]
    public async Task StartupRecovery_DemotesIdentityOnlyDestinationMapFromMovedToPending()
    {
        await using var context = await CreateContextAsync();
        var stagingPath = Path.Join(context.TempRoot, "downloads", "Artist", "Track.flac");
        var item = CreateQueueItem("identity-destination", "Artist", "Track", 1) with
        {
            Status = "completed",
            FinalizationStatus = "moved",
            EnrichmentStatus = "pending",
            FinalDestinationsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [stagingPath] = stagingPath
            })
        };
        await context.QueueRepository.EnqueueAsync(item, CancellationToken.None);
        await context.QueueRepository.UpdateFinalDestinationsAsync(
            item.QueueUuid,
            item.FinalDestinationsJson,
            cancellationToken: CancellationToken.None);
        await context.QueueRepository.MarkMoveSucceededAsync(item.QueueUuid, CancellationToken.None);

        var restartedRepository = new DownloadQueueRepository(
            context.Configuration,
            NullLogger<DownloadQueueRepository>.Instance);
        var recovered = await restartedRepository.GetByUuidAsync(item.QueueUuid, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal("pending", recovered!.FinalizationStatus);
        Assert.Equal("pending", recovered.EnrichmentStatus);
    }

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

    [Theory]
    [InlineData("queued")]
    [InlineData("resolving")]
    [InlineData("preparing")]
    [InlineData("prepared")]
    [InlineData("inqueue")]
    [InlineData("running")]
    [InlineData("downloading")]
    [InlineData("paused")]
    [InlineData("retrying")]
    public async Task HasActiveWatchlistDownloadsAsync_DetectsOnlyActiveWatchlistStatuses(string status)
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem($"watch-active-{status}", "Artist", $"Track {status}", 1) with
            {
                Status = status,
                PayloadJson = WatchlistPayloadJson
            },
            CancellationToken.None);

        Assert.True(await context.QueueRepository.HasActiveWatchlistDownloadsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    [InlineData("completed")]
    [InlineData("complete")]
    public async Task HasActiveWatchlistDownloadsAsync_IgnoresTerminalWatchlistStatuses(string status)
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem($"watch-terminal-{status}", "Artist", $"Track {status}", null) with
            {
                Status = status,
                PayloadJson = WatchlistPayloadJson
            },
            CancellationToken.None);

        Assert.False(await context.QueueRepository.HasActiveWatchlistDownloadsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("pending", "completed")]
    [InlineData("running", "completed")]
    [InlineData("moved", "pending")]
    [InlineData("moved", "running")]
    public async Task HasActiveWatchlistDownloadsAsync_TreatsCompletedDownloadsWithPendingMoveOrEnrichmentAsActive(
        string moveStatus,
        string enrichmentStatus)
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("watch-completed-finalizing", "Artist", "Track", 1) with
            {
                Status = "completed",
                FinalizationStatus = moveStatus,
                EnrichmentStatus = enrichmentStatus,
                PayloadJson = WatchlistPayloadJson,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);

        Assert.True(await context.QueueRepository.HasActiveWatchlistDownloadsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HasActiveWatchlistDownloadsAsync_IgnoresCompletedDownloadsAfterMoveAndEnrichmentSettle()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("watch-completed-settled", "Artist", "Track", 1) with
            {
                Status = "completed",
                FinalizationStatus = "moved",
                EnrichmentStatus = "completed",
                PayloadJson = WatchlistPayloadJson
            },
            CancellationToken.None);

        Assert.False(await context.QueueRepository.HasActiveWatchlistDownloadsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HasActiveWatchlistDownloadsAsync_IgnoresActiveManualDownloads()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("manual-active", "Artist", "Manual Track", 1) with
            {
                Status = "downloading",
                PayloadJson = "{\"title\":\"Manual Track\",\"artist\":\"Artist\"}"
            },
            CancellationToken.None);

        Assert.False(await context.QueueRepository.HasActiveWatchlistDownloadsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HasActiveWatchlistDownloadsAsync_UsesPopulatedWatchlistIdentityShape()
    {
        await using var context = await CreateContextAsync();
        const string mixedPayload = """
            {"WatchlistSource":"","watchlistSource":"boomplay","watchlistPlaylistId":"playlist-2","watchlistTrackId":"track-2"}
            """;
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("watch-mixed-shape", "Artist", "Mixed Track", 1) with
            {
                Status = "queued",
                PayloadJson = mixedPayload
            },
            CancellationToken.None);

        Assert.True(await context.QueueRepository.HasActiveWatchlistDownloadsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExistsDuplicateAsync_MatchesPayloadOnlyTrackIdentity()
    {
        await using var context = await CreateContextAsync();
        const string payloadJson = """
            {
              "Title":"Sahani",
              "Artist":"Davy Waweru, Muthoka",
              "Isrc":"QT3F22565438",
              "DeezerId":"359542303"
            }
            """;
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "payload-only-identity",
                artist: "Davy Waweru, Muthoka",
                title: "Sahani",
                destinationFolderId: 1) with
            {
                Status = "failed",
                PayloadJson = payloadJson,
                DurationMs = 205000
            },
            skipDuplicateCheck: true,
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Davy Waweru, Muthoka",
                TrackTitle = "Sahani",
                DestinationFolderId = 1,
                ContentType = "stereo",
                Isrc = "QT3F22565438",
                DurationMs = 205000
            },
            CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task EnqueueAsync_BlocksDuplicateWhenEngineTrackIdExistsOnlyInPayload()
    {
        await using var context = await CreateContextAsync();
        const string existingPayloadJson = """
            {
              "QobuzId":"123456",
              "ContentType":""
            }
            """;
        const string duplicatePayloadJson = """
            {
              "QobuzId":"123456",
              "ContentType":"stereo"
            }
            """;

        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "existing-qobuz-payload-id",
                artist: "Stored Artist",
                title: "Stored Title",
                destinationFolderId: 12) with
            {
                ContentType = null,
                PayloadJson = existingPayloadJson
            },
            skipDuplicateCheck: true,
            CancellationToken.None);

        var inserted = await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "duplicate-qobuz-payload-id",
                artist: "Incoming Artist",
                title: "Incoming Title",
                destinationFolderId: 12) with
            {
                ContentType = "stereo",
                PayloadJson = duplicatePayloadJson
            },
            CancellationToken.None);

        Assert.Null(inserted);
    }

    [Fact]
    public async Task ExistsDuplicateAsync_TreatsBlankQueueContentTypeAsStereo()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "blank-content-stereo-row",
                artist: "Same Artist",
                title: "Same Track",
                destinationFolderId: 14) with
            {
                ContentType = null,
                DurationMs = 180000
            },
            skipDuplicateCheck: true,
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Same Artist",
                TrackTitle = "Same Track",
                DestinationFolderId = 14,
                ContentType = "stereo",
                DurationMs = 180500
            },
            CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsDuplicateAsync_MatchesCompletedRowEvenWhenPayloadFileIsMissing()
    {
        await using var context = await CreateContextAsync();
        var missingPath = Path.Join(context.TempRoot, "downloads", "Artist", "Missing.flac");
        var payloadJson = $$"""{ "FilePath": "{{missingPath}}" }""";
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "completed-missing-file",
                artist: "Shared Artist",
                title: "Original Title",
                destinationFolderId: 9,
                deezerTrackId: "dz-track-missing") with
            {
                Status = "completed",
                PayloadJson = payloadJson
            },
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Different Artist Name",
                TrackTitle = "Different Track Name",
                DestinationFolderId = 9,
                ContentType = "stereo",
                DeezerTrackId = "dz-track-missing",
                RedownloadCooldownMinutes = 720
            },
            CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsDuplicateAsync_MatchesCompletedRowWhenPayloadFileExists()
    {
        await using var context = await CreateContextAsync();
        var existingPath = Path.Join(context.TempRoot, "downloads", "Artist", "Existing.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
        await File.WriteAllTextAsync(existingPath, "audio", CancellationToken.None);
        var payloadJson = $$"""{ "FilePath": "{{existingPath}}" }""";
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "completed-existing-file",
                artist: "Shared Artist",
                title: "Original Title",
                destinationFolderId: 9,
                deezerTrackId: "dz-track-existing") with
            {
                Status = "completed",
                PayloadJson = payloadJson
            },
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Different Artist Name",
                TrackTitle = "Different Track Name",
                DestinationFolderId = 9,
                ContentType = "stereo",
                DeezerTrackId = "dz-track-existing",
                RedownloadCooldownMinutes = 720
            },
            CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsDuplicateAsync_UsesDurationToleranceForMetadataMatches()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("duration-tolerance", "Precise Artist", "Precise Track", 9) with
            {
                DurationMs = 180000
            },
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Precise Artist",
                TrackTitle = "Precise Track",
                DestinationFolderId = 9,
                ContentType = "stereo",
                DurationMs = 181500
            },
            CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsDuplicateAsync_MatchesCompletedRowFromFinalDestinationsColumn()
    {
        await using var context = await CreateContextAsync();
        var staleStagingPath = Path.Join(context.TempRoot, "downloads", "Artist", "Stale.flac");
        var finalLibraryPath = Path.Join(context.TempRoot, "library", "Artist", "Final.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(finalLibraryPath)!);
        await File.WriteAllTextAsync(finalLibraryPath, "audio", CancellationToken.None);

        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem(
                queueUuid: "completed-final-destination",
                artist: "Shared Artist",
                title: "Original Title",
                destinationFolderId: 9,
                deezerTrackId: "dz-track-final") with
            {
                Status = "completed",
                PayloadJson = $$"""{ "FilePath": "{{staleStagingPath}}" }""",
                FinalDestinationsJson = $$"""{ "{{staleStagingPath}}": "{{finalLibraryPath}}" }"""
            },
            CancellationToken.None);

        var exists = await context.QueueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                ArtistName = "Different Artist Name",
                TrackTitle = "Different Track Name",
                DestinationFolderId = 9,
                ContentType = "stereo",
                DeezerTrackId = "dz-track-final",
                RedownloadCooldownMinutes = 720
            },
            CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task UpdateStatusAsync_CompletedWithDestination_SetsPendingEnrichmentAndFinalization()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("enrichment-with-destination", "Artist", "Track", 9),
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "enrichment-with-destination",
            "completed",
            downloaded: 1,
            progress: 100,
            cancellationToken: CancellationToken.None);

        var updated = await context.QueueRepository.GetByUuidAsync("enrichment-with-destination", CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal("pending", updated!.EnrichmentStatus);
        Assert.Equal("pending", updated.FinalizationStatus);
    }

    [Fact]
    public async Task UpdateStatusAsync_CompletedWithoutDestination_SetsEnrichmentAndFinalizationNotRequired()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("enrichment-no-destination", "Artist", "Track", destinationFolderId: null),
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "enrichment-no-destination",
            "completed",
            downloaded: 1,
            progress: 100,
            cancellationToken: CancellationToken.None);

        var updated = await context.QueueRepository.GetByUuidAsync("enrichment-no-destination", CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal("not_required", updated!.EnrichmentStatus);
        Assert.Equal("not_required", updated.FinalizationStatus);
    }

    [Fact]
    public async Task UpdateStatusAsync_PreservesProgressWhenStatusWriteDoesNotProvideProgress()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("progress-preserve", "Artist", "Track", 9),
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "progress-preserve",
            "running",
            progress: 42.5,
            cancellationToken: CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            "progress-preserve",
            "paused",
            cancellationToken: CancellationToken.None);

        var paused = await context.QueueRepository.GetByUuidAsync("progress-preserve", CancellationToken.None);
        Assert.NotNull(paused);
        Assert.Equal(42.5, paused!.Progress);

        await context.QueueRepository.UpdateStatusAsync(
            "progress-preserve",
            "failed",
            "Download failed",
            cancellationToken: CancellationToken.None);

        var failed = await context.QueueRepository.GetByUuidAsync("progress-preserve", CancellationToken.None);
        Assert.NotNull(failed);
        Assert.Equal(42.5, failed!.Progress);

        await context.QueueRepository.UpdateStatusAsync(
            "progress-preserve",
            "queued",
            error: null,
            cancellationToken: CancellationToken.None);

        var queued = await context.QueueRepository.GetByUuidAsync("progress-preserve", CancellationToken.None);
        Assert.NotNull(queued);
        Assert.Equal(0, queued!.Progress);

        await context.QueueRepository.UpdateStatusAsync(
            "progress-preserve",
            "completed",
            downloaded: 1,
            cancellationToken: CancellationToken.None);

        var completed = await context.QueueRepository.GetByUuidAsync("progress-preserve", CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(100, completed!.Progress);
    }

    [Fact]
    public async Task GetActivitiesTasksAsync_RendersAllVisibleRowsInFifoOrder()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("terminal-old", "Artist", "Terminal Old", 1) with { Status = "completed" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("terminal-middle", "Artist", "Terminal Middle", 1) with { Status = "failed" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("terminal-new", "Artist", "Terminal New", 1) with { Status = "canceled" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-queued-visible", "Artist", "Active Queued", 1) with { Status = "queued" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-running-visible", "Artist", "Active Running", 1) with { Status = "running" },
            CancellationToken.None);

        var items = await context.QueueRepository.GetActivitiesTasksAsync(terminalItemLimit: 2, CancellationToken.None);
        var queueUuids = items.Select(item => item.QueueUuid).ToList();

        Assert.Contains("active-queued-visible", queueUuids);
        Assert.Contains("active-running-visible", queueUuids);
        Assert.Contains("terminal-old", queueUuids);
        Assert.Contains("terminal-middle", queueUuids);
        Assert.Contains("terminal-new", queueUuids);
        Assert.Equal(5, queueUuids.Count);
        Assert.Equal(
            ["terminal-old", "terminal-middle", "terminal-new", "active-queued-visible", "active-running-visible"],
            queueUuids);
    }

    [Fact]
    public async Task MarkActivitiesClearedByStatusesAsync_HidesEveryCompletedUiStatus()
    {
        await using var context = await CreateContextAsync();
        var completedStatuses = new[]
        {
            "completed",
            "complete",
            "finished",
            "download finished",
            "done",
            "success",
            "skipped"
        };

        foreach (var status in completedStatuses)
        {
            await context.QueueRepository.EnqueueAsync(
                CreateQueueItem($"completed-shape-{status.Replace(' ', '-')}", "Artist", status, 44) with { Status = status },
                CancellationToken.None);
        }

        var hidden = await context.QueueRepository.MarkActivitiesClearedByStatusesAsync(completedStatuses, CancellationToken.None);

        Assert.Equal(completedStatuses.Length, hidden);
        var visible = await context.QueueRepository.GetActivitiesTasksAsync(terminalItemLimit: 20, CancellationToken.None);
        Assert.Empty(visible);
    }

    [Fact]
    public async Task MarkTerminalActivitiesClearedAsync_DoesNotHideResolvingActiveRows()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("active-resolving-visible", "Artist", "Resolving", 44) with { Status = "resolving" },
            CancellationToken.None);
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("terminal-failed-hidden", "Artist", "Failed", 44) with { Status = "failed" },
            CancellationToken.None);

        var hidden = await context.QueueRepository.MarkTerminalActivitiesClearedAsync(CancellationToken.None);

        Assert.Equal(1, hidden);
        var visible = await context.QueueRepository.GetActivitiesTasksAsync(terminalItemLimit: 20, CancellationToken.None);
        Assert.Contains(visible, item => item.QueueUuid == "active-resolving-visible");
        Assert.DoesNotContain(visible, item => item.QueueUuid == "terminal-failed-hidden");
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
    public async Task MarkActivitiesClearedByStatusAsync_HidesPendingMoveRowsWithoutDeletingDestinationId()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("completed-pending-visible", "Artist", "Pending", 44),
            CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            "completed-pending-visible",
            "completed",
            downloaded: 1,
            progress: 100,
            cancellationToken: CancellationToken.None);

        Assert.Contains(
            await context.QueueRepository.GetActivitiesTasksAsync(terminalItemLimit: 10, CancellationToken.None),
            item => item.QueueUuid == "completed-pending-visible");

        var hidden = await context.QueueRepository.MarkActivitiesClearedByStatusAsync("completed", CancellationToken.None);
        var deleted = await context.QueueRepository.DeleteClearableByStatusAsync("completed", CancellationToken.None);

        Assert.Equal(1, hidden);
        Assert.Equal(0, deleted);
        var persisted = await context.QueueRepository.GetByUuidAsync("completed-pending-visible", CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(44, persisted.DestinationFolderId);
        Assert.DoesNotContain(
            await context.QueueRepository.GetActivitiesTasksAsync(terminalItemLimit: 10, CancellationToken.None),
            item => item.QueueUuid == "completed-pending-visible");
    }

    [Fact]
    public async Task RequeueAsync_RestoresActivitiesVisibilityForClearedRow()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-hidden-retry", "Artist", "Retry", 77) with { Status = "failed" },
            CancellationToken.None);

        await context.QueueRepository.MarkActivitiesClearedByUuidAsync("failed-hidden-retry", CancellationToken.None);
        Assert.DoesNotContain(
            await context.QueueRepository.GetActivitiesTasksAsync(terminalItemLimit: 10, CancellationToken.None),
            item => item.QueueUuid == "failed-hidden-retry");

        var requeued = await context.QueueRepository.RequeueAsync(
            "failed-hidden-retry",
            QueueRequeueOrigin.AutoRetry,
            CancellationToken.None);

        Assert.True(requeued);
        Assert.Contains(
            await context.QueueRepository.GetActivitiesTasksAsync(terminalItemLimit: 10, CancellationToken.None),
            item => item.QueueUuid == "failed-hidden-retry");
    }

    [Fact]
    public async Task RequeueAsync_BlocksCancelledItem_WhenOriginIsNotManual()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("cancelled-auto-block", "Artist", "Blocked", 42) with { Status = "canceled" },
            CancellationToken.None);

        var requeued = await context.QueueRepository.RequeueAsync(
            "cancelled-auto-block",
            QueueRequeueOrigin.AutoRetry,
            CancellationToken.None);

        Assert.False(requeued);
        var persisted = await context.QueueRepository.GetByUuidAsync("cancelled-auto-block", CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("canceled", persisted!.Status, ignoreCase: true);
    }

    [Fact]
    public async Task RequeueAsync_AllowsCancelledItem_WhenOriginIsManual()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("cancelled-manual-allow", "Artist", "Allowed", 42) with { Status = "canceled" },
            CancellationToken.None);

        var requeued = await context.QueueRepository.RequeueAsync(
            "cancelled-manual-allow",
            QueueRequeueOrigin.Manual,
            CancellationToken.None);

        Assert.True(requeued);
        var persisted = await context.QueueRepository.GetByUuidAsync("cancelled-manual-allow", CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("queued", persisted!.Status, ignoreCase: true);
    }

    [Fact]
    public async Task ScheduleRetryAsync_PersistsDelayAndRemovesItemFromRunnableQueue()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("durable-retry", "Artist", "Retry", null) with { Status = "failed" },
            CancellationToken.None);

        var scheduled = await context.QueueRepository.ScheduleRetryAsync(
            "durable-retry",
            "qobuz",
            "temporary provider failure",
            maxAttempts: 3,
            CancellationToken.None);

        Assert.True(scheduled);
        Assert.True(await context.QueueRepository.HasScheduledRetriesAsync(CancellationToken.None));
        Assert.Equal(0, await context.QueueRepository.GetRunnableDownloadCountAsync(CancellationToken.None));
        var persisted = await context.QueueRepository.GetByUuidAsync("durable-retry", CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("retry_waiting", persisted!.Status);
        Assert.Equal("temporary provider failure", persisted.Error);

        Assert.True(await context.QueueRepository.ScheduleRetryAsync(
            "durable-retry",
            "qobuz",
            "second temporary failure",
            maxAttempts: 2,
            CancellationToken.None));
        Assert.False(await context.QueueRepository.ScheduleRetryAsync(
            "durable-retry",
            "qobuz",
            "must be exhausted",
            maxAttempts: 2,
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateStatusAsync_ReservesOneHundredPercentForCompletedAudio()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("validation-progress", "Artist", "Validation", null),
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "validation-progress",
            "running",
            progress: 100,
            cancellationToken: CancellationToken.None);
        var running = await context.QueueRepository.GetByUuidAsync("validation-progress", CancellationToken.None);
        Assert.Equal(95, running!.Progress);

        await context.QueueRepository.UpdateStatusAsync(
            "validation-progress",
            "completed",
            progress: 95,
            cancellationToken: CancellationToken.None);
        var completed = await context.QueueRepository.GetByUuidAsync("validation-progress", CancellationToken.None);
        Assert.Equal(100, completed!.Progress);
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

    [Fact]
    public async Task UpdateQueueIdentityAsync_ReplacesStaleIdentityColumnsAndPayload()
    {
        await using var context = await CreateContextAsync();
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("identity-refresh", "Old Artist", "Old Title", 10) with
            {
                Engine = "qobuz",
                Isrc = "OLDISRC12345",
                AppleTrackId = "old-apple",
                DurationMs = 100000,
                QualityRank = 1,
                PayloadJson = """{"SourceService":"qobuz","Isrc":"OLDISRC12345"}"""
            },
            CancellationToken.None);

        var existing = await context.QueueRepository.GetByUuidAsync("identity-refresh", CancellationToken.None);
        Assert.NotNull(existing);

        await context.QueueRepository.UpdateQueueIdentityAsync(
            existing! with
            {
                Engine = "qobuz",
                ArtistName = "Benzema & Dyana Cods",
                TrackTitle = "101%",
                Isrc = "QZPYN2262797",
                AppleTrackId = "1658724857",
                DurationMs = 173845,
                DestinationFolderId = 20,
                QualityRank = 27,
                ContentType = "stereo",
                PayloadJson = """{"SourceService":"apple","Isrc":"QZPYN2262797","SourceUrl":"https://music.apple.com/ke/album/101/1658724362?i=1658724857"}"""
            },
            CancellationToken.None);

        var refreshed = await context.QueueRepository.GetByUuidAsync("identity-refresh", CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Equal("Benzema & Dyana Cods", refreshed!.ArtistName);
        Assert.Equal("101%", refreshed.TrackTitle);
        Assert.Equal("QZPYN2262797", refreshed.Isrc);
        Assert.Equal("1658724857", refreshed.AppleTrackId);
        Assert.Equal(173845, refreshed.DurationMs);
        Assert.Equal(20, refreshed.DestinationFolderId);
        Assert.Equal(27, refreshed.QualityRank);
        Assert.Contains("\"SourceService\":\"apple\"", refreshed.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"Isrc\":\"QZPYN2262797\"", refreshed.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("OLDISRC12345", refreshed.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateStatusAsync_FailedItemDeletesOnlyStagingFilesAndKeepsDestinationFolderId()
    {
        await using var context = await CreateContextAsync(enableStagingCleanup: true);
        var downloadRoot = Path.Join(context.TempRoot, "downloads");
        var albumFolder = Path.Join(downloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var audioPath = Path.Join(albumFolder, "Track.flac");
        var lyricPath = Path.Join(albumFolder, "Track.lrc");
        await File.WriteAllTextAsync(audioPath, "audio", CancellationToken.None);
        await File.WriteAllTextAsync(lyricPath, "lyrics", CancellationToken.None);
        var payloadJson = $$"""
        {
          "filePath": "{{audioPath}}",
          "files": [
            { "path": "{{audioPath}}" },
            { "path": "{{lyricPath}}" }
          ],
          "extrasPath": "{{albumFolder}}"
        }
        """;
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-staging-cleanup", "Artist", "Track", 99) with { PayloadJson = payloadJson },
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "failed-staging-cleanup",
            "failed",
            "temporary failure",
            cancellationToken: CancellationToken.None);

        var item = await context.QueueRepository.GetByUuidAsync("failed-staging-cleanup", CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(99, item.DestinationFolderId);
        Assert.False(File.Exists(audioPath));
        Assert.False(File.Exists(lyricPath));
        Assert.False(Directory.Exists(albumFolder));
        Assert.True(Directory.Exists(downloadRoot));

        var deleted = await context.QueueRepository.DeleteClearableByUuidAsync("failed-staging-cleanup", CancellationToken.None);

        Assert.Equal(1, deleted);
    }

    [Fact]
    public async Task UpdateStatusAsync_StagingCleanupDeletesSameBasenameSidecars()
    {
        await using var context = await CreateContextAsync(enableStagingCleanup: true);
        var downloadRoot = Path.Join(context.TempRoot, "downloads");
        var albumFolder = Path.Join(downloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var audioPath = Path.Join(albumFolder, "Track.flac");
        var ttmlPath = Path.Join(albumFolder, "Track.ttml");
        var tempPath = audioPath + ".tmp";
        await File.WriteAllTextAsync(audioPath, "audio", CancellationToken.None);
        await File.WriteAllTextAsync(ttmlPath, "lyrics", CancellationToken.None);
        await File.WriteAllTextAsync(tempPath, "partial", CancellationToken.None);
        var payloadJson = $$"""
        {
          "filePath": "{{audioPath}}",
          "files": [
            { "path": "{{audioPath}}" }
          ]
        }
        """;
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-sidecar-cleanup", "Artist", "Track", 99) with { PayloadJson = payloadJson },
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "failed-sidecar-cleanup",
            "failed",
            "temporary failure",
            cancellationToken: CancellationToken.None);

        Assert.False(File.Exists(audioPath));
        Assert.False(File.Exists(ttmlPath));
        Assert.False(File.Exists(tempPath));
        Assert.False(Directory.Exists(albumFolder));
    }

    [Fact]
    public async Task UpdateStatusAsync_StagingCleanupRefusesPathsOutsideDownloadRoot()
    {
        await using var context = await CreateContextAsync(enableStagingCleanup: true);
        var outsideFolder = Path.Join(context.TempRoot, "library", "Artist");
        Directory.CreateDirectory(outsideFolder);
        var outsidePath = Path.Join(outsideFolder, "Track.flac");
        await File.WriteAllTextAsync(outsidePath, "audio", CancellationToken.None);
        var payloadJson = $$"""
        {
          "filePath": "{{outsidePath}}",
          "files": [
            { "path": "{{outsidePath}}" }
          ],
          "extrasPath": "{{outsideFolder}}"
        }
        """;
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-outside-root", "Artist", "Track", 100) with { PayloadJson = payloadJson },
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "failed-outside-root",
            "failed",
            "temporary failure",
            cancellationToken: CancellationToken.None);

        var item = await context.QueueRepository.GetByUuidAsync("failed-outside-root", CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(100, item.DestinationFolderId);
        Assert.True(File.Exists(outsidePath));
        Assert.True(Directory.Exists(outsideFolder));

        var deleted = await context.QueueRepository.DeleteClearableByUuidAsync("failed-outside-root", CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.NotNull(await context.QueueRepository.GetByUuidAsync("failed-outside-root", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateStatusAsync_StagingCleanupReadsPathPropertiesCaseInsensitively()
    {
        await using var context = await CreateContextAsync(enableStagingCleanup: true);
        var downloadRoot = Path.Join(context.TempRoot, "downloads");
        var albumFolder = Path.Join(downloadRoot, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var audioPath = Path.Join(albumFolder, "Track.flac");
        await File.WriteAllTextAsync(audioPath, "audio", CancellationToken.None);
        var payloadJson = $$"""
        {
          "FilePath": "{{audioPath}}",
          "Files": [
            { "Path": "{{audioPath}}" }
          ],
          "ExtrasPath": "{{albumFolder}}"
        }
        """;
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-cased-paths", "Artist", "Track", 101) with { PayloadJson = payloadJson },
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "failed-cased-paths",
            "failed",
            "temporary failure",
            cancellationToken: CancellationToken.None);

        Assert.False(File.Exists(audioPath));
        Assert.False(Directory.Exists(albumFolder));
    }

    [Fact]
    public async Task UpdateStatusAsync_StagingCleanupRefusesSymlinkTraversal()
    {
        await using var context = await CreateContextAsync(enableStagingCleanup: true);
        var downloadRoot = Path.Join(context.TempRoot, "downloads");
        var outsideFolder = Path.Join(context.TempRoot, "library-target");
        Directory.CreateDirectory(outsideFolder);
        var linkPath = Path.Join(downloadRoot, "linked-library");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outsideFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var outsidePathThroughLink = Path.Join(linkPath, "Track.flac");
        await File.WriteAllTextAsync(outsidePathThroughLink, "audio", CancellationToken.None);
        var payloadJson = $$"""
        {
          "filePath": "{{outsidePathThroughLink}}",
          "files": [
            { "path": "{{outsidePathThroughLink}}" }
          ]
        }
        """;
        await context.QueueRepository.EnqueueAsync(
            CreateQueueItem("failed-symlink-root", "Artist", "Track", 102) with { PayloadJson = payloadJson },
            CancellationToken.None);

        await context.QueueRepository.UpdateStatusAsync(
            "failed-symlink-root",
            "failed",
            "temporary failure",
            cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(Path.Join(outsideFolder, "Track.flac")));
        Assert.Equal(0, await context.QueueRepository.DeleteClearableByUuidAsync("failed-symlink-root", CancellationToken.None));
    }

    private static Task<TestContext> CreateContextAsync(bool enableStagingCleanup = false)
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-queue-duplicate-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var downloadRoot = Path.Join(tempRoot, "downloads");
        Directory.CreateDirectory(downloadRoot);

        var queueDbPath = Path.Join(tempRoot, "queue.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = tempRoot
            })
            .Build();

        var cleanupService = enableStagingCleanup
            ? new DownloadStagingCleanupService(
                NullLogger<DownloadStagingCleanupService>.Instance,
                downloadRootOverride: downloadRoot)
            : null;
        var queueRepository = new DownloadQueueRepository(
            config,
            NullLogger<DownloadQueueRepository>.Instance,
            cleanupService);
        return Task.FromResult(new TestContext(tempRoot, config, queueRepository));
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
        public TestContext(string tempRoot, IConfiguration configuration, DownloadQueueRepository queueRepository)
        {
            TempRoot = tempRoot;
            Configuration = configuration;
            QueueRepository = queueRepository;
        }

        public string TempRoot { get; }
        public IConfiguration Configuration { get; }
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
