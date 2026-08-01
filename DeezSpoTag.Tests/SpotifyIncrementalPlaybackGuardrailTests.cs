using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyIncrementalPlaybackGuardrailTests
{
    [Fact]
    public void SpotifyIsrcHydration_IsOwnedOnlyByLibrespot()
    {
        var metadata = ReadSource("DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs");
        var tracklist = ReadSource("DeezSpoTag.Web", "Services", "SpotifyTracklistService.cs");
        var pathfinder = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");
        var method = SliceMethod(metadata, "HydrateTrackIsrcsAsync", "FetchAlbumFallbackWithLibrespotAsync");

        Assert.Contains("FetchLibrespotTracksAsync(missing", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_pathfinderMetadataClient", method, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchTrackIsrcsAsync", pathfinder, StringComparison.Ordinal);
        Assert.DoesNotContain("track-isrc-v1", pathfinder, StringComparison.Ordinal);
        Assert.Contains("HydrateTrackIsrcsAsync", tracklist, StringComparison.Ordinal);
        Assert.DoesNotContain("HydratePlaylistTrackIsrcsWithLibrespotAsync", tracklist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HydrationScheduler_ProcessesFastGroupBeforeSlowGroupCompletes()
    {
        var processed = new ConcurrentQueue<int>();
        var activeGroups = 0;
        var maximumActiveGroups = 0;
        var items = Enumerable.Range(0, 10).ToArray();

        await SpotifyTracklistHydrationScheduler.RunAsync(
            items,
            groupSize: 5,
            async (group, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activeGroups);
                UpdateMaximum(ref maximumActiveGroups, active);
                await Task.Delay(group[0] == 0 ? 150 : 10, cancellationToken);
                Interlocked.Decrement(ref activeGroups);
                return group.ToList();
            },
            (item, _) =>
            {
                processed.Enqueue(item);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(10, processed.Count);
        Assert.Equal(5, processed.First());
        Assert.True(maximumActiveGroups >= 2);
    }

    [Fact]
    public void LibrespotBatchFailure_DoesNotSpawnSerialPerTrackProcesses()
    {
        var metadata = ReadSource("DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs");
        var helper = ReadSource("DeezSpoTag.Web", "Tools", "spotify_librespot_worker.py");
        var blob = ReadSource("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");
        var method = SliceMethod(metadata, "HydrateLibrespotBatchAsync", "MergeHydratedTracks");

        Assert.DoesNotContain("foreach (var trackId in batchTrackIds)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<string> { trackId }", method, StringComparison.Ordinal);
        Assert.Contains("ThreadPoolExecutor", helper, StringComparison.Ordinal);
        Assert.Contains("executor.map(fetch, track_ids)", helper, StringComparison.Ordinal);
        Assert.Contains("LibrespotWorkerProcess", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify_librespot_tracks.py", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialLibrespotHydration_ContinuesOnlyThroughPathfinderForMissingFields()
    {
        var metadata = ReadSource("DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs");
        var method = SliceMethod(metadata, "HydrateTrackDetailsWithBlobAsync", "HydrateTrackIsrcsAsync");

        Assert.Contains("tracks = await HydrateTrackDetailsWithLibrespotAsync", method, StringComparison.Ordinal);
        Assert.Contains("return await HydrateTrackDetailsAsync(tracks", method, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildLibrespotContextAsync", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifySearch_UsesOneWebPlayerAttemptThenTheLibrespotOnlyContext()
    {
        var search = ReadSource("DeezSpoTag.Web", "Services", "SpotifySearchService.cs");
        var method = SliceMethod(
            search,
            "private async Task<SearchContext?> BuildRequestContextAsync",
            "private async Task<SearchContext?> BuildWebPlayerCookieContextAsync");

        Assert.Contains("BuildWebPlayerCookieContextAsync", method, StringComparison.Ordinal);
        Assert.Contains("BuildLibrespotOnlyContextAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildLibrespotContextAsync", search, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyHome_PreservesLegacyPersonalizedFeedCompatibility()
    {
        var pathfinder = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "SpotifyHomeFeedApiController.cs");

        Assert.Contains("7fa05a3b71ee950cd63f5b738a0285f7c58b20a93e735ada5ad9a8d5e116d791", pathfinder, StringComparison.Ordinal);
        Assert.Contains("[\"homeEndUserIntegration\"] = IntegrationWebPlayer", pathfinder, StringComparison.Ordinal);
        Assert.Contains("variables[\"sp_t\"] = context.DeviceId", pathfinder, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenInfo.IsAnonymous == true", pathfinder, StringComparison.Ordinal);
        var homeMethod = SliceMethod(pathfinder, "public async Task<JsonDocument?> FetchHomeFeedWithBlobAsync", "public async Task<bool> ValidateBlobAsync");
        Assert.Contains("TryResolveActiveSpotifyBlobPathAsync", homeMethod, StringComparison.Ordinal);
        Assert.Contains("BuildBlobAuthContextAsync(blobPath, cancellationToken)", homeMethod, StringComparison.Ordinal);
        Assert.Contains("FetchHomeFeedLegacyWithBlobAsync", pathfinder, StringComparison.Ordinal);
        Assert.Contains("legacyTask", controller, StringComparison.Ordinal);
        var trendingMethod = SliceMethod(controller, "private async Task<object?> TryFetchTrendingSongsSectionAsync", "private async Task<object?> TryFetchTrendingSongsSectionByUriAsync");
        var trendingByUriMethod = SliceMethod(controller, "private async Task<object?> TryFetchTrendingSongsSectionByUriAsync", "private static object MapSpotifyTrackSummaryToHomeTrendingItem");
        Assert.Contains("FetchBrowseAllAnonymousAsync", trendingMethod, StringComparison.Ordinal);
        Assert.Contains("FetchBrowseSectionTrackSummariesAnonymousAsync", trendingByUriMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("WithBlobAsync", trendingMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("WithBlobAsync", trendingByUriMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyPlaylistRecommendations_UseTheCurrentPlaylistSectionContract()
    {
        var pathfinder = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");

        Assert.Contains("PlaylistSectionOperationName = \"playlistSection\"", pathfinder, StringComparison.Ordinal);
        Assert.Contains("2615df403a9043c1d7d3094fbeb4c9653b07b11a33d8081fbd31f0f7959ff4a1", pathfinder, StringComparison.Ordinal);
        Assert.Contains("spotify:section:0JQ5DAob0LgAOAm50K90Od", pathfinder, StringComparison.Ordinal);
        Assert.Contains("[\"sectionUri\"] = MoreLikeThisPlaylistSectionUri", pathfinder, StringComparison.Ordinal);
        Assert.Contains("[\"playlistUri\"] = contextUri", pathfinder, StringComparison.Ordinal);
        Assert.DoesNotContain("moreLikeThisPlaylist", pathfinder, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyHome_PreservesEveryRenderablePersonalizedSection()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "SpotifyHomeFeedApiController.cs");
        var view = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "home-index.js");

        Assert.Contains("PersonalSectionKeywords", controller, StringComparison.Ordinal);
        Assert.Contains("PersonalItemKeywords", controller, StringComparison.Ordinal);
        Assert.Contains("IsPersonalSpotifyHomeItem", controller, StringComparison.Ordinal);
        Assert.Contains("filterHomeSectionsForRender", view, StringComparison.Ordinal);
        Assert.Contains("itemCount >= 4", view, StringComparison.Ordinal);
        Assert.Contains("isEpisodesYouMightLikeSection", view, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistResolver_UsesIsrcFirstAndStrictMetadataOnlyForHardMismatch()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "SpotifyPlaylistTracklistApiController.cs");
        var background = ReadSource("DeezSpoTag.Web", "Services", "SpotifyTracklistMatchBackgroundService.cs");

        Assert.Contains("allowFallbackSearch: false", controller, StringComparison.Ordinal);
        Assert.Contains("PreferIsrcOnly: !item.AllowFallbackSearch", background, StringComparison.Ordinal);
        Assert.Contains("item.AllowFallbackSearch ? strictMode : true", background, StringComparison.Ordinal);
        Assert.Contains("ShouldRunTerminalMetadataPass(result)", background, StringComparison.Ordinal);
        Assert.Contains("strictMode: terminalStrictMode", background, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchPolling_UsesRevisionsAndOneSelfSchedulingLoop()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "SpotifyTracklistMatchesApiController.cs");
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("afterRevision", controller, StringComparison.Ordinal);
        Assert.Contains("revision = snapshot.Revision", controller, StringComparison.Ordinal);
        Assert.Contains("qs.set('afterRevision', String(revision))", view, StringComparison.Ordinal);
        Assert.Contains("spotifyMatchSessionId", view, StringComparison.Ordinal);
        Assert.Contains("setTimeout(pollMatches, 1000)", view, StringComparison.Ordinal);
        Assert.DoesNotContain("setInterval(pollMatches", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedMatch_IsAppliedWithoutWaitingForPlaylistCompletion()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("if (row && deezerId)", view, StringComparison.Ordinal);
        Assert.Contains("applyExternalMatchToRow(row, deezerId)", view, StringComparison.Ordinal);
        Assert.Contains("if (normalizedId)", view, StringComparison.Ordinal);
        Assert.Contains("hasPreview || hasMatchedPlaybackIdentity ? ''", view, StringComparison.Ordinal);
        Assert.DoesNotContain("status === 'matched'))", view, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchedTrack_ClickResolvesOnlyItsPlaybackContextOnDemand()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("resolvedPreviewUrl = await resolveDeezerPlaybackUrlForRow", view, StringComparison.Ordinal);
        Assert.Contains("fetchContext: true", view, StringComparison.Ordinal);
        Assert.Contains("enqueueMatchedPlaybackContextPrefetch(row)", view, StringComparison.Ordinal);
        Assert.Contains("matchedPlaybackPrefetchRows", view, StringComparison.Ordinal);
        Assert.DoesNotContain("playback is still preparing for this track", view, StringComparison.Ordinal);
    }

    [Fact]
    public void NonTerminalProgress_DoesNotDisableAStillMatchingRow()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("status === 'unmatched_final' || status === 'hard_mismatch'", view, StringComparison.Ordinal);
        Assert.DoesNotContain("else if (row && status)", view, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string ReadSource(params string[] relativeParts)
        => File.ReadAllText(Path.Join(ResolveRepoRoot(), Path.Join(relativeParts)));

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current || Interlocked.CompareExchange(ref maximum, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Tests")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
