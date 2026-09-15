using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Provenance capture: the immutable provider identity payload is created from the
/// provider's own match response and can never be rewritten by later mutations of the
/// mutable matched track.
/// </summary>
public sealed class ProviderIdentityCaptureTest
{
    private const string SpotifyTrackId = "0VjIjW4GlUZAMYd2vXMi3b";
    private const string SpotifyUrl = "https://open.spotify.com/track/0VjIjW4GlUZAMYd2vXMi3b";

    [Fact]
    public void Capture_KeepsEveryNativeProviderFieldSeparate()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                TrackId = SpotifyTrackId,
                AlbumId = "spotify-album",
                ReleaseId = "spotify-album",
                ArtistId = "spotify-artist",
                AlbumArtistId = "spotify-album-artist",
                Url = SpotifyUrl
            }
        };

        var payload = Capture("spotify", match);

        Assert.True(payload.IsNativeProviderResult);
        Assert.Equal("spotify", payload.ProviderId);
        Assert.Equal(SpotifyTrackId, payload.TrackId);
        Assert.Equal("spotify-album", payload.AlbumId);
        Assert.Equal("spotify-album", payload.ReleaseId);
        Assert.Equal("spotify-artist", payload.ArtistId);
        Assert.Equal("spotify-album-artist", payload.AlbumArtistId);
        Assert.Equal(SpotifyUrl, payload.Url);
    }

    [Fact]
    public void Capture_DoesNotSynthesizeAlbumIdFromReleaseId()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack { ReleaseId = "release-only" }
        };

        var payload = Capture("deezer", match);

        Assert.Equal("release-only", payload.ReleaseId);
        Assert.Null(payload.AlbumId);
    }

    [Fact]
    public void Capture_DoesNotSynthesizeReleaseIdFromAlbumId()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack { AlbumId = "album-only" }
        };

        var payload = Capture("deezer", match);

        Assert.Equal("album-only", payload.AlbumId);
        Assert.Null(payload.ReleaseId);
    }

    [Fact]
    public void Capture_IsImmutableAgainstLaterTrackMutation()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                TrackId = "native-track",
                AlbumId = "native-album",
                ReleaseId = "native-release",
                ArtistId = "native-artist",
                AlbumArtistId = "native-album-artist",
                Url = "https://www.deezer.com/track/1"
            }
        };

        var payload = Capture("deezer", match);

        match.Track.ReleaseId = "mutated-release";
        match.Track.AlbumId = "mutated-album";
        match.Track.Url = "https://www.deezer.com/track/2";
        match.Track.TrackId = "mutated-track";
        match.Track.ArtistId = "mutated-artist";
        match.Track.AlbumArtistId = "mutated-album-artist";

        Assert.Equal("native-track", payload.TrackId);
        Assert.Equal("native-album", payload.AlbumId);
        Assert.Equal("native-release", payload.ReleaseId);
        Assert.Equal("native-artist", payload.ArtistId);
        Assert.Equal("native-album-artist", payload.AlbumArtistId);
        Assert.Equal("https://www.deezer.com/track/1", payload.Url);
    }

    [Fact]
    public void ShazamResolvedThroughDeezer_HasNoNativeShazamIdentity()
    {
        var match = BuildShazamIdFirstDeezerMatch();
        var payload = Capture("shazam", match);
        Assert.False(payload.IsNativeProviderResult);
        Assert.All(Enum.GetValues<ProviderIdentityField>(), field => Assert.Null(payload.ValueFor(field)));
    }

    [Fact]
    public void ShazamResolvedThroughDeezer_StillCapturesDeezerIdentityForDeezerStage()
    {
        var match = BuildShazamIdFirstDeezerMatch();
        var payload = Capture("deezer", match);

        Assert.True(payload.IsNativeProviderResult);
        Assert.Equal("123", payload.TrackId);
        Assert.Equal("456", payload.ReleaseId);
    }

    [Fact]
    public void NativeShazamMatch_HasNativeIdentity()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                TrackId = "shazam-track",
                Url = "https://www.shazam.com/track/shazam-track",
                ArtistId = "shazam-artist",
                Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SHAZAM_MATCH_STRATEGY"] = ["FINGERPRINT"]
                }
            }
        };

        var payload = Capture("shazam", match);

        Assert.True(payload.IsNativeProviderResult);
        Assert.Equal("shazam-track", payload.TrackId);
        Assert.Equal("https://www.shazam.com/track/shazam-track", payload.Url);
    }

    [Fact]
    public void Capture_IgnoresBlankValues()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack { TrackId = "   ", ReleaseId = "", Url = "  " }
        };

        var payload = Capture("qobuz", match);

        Assert.All(Enum.GetValues<ProviderIdentityField>(), field => Assert.Null(payload.ValueFor(field)));
    }

    [Fact]
    public void Capture_SpotifyRejectsForeignUrls()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack { TrackId = SpotifyTrackId, Url = "https://play.qobuz.com/track/359542303" }
        };

        var payload = Capture("spotify", match);

        Assert.Equal(SpotifyTrackId, payload.TrackId);
        Assert.Null(payload.Url);
    }

    [Fact]
    public void MusicBrainzMapper_ReturnsAlbumAndReleaseIdentitySeparately()
    {
        var mapped = (AutoTagTrack)typeof(MusicBrainzMatcher)
            .GetMethod("ToAutoTagTrack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [BuildMusicBrainzTrack()])!;

        var match = new AutoTagMatchResult { Track = mapped };
        var payload = Capture("musicbrainz", match);

        Assert.Equal("recording-id", payload.TrackId);
        Assert.Equal("album-id", payload.AlbumId);
        Assert.Equal("release-id", payload.ReleaseId);
        Assert.Equal("artist-id", payload.ArtistId);
        Assert.Equal("album-artist-id", payload.AlbumArtistId);
        Assert.Equal("https://musicbrainz.org/recording/recording-id", payload.Url);
    }

    [Fact]
    public void SpotifyMapper_PreservesBothAlbumAndReleaseFromTheSameUpstreamValue()
    {
        var mapped = (AutoTagTrack)typeof(SpotifyMatcher)
            .GetMethod("ToAutoTagTrack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new SpotifyTrackInfo
            {
                TrackId = SpotifyTrackId,
                ReleaseId = "spotify-album",
                ArtistId = "spotify-artist",
                AlbumArtistId = "spotify-album-artist",
                Url = SpotifyUrl
            }])!;

        var payload = Capture("spotify", new AutoTagMatchResult { Track = mapped });

        Assert.Equal(SpotifyTrackId, payload.TrackId);
        Assert.Equal("spotify-album", payload.AlbumId);
        Assert.Equal("spotify-album", payload.ReleaseId);
        Assert.Equal("spotify-artist", payload.ArtistId);
        Assert.Equal("spotify-album-artist", payload.AlbumArtistId);
        Assert.Equal(SpotifyUrl, payload.Url);
    }

    private static AutoTagMatchResult BuildShazamIdFirstDeezerMatch()
        => new()
        {
            Track = new AutoTagTrack
            {
                TrackId = "123",
                ReleaseId = "456",
                Url = "https://www.deezer.com/track/123",
                ArtistId = "789",
                Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SHAZAM_MATCH_PROVIDER"] = ["DEEZER"],
                    ["SHAZAM_MATCH_STRATEGY"] = ["ID_FIRST"],
                    ["DEEZER_TRACK_ID"] = ["123"]
                }
            }
        };

    private static MusicBrainzTrack BuildMusicBrainzTrack()
        => new()
        {
            Id = "recording-id",
            Title = "Song",
            Artists = ["Artist"],
            AlbumArtists = ["Album Artist"],
            Album = "Album",
            Url = "https://musicbrainz.org/recording/recording-id",
            TrackId = "recording-id",
            RecordingId = "recording-id",
            ReleaseId = "release-id",
            AlbumId = "album-id",
            ArtistId = "artist-id",
            AlbumArtistId = "album-artist-id"
        };

    private static ProviderIdentityPayload Capture(string platform, AutoTagMatchResult match)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "CaptureProviderIdentity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<ProviderIdentityPayload>(method!.Invoke(null, [platform, match]));
    }
}