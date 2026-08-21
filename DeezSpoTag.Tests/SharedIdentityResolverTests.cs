using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SharedIdentityResolverTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private SharedIdentityResolver _resolver = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-shared-identity-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={Path.Join(_tempRoot, "library.db")}"
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        _resolver = new SharedIdentityResolver(_repository, NullLogger<SharedIdentityResolver>.Instance);
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
    public async Task OneLocalTrackMiss_IsOneRetryRowSharedByTwoPlaylists()
    {
        await AddPlaylistWithLocalTrackAsync("list-a", 101, "track-a");
        await AddPlaylistWithLocalTrackAsync("list-b", 101, "track-b");
        var searches = 0;

        var results = await _resolver.ResolveAsync(
            "plex",
            [new SharedIdentityResolveItem(101)],
            (_, _) =>
            {
                searches++;
                return Task.FromResult<string?>(null);
            });

        Assert.Equal(1, searches);
        Assert.Equal(SharedIdentityResolver.StatusPendingRefresh, Assert.Single(results).Status);
        var row = await _repository.GetWatchlistSharedIdentityAsync(101, "plex");
        Assert.NotNull(row);
        Assert.Equal(SharedIdentityResolver.StatusPendingRefresh, row!.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.NextRetryUtc);
        Assert.Single(await _repository.GetWatchlistSharedIdentitiesAsync([101], "plex"));
    }

    [Fact]
    public async Task PendingRefreshBeforeRetry_SkipsThatTrackOnly_AndNewTracksStillSearch()
    {
        await AddPlaylistWithLocalTrackAsync("list-skip", 201, "known-track");
        await AddPlaylistWithLocalTrackAsync("list-new", 202, "new-track");
        await _repository.UpsertWatchlistSharedIdentityAsync(
            new WatchlistSharedIdentityUpsertInput(
                201,
                "plex",
                TargetItemId: null,
                SharedIdentityResolver.StatusPendingRefresh,
                "No target match found.",
                AttemptCount: 1,
                NextRetryUtc: DateTimeOffset.UtcNow.AddMinutes(5)));
        await _repository.EnqueueMediaServerRefreshAsync(42, "plex", ["/tmp/pending.flac"]);
        var searched = new List<long>();

        var results = await _resolver.ResolveAsync(
            "plex",
            [new SharedIdentityResolveItem(201), new SharedIdentityResolveItem(202)],
            (item, _) =>
            {
                searched.Add(item.LocalTrackId);
                return Task.FromResult<string?>(null);
            });

        Assert.Equal([202], searched);
        Assert.Equal(SharedIdentityResolver.StatusPendingRefresh, results.Single(item => item.LocalTrackId == 201).Status);
        Assert.False(results.Single(item => item.LocalTrackId == 201).Searched);
        Assert.True(results.Single(item => item.LocalTrackId == 202).Searched);
        Assert.NotNull(await _repository.GetWatchlistSharedIdentityAsync(202, "plex"));
    }

    [Fact]
    public async Task PendingRefreshRetryDue_SearchesEvenIfOutboxIsPending()
    {
        await AddPlaylistWithLocalTrackAsync("list-retry", 211, "retry-track");
        await _repository.UpsertWatchlistSharedIdentityAsync(
            new WatchlistSharedIdentityUpsertInput(
                211,
                "plex",
                TargetItemId: null,
                SharedIdentityResolver.StatusPendingRefresh,
                "No target match found.",
                AttemptCount: 1,
                NextRetryUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await _repository.EnqueueMediaServerRefreshAsync(42, "plex", ["/tmp/pending.flac"]);
        var searched = new List<long>();

        var results = await _resolver.ResolveAsync(
            "plex",
            [new SharedIdentityResolveItem(211)],
            (item, _) =>
            {
                searched.Add(item.LocalTrackId);
                return Task.FromResult<string?>(null);
            });

        Assert.Equal([211], searched);
        Assert.True(Assert.Single(results).Searched);
    }

    [Fact]
    public async Task Confirm_RunsOnlyOnWriteLagOrCompletedRefresh()
    {
        await _repository.UpsertMediaServerTrackMetadataAsync(
        [
            new MediaServerTrackMetadataUpsertDto(301, "plex", "plex-301", "/tmp/301.flac", DateTimeOffset.UtcNow)
        ]);
        await _repository.UpsertWatchlistSharedIdentityAsync(
            new WatchlistSharedIdentityUpsertInput(301, "plex", "plex-301", SharedIdentityResolver.StatusResolved));
        var confirmCalls = 0;

        var cached = await _resolver.ResolveAsync(
            "plex",
            [new SharedIdentityResolveItem(301)],
            (_, _) => throw new InvalidOperationException("Cached metadata must not search."),
            confirmMissing: (_, _) =>
            {
                confirmCalls++;
                return Task.FromResult(false);
            });

        Assert.Equal(0, confirmCalls);
        Assert.Equal("plex-301", Assert.Single(cached).TargetItemId);

        var writeLag = await _resolver.ResolveAsync(
            "plex",
            [new SharedIdentityResolveItem(301)],
            (_, _) => throw new InvalidOperationException("Write-lag confirm must not search."),
            confirmMissing: (_, _) =>
            {
                confirmCalls++;
                return Task.FromResult(true);
            },
            confirmExisting: true);

        Assert.Equal(1, confirmCalls);
        Assert.Null(Assert.Single(writeLag).TargetItemId);
        Assert.True(writeLag[0].Confirmed);
        Assert.Empty(await _repository.GetMediaServerItemIdsByTrackIdsAsync("plex", [301]));
        Assert.Equal(
            SharedIdentityResolver.StatusPendingRefresh,
            (await _repository.GetWatchlistSharedIdentityAsync(301, "plex"))!.Status);
    }

    [Theory]
    [InlineData("plex", "plex-501")]
    [InlineData("jellyfin", "jf-501")]
    [InlineData("navidrome", "nd-501")]
    public async Task PromoteSharedIdentitiesFromMetadata_FlipsPendingRowsForEveryTarget(
        string targetService,
        string targetItemId)
    {
        await AddPlaylistWithLocalTrackAsync("promote-list", 501, "promote-track");
        await _repository.UpsertWatchlistSharedIdentityAsync(
            new WatchlistSharedIdentityUpsertInput(
                501,
                targetService,
                TargetItemId: null,
                SharedIdentityResolver.StatusPendingRefresh,
                "No target match found.",
                AttemptCount: 2,
                NextRetryUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.True(await _repository.HasDueIdentityRetryPlaylistAsync());
        await _repository.UpsertMediaServerTrackMetadataAsync(
        [
            new MediaServerTrackMetadataUpsertDto(
                501,
                targetService,
                targetItemId,
                "/music/Artist/Album/promote.flac",
                DateTimeOffset.UtcNow)
        ]);

        var promoted = await _repository.PromoteSharedIdentitiesFromMetadataAsync(targetService);

        Assert.Equal(1, promoted);
        var row = await _repository.GetWatchlistSharedIdentityAsync(501, targetService);
        Assert.NotNull(row);
        Assert.Equal(SharedIdentityResolver.StatusResolved, row!.Status);
        Assert.Equal(targetItemId, row.TargetItemId);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.NextRetryUtc);
        Assert.False(await _repository.HasDueIdentityRetryPlaylistAsync());
    }

    [Fact]
    public async Task FirstResolvedPlexId_EnqueuesOneJobForWaitingForSeedPlaylists()
    {
        await AddPlaylistWithLocalTrackAsync("seed-list", 401, "seed-track");
        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "seed-list",
            "plex",
            "plex-playlist",
            [new PlaylistWatchTargetMembershipWrite("seed-track", 401, null, "waiting_for_identity")]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "seed-list", "snapshot-seed");
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "seed-worker"));
        Assert.True(await _repository.CompleteWatchlistPlaylistSyncJobAsync(
            claimed,
            "seed-worker",
            WatchlistAppliedKind.WaitingForSeed,
            null,
            null));
        Assert.Empty(await _repository.GetWatchlistSyncJobsAsync("spotify", "seed-list"));

        var results = await _resolver.ResolveAsync(
            "plex",
            [new SharedIdentityResolveItem(401, "/tmp/401.flac")],
            (_, _) => Task.FromResult<string?>("plex-401"),
            currentRevision: "snapshot-seed");

        Assert.Equal("plex-401", Assert.Single(results).TargetItemId);
        var job = Assert.Single(await _repository.GetWatchlistSyncJobsAsync("spotify", "seed-list"));
        Assert.Equal("playlist", job.TrackId);
        Assert.Equal("plex", job.TargetService);
        Assert.False(string.IsNullOrWhiteSpace(job.SnapshotId));
        Assert.Equal("snapshot-seed:plex-membership-v2", job.SnapshotId);
    }

    private async Task AddPlaylistWithLocalTrackAsync(string sourceId, long localTrackId, string trackSourceId)
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
                Service: "plex",
                SyncTargets: ["plex"],
                PreferredEngine: null,
                DownloadEngineOrder: null,
                DownloadVariantMode: null,
                SyncMode: "mirror",
                UpdateArtwork: true,
                ReuseSavedArtwork: false));
        await _repository.AddPlaylistWatchTracksAsync(
            "spotify",
            sourceId,
            [new PlaylistWatchTrackInsert(trackSourceId, "ISRC" + localTrackId)]);
        await _repository.UpdatePlaylistWatchTrackVerificationAsync(
            "spotify",
            sourceId,
            new PlaylistWatchTrackVerification(trackSourceId, localTrackId, "identity_verified"));
    }
}
