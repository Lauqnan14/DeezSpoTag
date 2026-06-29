using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyArtistTopTrackTests
{
    private static readonly MethodInfo BuildArtistTopTracksMethod =
        typeof(SpotifyArtistService).GetMethod(
            "BuildArtistTopTracks",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpotifyArtistService.BuildArtistTopTracks not found.");

    [Fact]
    public void BuildArtistTopTracks_UsesPageArtistNameForTopTrackIdentity()
    {
        var page = new SpotifyArtistHydratedPage(
            new SpotifyArtistOverview(
                "2n4DcAtRMvfyRX3ljeC8Kp",
                "2Baba",
                null,
                null,
                new List<string>(),
                null,
                new List<string>(),
                "https://open.spotify.com/artist/2n4DcAtRMvfyRX3ljeC8Kp",
                null,
                null,
                "all"),
            new SpotifyArtistExtras(null, null, null, null),
            new List<SpotifyTrackSummary>
            {
                new(
                    "4hM9jLSD1lgswviJTkHsPP",
                    "African Queen",
                    "2Baba, BEENIE MAN,KUNLE,O.J.B.,BLACK FACE,DE NATIVES,FREESTYL,E.T.C.",
                    "Face 2 Face",
                    null,
                    "https://open.spotify.com/track/4hM9jLSD1lgswviJTkHsPP",
                    null,
                    null,
                    "2004-05-15")
            },
            new List<SpotifyRelatedArtist>(),
            new List<SpotifyAlbumSummary>(),
            new List<SpotifyAlbumSummary>());

        var value = BuildArtistTopTracksMethod.Invoke(
            null,
            new object?[] { page, Array.Empty<SpotifyAlbum>(), "2Baba" });
        var tracks = Assert.IsType<List<SpotifyTrack>>(value);
        var track = Assert.Single(tracks);

        Assert.Equal("2Baba", track.ArtistName);
    }
}
