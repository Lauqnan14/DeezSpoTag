using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Web.Services.AutoTag;
using DeezSpoTag.Web.Services.Audiomack;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Audiomack AutoTag platform: defensive API parsing, song→track mapping, URL slug
/// extraction, and descriptor honesty (no-auth; only reliably available tags claimed).
/// </summary>
public sealed class AudiomackPlatformTest
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
        // The fixture's uploader carries no id, so there is nothing to write.
        Assert.Null(track.ArtistId);
        Assert.Null(track.Isrc);
        Assert.Null(track.Bpm);
        Assert.Null(track.Key);
        Assert.Equal(DateTimeKind.Utc, track.ReleaseDate!.Value.Kind);
    }

    [Fact]
    public void ToAutoTagTrack_MapsUploaderArtistIdAndAlbumIdWithoutReleaseAlias()
    {
        // Audiomack's own payload: the uploader object carries the artist id, and the
        // album id is an album-scoped id only — it is not aliased into the release slot.
        const string json = """
        {"results":[{"id":78139729,"title":"Fallen Angel","artist":"Alikiba","album":"Only One","album_id":"55501","isrc":"ZA40S2401187","upc":"085365330924","explicit":"yes","genre":"electronic","label":"Sony","duration":261,"released_date":"2024-06-14","url":"https://audiomack.com/alikiba/song/fallen-angel","url_slug":"fallen-angel","uploader":{"id":"16579133","name":"Alikiba","url_slug":"alikiba"}}]}
        """;

        var song = AudiomackApiClient.ParseSongSearchResponse(json, 12)[0];
        var track = AudiomackMatcher.ToAutoTagTrack(song);

        Assert.NotNull(track);
        Assert.Equal("16579133", track!.ArtistId);
        // No separate album-artist id is exposed, so none is invented.
        Assert.Null(track.AlbumArtistId);
        Assert.Equal("55501", track.AlbumId);
        Assert.Null(track.ReleaseId);
        Assert.Equal("ZA40S2401187", track.Isrc);
        Assert.Equal("Sony", track.Label);
        Assert.Equal("085365330924", track.Barcode);
        Assert.True(track.Explicit);
        Assert.Equal(TimeSpan.FromSeconds(261), track.Duration);
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
        Assert.Contains(SupportedTag.Style.ToString(), claimed);
        Assert.Contains(SupportedTag.Mood.ToString(), claimed);
        Assert.Contains(SupportedTag.Label.ToString(), claimed);
        // The payload carries isrc (the real captured fixture does) and the matcher
        // maps it, so the descriptor must claim it — it used to be fetched and dropped.
        Assert.Contains(SupportedTag.ISRC.ToString(), claimed);
        Assert.Contains("isrc", platform.DownloadTags!, StringComparer.OrdinalIgnoreCase);
        // Audiomack's own uploader id is the artist id now mapped to file tags.
        Assert.Contains(SupportedTag.ArtistId.ToString(), claimed);
        Assert.Contains("artistId", platform.DownloadTags!, StringComparer.OrdinalIgnoreCase);
        // The real payload carries UPC and an explicit flag; both are mapped now.
        Assert.Contains(SupportedTag.Barcode.ToString(), claimed);
        Assert.Contains("barcode", platform.DownloadTags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(SupportedTag.Explicit.ToString(), claimed);
        Assert.Contains("explicit", platform.DownloadTags!, StringComparer.OrdinalIgnoreCase);
        // Audiomack exposes no separate recording id or release entity, so those must
        // not be claimed (they would be claimed-but-never-mapped).
        Assert.DoesNotContain(SupportedTag.RecordingId.ToString(), claimed);
        Assert.DoesNotContain(SupportedTag.ReleaseId.ToString(), claimed);
        Assert.DoesNotContain("recordingId", platform.DownloadTags!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("releaseId", platform.DownloadTags!, StringComparer.OrdinalIgnoreCase);
        // Audiomack does not provide these; the descriptor must not claim them.
        Assert.DoesNotContain(SupportedTag.BPM.ToString(), claimed);
        Assert.DoesNotContain(SupportedTag.Key.ToString(), claimed);
        Assert.DoesNotContain(SupportedTag.SyncedLyrics.ToString(), claimed);
    }

    [Fact]
    public void Identity_WritesOnlyTheAudiomackProviderFieldFamily()
    {
        // Audiomack is a provider in the AutoTag identity contract: it may only write
        // its own AUDIOMACK_* fields and must never claim a generic compatibility field
        // (those belong to no provider family).
        var forbidden = new[] { "ALBUMID", "ARTISTID", "ALBUMARTISTID", "RECORDINGID", "URL", "WWWAUDIOFILE" };
        foreach (var field in Enum.GetValues<ProviderIdentityField>())
        {
            var family = AutoTagIdentityTags.ResolveFamily("audiomack", field);
            Assert.All(family.WriteNames, name => Assert.StartsWith("AUDIOMACK_", name));
            Assert.DoesNotContain(family.CleanupNames, forbidden.Contains);
        }

        Assert.Contains("AUDIOMACK_TRACK_ID", AutoTagIdentityTags.ResolveFamily("audiomack", ProviderIdentityField.TrackId).WriteNames);
        Assert.Contains("AUDIOMACK_ALBUM_ID", AutoTagIdentityTags.ResolveFamily("audiomack", ProviderIdentityField.AlbumId).WriteNames);
        Assert.Contains("AUDIOMACK_RELEASE_ID", AutoTagIdentityTags.ResolveFamily("audiomack", ProviderIdentityField.ReleaseId).WriteNames);
        Assert.Contains("AUDIOMACK_ARTIST_ID", AutoTagIdentityTags.ResolveFamily("audiomack", ProviderIdentityField.ArtistId).WriteNames);
        Assert.Contains("AUDIOMACK_URL", AutoTagIdentityTags.ResolveFamily("audiomack", ProviderIdentityField.Url).WriteNames);
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

    [Fact]
    public void ToAutoTagTrack_MapsSubgenresAsStylesAndAllMoods()
    {
        const string json = """
        {
          "id": 8526341,
          "title": "Amapiano Nights",
          "artist": "Piano Pusha",
          "genre": "afrosounds",
          "subgenres": ["amapiano", "afrobeats"],
          "moods": ["happy", "party"],
          "mood": "happy"
        }
        """;
        var song = AudiomackApiClient.ParseSongResponse(json);
        var track = AudiomackMatcher.ToAutoTagTrack(song!);

        Assert.Equal("Afrosounds", Assert.Single(track!.Genres));
        Assert.Equal(new[] { "Amapiano", "Afrobeats" }, track.Styles);
        Assert.Equal("Happy, Party", track.Mood);
    }

    [Fact]
    public void ToAutoTagTrack_DropsAudiobookAndPromotesMusicSubgenre()
    {
        const string json = """
        {
          "id": 78139729,
          "title": "Fallen Angel",
          "artist": "Alikiba",
          "genre": "Audiobook",
          "subgenres": ["amapiano"]
        }
        """;
        var song = AudiomackApiClient.ParseSongResponse(json);
        Assert.True(AudiomackTaxonomy.IsNonMusicCandidate(song! with { Subgenres = Array.Empty<string>() }));
        Assert.False(AudiomackTaxonomy.IsNonMusicCandidate(song!));

        var track = AudiomackMatcher.ToAutoTagTrack(song!);
        Assert.DoesNotContain(track!.Genres, genre => genre.Contains("audiobook", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Amapiano", Assert.Single(track.Genres));
        Assert.Equal("Amapiano", Assert.Single(track.Styles));
        Assert.Null(track.Mood);
    }

    [Fact]
    public void ToAutoTagTrack_UsesTagDisplayAndSkipsLocationChips()
    {
        var json = File.ReadAllText(Path.Combine(FixturesRoot(), "track-page-song.json"));
        var song = AudiomackApiClient.ParseSongResponse(json);
        Assert.NotNull(song);
        Assert.Equal("electronic", song!.Genre);
        Assert.Equal("song", song.ContentType);
        Assert.Equal("Billnass", song.Featuring);
        Assert.Equal("alikiba", song.ArtistSlug);

        var track = AudiomackMatcher.ToAutoTagTrack(song);
        Assert.Equal("Electronic", Assert.Single(track!.Genres));
        Assert.Equal("Amapiano", Assert.Single(track.Styles));
        Assert.DoesNotContain(track.Styles, style => style.Contains("Tanzania", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(track.Genres, genre => genre.Contains("audiobook", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Alikiba", track.Artists);
        Assert.Contains("Billnass", track.Artists);
        Assert.Equal("https://audiomack.com/alikiba/song/fallen-angel", track.Url);
        Assert.Equal(TimeSpan.FromSeconds(261), track.Duration);
        Assert.Equal(DateTimeKind.Utc, track.ReleaseDate!.Value.Kind);
    }

    [Fact]
    public void ToAutoTagTrack_ClassifiesTypedTagsByAudiomackType()
    {
        const string json = """
        {
          "id": 9,
          "title": "Typed",
          "artist": "A",
          "genre": "afrosounds",
          "tags": [
            { "name": "amapiano", "type": "subgenre" },
            { "name": "happy", "type": "mood" },
            { "name": "electronic", "type": "genre" },
            { "name": "audiobook", "type": "genre" }
          ]
        }
        """;
        var track = AudiomackMatcher.ToAutoTagTrack(AudiomackApiClient.ParseSongResponse(json)!);
        Assert.Contains("Afrosounds", track!.Genres);
        Assert.Contains("Electronic", track.Genres);
        Assert.DoesNotContain(track.Genres, genre => genre.Contains("audiobook", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Amapiano", Assert.Single(track.Styles));
        Assert.Equal("Happy", track.Mood);
    }

    [Fact]
    public void Merge_ReplacesJunkGenreWithPageGenre()
    {
        var searchHit = new AudiomackSongCandidate(
            Id: "78139729", Title: "Fallen Angel", Artist: "Alikiba", Album: null,
            Genre: "Audiobook", Mood: null, Isrc: null, Label: null, DurationSeconds: 261,
            ArtworkUrl: null, ReleasedDate: null, Url: "https://audiomack.com/alikiba/song/fallen-angel",
            UrlSlug: "fallen-angel", ArtistSlug: "alikiba", UploaderName: "Alikiba", AlbumId: null);
        var page = AudiomackApiClient.ParseSongResponse(
            File.ReadAllText(Path.Combine(FixturesRoot(), "track-page-song.json")))!;

        var merged = AudiomackTaxonomy.Merge(searchHit, page);
        var track = AudiomackMatcher.ToAutoTagTrack(merged);

        Assert.Equal("electronic", merged.Genre);
        Assert.Equal("Electronic", Assert.Single(track!.Genres));
        Assert.Equal("Amapiano", Assert.Single(track.Styles));
        Assert.False(AudiomackTaxonomy.IsNonMusicCandidate(merged));
    }

    [Fact]
    public void FindSongObject_ParsesLiveRscSongPayload()
    {
        var payload = File.ReadAllText(Path.Combine(FixturesRoot(), "page-song-rsc-inner.txt"));
        var song = AudiomackNextDataExtractor.FindSongObject(payload);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("Fallen Angel", candidate!.Title);
        Assert.Equal("electronic", candidate.Genre);
        Assert.Equal("song", candidate.ContentType);
        Assert.Contains("Amapiano", candidate.TagDisplay);
        // The real payload identifies the artist through the uploader's own id.
        Assert.Equal("16579133", candidate.UploaderId);
        var track = AudiomackMatcher.ToAutoTagTrack(candidate);
        Assert.Equal("Electronic", Assert.Single(track!.Genres));
        Assert.Equal("Amapiano", Assert.Single(track.Styles));
        Assert.Contains("Billnass", track.Artists);
        Assert.Equal("16579133", track.ArtistId);
        // The real payload carries an ISRC the pipeline used to drop.
        Assert.Equal("ZA40S2401187", track.Isrc);
        // Label, barcode and explicitness are all in the real payload; label lives on
        // the uploader, UPC and explicit on the song row.
        Assert.Equal("Kings Music Records Label", track.Label);
        Assert.Equal("085365330924", track.Barcode);
        Assert.False(track.Explicit);
        Assert.Null(track.AlbumId);
    }

    [Fact]
    public void ExtractNextData_UnescapesRscChunkThenFindsSong()
    {
        const string html = """
        <html><body><script>self.__next_f.push([1,"{\"id\":40123,\"title\":\"Water\",\"artist\":\"Tyla\",\"genre\":\"afrobeats\",\"type\":\"song\",\"mood\":\"party\",\"tagdisplay\":\"Afrobeats\"}"])</script></body></html>
        """;
        var payload = AudiomackNextDataExtractor.ExtractNextData(html);
        var song = AudiomackNextDataExtractor.FindSongObject(payload);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("Water", candidate!.Title);
        Assert.Equal("party", candidate.Mood);
        Assert.Equal("Afrobeats", Assert.Single(AudiomackTaxonomy.CollectStyles(candidate)));
    }

    [Fact]
    public void FindSongObject_FindsTypeSongWithoutSongWrapper()
    {
        const string payload = """
        {"music":{"id":33046465,"title":"Fallen Angel","artist":"Alikiba","genre":"electronic","type":"song","usertags":"amapiano","tagdisplay":"Amapiano,Tanzania->Tanzania->Dar es Salaam","mood":"happy"}}
        """;
        var song = AudiomackNextDataExtractor.FindSongObject(payload);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("Fallen Angel", candidate!.Title);
        Assert.Equal("electronic", candidate.Genre);
        Assert.Equal("happy", candidate.Mood);
        Assert.Equal(new[] { "Amapiano" }, AudiomackTaxonomy.CollectStyles(candidate));
    }

    [Fact]
    public void FindSongObject_StillFindsLegacySongWrapper()
    {
        const string payload = """{"song":{"id":1,"title":"Legacy","genre":"afrobeats","moods":["party"]}}""";
        var song = AudiomackNextDataExtractor.FindSongObject(payload);
        Assert.NotNull(song);
        Assert.Equal("Legacy", song.Value.GetProperty("title").GetString());
    }

    [Fact]
    public void FindJsonLdSong_ReadsMusicRecordingGenre()
    {
        const string html = """
        <html><head>
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"MusicRecording","name":"Fallen Angel","genre":"electronic","byArtist":{"@type":"MusicGroup","name":"Alikiba"},"url":"https://audiomack.com/alikiba/song/fallen-angel"}</script>
        </head></html>
        """;
        var song = AudiomackNextDataExtractor.FindJsonLdSong(html);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("Fallen Angel", candidate!.Title);
        Assert.Equal("electronic", candidate.Genre);
        Assert.Equal("Alikiba", candidate.Artist);
        Assert.Equal("song", candidate.ContentType);
    }

    private static string FixturesRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Join(directory, "DeezSpoTag.Tests", "Fixtures", "Audiomack");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Audiomack fixtures directory was not found.");
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
