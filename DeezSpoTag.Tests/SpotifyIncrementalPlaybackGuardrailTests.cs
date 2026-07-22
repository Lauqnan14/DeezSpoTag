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
        var helper = ReadSource("DeezSpoTag.Web", "Tools", "spotify_librespot_tracks.py");
        var method = SliceMethod(metadata, "HydrateLibrespotBatchAsync", "MergeHydratedTracks");

        Assert.DoesNotContain("foreach (var trackId in batchTrackIds)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<string> { trackId }", method, StringComparison.Ordinal);
        Assert.Contains("ThreadPoolExecutor", helper, StringComparison.Ordinal);
        Assert.Contains("executor.map(fetch_track, ids)", helper, StringComparison.Ordinal);
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
