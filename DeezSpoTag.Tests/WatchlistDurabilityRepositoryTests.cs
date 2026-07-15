using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistDurabilityRepositoryTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watch-durability-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = NewRepository();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReconciliationRequests_AreDurableCoalescedAndCannotLoseAConcurrentRefresh()
    {
        Assert.True(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", " Spotify ", " list-a "));
        Assert.True(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "deezer", "list-b"));

        var persistedRepository = NewRepository();
        var original = await persistedRepository.GetWatchlistReconciliationRequestsAsync();
        Assert.Equal(2, original.Count);
        Assert.Contains(original, request => request.Kind == "playlist" && request.Source == "spotify" && request.Identifier == "list-a");
        Assert.Contains(original, request => request.Kind == "playlist" && request.Source == "deezer" && request.Identifier == "list-b");

        var originalListA = Assert.Single(original, request => request.Identifier == "list-a");
        await Task.Delay(5);
        Assert.False(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "spotify", "list-a"));
        Assert.Equal(0, await _repository.CompleteWatchlistReconciliationRequestsAsync([originalListA]));
        Assert.Equal(2, await _repository.GetWatchlistReconciliationRequestCountAsync());

        var refreshedListA = Assert.Single(
            await _repository.GetWatchlistReconciliationRequestsAsync(),
            request => request.Identifier == "list-a");
        Assert.Equal(1, await _repository.CompleteWatchlistReconciliationRequestsAsync([refreshedListA]));

        Assert.True(await _repository.EnqueueWatchlistReconciliationRequestAsync("all", null, null));
        var global = Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync());
        Assert.Equal("all", global.Kind);
        await Task.Delay(5);
        Assert.False(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "tidal", "list-c"));
        Assert.Equal(0, await _repository.CompleteWatchlistReconciliationRequestsAsync([global]));
        var refreshedGlobal = Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync());
        Assert.Equal("all", refreshedGlobal.Kind);
        Assert.Equal(1, await _repository.CompleteWatchlistReconciliationRequestsAsync([refreshedGlobal]));
        Assert.Equal(0, await _repository.GetWatchlistReconciliationRequestCountAsync());
    }

    [Fact]
    public async Task SyncJobs_AreCreatedPerConfiguredTargetAndEnforceLeaseOwnership()
    {
        await AddPlaylistWithTargetsAsync("lease-list", ["plex", "jellyfin"]);
        var jobs = await _repository.EnqueueWatchlistSyncJobAsync(
            "spotify",
            "lease-list",
            "track-1",
            destinationFolderId: 42,
            finalFilePaths: ["/music/track-1.flac"],
            queueUuid: "queue-1");

        Assert.Equal(new[] { "jellyfin", "plex" }, jobs.Select(job => job.TargetService).Order().ToArray());
        Assert.All(jobs, job => Assert.Equal("queue-1", job.QueueUuid));
        var first = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(1), "worker-a"));
        Assert.Equal("worker-a", first.LeaseOwner);
        Assert.Equal("processing", first.Status);
        Assert.False(await _repository.CompleteWatchlistSyncJobAsync(first.Id, "worker-b"));
        Assert.False(await _repository.RetryWatchlistSyncJobAsync(first.Id, "worker-b", 1, DateTimeOffset.UtcNow, "wrong owner"));
        Assert.False(await _repository.RenewWatchlistSyncJobLeaseAsync(first.Id, "worker-b", TimeSpan.FromMinutes(1)));
        Assert.True(await _repository.RenewWatchlistSyncJobLeaseAsync(first.Id, "worker-a", TimeSpan.FromMinutes(1)));
        Assert.True(await _repository.CompleteWatchlistSyncJobAsync(first.Id, "worker-a"));

        var second = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(1), "worker-b"));
        await SetExpiredProcessingLeaseAsync(second.Id);
        var counts = await _repository.GetWatchlistSyncJobStatusCountsAsync();
        Assert.Equal(1, counts.Due);
        Assert.Equal(1, counts.ExpiredProcessing);
        Assert.Equal(0, counts.Processing);

        var reclaimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(1), "worker-c"));
        Assert.Equal(second.Id, reclaimed.Id);
        Assert.Equal("worker-c", reclaimed.LeaseOwner);
        Assert.True(await _repository.BlockWatchlistSyncJobAsync(reclaimed.Id, "worker-c", "configuration invalid"));
        Assert.Equal(1, (await _repository.GetWatchlistSyncJobStatusCountsAsync()).Blocked);

        await AddPlaylistWithTargetsAsync("lease-list", ["plex", "jellyfin"]);
        var resumed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(1), "worker-d"));
        Assert.Equal(reclaimed.Id, resumed.Id);
        Assert.Equal(0, resumed.AttemptCount);
        Assert.Null(resumed.LastError);
        Assert.True(await _repository.CompleteWatchlistSyncJobAsync(resumed.Id, "worker-d"));
    }

    [Fact]
    public async Task TargetAndSourceChanges_DeleteObsoleteMembershipClaimsAndJobs()
    {
        await AddPlaylistWithTargetsAsync("prune-list", ["plex", "jellyfin"]);
        await _repository.AddPlaylistWatchTracksAsync(
            "spotify",
            "prune-list",
            [new PlaylistWatchTrackInsert("keep", null), new PlaylistWatchTrackInsert("remove", null)]);
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "spotify",
            "prune-list",
            "remove",
            ["queue-remove"],
            destinationFolderId: 42);
        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "prune-list",
            "jellyfin",
            "jellyfin-list",
            [new PlaylistWatchTargetMembership("remove", 99, "remote-remove")]);
        await _repository.EnqueueWatchlistSyncJobAsync(
            "spotify",
            "prune-list",
            "remove",
            destinationFolderId: 42,
            finalFilePaths: ["/music/remove.flac"],
            queueUuid: "queue-remove");

        Assert.Equal(1, await _repository.RemovePlaylistWatchTracksNotInAsync("spotify", "prune-list", ["keep"]));
        Assert.Empty(await _repository.GetPlaylistWatchDownloadClaimsAsync("queue-remove"));
        Assert.False(await _repository.IsPlaylistWatchTrackSyncedToTargetAsync("spotify", "prune-list", "remove", "jellyfin"));
        Assert.DoesNotContain(
            await _repository.ClaimDueWatchlistSyncJobsAsync(100, TimeSpan.FromMinutes(1), "prune-worker"),
            job => job.TrackId == "remove");

        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "prune-list",
            "jellyfin",
            "jellyfin-list",
            [new PlaylistWatchTargetMembership("keep", 100, "remote-keep")]);
        await AddPlaylistWithTargetsAsync("prune-list", ["plex"]);
        Assert.False(await _repository.IsPlaylistWatchTrackSyncedToTargetAsync("spotify", "prune-list", "keep", "jellyfin"));
        var recreated = await _repository.EnqueueWatchlistSyncJobAsync(
            "spotify",
            "prune-list",
            "keep",
            destinationFolderId: 42,
            finalFilePaths: ["/music/keep.flac"],
            queueUuid: "queue-keep");
        Assert.Equal("plex", Assert.Single(recreated).TargetService);
    }

    private LibraryRepository NewRepository()
        => new(_configuration, NullLogger<LibraryRepository>.Instance);

    private async Task AddPlaylistWithTargetsAsync(string sourceId, IReadOnlyList<string> targets)
    {
        await _repository.AddPlaylistWatchlistAsync(
            "spotify",
            sourceId,
            new PlaylistWatchlistMetadataInput(sourceId, null, null, 1));
        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                Source: "spotify",
                SourceId: sourceId,
                DestinationFolderId: 42,
                Service: targets.FirstOrDefault(),
                SyncTargets: targets,
                PreferredEngine: null,
                DownloadEngineOrder: null,
                DownloadVariantMode: null,
                SyncMode: "mirror",
                UpdateArtwork: true,
                ReuseSavedArtwork: false));
    }

    private async Task SetExpiredProcessingLeaseAsync(long jobId)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE watchlist_sync_job
SET status='processing', lease_until_utc=@expired, next_attempt_utc=@expired
WHERE id=@id;";
        command.Parameters.AddWithValue("id", jobId);
        command.Parameters.AddWithValue("expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
