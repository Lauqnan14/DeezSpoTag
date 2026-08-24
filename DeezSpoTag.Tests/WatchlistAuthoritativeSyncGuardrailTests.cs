using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistAuthoritativeSyncGuardrailTests
{
    private static string Root => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void TargetBindings_AreValidatedAndMissingTargetsUseTheExistingFullSyncPath()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "PlaylistSyncService.cs"));

        Assert.Contains("ResolveAuthoritativePlexPlaylistIdAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAuthoritativeJellyfinPlaylistIdAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAuthoritativeNavidromePlaylistIdAsync", source, StringComparison.Ordinal);
        Assert.Contains("PersistTargetPlaylistBindingAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistResolvedTargetBindingAsync", source, StringComparison.Ordinal);
        Assert.Contains("RecreateMissingTargetPlaylistAsync", source, StringComparison.Ordinal);
        Assert.Contains("TargetLookupStatus.NotFound", source, StringComparison.Ordinal);
        Assert.Contains("TargetLookupStatus.Transient", source, StringComparison.Ordinal);
        Assert.Contains("return await SyncAvailablePlaylistTracksAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Target Plex playlist was not found.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Target Jellyfin playlist was not found.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Target Navidrome playlist was not found.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecreateMissingTargetPlaylistAsync_IsCalledOnlyOnNotFound()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "PlaylistSyncService.cs"));

        Assert.Equal(3, CountOccurrences(source, "return await RecreateMissingTargetPlaylistAsync("));
        foreach (var methodName in new[]
                 {
                     "private async Task<PlaylistSyncResult> SyncPlexPlaylistArtworkOnlyAsync(",
                     "private async Task<PlaylistSyncResult> SyncJellyfinPlaylistArtworkOnlyAsync(",
                     "private async Task<PlaylistSyncResult> SyncNavidromePlaylistArtworkOnlyAsync("
                 })
        {
            var start = source.IndexOf(methodName, StringComparison.Ordinal);
            Assert.True(start >= 0, methodName);
            var body = source[start..(start + 1800)];
            var transient = body.IndexOf("TargetLookupStatus.Transient", StringComparison.Ordinal);
            var notFound = body.IndexOf("TargetLookupStatus.NotFound", StringComparison.Ordinal);
            var recreate = body.IndexOf("RecreateMissingTargetPlaylistAsync", StringComparison.Ordinal);
            Assert.True(transient >= 0 && notFound >= 0 && recreate > notFound);
            Assert.True(transient < recreate);
            Assert.Contains("PlaylistSyncResultKind.Retry", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TargetJobs_AreFairIndependentBoundedAndRepairable()
    {
        var worker = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistPostDownloadSyncService.cs"));
        var repository = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Services",
            "Library",
            "LibraryRepository.cs"));
        var coordinator = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistRunCoordinator.cs"));

        Assert.DoesNotContain("Task.WhenAll(jobs", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetOperationTimeout", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetSyncJobTimeout", worker, StringComparison.Ordinal);
        Assert.Contains("TargetSyncBudget", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeBudget", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("while (processed < maxJobs", worker, StringComparison.Ordinal);
        Assert.Contains("while (true)", ExtractMethodBody(worker, "public async Task<int> ProcessTargetSyncWorkAsync("), StringComparison.Ordinal);
        Assert.Contains("RenewWatchlistSyncJobLeaseAsync", worker, StringComparison.Ordinal);
        Assert.Contains("GetNextWatchlistSyncJobDueUtcAsync", repository, StringComparison.Ordinal);
        Assert.Contains("RepairWatchlistSyncBacklogAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("ranked.playlist_priority ASC", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("PARTITION BY lower(job.target_service),", repository, StringComparison.Ordinal);
        Assert.Contains("Recovered expired target synchronization lease.", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkSync_UsesUploadResultAndAppliesCachedArtworkToNewTargets()
    {
        var service = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "PlaylistSyncService.cs"));
        var client = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Integrations",
            "Navidrome",
            "NavidromeApiClient.cs"));

        Assert.DoesNotContain("HasPlaylistImageAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("HasPlaylistImageAsync", client, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyArtworkToNewTargetAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyOrScheduleMembershipArtworkAsync", service, StringComparison.Ordinal);
        Assert.Contains("ScheduleArtworkForActiveRevisionAsync", service, StringComparison.Ordinal);
        // Membership path binds target ids immediately (Plex/JF/Navidrome) plus art-only paths.
        Assert.True(CountOccurrences(service, "await PersistTargetPlaylistBindingAsync(") >= 6);
        Assert.Contains("RecreateMissingTargetPlaylistAsync", service, StringComparison.Ordinal);
        Assert.Contains("IsResolvedMembershipVerified", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IsIntendedMembershipVerified", service, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string ExtractMethodBody(string source, string methodMarker)
    {
        var methodIndex = source.IndexOf(methodMarker, StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, $"Missing method marker: {methodMarker}");
        var bodyStart = source.IndexOf('{', methodIndex);
        Assert.True(bodyStart >= 0, $"Missing method body start for: {methodMarker}");
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(bodyStart, i - bodyStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Missing method body end for: {methodMarker}");
    }

    [Fact]
    public void AdmissionBlock_DoesNotStopSnapshotOrTargetSynchronization()
    {
        var coordinator = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistRunCoordinator.cs"));
        var engine = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));

        Assert.Contains("ProcessPlaylistQueueAdmissionsAsync", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistReconciliationMode", coordinator, StringComparison.Ordinal);
        Assert.Contains("AdmitCachedMissingTracksAsync", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildSystemicFingerprint", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("new WatchFailureClassification(true", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedClaimsWithoutLocalIdentity_DoNotRemainPending()
    {
        var engine = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));
        var localIdBranch = engine.IndexOf(
            "if (!libraryDecision.LocalTrackId.HasValue)",
            StringComparison.Ordinal);
        var nextBranch = engine.IndexOf(
            "await _libraryRepository.UpdatePlaylistWatchDownloadClaimStatusAsync(",
            localIdBranch,
            StringComparison.Ordinal);

        Assert.True(localIdBranch >= 0);
        Assert.True(nextBranch > localIdBranch);
    }

    [Fact]
    public void CompleteSnapshots_CanRetainUnresolvedSourceTracks()
    {
        var contract = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "PlaylistCandidateContract.cs"));
        var engine = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));

        Assert.DoesNotContain(
            "&& candidates.All(candidate => IsResolvable(source, candidate))",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "var candidateCacheComplete = liveSnapshot.IsComplete;",
            engine,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTrackLedger_PersistsSnapshotMetadataAndDrivesAdmissionOrder()
    {
        var schema = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Services", "Library", "Schema", "library.sql"));
        var database = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Services", "Library", "LibraryDbService.cs"));
        var repository = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var engine = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("source_position INTEGER", schema, StringComparison.Ordinal);
        Assert.Contains("candidate_revision TEXT", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("idx_playlist_watch_track_admission", schema, StringComparison.Ordinal);
        Assert.Contains("EnsureColumnAsync(connection, PlaylistWatchTrackTable, \"source_position\"", database, StringComparison.Ordinal);
        Assert.Contains("EnsureIndexAsync(connection, \"idx_playlist_watch_track_admission\"", database, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT(source, source_id, track_source_id) DO UPDATE SET", repository, StringComparison.Ordinal);
        Assert.Contains("ORDER BY CASE WHEN source_position IS NULL THEN 1 ELSE 0 END", repository, StringComparison.Ordinal);
        Assert.Contains("Position = statusByTrackId.TryGetValue", engine, StringComparison.Ordinal);
        Assert.Contains("foreach (var candidate in orderedCandidates)", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetVerification_IsIndependentAndPlexPlaylistOmissionsDoNotDeleteLibraryIdentities()
    {
        var service = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));
        var repository = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Contains("ResolvePersistedAvailableTrackRowsAsync", service, StringComparison.Ordinal);
        Assert.Contains("matchSummary.SourceTracks", service, StringComparison.Ordinal);
        Assert.Contains("IsResolvedMembershipVerified(", service, StringComparison.Ordinal);
        Assert.Contains("PlaylistSyncResultKind.IdentityGap", service, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyOrScheduleMembershipArtworkAsync", service, StringComparison.Ordinal);
        Assert.Contains("GetMediaServerIdentityRefreshFilesAsync", service, StringComparison.Ordinal);
        Assert.Contains("EnqueueTargetAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestLibraryRefreshAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("await _mediaServerRefreshService.RefreshAsync(targetService, cancellationToken)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("HasNoTargetCoverage", service, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildFailedResult", service, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPartialResult", service, StringComparison.Ordinal);
        Assert.DoesNotContain("public static PlaylistSyncResult Failed(string message)", service, StringComparison.Ordinal);
        Assert.Contains("Failed(string message, PlaylistSyncResultKind kind)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldAcceptMembershipWithExceptions", service, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendDeferredTargetIdentityMessage", service, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(service, "DeleteMediaServerTrackMetadataAsync("));
        Assert.DoesNotContain("DeleteConfirmedMissingPlexTrackMetadataAsync(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPlexRatingKeysByTrackIdsAsync(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("UpsertPlexTrackMetadataAsync(", service, StringComparison.Ordinal);
        Assert.Contains("CheckTrackAvailabilityAsync(", service, StringComparison.Ordinal);
        Assert.Contains("availability == PlexItemAvailability.Missing", service, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM media_server_track_metadata", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("plex_track_metadata", repository, StringComparison.Ordinal);
        Assert.Contains("ORDER BY ranked.missing_priority", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("PARTITION BY lower(job.target_service),", repository, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Success = services.Contains(PlexService, StringComparer.OrdinalIgnoreCase)",
            service,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkJobs_ScheduleImmediatelyAndPreferArtworkOverPlaylistJobs()
    {
        var engine = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var repository = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var sync = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));

        // Membership is no longer blocked waiting for art cache.
        Assert.DoesNotContain("playlist_artwork_cache_unavailable", engine, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Playlist artwork must be cached before initial target synchronization.",
            engine,
            StringComparison.Ordinal);
        Assert.Contains("SchedulePlaylistArtworkTargetSyncAsync", engine, StringComparison.Ordinal);
        Assert.Contains("ScheduleArtworkForActiveRevisionAsync", sync, StringComparison.Ordinal);
        Assert.Contains("ScheduleArtworkForTargetAsync", sync, StringComparison.Ordinal);
        Assert.Contains("The target playlist artwork is missing or stale.", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistArtworkTargetSyncScheduler", sync, StringComparison.Ordinal);
        Assert.Contains("EnqueueWatchlistPlaylistArtworkSyncJobAsync", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("WHEN lower(job.track_id) LIKE 'artwork:%' THEN 0", repository, StringComparison.Ordinal);
        Assert.Contains("ranked.playlist_priority ASC", repository, StringComparison.Ordinal);
    }
}
