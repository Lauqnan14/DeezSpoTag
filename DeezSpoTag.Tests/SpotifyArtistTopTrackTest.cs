using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyArtistTopTrackTest
{
    private static readonly MethodInfo BuildArtistTopTracksMethod =
        typeof(SpotifyArtistService).GetMethod(
            "BuildArtistTopTracks",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpotifyArtistService.BuildArtistTopTracks not found.");

    [Theory]
    [InlineData("track")]
    [InlineData("itemV2")]
    public void PathfinderTopTracks_ParseExistingWrappers(string wrapper)
    {
        const string track = "{\"uri\":\"spotify:track:0123456789ABCDEFGHIJKL\",\"name\":\"Song\",\"duration\":{\"totalMilliseconds\":180000},\"artists\":{\"items\":[{\"uri\":\"spotify:artist:abcdefghijklmnopqrstuv\",\"profile\":{\"name\":\"Artist\"}}]}}";
        var wrapped = wrapper == "track" ? "{\"track\":" + track + "}" : "{\"itemV2\":{\"data\":" + track + "}}";
        using var json = System.Text.Json.JsonDocument.Parse("{\"discography\":{\"topTracks\":{\"items\":[" + wrapped + "," + wrapped + "]}}}");
        var method = typeof(SpotifyPathfinderMetadataClient).GetMethod("ParseArtistTopTracks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var parsed = Assert.IsType<List<SpotifyTrackSummary>>(method.Invoke(null, new object[] { json.RootElement }));
        var song = Assert.Single(parsed);
        Assert.Equal("0123456789ABCDEFGHIJKL", song.Id);
        Assert.Equal("Song", song.Name);
        Assert.Equal(180000, song.DurationMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PathfinderSimilarArtists_ParseBothShapes(bool relatedContent)
    {
        const string artists = "{\"relatedArtists\":{\"items\":[{\"uri\":\"spotify:artist:abcdefghijklmnopqrstuv\",\"profile\":{\"name\":\"Artist\"}},{\"uri\":\"spotify:artist:abcdefghijklmnopqrstuv\",\"profile\":{\"name\":\"Artist\"}}]}}";
        using var json = System.Text.Json.JsonDocument.Parse(relatedContent ? "{\"relatedContent\":" + artists + "}" : artists);
        var method = typeof(SpotifyPathfinderMetadataClient).GetMethod("ParseArtistRelatedArtists", BindingFlags.NonPublic | BindingFlags.Static)!;
        var parsed = Assert.IsType<List<SpotifyRelatedArtist>>(method.Invoke(null, new object[] { json.RootElement }));
        var artist = Assert.Single(parsed);
        Assert.Equal("abcdefghijklmnopqrstuv", artist.Id);
        Assert.Equal("Artist", artist.Name);
    }

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
