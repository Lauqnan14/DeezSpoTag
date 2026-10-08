using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the shared playlist snapshot ceiling and the complete/incomplete semantics that go with
/// it.
///
/// The governing rule: reaching the ceiling is an intentional bounded result, not a provider
/// failure. A playlist larger than the limit is snapshotted partially, reconciled over the window
/// that was actually collected, reported as incomplete, and never allowed to authorise removing
/// anything outside that window.
/// </summary>
public sealed class PlaylistSnapshotLimitTest
{
    private static string EngineSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

    [Fact]
    public void DefaultLimit_IsThreeThousand()
    {
        Assert.Equal(3000, DeezSpoTagSettings.DefaultPlaylistSnapshotTrackLimit);
        Assert.Equal(
            DeezSpoTagSettings.DefaultPlaylistSnapshotTrackLimit,
            new DeezSpoTagSettings().PlaylistSnapshotTrackLimit);
    }

    [Fact]
    public void TheLimitIsOneSharedValue_NotDuplicatedPerProvider()
    {
        // A single configured value must reach the engine. No provider may carry its own copy.
        var source = EngineSource();
        Assert.Contains("ResolveSnapshotTrackLimit()", source, StringComparison.Ordinal);
        Assert.Contains("PlaylistSnapshotTrackLimit", source, StringComparison.Ordinal);
        Assert.Contains("DefaultPlaylistSnapshotTrackLimit", source, StringComparison.Ordinal);

        // The old unbounded snapshot sentinel must be gone entirely.
        Assert.DoesNotContain("CompletePlaylistCandidateFetchCount", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderPageSize_RemainsIndependentOfTheSnapshotCeiling()
    {
        var source = EngineSource();
        var apple = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Services", "Apple", "AppleMusicCatalogService.cs"));

        // The ceiling is how many candidates are retained, not how many are requested. Providers
        // keep their own valid page size and simply stop paging once the ceiling is reached.
        Assert.Contains("Math.Min(100, maxCandidates)", source, StringComparison.Ordinal);
        Assert.Contains("while (candidates.Count < maxCandidates)", source, StringComparison.Ordinal);
        Assert.Contains("const int pageSize = 100", apple, StringComparison.Ordinal);

        // Tidal keeps its own explicit limit rather than adopting the ceiling.
        Assert.Contains("limit=100&offset={offset}", source, StringComparison.Ordinal);

        // No provider page size may be set to the snapshot ceiling.
        Assert.DoesNotContain("pageSize = 3000", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize = 3000", apple, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize = ResolveSnapshotTrackLimit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("limit=3000", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedSnapshotIsNotTreatedAsASourceFailure()
    {
        var source = EngineSource();

        // Both guards that used to hard-fail now defer to the truncation flag.
        Assert.Contains("var snapshotTruncated = liveSnapshot.SnapshotTruncated;", source, StringComparison.Ordinal);
        Assert.Contains("if (!snapshotTruncated", source, StringComparison.Ordinal);

        // The failure path itself is untouched: a genuine provider error still fails closed and
        // preserves the previous good snapshot.
        Assert.Contains("WatchlistPlaylistState.SourceFailure", source, StringComparison.Ordinal);
        Assert.Contains("the previous valid snapshot was preserved", source, StringComparison.Ordinal);
        Assert.Contains("IsPlaylistScopedSourceFailure", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialSnapshotsCannotAuthoriseDeletion()
    {
        var source = EngineSource();

        // The destructive reconciliation is still gated on a complete snapshot. A truncated
        // snapshot therefore cannot remove ledger tracks, target membership, or infer removal for
        // anything past the fetched window.
        var cacheIndex = source.IndexOf("UpsertPlaylistTrackCandidateCacheAsync", StringComparison.Ordinal);
        var gateIndex = source.IndexOf("if (liveSnapshot.IsComplete)", StringComparison.Ordinal);
        var removeIndex = source.IndexOf("RemovePlaylistWatchTracksNotInAsync", StringComparison.Ordinal);

        Assert.True(cacheIndex > 0, "candidate cache must still be written");
        Assert.True(gateIndex > cacheIndex, "the completeness gate must follow the cache write");
        Assert.True(removeIndex > gateIndex, "removal must sit inside the completeness gate");
        Assert.DoesNotContain("RemovePlaylistWatchTracksNotInAsync", source[..removeIndex], StringComparison.Ordinal);
    }

    [Fact]
    public void TruncationPreservesTheProviderReportedTotal()
    {
        var source = EngineSource();

        // The provider's own total must survive so a truncated snapshot stays distinguishable
        // from a complete one. Reporting the collected count instead would hide the truncation.
        Assert.Contains("TrackCount: metadata.TotalTracks ?? candidates.Count", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackCount: candidates.Count,\n                IsComplete: isComplete", source, StringComparison.Ordinal);

        // And the truncation predicate relies on exactly that total.
        Assert.Contains("Candidates.Count < reported", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HittingTheCeilingIsNotReportedAsAnIncompleteSource()
    {
        var source = EngineSource();

        // Exiting the paging loop because the ceiling was reached must not be misdiagnosed as the
        // provider falling short, which previously produced a spurious
        // "spotify_source_count_incomplete" failure.
        Assert.Contains("var hitSnapshotCeiling = candidates.Count >= maxCandidates;", source, StringComparison.Ordinal);
        Assert.Contains("!hitSnapshotCeiling", source, StringComparison.Ordinal);
        Assert.Contains("spotify_source_count_incomplete", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedPlaylistIsReportedAsIncomplete()
    {
        var source = EngineSource();

        // Truncation always reports incomplete, so the UI never implies full coverage.
        Assert.Contains("var hasOutstandingPlaylistWork = snapshotTruncated", source, StringComparison.Ordinal);
        Assert.Contains("was truncated at", source, StringComparison.Ordinal);
        Assert.Contains("tracks beyond it were left untouched", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStreamingProviderAdapterObeysTheSharedLimit()
    {
        var source = EngineSource();

        // All six external playlist platforms must route through a limit-aware fetch, so the
        // shared ceiling is applied uniformly instead of being silently discarded.
        foreach (var method in new[]
                 {
                     "GetSpotifyPlaylistSnapshotAsync",
                     "GetDeezerSnapshotAsync",
                     "GetAppleSnapshotAsync",
                     "GetBoomplaySnapshotAsync",
                     "GetQobuzSnapshotAsync",
                     "GetTidalSnapshotAsync"
                 })
        {
            Assert.True(
                source.Contains($"{method},\n", StringComparison.Ordinal)
                || source.Contains($"{method},\r\n", StringComparison.Ordinal),
                $"{method} must be wired directly into its adapter");
        }

        // Only the two generated sources may ignore the ceiling. They are intentionally excluded
        // from the universal snapshot policy.
        var discardCount = source.Split("async (id, _, token)").Length - 1;
        Assert.Equal(2, discardCount);
        Assert.Contains("GetSmartTracklistSnapshotAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetRecommendationTrackCandidatesAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EachProviderPreservesItsReportedTotalWhenTruncating()
    {
        var source = EngineSource();

        // The shared helper is how providers report a bounded read: the provider's own total is
        // kept so truncation is visible, and IsComplete is cleared without inventing a failure.
        Assert.Contains("private static LivePlaylistSnapshot BuildPlaylistSnapshot(", source, StringComparison.Ordinal);
        Assert.Contains("var wasTruncated = reported > candidates.Count;", source, StringComparison.Ordinal);
        Assert.Contains("IsComplete = baseMetadata.IsComplete && !wasTruncated", source, StringComparison.Ordinal);

        // Deezer and Qobuz supply their real totals, plus the pre-dedup observed count so a
        // duplicated playlist entry is not mistaken for a short read.
        Assert.Contains("BuildPlaylistSnapshot(candidates, tracks.Count, SourceItemsObserved: tracks.Count)", source, StringComparison.Ordinal);
        Assert.Contains("BuildPlaylistSnapshot(candidates, rows.Count, SourceItemsObserved: rows.Count)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncationIsNeverGivenAFailureCode()
    {
        var source = EngineSource();

        // A bounded read must not look like a provider error, or it would trip failure handling.
        // Apple is the one provider that computes a failure code, so it must explicitly exempt
        // truncation.
        Assert.Contains("var wasTruncated", source, StringComparison.Ordinal);
        Assert.Contains("FailureCode: isComplete || wasTruncated", source, StringComparison.Ordinal);

        // The shared helper never assigns a failure code.
        var helperStart = source.IndexOf("private static LivePlaylistSnapshot BuildPlaylistSnapshot(", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("private static LivePlaylistSnapshot BuildLivePlaylistSnapshot(", helperStart, StringComparison.Ordinal);
        Assert.DoesNotContain("FailureCode", source[helperStart..helperEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void BoomplayCapsBeforeItsExpensiveIdentityMapping()
    {
        var source = EngineSource();

        // Boomplay pays a per-track Deezer identity lookup, so the ceiling has to be applied before
        // that mapping rather than after, or an oversized playlist multiplies mapping work.
        var capIndex = source.IndexOf(".Take(maxCandidates)", StringComparison.Ordinal);
        var mapIndex = source.IndexOf("var candidates = await MapBoomplayWatchIntentTrackCandidatesAsync(tracksToMap, cancellationToken);", StringComparison.Ordinal);
        Assert.True(capIndex > 0, "Boomplay must cap its source tracks before mapping");
        Assert.True(mapIndex > capIndex, "the cap must be applied before the mapping call");
    }

    [Fact]
    public void AppleCatalogKeepsItsOwnPageSizeWhenBounded()
    {
        var apple = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Services", "Apple", "AppleMusicCatalogService.cs"));

        // The bound is an added ceiling on collected tracks, not a change to the request size.
        Assert.Contains("const int pageSize = 100", apple, StringComparison.Ordinal);
        Assert.Contains("int maxTracks = 0", apple, StringComparison.Ordinal);
        Assert.Contains("if (hasCeiling && tracks.Count >= maxTracks)", apple, StringComparison.Ordinal);
        Assert.DoesNotContain("const int pageSize = 3000", apple, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalStopsPagingWhenTheCeilingIsReached()
    {
        var source = EngineSource();

        // Tidal pages with its own limit=100 and must break out of the loop on the shared ceiling
        // rather than continuing to request pages it will discard.
        Assert.Contains("if (candidates.Count >= maxCandidates)", source, StringComparison.Ordinal);
        Assert.Contains("candidates.Count < maxCandidates &&", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFastHeadCheckPathIsUnchanged()
    {
        var source = EngineSource();

        // Truncation handling must not have weakened the change-detection fast path: an unchanged
        // playlist still reuses its cached candidates and is never expanded.
        Assert.Contains("WatchUseSnapshotIdChecking", source, StringComparison.Ordinal);
        Assert.Contains("SupportsStrictSnapshotReuse", source, StringComparison.Ordinal);
        Assert.Contains("existingCandidateCache", source, StringComparison.Ordinal);
        Assert.Contains("PlaylistCandidateContract.IsReusableCache", source, StringComparison.Ordinal);
        Assert.Contains("Snapshot unchanged. Reusing cached candidates.", source, StringComparison.Ordinal);
        Assert.Contains("FetchLivePlaylistHeadAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateCacheStillRecordsIncompletenessDurably()
    {
        var source = EngineSource();

        // A truncated snapshot must persist its incompleteness so a later cycle does not mistake
        // the partial cache for a complete one.
        Assert.Contains("var candidateCacheComplete = liveSnapshot.IsComplete;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyVirtualPlaylistLimitStillScopesOnlyToTheTrendingFeed()
    {
        var source = EngineSource();

        // The 1,000 limit belongs to Spotify's generated 'home-trending-songs' virtual feed, not to
        // real watched playlists, so it is intentionally left alone and does not follow the
        // universal ceiling. It must not leak onto artist-top or ordinary playlists.
        Assert.Contains("SpotifyVirtualPlaylistCandidateLimit = 1000", source, StringComparison.Ordinal);
        Assert.Contains("Math.Min(maxCandidates, SpotifyVirtualPlaylistCandidateLimit)", source, StringComparison.Ordinal);

        var trendingIndex = source.IndexOf("IsSpotifyHomeTrendingSourceId", StringComparison.Ordinal);
        var usageIndex = source.IndexOf("Math.Min(maxCandidates, SpotifyVirtualPlaylistCandidateLimit)", StringComparison.Ordinal);
        Assert.True(usageIndex > trendingIndex, "the 1,000 limit must stay inside the trending-feed branch");
    }
}
