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
        Assert.Contains("return await SyncAvailablePlaylistTracksAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Target Plex playlist was not found.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Target Jellyfin playlist was not found.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Target Navidrome playlist was not found.", source, StringComparison.Ordinal);
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

        Assert.Contains("Task.WhenAll", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetOperationTimeout", worker, StringComparison.Ordinal);
        Assert.Contains("RenewWatchlistSyncJobLeaseAsync", worker, StringComparison.Ordinal);
        Assert.Contains("GetNextWatchlistSyncJobDueUtcAsync", repository, StringComparison.Ordinal);
        Assert.Contains("RepairWatchlistSyncBacklogAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER() OVER", repository, StringComparison.Ordinal);
        Assert.Contains("PARTITION BY lower(job.target_service),", repository, StringComparison.Ordinal);
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
        Assert.Contains("ApplyArtworkToNewTargetAsync", service, StringComparison.Ordinal);
        Assert.Contains("SetPlaylistWatchArtworkTargetStateAsync", service, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(service, "&& !await ApplyArtworkToNewTargetAsync("));
        Assert.Equal(6, CountOccurrences(service, "await PersistTargetPlaylistBindingAsync("));
        Assert.Contains("RecreateMissingTargetPlaylistAsync", service, StringComparison.Ordinal);
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
    public void TargetVerification_IsIndependentExactAndRepairsStaleIdentityMappings()
    {
        var service = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));
        var repository = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Equal(3, CountOccurrences(service, "verifiedMemberships.Count != tracks.Count"));
        Assert.Equal(3, CountOccurrences(service, "DeleteMediaServerTrackMetadataAsync("));
        Assert.Contains("DELETE FROM media_server_track_metadata", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("plex_track_metadata", repository, StringComparison.Ordinal);
        Assert.Contains("PARTITION BY lower(job.target_service),", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkJobs_VerifyAppliedTargetsAndRunBeforePlaylistJobs()
    {
        var engine = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var repository = File.ReadAllText(Path.Combine(Root, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Contains("IsPlaylistArtworkCurrentOnTargetAsync", engine, StringComparison.Ordinal);
        Assert.Contains("The target playlist artwork is missing or stale.", engine, StringComparison.Ordinal);
        Assert.Contains("EnqueueWatchlistPlaylistArtworkSyncJobAsync", engine, StringComparison.Ordinal);
        Assert.Contains("WHEN lower(job.track_id) LIKE 'artwork:%' THEN 0", repository, StringComparison.Ordinal);
        Assert.Contains("WHEN lower(job.track_id) = 'playlist' THEN 1", repository, StringComparison.Ordinal);
    }
}
