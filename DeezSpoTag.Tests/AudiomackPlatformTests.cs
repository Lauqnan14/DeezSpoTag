using System;
using System.Linq;
using DeezSpoTag.Web.Services.AutoTag;
using DeezSpoTag.Web.Services.Audiomack;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Audiomack AutoTag platform: defensive API parsing, song→track mapping, URL slug
/// extraction, and descriptor honesty (no-auth; only reliably available tags claimed).
/// </summary>
public sealed class AudiomackPlatformTests
{
    private const string SearchJson = """
    {
      "results": [
        {
          "id": 40123,
          "title": "Water",
          "artist": "Tyla",
          "album": null,
          "genre": "Afrobeats",
          "mood": "Party",
          "duration": 203,
          "image": "https://images.audiomack.com/cover.jpg",
          "released_date": "2023-07-28T00:00:00Z",
          "url": "https://audiomack.com/tyla/song/water",
          "url_slug": "water",
          "uploader": { "name": "Tyla", "url_slug": "tyla" }
        },
        { "id": 40124, "title": "Water (Remix)", "artist": "Tyla" },
        { "title": "no-id-entry" },
        "not-an-object"
      ]
    }
    """;

    [Fact]
    public void ParseSongSearchResponse_ParsesCandidatesAndSkipsMalformed()
    {
        var songs = AudiomackApiClient.ParseSongSearchResponse(SearchJson, limit: 12);

        Assert.Equal(3, songs.Count);
        var first = songs[0];
        Assert.Equal("40123", first.Id);
        Assert.Equal("Water", first.Title);
        Assert.Equal("Tyla", first.Artist);
        Assert.Equal("Afrobeats", first.Genre);
        Assert.Equal("Party", first.Mood);
        Assert.Equal(203, first.DurationSeconds);
        Assert.Equal("https://images.audiomack.com/cover.jpg", first.ArtworkUrl);
        Assert.Equal("https://audiomack.com/tyla/song/water", first.Url);
        Assert.Equal("tyla", first.ArtistSlug);
    }

    [Fact]
    public void ParseSongSearchResponse_RespectsLimitAndHandlesBadJson()
    {
        Assert.Equal(2, AudiomackApiClient.ParseSongSearchResponse(SearchJson, limit: 2).Count);
        Assert.Empty(AudiomackApiClient.ParseSongSearchResponse("not json", limit: 12));
        Assert.Empty(AudiomackApiClient.ParseSongSearchResponse(null, limit: 12));
    }

    [Fact]
    public void ParseSongResponse_UnwrapsDataPayload()
    {
        const string json = """{ "data": { "id": 99, "title": "Song", "artist": "A" } }""";
        var song = AudiomackApiClient.ParseSongResponse(json);
        Assert.NotNull(song);
        Assert.Equal("99", song!.Id);
        Assert.Equal("Song", song.Title);
    }

    [Fact]
    public void SongCandidate_RejectsNonHttpArtworkUrls()
    {
        const string json = """{ "id": 1, "title": "S", "image": "data:image/png;base64,AAAA" }""";
        var song = AudiomackApiClient.ParseSongResponse(json);
        Assert.NotNull(song);
        Assert.Null(song!.ArtworkUrl);
    }

    [Fact]
    public void ToAutoTagTrack_MapsProvidedFieldsOnly()
    {
        var song = AudiomackApiClient.ParseSongSearchResponse(SearchJson, 12)[0];
        var track = AudiomackMatcher.ToAutoTagTrack(song);

        Assert.NotNull(track);
        Assert.Equal("Water", track!.Title);
        Assert.Equal("Tyla", Assert.Single(track.Artists));
        // No album context in the fixture: no album-artist claim, no album id.
        Assert.Empty(track.AlbumArtists);
        Assert.Null(track.Album);
        Assert.Null(track.AlbumId);
        Assert.Null(track.ReleaseId);
        Assert.Equal("Afrobeats", Assert.Single(track.Genres));
        Assert.Equal("Party", track.Mood);
        Assert.Equal(TimeSpan.FromSeconds(203), track.Duration);
        Assert.Equal("https://images.audiomack.com/cover.jpg", track.Art);
        Assert.Equal("40123", track.TrackId);
        Assert.Null(track.Isrc);
        Assert.Null(track.Bpm);
        Assert.Null(track.Key);
        Assert.Equal(DateTimeKind.Utc, track.ReleaseDate!.Value.Kind);
    }

    [Fact]
    public void ToAutoTagTrack_NullTitleYieldsNull()
    {
        Assert.Null(AudiomackMatcher.ToAutoTagTrack(new AudiomackSongCandidate(
            Id: "1", Title: null, Artist: "A", Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: null, UrlSlug: null, ArtistSlug: null,
            UploaderName: null, AlbumId: null)));
    }

    [Fact]
    public void TryExtractSongSlugs_ParsesAudiomackSongUrls()
    {
        Assert.True(AudiomackIdNormalizer.TryExtractSongSlugs(
            "https://audiomack.com/tyla/song/water", out var artist, out var song));
        Assert.Equal("tyla", artist);
        Assert.Equal("water", song);

        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs(
            "https://open.spotify.com/track/abc", out _, out _));
    }

    [Fact]
    public void Describe_NoAuthAndHonestCapabilitySet()
    {
        var descriptor = new AudiomackPlatform(new StubWebEnvironment()).Describe();
        var platform = descriptor.Platform;

        Assert.Equal("audiomack", platform.Id);
        Assert.False(platform.RequiresAuth);
        Assert.False(descriptor.RequiresAuth);

        var claimed = platform.SupportedTags!.Select(tag => tag.ToString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(SupportedTag.Genre.ToString(), claimed);
        Assert.Contains(SupportedTag.Mood.ToString(), claimed);
        // Audiomack does not provide these; the descriptor must not claim them.
        Assert.DoesNotContain(SupportedTag.BPM.ToString(), claimed);
        Assert.DoesNotContain(SupportedTag.Key.ToString(), claimed);
        Assert.DoesNotContain(SupportedTag.SyncedLyrics.ToString(), claimed);
        Assert.DoesNotContain(SupportedTag.ISRC.ToString(), claimed);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(99, 30)]
    [InlineData(12, 12)]
    public void NormalizeConfig_ClampsSearchLimit(int input, int expected)
    {
        var method = typeof(AudiomackMatcher).GetMethod(
            "NormalizeConfig",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("AudiomackMatcher.NormalizeConfig not found.");

        var result = Assert.IsType<AudiomackMatchConfig>(
            method.Invoke(null, new object?[] { new AudiomackMatchConfig { SearchLimit = input } }));

        Assert.Equal(expected, result.SearchLimit);
    }
}

internal sealed class StubWebEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "DeezSpoTag.Web";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public string EnvironmentName { get; set; } = "Production";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string WebRootPath { get; set; } = AppContext.BaseDirectory;
}
