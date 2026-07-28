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
        Assert.Contains("PersistResolvedTargetBindingAsync", source, StringComparison.Ordinal);
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

        Assert.Contains("Task.WhenAll", worker, StringComparison.Ordinal);
        Assert.Contains("TargetOperationTimeout", worker, StringComparison.Ordinal);
        Assert.Contains("RepairWatchlistSyncBacklogAsync", worker, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER() OVER", repository, StringComparison.Ordinal);
        Assert.Contains("PARTITION BY lower(job.target_service)", repository, StringComparison.Ordinal);
        Assert.Contains("Recovered expired target synchronization lease.", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkSync_VerifiesNavidromeAndRecreatesMissingTargets()
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

        Assert.Contains("HasPlaylistImageAsync", service, StringComparison.Ordinal);
        Assert.Contains("HasPlaylistImageAsync", client, StringComparison.Ordinal);
        Assert.Contains("RecreateMissingTargetPlaylistAsync", service, StringComparison.Ordinal);
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

        Assert.Contains("PlaylistReconciliationMode.SyncOnly", coordinator, StringComparison.Ordinal);
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
}
