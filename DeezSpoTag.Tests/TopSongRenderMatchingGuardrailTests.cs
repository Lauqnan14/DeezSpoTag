using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TopSongRenderMatchingGuardrailTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void SharedSectionMatcher_WaitsForTerminalBackgroundResult()
    {
        var helpers = ReadSource("DeezSpoTag.Web/wwwroot/js/spotify-url-helpers.js");
        var matcher = ExtractBetween(
            helpers,
            "function createDeezerSectionMatcher(options)",
            "globalObj.SpotifyUrlHelpers = Object.freeze");

        Assert.Contains("async function pollUntilTerminal", matcher, StringComparison.Ordinal);
        Assert.Contains("while (pending > 0)", matcher, StringComparison.Ordinal);
        Assert.Contains("await pollUntilTerminal(token, pendingCount", matcher, StringComparison.Ordinal);
        Assert.Contains("const queuedMatch = matchQueue.then(() => matchEntries(entries));", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("setInterval(", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("waitForCurrent", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("isRunning", matcher, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeTrending_MatchesOnRenderAndClickOnlyStartsPlayback()
    {
        var home = ReadSource("DeezSpoTag.Web/wwwroot/js/home-index.js");
        var scheduler = ExtractBetween(
            home,
            "function scheduleHomeTrendingTrackMappingWarmup()",
            "function buildHomeTrendingPlaybackRequest");
        var readiness = ExtractBetween(
            home,
            "function ensureHomeTrendingButtonReadyForPlayback",
            "async function playHomeTrendingTrackInApp");
        var click = ExtractBetween(
            home,
            "async function playHomeTrendingTrackInApp",
            "function isMadeForYouSection");

        Assert.Contains("scheduleHomeTrendingTrackMappingWarmup();", home, StringComparison.Ordinal);
        Assert.Contains("void primeHomeTrendingTrackMappings({ visibleFirst: true });", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", scheduler, StringComparison.Ordinal);
        Assert.Contains("getValidHomeTrendingDeezerId(playButton)", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("startHomeTrendingPlaylistStyleMatching", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("waitForCurrent", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("await", readiness, StringComparison.Ordinal);
        Assert.Contains("const ready = ensureHomeTrendingButtonReadyForPlayback", click, StringComparison.Ordinal);
        Assert.Contains("await player.play(request);", click, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistTopSongs_MatchOnRenderAndClickOnlyStartsPlayback()
    {
        var artist = ReadSource("DeezSpoTag.Web/Views/Artist/Index.cshtml");
        var scheduler = ExtractBetween(
            artist,
            "function scheduleArtistTopTrackPreviewWarmup()",
            "function getArtistTopTrackMatcher");
        var readiness = ExtractBetween(
            artist,
            "function ensureArtistTopTrackButtonReadyForPlayback",
            "async function resolveSpotifyUrlToDeezer");
        var click = ExtractBetween(
            artist,
            "async function playArtistTrackInApp",
            "function ensureArtistTopTrackButtonReadyForPlayback");

        Assert.Contains("scheduleArtistTopTrackPreviewWarmup();", artist, StringComparison.Ordinal);
        Assert.Contains("void primeArtistTopTrackPreviews({ visibleFirst: true });", scheduler, StringComparison.Ordinal);
        Assert.Contains("void resolveAmazonTopTrackDeezerIds();", artist, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("startArtistTopTrackPlaylistStyleMatching", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("await", readiness, StringComparison.Ordinal);
        Assert.Contains("const ready = ensureArtistTopTrackButtonReadyForPlayback", click, StringComparison.Ordinal);
        Assert.Contains("await player.play(request);", click, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryArtistTopSongs_MatchOnRenderAndClickOnlyStartsPlayback()
    {
        var library = ReadSource("DeezSpoTag.Web/wwwroot/js/library.js");
        var scheduler = ExtractBetween(
            library,
            "function scheduleSpotifyTopTrackPreviewWarmup()",
            "function getSpotifyTopTrackMatcher");
        var readiness = ExtractBetween(
            library,
            "function ensureLibrarySpotifyButtonReadyForPlayback",
            "async function getNextLibraryPlayableSpotifyButton");
        var click = ExtractBetween(
            library,
            "async function playSpotifyTrackInApp",
            "async function playLocalLibraryTrackInApp");

        Assert.Contains("scheduleSpotifyTopTrackPreviewWarmup();", library, StringComparison.Ordinal);
        Assert.Contains("void primeSpotifyTrackPreviews({ visibleFirst: true });", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("startSpotifyTopTrackPlaylistStyleMatching", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("await", readiness, StringComparison.Ordinal);
        Assert.Contains("const ready = ensureLibrarySpotifyButtonReadyForPlayback", click, StringComparison.Ordinal);
        Assert.Contains("await player.play(request);", click, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root.");
    }
}
