using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
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

        var initiallyClaimed = await _repository.ClaimDueWatchlistReconciliationRequestsAsync(
            2, TimeSpan.FromMinutes(1), "worker-a");
        var originalListA = Assert.Single(initiallyClaimed, request => request.Identifier == "list-a");
        await Task.Delay(5);
        Assert.False(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "spotify", "list-a"));
        Assert.Equal(0, await _repository.CompleteClaimedWatchlistReconciliationRequestsAsync([originalListA], "worker-a"));
        Assert.Equal(2, await _repository.GetWatchlistReconciliationRequestCountAsync());

        var refreshedListA = Assert.Single(await _repository.ClaimDueWatchlistReconciliationRequestsAsync(
            1, TimeSpan.FromMinutes(1), "worker-b"));
        Assert.Equal(1, await _repository.CompleteClaimedWatchlistReconciliationRequestsAsync([refreshedListA], "worker-b"));

        Assert.True(await _repository.EnqueueWatchlistReconciliationRequestAsync("all", null, null));
        var global = Assert.Single(await _repository.ClaimDueWatchlistReconciliationRequestsAsync(
            1, TimeSpan.FromMinutes(1), "worker-c"));
        Assert.Equal("all", global.Kind);
        await Task.Delay(5);
        Assert.False(await _repository.HasWatchlistReconciliationRequestAsync("playlist", "tidal", "list-c"));
        Assert.True(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "tidal", "list-c"));
        Assert.True(await _repository.HasWatchlistReconciliationRequestAsync("playlist", "tidal", "list-c"));
        Assert.Equal(1, await _repository.CompleteClaimedWatchlistReconciliationRequestsAsync([global], "worker-c"));

        var originalListB = Assert.Single(initiallyClaimed, request => request.Identifier == "list-b");
        Assert.Equal(1, await _repository.CompleteClaimedWatchlistReconciliationRequestsAsync([originalListB], "worker-a"));
        var targeted = Assert.Single(await _repository.ClaimDueWatchlistReconciliationRequestsAsync(
            1, TimeSpan.FromMinutes(1), "worker-d"));
        Assert.Equal("list-c", targeted.Identifier);
        Assert.Equal(1, await _repository.CompleteClaimedWatchlistReconciliationRequestsAsync([targeted], "worker-d"));
        Assert.Equal(0, await _repository.GetWatchlistReconciliationRequestCountAsync());
    }

    [Fact]
    public async Task WatchlistDedupe_IsGlobalAcrossDestinationFoldersAndPreservesAudioVariants()
    {
        var firstRoot = Path.Join(_tempRoot, "library-a");
        var secondRoot = Path.Join(_tempRoot, "library-b");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var existingPath = Path.Join(firstRoot, "Existing Track.flac");
        await File.WriteAllBytesAsync(existingPath, [1, 2, 3, 4]);

        var firstFolder = await _repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            firstRoot, "Library A", true, "Library A", "27", false, null, null, "profile-a"));
        var secondFolder = await _repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            secondRoot, "Library B", true, "Library B", "27", false, null, null, "profile-b"));
        await ExecuteSqlAsync(@"
INSERT INTO artist (id, name) VALUES (9001, 'Existing Artist');
INSERT INTO album (id, artist_id, title) VALUES (9001, 9001, 'Existing Album');
INSERT INTO track (id, album_id, title, duration_ms) VALUES (9001, 9001, 'Existing Track', 180000);
INSERT INTO audio_file (id, path, relative_path, folder_id, duration_ms, extension, audio_variant, quality_rank)
VALUES (9001, @path, 'Existing Track.flac', @folderId, 180000, '.flac', 'stereo', 3);
INSERT INTO track_local (track_id, audio_file_id) VALUES (9001, 9001);",
            ("path", existingPath),
            ("folderId", firstFolder.Id));

        var dedupe = new DownloadDedupeService(
            null!,
            _repository,
            NullLogger<DownloadDedupeService>.Instance,
            new PassthroughLocalTrackAmbiguityResolver());
        var stereoDecision = await dedupe.CheckLibraryPresenceAsync(new DownloadDedupeRequest
        {
            TrackTitle = "Existing Track",
            TrackArtist = "Existing Artist",
            DurationMs = 180000,
            DestinationFolderId = secondFolder.Id,
            RequestedAudioVariant = "stereo"
        });
        var atmosDecision = await dedupe.CheckLibraryPresenceAsync(new DownloadDedupeRequest
        {
            TrackTitle = "Existing Track",
            TrackArtist = "Existing Artist",
            DurationMs = 180000,
            DestinationFolderId = secondFolder.Id,
            RequestedAudioVariant = "atmos"
        });

        Assert.False(stereoDecision.Allowed);
        Assert.Equal("library_duplicate", stereoDecision.ReasonCode);
        Assert.Equal(9001, stereoDecision.LocalTrackId);
        Assert.True(atmosDecision.Allowed);
        var stereoIdentity = await _repository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                null,
                "Existing Track",
                "Existing Artist",
                180000,
                AlbumTitle: "Existing Album"),
            audioVariant: "stereo");
        var atmosIdentity = await _repository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                null,
                "Existing Track",
                "Existing Artist",
                180000,
                AlbumTitle: "Existing Album"),
            audioVariant: "atmos");

        Assert.Equal(9001, stereoIdentity.LocalTrackId);
        Assert.Equal(3, await _repository.GetBestLocalQualityRankForTrackAsync(
            stereoIdentity.LocalTrackId!.Value,
            audioVariant: "stereo"));
        Assert.False(atmosIdentity.Exists);
    }

    [Fact]
    public async Task SyncJobs_AreCreatedPerPlaylistTargetAndEnforceLeaseOwnership()
    {
        await AddPlaylistWithTargetsAsync("lease-list", ["plex"]);
        var jobs = await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "lease-list",
            "snapshot-1");

        Assert.Equal(new[] { "plex" }, jobs.Select(static job => job.TargetService).Order(StringComparer.Ordinal).ToArray());
        Assert.All(jobs, static job => Assert.Equal("playlist", job.TrackId));
        var first = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(1), "worker-a"));
        Assert.Equal("worker-a", first.LeaseOwner);
        Assert.Equal("processing", first.Status);
        Assert.False(await _repository.CompleteWatchlistSyncJobAsync(first.Id, "worker-b"));
        Assert.False(await _repository.RetryWatchlistSyncJobAsync(first.Id, "worker-b", 1, DateTimeOffset.UtcNow, "wrong owner"));
        Assert.False(await _repository.RenewWatchlistSyncJobLeaseAsync(first.Id, "worker-b", TimeSpan.FromMinutes(1)));
        Assert.True(await _repository.RenewWatchlistSyncJobLeaseAsync(first.Id, "worker-a", TimeSpan.FromMinutes(1)));
        Assert.True(await _repository.CompleteWatchlistSyncJobAsync(first.Id, "worker-a"));

        _ = await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "lease-list", "snapshot-2");
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

        await AddPlaylistWithTargetsAsync("lease-list", ["plex"]);
        var resumed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(1), "worker-d"));
        Assert.Equal(reclaimed.Id, resumed.Id);
        Assert.Equal(0, resumed.AttemptCount);
        Assert.Null(resumed.LastError);
        Assert.True(await _repository.CompleteWatchlistSyncJobAsync(resumed.Id, "worker-d"));
    }

    [Fact]
    public async Task SyncJobs_ExposeTheNextRetryDueTimeToTheCoordinator()
    {
        await AddPlaylistWithTargetsAsync("scheduled-retry-list", ["plex"]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "scheduled-retry-list",
            "snapshot-1");
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "retry-scheduler"));
        var expectedDue = DateTimeOffset.UtcNow.AddMinutes(3);
        Assert.True(await _repository.RetryWatchlistSyncJobAsync(
            claimed.Id,
            "retry-scheduler",
            1,
            expectedDue,
            "transient target failure"));

        var actualDue = await _repository.GetNextWatchlistSyncJobDueUtcAsync();

        Assert.NotNull(actualDue);
        Assert.InRange(actualDue.Value, expectedDue.AddSeconds(-1), expectedDue.AddSeconds(1));
    }

    [Fact]
    public async Task TrackVerification_CompletedRequiresAnIndexedLocalTrack()
    {
        await AddPlaylistWithTargetsAsync("identity-state-list", ["plex"]);
        await _repository.AddPlaylistWatchTracksAsync(
            "spotify",
            "identity-state-list",
            [new PlaylistWatchTrackInsert("track-1", "TESTISRC0001")]);
        await _repository.UpdatePlaylistWatchTrackStatusAsync(
            "spotify",
            "identity-state-list",
            "track-1",
            "completed");

        await _repository.UpdatePlaylistWatchTrackVerificationAsync(
            "spotify",
            "identity-state-list",
            new PlaylistWatchTrackVerification("track-1", null, "missing", "Not indexed."));
        var missing = Assert.Single(await _repository.GetPlaylistWatchTrackStatusesAsync(
            "spotify",
            "identity-state-list"));
        Assert.Equal("missing", missing.Status);
        Assert.Null(missing.LocalTrackId);

        await _repository.UpdatePlaylistWatchTrackVerificationAsync(
            "spotify",
            "identity-state-list",
            new PlaylistWatchTrackVerification("track-1", 9001, "identity_verified", "Indexed."));
        var available = Assert.Single(await _repository.GetPlaylistWatchTrackStatusesAsync(
            "spotify",
            "identity-state-list"));
        Assert.Equal("completed", available.Status);
        Assert.Equal(9001, available.LocalTrackId);
    }

    [Fact]
    public async Task ArtworkSyncJobs_AreRevisionedPerTargetAndDoNotRequeueAppliedTargets()
    {
        await AddPlaylistWithTargetsAsync("artwork-list", ["plex", "jellyfin", "navidrome"]);
        await _repository.UpsertPlaylistWatchArtworkStateAsync(new PlaylistWatchArtworkStateDto(
            "spotify",
            "artwork-list",
            "https://images.example/cover-a.jpg",
            "still-a",
            "/cache/cover-a.jpg",
            null,
            null,
            "available",
            null,
            DateTimeOffset.UtcNow,
            "revision-a"));

        var firstRevision = new[]
        {
            await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync("spotify", "artwork-list", "plex", "revision-a"),
            await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync("spotify", "artwork-list", "jellyfin", "revision-a"),
            await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync("spotify", "artwork-list", "navidrome", "revision-a")
        }.OfType<WatchlistSyncJobDto>().ToList();

        Assert.Equal(
            new[] { "jellyfin", "navidrome", "plex" },
            firstRevision.Select(static job => job.TargetService).Order(StringComparer.Ordinal).ToArray());
        Assert.All(firstRevision, static job => Assert.Equal("artwork:revision-a", job.TrackId));

        await _repository.SetPlaylistWatchArtworkTargetStateAsync(
            "spotify",
            "artwork-list",
            "plex",
            "revision-a",
            success: true,
            error: null);
        Assert.True(await _repository.IsPlaylistWatchArtworkRevisionAppliedAsync(
            "spotify",
            "artwork-list",
            "plex",
            "revision-a"));
        Assert.False(await _repository.IsPlaylistWatchArtworkRevisionAppliedAsync(
            "spotify",
            "artwork-list",
            "jellyfin",
            "revision-a"));
        await ExecuteSqlAsync(@"
UPDATE watchlist_sync_job
SET status='completed'
WHERE source='spotify' AND playlist_id='artwork-list' AND target_service='plex';");

        var sameRevision = await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync(
            "spotify",
            "artwork-list",
            "plex",
            "revision-a");
        Assert.Null(sameRevision);

        var nextRevision = new[]
        {
            await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync("spotify", "artwork-list", "plex", "revision-b"),
            await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync("spotify", "artwork-list", "jellyfin", "revision-b"),
            await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync("spotify", "artwork-list", "navidrome", "revision-b")
        }.OfType<WatchlistSyncJobDto>().ToList();
        Assert.Equal(3, nextRevision.Count);
        Assert.All(nextRevision, static job => Assert.Equal("artwork:revision-b", job.TrackId));
        Assert.Equal(0, await CountArtworkJobsAsync("artwork-list", "revision-a"));
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
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "prune-list",
            "snapshot-1");

        Assert.Equal(1, await _repository.RemovePlaylistWatchTracksNotInAsync("spotify", "prune-list", ["keep"]));
        Assert.Empty(await _repository.GetPlaylistWatchDownloadClaimsAsync("queue-remove"));
        Assert.False(await _repository.IsPlaylistWatchTrackSyncedToTargetAsync("spotify", "prune-list", "remove", "jellyfin"));
        Assert.Equal(
            new[] { "jellyfin", "plex" },
            (await _repository.ClaimDueWatchlistSyncJobsAsync(100, TimeSpan.FromMinutes(1), "prune-worker"))
                .Where(static job => job.TrackId == "playlist")
                .Select(static job => job.TargetService)
                .Order(StringComparer.Ordinal)
                .ToArray());

        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "prune-list",
            "jellyfin",
            "jellyfin-list",
            [new PlaylistWatchTargetMembership("keep", 100, "remote-keep")]);
        await AddPlaylistWithTargetsAsync("prune-list", ["plex"]);
        Assert.False(await _repository.IsPlaylistWatchTrackSyncedToTargetAsync("spotify", "prune-list", "keep", "jellyfin"));
        var recreated = await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "prune-list",
            "snapshot-2");
        var job = Assert.Single(recreated);
        Assert.Equal("plex", job.TargetService);
        Assert.Equal("playlist", job.TrackId);
    }

    [Fact]
    public void CandidateContract_RejectsLegacyAndOpaqueBoomplayCandidates()
    {
        var opaque = Candidate("boomplay-id", title: string.Empty, artist: string.Empty);
        var hydrated = opaque with
        {
            DeezerId = "12345",
            MappingStatus = BoomplayWatchlistMappingService.MatchedStatus,
            Title = "Hydrated title",
            Artist = "Hydrated artist"
        };

        Assert.False(PlaylistCandidateContract.IsResolvable("boomplay", opaque));
        Assert.True(PlaylistCandidateContract.IsResolvable("boomplay", hydrated));
        Assert.False(PlaylistCandidateContract.IsReusableCache(
            "boomplay", 0, [hydrated], 1, isComplete: true));
        Assert.True(PlaylistCandidateContract.IsReusableCache(
            "boomplay", PlaylistCandidateContract.CurrentCacheSchemaVersion, [opaque], 1, isComplete: true));
        Assert.Empty(PlaylistCandidateContract.ResolvableCandidates("boomplay", [opaque]));
        Assert.True(PlaylistCandidateContract.IsReusableCache(
            "boomplay", PlaylistCandidateContract.CurrentCacheSchemaVersion, [hydrated], 1, isComplete: true));
        Assert.True(PlaylistCandidateContract.IsReusableCache(
            "boomplay", PlaylistCandidateContract.CurrentCacheSchemaVersion, [hydrated], 2, isComplete: true));
    }

    [Fact]
    public void TypedHistoryStatuses_PersistEverySelectionOutcome()
    {
        var statuses = new Dictionary<WatchlistHistoryStatus, string>
        {
            [WatchlistHistoryStatus.SkippedAlreadyAvailable] = "skipped_already_available",
            [WatchlistHistoryStatus.SkippedAlreadyQueued] = "skipped_already_queued",
            [WatchlistHistoryStatus.StaleClaimRecovered] = "stale_claim_recovered",
            [WatchlistHistoryStatus.SkippedBlocked] = "skipped_blocked",
            [WatchlistHistoryStatus.SkippedUnavailableRecheckWindow] = "skipped_unavailable_recheck_window"
        };

        Assert.All(statuses, pair => Assert.Equal(
            pair.Value,
            WatchlistHistoryService.ToPersistedStatus(pair.Key)));
    }

    [Fact]
    public void SpotifyEmptyPage_IsAcceptedOnlyWithExplicitAuthoritativeZeroCount()
    {
        Assert.True(WatchlistEngine.IsAuthoritativeEmptySpotifyPage(0, 0, 0, 0));
        Assert.False(WatchlistEngine.IsAuthoritativeEmptySpotifyPage(0, 0, null, null));
        Assert.False(WatchlistEngine.IsAuthoritativeEmptySpotifyPage(50, 0, 0, 0));
        Assert.False(WatchlistEngine.IsAuthoritativeEmptySpotifyPage(0, 1, 1, 1));
    }

    [Fact]
    public void ArtistWatchOutcome_SettlesMixedHandledTracksWithoutRequiringEveryTrackToBeNewlyQueued()
    {
        var settled = new ArtistWatchQueueOutcome(
            Requested: 5,
            Queued: 1,
            AlreadyHandled: 3,
            Unavailable: 1,
            Deferred: 0,
            Failed: 0);
        var deferred = settled with { Deferred = 1, Unavailable = 0 };

        Assert.True(settled.IsSettled);
        Assert.False(deferred.IsSettled);
    }

    [Fact]
    public async Task HistoryPersistenceFailure_DoesNotEscapeIntoQueuePlanning()
    {
        var unavailableConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=/proc/deezspotag-unwritable/library.db"
            })
            .Build();
        var unavailableRepository = new LibraryRepository(
            unavailableConfiguration,
            NullLogger<LibraryRepository>.Instance);
        var history = new WatchlistHistoryService(unavailableRepository, activitiesRealtime: null);

        var result = await history.RecordAsync(
            new WatchlistHistoryWrite(
                "spotify", "playlist", "list", "playlist:spotify:list", "List", "playlist", 1,
                WatchlistHistoryStatus.MissingTracksQueued, null),
            default);

        Assert.Null(result);
    }

    [Fact]
    public async Task CandidateAndProviderRevisionChanges_ClearOnlyStaleUnavailableDecisions()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "spotify", "revision-list", new PlaylistWatchlistMetadataInput("Revision", null, null, 2));
        await _repository.AddPlaylistWatchTracksAsync(
            "spotify", "revision-list", [new PlaylistWatchTrackInsert("track-a", null), new PlaylistWatchTrackInsert("track-b", null)]);
        await _repository.MarkPlaylistWatchTrackUnavailableAsync(
            "spotify", "revision-list", "track-a", null, "not found", "old-revision", DateTimeOffset.UtcNow.AddDays(7));
        await _repository.MarkPlaylistWatchTrackUnavailableAsync(
            "spotify", "revision-list", "track-b", null, "not found", "current-revision", DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal(1, await _repository.ClearPlaylistWatchUnavailableStatusesWithDifferentFingerprintAsync(
            "spotify", "revision-list", "current-revision"));
        var tracks = await _repository.GetPlaylistWatchTrackStatusesAsync("spotify", "revision-list");
        Assert.Equal("pending", Assert.Single(tracks, track => track.TrackSourceId == "track-a").Status);
        Assert.Equal("unavailable", Assert.Single(tracks, track => track.TrackSourceId == "track-b").Status);
    }

    [Fact]
    public async Task FailedAndRefreshedReconciliationClaims_RemainDurableAcrossWorkers()
    {
        Assert.True(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "spotify", "leased-list"));
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistReconciliationRequestsAsync(
            1, TimeSpan.FromMinutes(1), "worker-a"));
        Assert.Equal(0, await _repository.RenewClaimedWatchlistReconciliationRequestsAsync("worker-b", TimeSpan.FromMinutes(1)));
        Assert.Equal(1, await _repository.RenewClaimedWatchlistReconciliationRequestsAsync("worker-a", TimeSpan.FromMinutes(1)));
        Assert.Equal(0, await _repository.CompleteClaimedWatchlistReconciliationRequestsAsync([claimed], "worker-b"));
        Assert.Equal(1, await _repository.RetryClaimedWatchlistReconciliationRequestsAsync(
            [claimed], "worker-a", "injected failure"));

        var retry = Assert.Single(await NewRepository().GetWatchlistReconciliationRequestsAsync());
        Assert.Equal("retry", retry.Status);
        Assert.Equal(1, retry.AttemptCount);
        Assert.Equal("injected failure", retry.LastError);

        await SetReconciliationDueAsync();
        var refreshedClaim = Assert.Single(await NewRepository().ClaimDueWatchlistReconciliationRequestsAsync(
            1, TimeSpan.FromMinutes(1), "worker-b"));
        Assert.False(await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "spotify", "leased-list"));
        Assert.Equal(0, await _repository.CompleteClaimedWatchlistReconciliationRequestsAsync([refreshedClaim], "worker-b"));
        Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync());
    }

    [Fact]
    public async Task FinalizationOutbox_IsReclaimedAfterAnInterruptedWorkerLease()
    {
        await _repository.UpsertWatchlistFinalizationOutboxAsync(
            "queue-finalize", "{\"WatchlistOrigin\":\"playlist\"}", ["/music/final.flac"]);
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistFinalizationOutboxAsync(
            1, TimeSpan.FromMinutes(1), "worker-a"));
        Assert.Equal("processing", claimed.Status);
        await ExecuteSqlAsync(@"
UPDATE watchlist_finalization_outbox
SET lease_until_utc=@expired
WHERE id=@id;",
            ("expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")),
            ("id", claimed.Id));

        var reclaimed = Assert.Single(await NewRepository().ClaimDueWatchlistFinalizationOutboxAsync(
            1, TimeSpan.FromMinutes(1), "worker-b"));
        Assert.Equal(claimed.Id, reclaimed.Id);
        Assert.Equal("worker-b", reclaimed.LeaseOwner);
        Assert.Equal("/music/final.flac", Assert.Single(reclaimed.FinalFilePaths));
        Assert.True(await _repository.CompleteWatchlistFinalizationOutboxAsync(reclaimed.Id, "worker-b"));
        Assert.Empty(await _repository.ClaimDueWatchlistFinalizationOutboxAsync(
            1, TimeSpan.FromMinutes(1), "worker-c"));
    }

    [Fact]
    public async Task StalePersistedPlaylistAndArtistWork_IsRecoveredForImmediateRetry()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "spotify", "stale-list", new PlaylistWatchlistMetadataInput("Stale", null, null, 1));
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "spotify", "stale-list", null, 1, null, null, null,
            "processing", null, null, 5, "selecting_tracks", 1, 1,
            DateTimeOffset.UtcNow.AddMinutes(-21), DateTimeOffset.UtcNow.AddMinutes(-5)));
        var staleHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-21).ToString("O");
        await ExecuteSqlAsync(
            "INSERT INTO artist_watch_state(artist_id,current_phase,deadline_utc,heartbeat_utc,updated_at,consecutive_failures) VALUES(99,'reconciling',@expired,@stale,@stale,5);",
            ("expired", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")),
            ("stale", staleHeartbeat));

        Assert.Equal(2, await _repository.RecoverStaleWatchlistWorkAsync());
        var playlist = await _repository.GetPlaylistWatchStateAsync("spotify", "stale-list");
        var artist = await _repository.GetArtistWatchStateAsync(99);
        Assert.NotNull(playlist);
        Assert.NotNull(artist);
        Assert.Equal("stale_recovered", playlist!.CurrentPhase);
        Assert.Equal("stale_recovered", artist!.CurrentPhase);
        Assert.Equal(1, playlist.ConsecutiveFailures);
        Assert.Equal(1, artist.ConsecutiveFailures);
        Assert.Null(playlist.DeadlineUtc);
        Assert.Null(artist.DeadlineUtc);
        Assert.NotNull(playlist.NextAttemptUtc);
        Assert.NotNull(artist.NextAttemptUtc);
    }

    [Fact]
    public async Task RecoverStaleWatchlistWork_RecoversWaitingPhaseWhenHeartbeatAndLeaseHaveFailed()
    {
        await AddPlaylistWithTargetsAsync("waiting-stale-list", ["plex"]);
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "spotify", "waiting-stale-list", null, 1, null, null, null,
            "waiting_for_target_sync", "mirroring available tracks", null, 0,
            "waiting_for_target_sync", 1, 1,
            DateTimeOffset.UtcNow.AddMinutes(-21), DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.Equal(1, await _repository.RecoverStaleWatchlistWorkAsync());
        var playlist = await _repository.GetPlaylistWatchStateAsync("spotify", "waiting-stale-list");
        Assert.Equal("stale_recovered", playlist!.CurrentPhase);
        Assert.Equal(1, playlist.ConsecutiveFailures);
    }

    [Fact]
    public async Task RecoverStaleWatchlistWork_DoesNotRecoverHealthyHeartbeatOrLiveLease()
    {
        await AddPlaylistWithTargetsAsync("healthy-heartbeat-list", ["plex"]);
        await AddPlaylistWithTargetsAsync("live-lease-list", ["plex"]);
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "spotify", "healthy-heartbeat-list", null, 1, null, null, null,
            "waiting_for_target_sync", null, null, 0, "waiting_for_target_sync", 1, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-5)));
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "spotify", "live-lease-list", null, 1, null, null, null,
            "waiting_for_target_sync", null, null, 0, "waiting_for_target_sync", 1, 1,
            DateTimeOffset.UtcNow.AddMinutes(-21), DateTimeOffset.UtcNow.AddMinutes(-5)));
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "live-lease-list", "snapshot-1");
        Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(15), "live-worker"));

        Assert.Equal(0, await _repository.RecoverStaleWatchlistWorkAsync());
        Assert.Equal("waiting_for_target_sync", (await _repository.GetPlaylistWatchStateAsync("spotify", "healthy-heartbeat-list"))!.CurrentPhase);
        Assert.Equal("waiting_for_target_sync", (await _repository.GetPlaylistWatchStateAsync("spotify", "live-lease-list"))!.CurrentPhase);
    }

    [Fact]
    public async Task ApplyWatchlistSmoothSyncRecovery_ClearsBackoffAndIdentityBacklogWithoutWipingBindings()
    {
        await AddPlaylistWithTargetsAsync("nas-backoff-list", ["plex", "jellyfin"]);
        await _repository.UpdatePlaylistWatchTargetPlaylistIdAsync("spotify", "nas-backoff-list", "plex", "plex-keep-me");
        await _repository.UpdatePlaylistWatchTargetPlaylistIdAsync("spotify", "nas-backoff-list", "jellyfin", "jellyfin-keep-me");
        await _repository.AddPlaylistWatchTracksAsync(
            "spotify",
            "nas-backoff-list",
            [new PlaylistWatchTrackInsert("track-1", "ISRC00000001")]);
        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "nas-backoff-list",
            "plex",
            "plex-keep-me",
            [new PlaylistWatchTargetMembership("track-1", 101, "plex-track-1")]);
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "spotify", "nas-backoff-list", "snap", 1, null, null, DateTimeOffset.UtcNow.AddHours(-1),
            "backoff", "Recovered stale Watchlist work after its persisted deadline expired.",
            DateTimeOffset.UtcNow.AddHours(-3), 4, "stale_recovered", 1, 1,
            DateTimeOffset.UtcNow.AddHours(-1), null));

        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "nas-backoff-list", "snap");
        var blocked = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(1), "recovery-worker"));
        Assert.True(await _repository.BlockWatchlistSyncJobAsync(
            blocked.Id,
            "recovery-worker",
            "Jellyfin verification is incomplete. Source tracks: 267"));
        await ExecuteSqlAsync("UPDATE watchlist_sync_job SET attempt_count=10 WHERE id=@id;", ("id", blocked.Id));

        var retry = Assert.Single((await _repository.GetWatchlistSyncJobsAsync("spotify", "nas-backoff-list"))
            .Where(job => job.Id != blocked.Id));
        await ExecuteSqlAsync(
            "UPDATE watchlist_sync_job SET status='retry', last_error=@error, next_attempt_utc=@future WHERE id=@id;",
            ("error", "Waiting for the durable playlist reconciliation request to complete."),
            ("future", DateTimeOffset.UtcNow.AddHours(2).ToString("O")),
            ("id", retry.Id));

        await _repository.UpsertWatchlistTargetCircuitStateAsync(
            new LibraryRepository.WatchlistTargetCircuitStateUpsertInput(
                "jellyfin",
                IsOpen: true,
                OpenUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                Reason: "Source tracks: 267 Target matches: 50 verification is incomplete",
                FailureCount: 4));

        Assert.True(await _repository.ApplyWatchlistSmoothSyncRecoveryAsync());
        Assert.False(await _repository.ApplyWatchlistSmoothSyncRecoveryAsync());

        var state = await _repository.GetPlaylistWatchStateAsync("spotify", "nas-backoff-list");
        Assert.NotNull(state);
        Assert.Equal("pending", state!.LastRunStatus);
        Assert.Equal("pending", state.CurrentPhase);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Null(state.DeadlineUtc);
        Assert.NotNull(state.NextAttemptUtc);
        Assert.Equal(1L, await QueryScalarAsync("SELECT recovery_generation FROM playlist_watch_state WHERE source_id='nas-backoff-list';"));

        var preference = await _repository.GetPlaylistWatchPreferenceAsync("spotify", "nas-backoff-list");
        Assert.Equal("plex-keep-me", preference!.PlexPlaylistId);
        Assert.Equal("jellyfin-keep-me", preference.JellyfinPlaylistId);
        Assert.True(await _repository.IsPlaylistWatchTrackSyncedToTargetAsync("spotify", "nas-backoff-list", "track-1", "plex"));

        var jobs = await _repository.GetWatchlistSyncJobsAsync("spotify", "nas-backoff-list");
        var resetBlocked = Assert.Single(jobs, job => job.Id == blocked.Id);
        Assert.Equal("pending", resetBlocked.Status);
        Assert.Equal(0, resetBlocked.AttemptCount);
        Assert.True(resetBlocked.NextAttemptUtc <= DateTimeOffset.UtcNow.AddSeconds(2));
        var resetRetry = Assert.Single(jobs, job => job.Id == retry.Id);
        Assert.Equal("retry", resetRetry.Status);
        Assert.True(resetRetry.NextAttemptUtc <= DateTimeOffset.UtcNow.AddSeconds(2));

        var circuit = await _repository.GetWatchlistTargetCircuitStateAsync("jellyfin");
        Assert.NotNull(circuit);
        Assert.False(circuit!.IsOpen);
        Assert.Equal(0, circuit.FailureCount);
        Assert.Null(circuit.Reason);
    }

    [Fact]
    public async Task TouchPlaylistWatchHeartbeat_ExtendsDeadlineWithoutChangingPhase()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "spotify", "heartbeat-list", new PlaylistWatchlistMetadataInput("Heartbeat", null, null, 1));
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "spotify", "heartbeat-list", null, 1, null, null, null,
            "waiting_for_target_sync", null, null, 0, "waiting_for_target_sync", 3, 10,
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(5)));

        await _repository.TouchPlaylistWatchHeartbeatAsync(
            "spotify", "heartbeat-list", TimeSpan.FromMinutes(45));
        var afterHeartbeat = await _repository.GetPlaylistWatchStateAsync("spotify", "heartbeat-list");
        Assert.Equal("waiting_for_target_sync", afterHeartbeat!.CurrentPhase);
        Assert.Equal(3, afterHeartbeat.CurrentTrackIndex);
        Assert.True(afterHeartbeat.DeadlineUtc > DateTimeOffset.UtcNow.AddMinutes(40));

        await _repository.UpdatePlaylistWatchProgressAsync(
            "spotify", "heartbeat-list", "selecting_tracks", 7, 10);
        var afterProgress = await _repository.GetPlaylistWatchStateAsync("spotify", "heartbeat-list");
        Assert.Equal("waiting_for_target_sync", afterProgress!.CurrentPhase);
        Assert.Equal(7, afterProgress.CurrentTrackIndex);
        Assert.Equal(10, afterProgress.CurrentTrackTotal);
    }

    [Fact]
    public async Task PlaylistSyncWork_CoalescesToOneJobPerPlaylistTarget()
    {
        await AddPlaylistWithTargetsAsync("refresh-list", ["plex", "jellyfin", "navidrome"]);
        var first = await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "refresh-list", "snapshot-1");
        var second = await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "refresh-list", "snapshot-1");

        Assert.Equal(new[] { "jellyfin", "navidrome", "plex" }, first.Select(static job => job.TargetService).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "jellyfin", "navidrome", "plex" }, second.Select(static job => job.TargetService).Order(StringComparer.Ordinal).ToArray());
        Assert.All(first, static job => Assert.Equal("playlist", job.TrackId));
        Assert.Equal("snapshot-1:plex-membership-v2", first.Single(static job => job.TargetService == "plex").SnapshotId);
        Assert.All(first.Where(static job => job.TargetService != "plex"), static job => Assert.Equal("snapshot-1", job.SnapshotId));
        var claimed = await _repository.ClaimDueWatchlistSyncJobsAsync(100, TimeSpan.FromMinutes(1), "target-worker");
        Assert.Equal(3, claimed.Count);
    }

    [Fact]
    public async Task CompletedPlaylistSnapshot_IsNotScheduledAgainUntilSnapshotChanges()
    {
        await AddPlaylistWithTargetsAsync("revision-list", ["plex"]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify", "revision-list", "snapshot-1");
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1, TimeSpan.FromMinutes(1), "revision-worker"));

        Assert.Equal("snapshot-1:plex-membership-v2", claimed.SnapshotId);
        Assert.True(await _repository.CompleteWatchlistPlaylistSyncJobAsync(
            claimed, "revision-worker", WatchlistAppliedKind.Full, null, null));
        Assert.Empty(await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify", "revision-list", "snapshot-1"));

        var changed = Assert.Single(await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify", "revision-list", "snapshot-2"));
        Assert.Equal("snapshot-2:plex-membership-v2", changed.SnapshotId);
    }

    [Fact]
    public async Task PartialAppliedSnapshot_IsNotScheduledAgainUntilSnapshotChanges()
    {
        await AddPlaylistWithTargetsAsync("partial-revision-list", ["jellyfin"]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify", "partial-revision-list", "snapshot-s");
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1, TimeSpan.FromMinutes(1), "partial-worker"));

        Assert.True(await _repository.CompleteWatchlistPlaylistSyncJobAsync(
            claimed, "partial-worker", WatchlistAppliedKind.Partial, "hash-s", "source-s"));
        Assert.Empty(await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify", "partial-revision-list", "snapshot-s"));
        var changed = Assert.Single(await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify", "partial-revision-list", "snapshot-s2"));
        Assert.Equal("snapshot-s2", changed.SnapshotId);
        Assert.False(await _repository.CompleteWatchlistPlaylistSyncJobAsync(
            claimed with { SnapshotId = "   " },
            "partial-worker",
            WatchlistAppliedKind.Partial,
            null,
            null));
    }

    [Fact]
    public async Task RepairWatchlistSyncBacklog_DoesNotReopenIdentityGapAppliedState()
    {
        await AddPlaylistWithTargetsAsync("partial-target-list", ["plex"]);
        await _repository.AddPlaylistWatchTracksAsync(
            "spotify",
            "partial-target-list",
            [
                new PlaylistWatchTrackInsert("track-1", "ISRC00000001"),
                new PlaylistWatchTrackInsert("track-2", "ISRC00000002")
            ]);
        await _repository.UpdatePlaylistWatchTrackVerificationAsync(
            "spotify",
            "partial-target-list",
            new PlaylistWatchTrackVerification("track-1", 101, "identity_verified"));
        await _repository.UpdatePlaylistWatchTrackVerificationAsync(
            "spotify",
            "partial-target-list",
            new PlaylistWatchTrackVerification("track-2", 102, "identity_verified"));
        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "partial-target-list",
            "plex",
            "plex-playlist",
            [
                new PlaylistWatchTargetMembershipWrite("track-1", 101, "plex-track-1", "playlist_synced"),
                new PlaylistWatchTargetMembershipWrite("track-2", 102, null, "waiting_for_identity")
            ]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "partial-target-list",
            "snapshot-1");
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "repair-worker"));
        Assert.True(await _repository.CompleteWatchlistPlaylistSyncJobAsync(
            claimed,
            "repair-worker",
            WatchlistAppliedKind.Partial,
            null,
            null));
        Assert.Empty(await _repository.GetWatchlistSyncJobsAsync("spotify", "partial-target-list"));
        Assert.Empty(await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "partial-target-list",
            "snapshot-1"));

        var repaired = await _repository.RepairWatchlistSyncBacklogAsync(10);

        Assert.Equal(0, repaired);
        Assert.Empty(await _repository.GetWatchlistSyncJobsAsync("spotify", "partial-target-list"));

        var catchUp = Assert.Single(await _repository.EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
            102,
            "plex",
            "snapshot-1"));
        Assert.Equal("snapshot-1:plex-membership-v2", catchUp.SnapshotId);
        var catchUpClaimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "catchup-worker"));
        Assert.True(await _repository.CompleteWatchlistPlaylistSyncJobAsync(
            catchUpClaimed,
            "catchup-worker",
            WatchlistAppliedKind.Partial,
            null,
            null));
        Assert.Empty(await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "partial-target-list",
            "snapshot-1"));
        Assert.False(await _repository.CompleteWatchlistPlaylistSyncJobAsync(
            catchUpClaimed with { SnapshotId = null },
            "catchup-worker",
            WatchlistAppliedKind.Partial,
            null,
            null));
    }

    [Fact]
    public async Task RepairWatchlistSyncBacklog_ReopensBlockedJobsBelowAttemptCap()
    {
        await AddPlaylistWithTargetsAsync("blocked-before-cap-list", ["plex"]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "blocked-before-cap-list",
            "snapshot-1");
        var claimed = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "blocked-worker"));
        Assert.True(await _repository.BlockWatchlistSyncJobAsync(
            claimed.Id,
            "blocked-worker",
            "Temporary Plex failure."));
        await ExecuteSqlAsync(
            "UPDATE watchlist_sync_job SET attempt_count=3 WHERE id=@id;",
            ("id", claimed.Id));

        var repaired = await _repository.RepairWatchlistSyncBacklogAsync(10);

        Assert.True(repaired > 0);
        var reopened = Assert.Single(await _repository.GetWatchlistSyncJobsAsync(
            "spotify",
            "blocked-before-cap-list"));
        Assert.Equal("retry", reopened.Status);
        Assert.Equal(3, reopened.AttemptCount);
        Assert.Null(reopened.LeaseOwner);
    }

    [Fact]
    public async Task CloseExpiredWatchlistTargetCircuits_ClosesExpiredCircuit()
    {
        await _repository.UpsertWatchlistTargetCircuitStateAsync(
            new LibraryRepository.WatchlistTargetCircuitStateUpsertInput(
                "plex",
                IsOpen: true,
                OpenUntilUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
                Reason: "Expired Plex pause.",
                FailureCount: 5));

        var closed = await _repository.CloseExpiredWatchlistTargetCircuitsAsync();

        Assert.Equal(1, closed);
        var circuit = await _repository.GetWatchlistTargetCircuitStateAsync("plex");
        Assert.NotNull(circuit);
        Assert.False(circuit.IsOpen);
        Assert.Null(circuit.OpenUntilUtc);
        Assert.Null(circuit.Reason);
        Assert.Equal(0, circuit.FailureCount);
    }

    [Fact]
    public async Task PlaylistSyncWork_ExcludesAttemptedTargetAndLeavesSiblingTargetsClaimable()
    {
        await AddPlaylistWithTargetsAsync("isolated-list", ["plex", "jellyfin", "navidrome"]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "isolated-list", "snapshot-1");

        var plex = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "worker-a"));
        Assert.Equal("plex", plex.TargetService);
        Assert.True(await _repository.RetryWatchlistSyncJobAsync(
            plex.Id,
            "worker-a",
            1,
            DateTimeOffset.UtcNow,
            "Plex failed"));

        var jellyfin = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "worker-a",
            [plex.Id]));
        Assert.Equal("jellyfin", jellyfin.TargetService);
        Assert.True(await _repository.CompleteWatchlistSyncJobAsync(jellyfin.Id, "worker-a"));

        var navidrome = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            1,
            TimeSpan.FromMinutes(1),
            "worker-a",
            [plex.Id, jellyfin.Id]));
        Assert.Equal("navidrome", navidrome.TargetService);
        Assert.True(await _repository.CompleteWatchlistSyncJobAsync(navidrome.Id, "worker-a"));

        var diagnostics = await _repository.GetWatchlistSyncJobsAsync("spotify", "isolated-list");
        var failedPlex = Assert.Single(diagnostics);
        Assert.Equal("plex", failedPlex.TargetService);
        Assert.Equal("retry", failedPlex.Status);
        Assert.Equal("Plex failed", failedPlex.LastError);
    }

    [Fact]
    public async Task TargetSyncClaiming_IsFairAcrossServersAndArtworkMembershipJobTypes()
    {
        await AddPlaylistWithTargetsAsync("fair-list", ["plex", "jellyfin", "navidrome"]);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "fair-list", "snapshot-1");
        foreach (var target in new[] { "plex", "jellyfin", "navidrome" })
        {
            Assert.NotNull(await _repository.EnqueueWatchlistPlaylistArtworkSyncJobAsync(
                "spotify", "fair-list", target, "artwork-revision-1"));
        }

        var claimed = await _repository.ClaimDueWatchlistSyncJobsAsync(
            6, TimeSpan.FromMinutes(1), "fair-worker");

        Assert.Equal(6, claimed.Count);
        Assert.Equal(3, claimed.Count(static job => job.TrackId == "playlist"));
        Assert.Equal(3, claimed.Count(static job => job.TrackId.StartsWith("artwork:", StringComparison.Ordinal)));
        Assert.Equal(2, claimed.Count(static job => job.TargetService == "plex"));
        Assert.Equal(2, claimed.Count(static job => job.TargetService == "jellyfin"));
        Assert.Equal(2, claimed.Count(static job => job.TargetService == "navidrome"));
    }

    [Fact]
    public async Task RuntimeReset_ClearsEveryRuntimeRowAndPreservesConfigurationAndCandidateCache()
    {
        await AddPlaylistWithTargetsAsync("reset-list", ["plex"]);
        await _repository.UpsertPlaylistTrackCandidateCacheAsync(
            "spotify", "reset-list", "snapshot", JsonSerializer.Serialize(new[] { Candidate("track-1", "Title", "Artist") }),
            4, "identity", "provider", true);
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "spotify", "reset-list", "snapshot", 1, null, null, DateTimeOffset.UtcNow,
            "processing", "active", null, 0, "reconciling", 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)));
        await _repository.EnqueueWatchlistReconciliationRequestAsync("all", null, null);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "reset-list", "snapshot");
        _ = await _repository.ClaimDueWatchlistSyncJobsAsync(1, TimeSpan.FromMinutes(15), "active-worker");
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync("spotify", "reset-list", "track-1", ["queue-1"], 42);
        await _repository.UpsertWatchlistFinalizationOutboxAsync("queue-1", "{}", ["/music/final.flac"]);
        await ExecuteSqlAsync(@"
INSERT INTO watchlist_scheduler_state(watch_type,active_source) VALUES('playlist','spotify');
INSERT INTO watchlist_source_circuit_state(watch_type,source,is_open) VALUES('playlist','spotify',1);
INSERT INTO watchlist_target_circuit_state(target_service,is_open) VALUES('jellyfin',1);
INSERT INTO artist_watchlist(artist_id,spotify_id,artist_name) VALUES(99,'artist-99','Artist 99');
INSERT INTO artist_watch_state(artist_id,current_phase) VALUES(99,'reconciling');");

        var cleanup = await _repository.ClearWatchlistRuntimeAsync();

        Assert.Equal(1, cleanup.ReconciliationRequestsDeleted);
        Assert.Equal(1, cleanup.SyncJobsDeleted);
        Assert.Equal(1, cleanup.FinalizationOutboxDeleted);
        Assert.Equal(1, cleanup.ClaimsDeleted);
        Assert.Equal(1, cleanup.SchedulerRowsDeleted);
        Assert.Equal(1, cleanup.SourceCircuitsDeleted);
        Assert.Equal(1, cleanup.TargetCircuitsDeleted);
        Assert.Equal(1, cleanup.PlaylistStatesDeleted);
        Assert.Equal(1, cleanup.ArtistStatesDeleted);
        Assert.Equal(0, await CountRowsAsync("watchlist_target_circuit_state"));
        Assert.Equal(0, await CountRowsAsync("watchlist_reconciliation_request"));
        Assert.Equal(0, await CountRowsAsync("watchlist_sync_job"));
        Assert.Equal(0, await CountRowsAsync("watchlist_finalization_outbox"));
        Assert.Equal(0, await CountRowsAsync("playlist_watch_download_claim"));
        Assert.Equal(0, await CountRowsAsync("watchlist_scheduler_state"));
        Assert.Equal(0, await CountRowsAsync("watchlist_source_circuit_state"));
        Assert.Equal(0, await CountRowsAsync("playlist_watch_state"));
        Assert.Equal(0, await CountRowsAsync("artist_watch_state"));
        Assert.Equal(1, await CountRowsAsync("artist_watchlist"));
        Assert.Single(await _repository.GetPlaylistWatchlistAsync(), item => item.SourceId == "reset-list");
        Assert.NotNull(await _repository.GetPlaylistWatchPreferenceAsync("spotify", "reset-list"));
        Assert.NotNull(await _repository.GetPlaylistTrackCandidateCacheAsync("spotify", "reset-list"));
    }

    [Fact]
    public async Task TargetCircuitState_RoundTripsAndIsKeyedByTargetServiceOnly()
    {
        Assert.Null(await _repository.GetWatchlistTargetCircuitStateAsync("jellyfin"));

        await _repository.UpsertWatchlistTargetCircuitStateAsync(
            new LibraryRepository.WatchlistTargetCircuitStateUpsertInput(
                "Jellyfin",
                IsOpen: true,
                OpenUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                Reason: "Failed to clear existing Jellyfin playlist items.",
                FailureCount: 5));

        var jellyfinState = await _repository.GetWatchlistTargetCircuitStateAsync("JELLYFIN");
        Assert.NotNull(jellyfinState);
        Assert.Equal("jellyfin", jellyfinState!.TargetService);
        Assert.True(jellyfinState.IsOpen);
        Assert.Equal(5, jellyfinState.FailureCount);
        Assert.Equal("Failed to clear existing Jellyfin playlist items.", jellyfinState.Reason);

        // A different target service is an independent circuit, unaffected by Jellyfin's state --
        // this is the whole point: one down target shouldn't imply anything about the others.
        Assert.Null(await _repository.GetWatchlistTargetCircuitStateAsync("plex"));

        await _repository.UpsertWatchlistTargetCircuitStateAsync(
            new LibraryRepository.WatchlistTargetCircuitStateUpsertInput(
                "jellyfin",
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                FailureCount: 0));

        var resetState = await _repository.GetWatchlistTargetCircuitStateAsync("jellyfin");
        Assert.NotNull(resetState);
        Assert.False(resetState!.IsOpen);
        Assert.Equal(0, resetState.FailureCount);
        Assert.Null(resetState.Reason);
    }

    [Fact]
    public async Task SchemaMigration_ReconcilesLegacySyncJobsWithoutAPlaylistSnapshot()
    {
        await AddPlaylistWithTargetsAsync("legacy-sync-list", ["plex", "jellyfin"]);
        await ExecuteSqlAsync(@"
DELETE FROM app_schema_migration WHERE migration_id='watchlist-per-target-playlist-sync-job-v1';
INSERT INTO watchlist_sync_job (
    source, playlist_id, track_id, target_service, queue_uuid, destination_folder_id,
    final_file_paths_json, attempt_count, status, next_attempt_utc, last_error)
VALUES
    ('spotify', 'legacy-sync-list', 'track-a', 'plex', 'queue-a', 42, json_array('/music/a.flac'), 3, 'retry', @nextAttempt, 'old target failure'),
    ('spotify', 'legacy-sync-list', 'track-a', 'jellyfin', 'queue-a', 42, json_array('/music/a.flac'), 1, 'pending', @nextAttempt, NULL),
    ('spotify', 'legacy-sync-list', 'track-b', 'plex', 'queue-b', 42, json_array('/music/b.flac'), 2, 'processing', @nextAttempt, 'old processing row');",
            ("nextAttempt", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")));

        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

        var jobs = await _repository.ClaimDueWatchlistSyncJobsAsync(100, TimeSpan.FromMinutes(1), "migration-worker");
        Assert.DoesNotContain(jobs, item => item.Source == "spotify" && item.PlaylistId == "legacy-sync-list");
        Assert.Contains(
            await _repository.GetWatchlistReconciliationRequestsAsync(),
            request => request.Kind == "playlist"
                       && request.Source == "spotify"
                       && request.Identifier == "legacy-sync-list");
    }

    [Fact]
    public async Task DeploymentRepair_PreservesLegacyCacheAndResetsFalseUnavailableState()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "boomplay", "legacy-list", new PlaylistWatchlistMetadataInput("Legacy", null, null, 1));
        await _repository.AddPlaylistWatchTracksAsync(
            "boomplay", "legacy-list", [new PlaylistWatchTrackInsert("opaque-id", null)]);
        await _repository.MarkPlaylistWatchTrackUnavailableAsync(
            "boomplay", "legacy-list", "opaque-id", null, "not resolvable", "legacy", DateTimeOffset.UtcNow.AddDays(7));
        await _repository.UpsertPlaylistTrackCandidateCacheAsync(
            "boomplay", "legacy-list", "snapshot", JsonSerializer.Serialize(new[] { Candidate("opaque-id", "", "") }),
            0, null, null, false);
        await _repository.UpsertPlaylistWatchStateAsync(new LibraryRepository.PlaylistWatchStateUpsertInput(
            "boomplay", "legacy-list", "snapshot", 1, null, null, DateTimeOffset.UtcNow,
            "backoff", "Unknown Watchlist history status was rejected.", DateTimeOffset.UtcNow.AddHours(1), 4));
        await ExecuteSqlAsync("DELETE FROM app_schema_migration WHERE migration_id='watchlist-reliability-repair-v1';");

        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

        Assert.NotNull(await _repository.GetPlaylistTrackCandidateCacheAsync("boomplay", "legacy-list"));
        var track = Assert.Single(await _repository.GetPlaylistWatchTrackStatusesAsync("boomplay", "legacy-list"));
        Assert.Equal("pending", track.Status);
        var state = await _repository.GetPlaylistWatchStateAsync("boomplay", "legacy-list");
        Assert.NotNull(state);
        Assert.Equal("pending", state!.LastRunStatus);
        Assert.Null(state.NextAttemptUtc);
        Assert.Single(await _repository.GetPlaylistWatchlistAsync(), item => item.SourceId == "legacy-list");
        Assert.Contains(await _repository.GetWatchlistReconciliationRequestsAsync(), request => request.Kind == "all");
    }

    [Fact]
    public async Task LegacyAppleStorefrontRepair_IsIdempotentAndPreservesExistingStorefronts()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "apple",
            "legacy-apple",
            new PlaylistWatchlistMetadataInput("Legacy Apple", null, null, 192));
        await _repository.AddPlaylistWatchlistAsync(
            "apple",
            "existing-apple",
            new PlaylistWatchlistMetadataInput(
                "Existing Apple",
                null,
                null,
                50,
                SourceStorefront: "gb"));

        var repaired = await _repository.BackfillLegacyApplePlaylistStorefrontAsync(" US ");
        var repeated = await _repository.BackfillLegacyApplePlaylistStorefrontAsync("us");

        Assert.Equal(["legacy-apple"], repaired);
        Assert.Empty(repeated);
        Assert.Equal(
            "us",
            (await _repository.GetPlaylistWatchlistEntryAsync("apple", "legacy-apple"))?.SourceStorefront);
        Assert.Equal(
            "gb",
            (await _repository.GetPlaylistWatchlistEntryAsync("apple", "existing-apple"))?.SourceStorefront);
    }

    [Fact]
    public async Task SchemaRepair_QueuesOutdatedCandidateCacheWithoutDeletingIt()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "apple",
            "cached-apple",
            new PlaylistWatchlistMetadataInput(
                "Cached Apple",
                null,
                null,
                1,
                SourceStorefront: "us"));
        await _repository.UpsertPlaylistTrackCandidateCacheAsync(
            "apple",
            "cached-apple",
            "old-snapshot",
            JsonSerializer.Serialize(new[] { Candidate("track-1", "Track", "Artist") }),
            schemaVersion: 3,
            identityRevision: "old-identity",
            providerReadinessRevision: "old-readiness",
            isComplete: true);

        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

        var preserved = await _repository.GetPlaylistTrackCandidateCacheAsync("apple", "cached-apple");
        Assert.NotNull(preserved);
        Assert.Equal("old-snapshot", preserved!.SnapshotId);
        Assert.Contains(
            await _repository.GetWatchlistReconciliationRequestsAsync(),
            request => request.Kind == "playlist"
                       && request.Source == "apple"
                       && request.Identifier == "cached-apple");
    }

    private static PlaylistTrackCandidate Candidate(string id, string title, string artist)
        => new(id, null, title, artist, string.Empty, null, null, null, []);

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

    private async Task<long> CountArtworkJobsAsync(string sourceId, string revision)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*)
FROM watchlist_sync_job
WHERE source='spotify' AND playlist_id=@sourceId AND track_id='artwork:' || @revision;";
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("revision", revision);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private Task SetReconciliationDueAsync()
        => ExecuteSqlAsync(@"
UPDATE watchlist_reconciliation_request
SET next_attempt_utc=@due
WHERE identifier='leased-list';", ("due", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> QueryScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task<long> CountRowsAsync(string table)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "watchlist_reconciliation_request", "watchlist_sync_job", "watchlist_finalization_outbox",
            "playlist_watch_download_claim", "watchlist_scheduler_state", "watchlist_source_circuit_state",
            "watchlist_target_circuit_state", "playlist_watch_state", "artist_watch_state", "artist_watchlist"
        };
        Assert.Contains(table, allowed);
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
