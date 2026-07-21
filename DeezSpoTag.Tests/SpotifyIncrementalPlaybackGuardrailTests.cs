using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyIncrementalPlaybackGuardrailTests
{
    [Fact]
    public void PathfinderPlaylistMatching_UsesLibrespotOnlyForMissingIsrcIdentity()
    {
        var metadata = ReadSource("DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs");
        var tracklist = ReadSource("DeezSpoTag.Web", "Services", "SpotifyTracklistService.cs");
        var method = SliceMethod(metadata, "HydratePlaylistTrackIsrcsWithLibrespotAsync", "FetchAlbumFallbackWithLibrespotAsync");

        Assert.Contains("FetchLibrespotTrackIdentitiesAsync(trackIds", method, StringComparison.Ordinal);
        Assert.Contains("HydrateTrackDetailsWithLibrespotAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_pathfinderMetadataClient", method, StringComparison.Ordinal);
        Assert.DoesNotContain("HydrateFallbackLibrespotTracksAsync", method, StringComparison.Ordinal);
        Assert.Contains("HydratePlaylistTrackIsrcsWithLibrespotAsync", tracklist, StringComparison.Ordinal);
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
