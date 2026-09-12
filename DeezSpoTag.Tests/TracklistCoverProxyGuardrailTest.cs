using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Tracklist page must display Spotify artwork through the app proxy but must keep the raw
/// CDN URL in the download payload. If display ever regresses to a direct i.scdn.co request the
/// covers fail with net::ERR_TIMED_OUT; if the download payload is ever proxied the download
/// engine would receive a URL it cannot fetch.
/// </summary>
public sealed class TracklistCoverProxyGuardrailTest
{
    private static string ReadTracklistView() => File.ReadAllText(Path.Join(
        TestSourcePaths.RepositoryRoot,
        "DeezSpoTag.Web",
        "Views",
        "Tracklist",
        "Index.cshtml"));

    [Fact]
    public void SpotifyArtworkIsDisplayedThroughTheProxy()
    {
        var source = ReadTracklistView();

        Assert.Contains(
            "src=\"${proxySpotifyCoverUrl(coverImage)}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "src=\"${proxySpotifyCoverUrl(ownerAvatar)}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "const cover = proxySpotifyCoverUrl(track.album?.cover_medium);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDownloadPayloadStillCarriesTheRawCdnUrl()
    {
        var source = ReadTracklistView();

        Assert.Contains(
            "data-track-cover=\"${escapeHtml(normalizeCoverUrl(track.album?.cover_medium || ''))}\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-track-cover=\"${escapeHtml(proxySpotifyCoverUrl(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheProxyHelperOnlyRewritesAllowedSpotifyHosts()
    {
        var source = ReadTracklistView();
        var start = source.IndexOf("function proxySpotifyCoverUrl(", StringComparison.Ordinal);
        Assert.True(start > 0, "the proxy helper should exist");
        var body = source[start..(start + 600)];

        Assert.Contains("i\\.scdn\\.co", body, StringComparison.Ordinal);
        Assert.Contains("/api/externalimages?url=", body, StringComparison.Ordinal);
        Assert.Contains("encodeURIComponent", body, StringComparison.Ordinal);
    }
}
