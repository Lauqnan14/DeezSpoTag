using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using DeezSpoTag.Web.Services.Audiomack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void ContaminationFixture_ContainsBothSongsSoItCannotDegradeToSingleSong()
    {
        var html = File.ReadAllText(Path.Combine(FixturesRoot(), "track-page-multi-song-contamination.html"));
        Assert.Contains("9394068", html);
        Assert.Contains("10781857", html);
        Assert.Contains("always-remember-us-this-way", html);
        Assert.Contains("physical", html);
        Assert.Contains("HWASA", html);
        Assert.Contains("kpop,motivation", html);
    }

    private static AudiomackSongCandidate LadyGagaCandidate => new AudiomackSongCandidate(
        Id: "9394068",
        Title: "Always Remember Us This Way",
        Artist: "Lady Gaga",
        Album: "A Star Is Born",
        Genre: "pop",
        Mood: null,
        Isrc: "USUM71700586",
        Label: null,
        DurationSeconds: 271,
        ArtworkUrl: null,
        ReleasedDate: null,
        Url: "https://audiomack.com/lady-gaga/song/always-remember-us-this-way",
        UrlSlug: "always-remember-us-this-way",
        ArtistSlug: "lady-gaga",
        UploaderName: "Lady Gaga",
        AlbumId: null);

    private static AudiomackSongCandidate PhysicalCandidate => new AudiomackSongCandidate(
        Id: "10781857",
        Title: "Physical",
        Artist: "Dua Lipa",
        Album: "Future Nostalgia",
        Genre: "dance pop",
        Mood: "energetic",
        Isrc: "GBUM71507956",
        Label: null,
        DurationSeconds: 193,
        ArtworkUrl: null,
        ReleasedDate: null,
        Url: "https://audiomack.com/dua-lipa/song/physical",
        UrlSlug: "physical",
        ArtistSlug: "dua-lipa",
        UploaderName: "Dua Lipa",
        AlbumId: null) with
    {
        Featuring = "HWASA (화사)",
        Subgenres = new[] { "disco", "dance pop" },
        UserTags = new[] { "kpop", "motivation" },
        TagDisplay = new[] { "K-Pop", "Motivation" }
    };

    [Fact]
    public void SongIdentity_RejectsConflictingIds()
    {
        var received = LadyGagaCandidate with { Id = "10781857", Url = null, UrlSlug = null, ArtistSlug = null, Isrc = "USUM71700586" };
        Assert.False(AudiomackIdNormalizer.IsSameSong(LadyGagaCandidate, received, out var reason));
        Assert.Equal("id-mismatch", reason);
    }

    [Fact]
    public void SongIdentity_RejectsConflictingPaths()
    {
        var received = LadyGagaCandidate with
        {
            Id = "9394068",
            Url = "https://audiomack.com/dua-lipa/song/physical",
            UrlSlug = "physical",
            ArtistSlug = "dua-lipa",
            Isrc = "USUM71700586"
        };
        Assert.False(AudiomackIdNormalizer.IsSameSong(LadyGagaCandidate, received, out var reason));
        Assert.Equal("path-mismatch", reason);
    }

    [Fact]
    public void SongIdentity_RejectsConflictingIsrc()
    {
        var received = LadyGagaCandidate with { Isrc = "GBUM71507956" };
        Assert.False(AudiomackIdNormalizer.IsSameSong(LadyGagaCandidate, received, out var reason));
        Assert.Equal("isrc-mismatch", reason);
    }

    [Fact]
    public void SongIdentity_RequiresMatchingIdOrPath()
    {
        // Only ISRC evidence is not enough to authorize a merge.
        var expected = new AudiomackSongCandidate(
            Id: null, Title: "Always Remember Us This Way", Artist: "Lady Gaga", Album: null,
            Genre: null, Mood: null, Isrc: "USUM71700586", Label: null, DurationSeconds: null,
            ArtworkUrl: null, ReleasedDate: null, Url: null, UrlSlug: null, ArtistSlug: null,
            UploaderName: null, AlbumId: null);
        var received = LadyGagaCandidate with { Id = null, Url = null, UrlSlug = null, ArtistSlug = null };
        Assert.False(AudiomackIdNormalizer.IsSameSong(expected, received, out var reason));
        Assert.Equal("insufficient-identity-evidence", reason);
    }

    [Fact]
    public void SongIdentity_RejectsInternallyConflictingUrlAndSlugs()
    {
        var candidate = LadyGagaCandidate with { Url = "https://audiomack.com/dua-lipa/song/physical" };
        Assert.False(AudiomackIdNormalizer.TryGetSongIdentity(candidate, out _, out _, out var reason));
        Assert.Equal("url-slug-mismatch", reason);
    }

    [Fact]
    public void SongIdentity_AcceptsVerifiedSameSong()
    {
        var overlay = LadyGagaCandidate with { TagDisplay = new[] { "Pop" }, Featuring = " " };
        Assert.True(AudiomackIdNormalizer.IsSameSong(LadyGagaCandidate, overlay, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void SongUrl_HostAndPathValidation()
    {
        // Exact host and proper subdomain boundary are accepted.
        Assert.True(AudiomackIdNormalizer.TryExtractSongSlugs("https://audiomack.com/alikiba/song/fallen-angel", out _, out _));
        Assert.True(AudiomackIdNormalizer.TryExtractSongSlugs("https://www.audiomack.com/alikiba/song/fallen-angel", out _, out _));
        // Lookalike suffix must not qualify.
        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs("https://evil-audiomack.com/alikiba/song/fallen-angel", out _, out _));
        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs("https://audiomack.com.evil.example/alikiba/song/fallen-angel", out _, out _));
        // User-info and non-HTTP schemes are rejected.
        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs("https://user:pw@audiomack.com/alikiba/song/fallen-angel", out _, out _));
        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs("ftp://audiomack.com/alikiba/song/fallen-angel", out _, out _));
        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs("javascript://audiomack.com/alikiba/song/fallen-angel", out _, out _));
        // Extra path components are rejected.
        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs("https://audiomack.com/alikiba/song/fallen-angel/extra", out _, out _));
        // Query strings, fragments and trailing slashes do not change identity.
        Assert.True(AudiomackIdNormalizer.TryExtractSongSlugs("https://audiomack.com/alikiba/song/fallen-angel/?x=1#frag", out var a1, out var s1));
        Assert.Equal("alikiba", a1);
        Assert.Equal("fallen-angel", s1);
    }

    [Fact]
    public void CanonicalSongPath_EquivalentShapesNormalizeEqual()
    {
        Assert.True(AudiomackIdNormalizer.TryGetCanonicalSongPath("https://audiomack.com/alikiba/song/fallen-angel", out var withSong));
        Assert.True(AudiomackIdNormalizer.TryGetCanonicalSongPath("https://audiomack.com/Alikiba/Fallen-Angel/", out var twoPart));
        Assert.True(AudiomackIdNormalizer.TryGetCanonicalSongPath("https://www.audiomack.com/alikiba/song/fallen%2Dangel?x=1", out var encoded));
        Assert.Equal(withSong, twoPart);
        Assert.Equal(withSong, encoded);
        Assert.Equal("alikiba/fallen-angel", withSong);
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
            Id: "33046465", Title: "Fallen Angel", Artist: "Alikiba", Album: null,
            Genre: "Audiobook", Mood: null, Isrc: null, Label: null, DurationSeconds: 261,
            ArtworkUrl: null, ReleasedDate: null, Url: "https://audiomack.com/alikiba/song/fallen-angel",
            UrlSlug: "fallen-angel", ArtistSlug: "alikiba", UploaderName: "Alikiba", AlbumId: null);
        var page = AudiomackApiClient.ParseSongResponse(
            File.ReadAllText(Path.Combine(FixturesRoot(), "track-page-song.json")))!;

        Assert.True(AudiomackTaxonomy.TryMerge(searchHit, page, out var merged, out var reason));
        Assert.Null(reason);
        var track = AudiomackMatcher.ToAutoTagTrack(merged);

        Assert.Equal("electronic", merged.Genre);
        Assert.Equal("Electronic", Assert.Single(track!.Genres));
        Assert.Equal("Amapiano", Assert.Single(track.Styles));
        Assert.False(AudiomackTaxonomy.IsNonMusicCandidate(merged));
    }

    [Fact]
    public void Merge_RejectsConflictingSearchHitIdAndChangesNothing()
    {
        // An earlier build let this merge through and unioned HWASA/K-Pop into an
        // unrelated Lady Gaga row; the guard must reject it without touching a field.
        var searchHit = new AudiomackSongCandidate(
            Id: "78139729", Title: "Fallen Angel", Artist: "Alikiba", Album: null,
            Genre: "Audiobook", Mood: null, Isrc: null, Label: null, DurationSeconds: 261,
            ArtworkUrl: null, ReleasedDate: null, Url: "https://audiomack.com/alikiba/song/fallen-angel",
            UrlSlug: "fallen-angel", ArtistSlug: "alikiba", UploaderName: "Alikiba", AlbumId: null)
        {
            Featuring = "HWASA (화사)",
            UserTags = new[] { "kpop", "motivation" },
            TagDisplay = new[] { "K-Pop", "Motivation" }
        };
        var page = AudiomackApiClient.ParseSongResponse(
            File.ReadAllText(Path.Combine(FixturesRoot(), "track-page-song.json")))!;

        Assert.False(AudiomackTaxonomy.TryMerge(searchHit, page, out var merged, out var reason));
        Assert.Equal("id-mismatch", reason);
        Assert.Equal(searchHit.Id, merged.Id);
        Assert.Equal(searchHit.Title, merged.Title);
        Assert.Equal(searchHit.Artist, merged.Artist);
        Assert.Equal(searchHit.Album, merged.Album);
        Assert.Equal(searchHit.Genre, merged.Genre);
        Assert.Equal(searchHit.Mood, merged.Mood);
        Assert.Equal(searchHit.Isrc, merged.Isrc);
        Assert.Equal(searchHit.Label, merged.Label);
        Assert.Equal(searchHit.DurationSeconds, merged.DurationSeconds);
        Assert.Equal(searchHit.ArtworkUrl, merged.ArtworkUrl);
        Assert.Equal(searchHit.ReleasedDate, merged.ReleasedDate);
        Assert.Equal(searchHit.Url, merged.Url);
        Assert.Equal(searchHit.UrlSlug, merged.UrlSlug);
        Assert.Equal(searchHit.ArtistSlug, merged.ArtistSlug);
        Assert.Equal(searchHit.UploaderName, merged.UploaderName);
        Assert.Equal(searchHit.AlbumId, merged.AlbumId);
        Assert.Equal(searchHit.Featuring, merged.Featuring);
        Assert.Equal(searchHit.UserTags, merged.UserTags);
        Assert.Equal(searchHit.TagDisplay, merged.TagDisplay);
        Assert.Equal(searchHit.Subgenres, merged.Subgenres);
        Assert.Equal(searchHit.Moods, merged.Moods);
        Assert.Equal(searchHit.Artists, merged.Artists);
        Assert.Equal(searchHit.TypedTags, merged.TypedTags);
        Assert.Equal(searchHit.UploaderId, merged.UploaderId);
        Assert.Equal(searchHit.Upc, merged.Upc);
        Assert.Equal(searchHit.Explicit, merged.Explicit);
        Assert.Equal(searchHit.ContentType, merged.ContentType);
    }

    [Fact]
    public void FindSongObject_ParsesLiveRscSongPayload()
    {
        var payload = File.ReadAllText(Path.Combine(FixturesRoot(), "page-song-rsc-inner.txt"));
        var song = AudiomackNextDataExtractor.FindSongObject(payload, new AudiomackSongCandidate(
            Id: "33046465", Title: null, Artist: null, Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: "https://audiomack.com/alikiba/song/fallen-angel",
            UrlSlug: "fallen-angel", ArtistSlug: "alikiba", UploaderName: null, AlbumId: null));
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
        var song = AudiomackNextDataExtractor.FindSongObject(payload, new AudiomackSongCandidate(
            Id: "40123", Title: null, Artist: null, Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: null, UrlSlug: null, ArtistSlug: null,
            UploaderName: null, AlbumId: null));
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
        var song = AudiomackNextDataExtractor.FindSongObject(payload, new AudiomackSongCandidate(
            Id: "33046465", Title: null, Artist: null, Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: null, UrlSlug: null, ArtistSlug: null,
            UploaderName: null, AlbumId: null));
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
        var song = AudiomackNextDataExtractor.FindSongObject(payload, new AudiomackSongCandidate(
            Id: "1", Title: null, Artist: null, Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: null, UrlSlug: null, ArtistSlug: null,
            UploaderName: null, AlbumId: null));
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
        var song = AudiomackNextDataExtractor.FindJsonLdSong(html, new AudiomackSongCandidate(
            Id: null, Title: null, Artist: null, Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: "https://audiomack.com/alikiba/song/fallen-angel",
            UrlSlug: null, ArtistSlug: null, UploaderName: null, AlbumId: null));
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("Fallen Angel", candidate!.Title);
        Assert.Equal("electronic", candidate.Genre);
        Assert.Equal("Alikiba", candidate.Artist);
        Assert.Equal("song", candidate.ContentType);
    }

    [Fact]
    public void PageExtraction_SelectsRequestedSongOverRicherRecommendation()
    {
        var html = File.ReadAllText(Path.Combine(FixturesRoot(), "track-page-multi-song-contamination.html"));
        var payload = AudiomackNextDataExtractor.ExtractNextData(html);

        var song = AudiomackNextDataExtractor.FindSongObject(payload, LadyGagaCandidate);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("9394068", candidate!.Id);
        Assert.Equal("Always Remember Us This Way", candidate.Title);
        Assert.True(string.IsNullOrWhiteSpace(candidate.Featuring));
        Assert.Empty(candidate.TagDisplay);
        Assert.Empty(candidate.UserTags);
        Assert.DoesNotContain("kpop", candidate.UserTags);
        Assert.Equal("https://audiomack.com/lady-gaga/song/always-remember-us-this-way", candidate.Url);
    }

    [Fact]
    public void PageExtraction_PicksIntendedSongWhenUnrelatedLegacyWrapperComesFirst()
    {
        // The rich unrelated song wears the legacy "song" wrapper first; the
        // older first-wrapper selection would return it. Identity-bound
        // selection must skip it and pick the verified Lady Gaga object after.
        const string physicalObject = """{"id":10781857,"title":"Physical","artist":"Dua Lipa","genre":"dance pop","mood":"energetic","subgenres":["disco"],"usertags":"kpop,motivation","tagdisplay":"K-Pop,Motivation","featuring":"HWASA (화사)","url":"https://audiomack.com/dua-lipa/song/physical","url_slug":"physical","artist_slug":"dua-lipa","type":"song"}""";
        const string ladyGagaObject = """{"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","genre":"pop","url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song"}""";
        var payload = $$"""{"song":{{physicalObject}}} {"music":{{ladyGagaObject}}}""";

        var song = AudiomackNextDataExtractor.FindSongObject(payload, LadyGagaCandidate);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("9394068", candidate!.Id);
        Assert.True(string.IsNullOrWhiteSpace(candidate.Featuring));
        Assert.Empty(candidate.TagDisplay);

        // The same page legitimately serves the other song when that is the
        // requested identity.
        var other = AudiomackNextDataExtractor.FindSongObject(payload, PhysicalCandidate);
        Assert.NotNull(other);
        var otherCandidate = AudiomackSongCandidate.FromJson(other.Value);
        Assert.Equal("10781857", otherCandidate!.Id);
        Assert.Equal("Physical", otherCandidate.Title);
    }

    [Fact]
    public void PageExtraction_RejectsAbsentExpectedSong()
    {
        const string payload = """{"music":{"id":10781857,"title":"Physical","artist":"Dua Lipa","genre":"dance pop","type":"song","url":"https://audiomack.com/dua-lipa/song/physical","url_slug":"physical","artist_slug":"dua-lipa"}}""";
        var other = LadyGagaCandidate with { Id = "12345", Url = "https://audiomack.com/lady-gaga/song/other", UrlSlug = "other" };
        Assert.Null(AudiomackNextDataExtractor.FindSongObject(payload, other));
    }

    [Fact]
    public void PageExtraction_DeduplicatesSameSongAcrossRepresentations()
    {
        const string wrapped = """{"song":{"id":33046465,"title":"Fallen Angel","artist":"Alikiba","type":"song","genre":"electronic","url":"https://audiomack.com/alikiba/song/fallen-angel","url_slug":"fallen-angel","artist_slug":"alikiba"}}""";
        const string typed = """{"music":{"id":33046465,"title":"Fallen Angel","artist":"Alikiba","type":"song","mood":"happy","url":"https://audiomack.com/alikiba/song/fallen-angel","url_slug":"fallen-angel","artist_slug":"alikiba"}}""";
        var expected = new AudiomackSongCandidate(
            Id: "33046465", Title: "Fallen Angel", Artist: "Alikiba", Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: "https://audiomack.com/alikiba/song/fallen-angel",
            UrlSlug: "fallen-angel", ArtistSlug: "alikiba", UploaderName: null, AlbumId: null);

        var song = AudiomackNextDataExtractor.FindSongObject(wrapped + typed, expected);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("33046465", candidate!.Id);
        // The verified duplicate may contribute the fields the other representation lacks.
        Assert.Equal("electronic", candidate.Genre);
        Assert.Equal("happy", candidate.Mood);
    }

    [Fact]
    public void PageExtraction_RejectsAmbiguousConflictingDuplicateIdentity()
    {
        // Both objects match the expected path, but they disagree on the id —
        // the overlay is ambiguous and must be rejected wholesale.
        const string first = """{"music":{"id":1,"title":"Fallen Angel","artist":"Alikiba","type":"song","url":"https://audiomack.com/alikiba/song/fallen-angel","url_slug":"fallen-angel","artist_slug":"alikiba"}}""";
        const string second = """{"music":{"id":2,"title":"Fallen Angel","artist":"Alikiba","type":"song","url":"https://audiomack.com/alikiba/song/fallen-angel","url_slug":"fallen-angel","artist_slug":"alikiba"}}""";
        var expected = new AudiomackSongCandidate(
            Id: null, Title: "Fallen Angel", Artist: "Alikiba", Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: "https://audiomack.com/alikiba/song/fallen-angel",
            UrlSlug: null, ArtistSlug: null, UploaderName: null, AlbumId: null);

        Assert.Null(AudiomackNextDataExtractor.FindSongObject(first + second, expected));
    }

    [Fact]
    public void FindJsonLdSong_CorrectCanonicalUrlWithoutIdIsAccepted()
    {
        const string html = """
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"MusicRecording","name":"Always Remember Us This Way","byArtist":{"@type":"MusicGroup","name":"Lady Gaga"},"url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","isrcCode":"USUM71700586"}</script>
        """;
        var expected = LadyGagaCandidate with { Id = null, Url = "https://audiomack.com/lady-gaga/song/always-remember-us-this-way" };
        var song = AudiomackNextDataExtractor.FindJsonLdSong(html, expected);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("Always Remember Us This Way", candidate!.Title);
    }

    [Fact]
    public void FindJsonLdSong_WrongOrAbsentUrlIsRejected()
    {
        const string html = """
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"MusicRecording","name":"Physical","byArtist":{"@type":"MusicGroup","name":"Dua Lipa"},"url":"https://audiomack.com/dua-lipa/song/physical"}</script>
        """;
        Assert.Null(AudiomackNextDataExtractor.FindJsonLdSong(html, LadyGagaCandidate));
        // No URL at all on the JSON-LD object: an id-less object cannot be accepted.
        const string noUrl = """
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"MusicRecording","name":"Always Remember Us This Way","byArtist":{"@type":"MusicGroup","name":"Lady Gaga"}}</script>
        """;
        Assert.Null(AudiomackNextDataExtractor.FindJsonLdSong(noUrl, LadyGagaCandidate with { Id = null }));
    }

    [Fact]
    public void FindJsonLdSong_LaterVerifiedMusicRecordingWinsOverEarlierUnrelatedOne()
    {
        const string html = """
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"MusicRecording","name":"Physical","byArtist":{"@type":"MusicGroup","name":"Dua Lipa"},"url":"https://audiomack.com/dua-lipa/song/physical"}</script>
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"MusicRecording","name":"Always Remember Us This Way","byArtist":{"@type":"MusicGroup","name":"Lady Gaga"},"url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way"}</script>
        """;
        var song = AudiomackNextDataExtractor.FindJsonLdSong(html, LadyGagaCandidate);
        Assert.NotNull(song);
        var candidate = AudiomackSongCandidate.FromJson(song.Value);
        Assert.Equal("Always Remember Us This Way", candidate!.Title);
    }

    [Fact]
    public void TryMerge_RejectsInvalidOverlayAndLeavesEveryFieldUnchanged()
    {
        var current = LadyGagaCandidate;
        Assert.False(AudiomackTaxonomy.TryMerge(current, PhysicalCandidate, out var merged, out var reason));
        Assert.Equal("id-mismatch", reason);
        Assert.Equal(current.Id, merged.Id);
        Assert.Equal(current.Title, merged.Title);
        Assert.Equal(current.Artist, merged.Artist);
        Assert.Equal(current.Album, merged.Album);
        Assert.Equal(current.Genre, merged.Genre);
        Assert.Equal(current.Mood, merged.Mood);
        Assert.Equal(current.Isrc, merged.Isrc);
        Assert.Equal(current.Label, merged.Label);
        Assert.Equal(current.DurationSeconds, merged.DurationSeconds);
        Assert.Equal(current.ArtworkUrl, merged.ArtworkUrl);
        Assert.Equal(current.ReleasedDate, merged.ReleasedDate);
        Assert.Equal(current.Url, merged.Url);
        Assert.Equal(current.UrlSlug, merged.UrlSlug);
        Assert.Equal(current.ArtistSlug, merged.ArtistSlug);
        Assert.Equal(current.UploaderName, merged.UploaderName);
        Assert.Equal(current.AlbumId, merged.AlbumId);
        Assert.Equal(current.Subgenres, merged.Subgenres);
        Assert.Equal(current.Moods, merged.Moods);
        Assert.Equal(current.UserTags, merged.UserTags);
        Assert.Equal(current.TagDisplay, merged.TagDisplay);
        Assert.Equal(current.Artists, merged.Artists);
        Assert.Equal(current.TypedTags, merged.TypedTags);
    }

    [Fact]
    public void TryMerge_ValidSamePathOverlaySuppliesMissingFields()
    {
        var current = new AudiomackSongCandidate(
            Id: "9394068", Title: "Always Remember Us This Way", Artist: "Lady Gaga", Album: null,
            Genre: "Audiobook", Mood: null, Isrc: null, Label: null, DurationSeconds: null,
            ArtworkUrl: null, ReleasedDate: null,
            Url: "https://audiomack.com/lady-gaga/song/always-remember-us-this-way",
            UrlSlug: "always-remember-us-this-way", ArtistSlug: "lady-gaga", UploaderName: null, AlbumId: null);
        var richer = new AudiomackSongCandidate(
            Id: null, Title: null, Artist: null, Album: "A Star Is Born",
            Genre: "pop", Mood: "emotional", Isrc: null, Label: "Interscope", DurationSeconds: 271,
            ArtworkUrl: null, ReleasedDate: null,
            Url: "https://audiomack.com/lady-gaga/song/always-remember-us-this-way",
            UrlSlug: "always-remember-us-this-way", ArtistSlug: "lady-gaga", UploaderName: null, AlbumId: null)
        {
            Subgenres = new[] { "country pop" },
            Moods = new[] { "emotional" }
        };

        Assert.True(AudiomackTaxonomy.TryMerge(current, richer, out var merged, out var reason));
        Assert.Null(reason);
        Assert.Equal("9394068", merged.Id);
        Assert.Equal("pop", merged.Genre);
        Assert.Equal("A Star Is Born", merged.Album);
        Assert.Equal("Interscope", merged.Label);
        Assert.Equal(271, merged.DurationSeconds);
        Assert.Equal(new[] { "country pop" }, merged.Subgenres);
        Assert.Equal(new[] { "emotional" }, merged.Moods);
    }

    [Fact]
    public void ExtractNextData_UnterminatedChunkTerminates()
    {
        // One well-formed chunk, then a truncated push: extraction must deliver
        // the valid chunk and stop instead of spinning on the unterminated one.
        const string html = """
        <script>self.__next_f.push([1,"{\"id\":40123,\"title\":\"Water\"}"])</script>
        <script>self.__next_f.push([1,"{\"id\":1,\"title\":\"Broken chunk with no closing array
        """;
        var payload = AudiomackNextDataExtractor.ExtractNextData(html);
        Assert.Contains("\"id\":40123", payload);
        Assert.DoesNotContain("Broken chunk", payload);
    }

    [Fact]
    public void ExtractNextData_SkipsMalformedChunksAndAdvances()
    {
        // Not an array after the marker, escaped braces/quotes in strings, and a
        // JSON-shaped array that fails to deserialize: every shape must either
        // yield only its string parts or be skipped, and the scan must advance.
        const string html = """
        <script>self.__next_f.push(not-an-array)</script>
        <script>self.__next_f.push([1,"{\"id\":2,\"title\":\"a } b \\\"quoted\\\"\"}"])</script>
        <script>self.__next_f.push([1,"a",])</script>
        """;
        var payload = AudiomackNextDataExtractor.ExtractNextData(html);
        Assert.Contains("\"id\":2", payload);
        Assert.DoesNotContain("not-an-array", payload);
        Assert.DoesNotContain("\"a\"", payload);
    }

    // ---------------------------------------------------------------------
    // Audiomack matcher evidence chain over deterministic fake HTTP.
    // ---------------------------------------------------------------------

    private const string LadyGagaSearchHitJson = """
    {"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","album":"A Star Is Born","genre":"pop","isrc":"USUM71700586","duration":271,"url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song","uploader":{"id":"4482381","name":"Lady Gaga","url_slug":"lady-gaga"}}
    """;

    private const string LadyGagaDetailJson = """
    {"data":{"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","album":"A Star Is Born","genre":"pop","isrc":"USUM71700586","label":"Interscope","duration":271,"upc":"00602508510452","explicit":"no","url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song","uploader":{"id":"4482381","name":"Lady Gaga","url_slug":"lady-gaga","label":"Interscope"}}}
    """;

    private const string PhysicalSearchHitJson = """
    {"id":10781857,"title":"Physical","artist":"Dua Lipa","album":"Future Nostalgia","genre":"dance pop","isrc":"GBUM71507956","duration":193,"featuring":"HWASA (화사)","subgenres":["disco"],"moods":["energetic"],"usertags":"kpop,motivation","tagdisplay":"K-Pop,Motivation","url":"https://audiomack.com/dua-lipa/song/physical","url_slug":"physical","artist_slug":"dua-lipa","type":"song","uploader":{"id":"1097155","name":"Dua Lipa","url_slug":"dua-lipa"}}
    """;

    private static AutoTagAudioInfo LadyGagaSource => new()
    {
        Title = "Always Remember Us This Way",
        Artist = "Lady Gaga",
        Artists = { "Lady Gaga" },
        Album = "A Star Is Born",
        DurationSeconds = 271,
        Isrc = "USUM71700586"
    };

    private static AutoTagAudioInfo LadyGagaSourceWith(params (string Key, string[] Values)[] tags)
    {
        var info = LadyGagaSource;
        foreach (var (key, values) in tags)
        {
            // Multiple values may arrive under one key (or across keys sharing it).
            info.Tags[key] = info.Tags.TryGetValue(key, out var existing)
                ? existing.Concat(values).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : values.ToList();
        }

        return info;
    }

    private static (AudiomackMatcher Matcher, StubAudiomackBackend Backend, CapturingLogger<AudiomackMatcher> Logger)
        CreateMatcher(StubAudiomackBackend backend)
    {
        var apiClient = new AudiomackApiClient(
            backend,
            new AudiomackWebCredentialsProvider(backend, NullLogger<AudiomackWebCredentialsProvider>.Instance),
            NullLogger<AudiomackApiClient>.Instance);
        var logger = new CapturingLogger<AudiomackMatcher>();
        return (new AudiomackMatcher(apiClient, logger), backend, logger);
    }

    [Fact]
    public async Task TextRoute_SelectsRequestedRecordingAndIgnoresRicherRecommendation()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch("always remember us this way", LadyGagaSearchHitJson, PhysicalSearchHitJson);
        backend.AddSong("lady-gaga/always-remember-us-this-way", LadyGagaDetailJson);
        backend.AddPage(
            "lady-gaga/always-remember-us-this-way",
            File.ReadAllText(Path.Combine(FixturesRoot(), "track-page-multi-song-contamination.html")));
        var (matcher, _, logger) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("text", result!.MatchStrategy);
        Assert.Equal("Always Remember Us This Way", result.Track.Title);
        Assert.Equal("9394068", result.Track.TrackId);
        Assert.Equal("Lady Gaga", Assert.Single(result.Track.Artists));
        Assert.Equal("https://audiomack.com/lady-gaga/song/always-remember-us-this-way", result.Track.Url);
        Assert.Equal("Interscope", result.Track.Label);
        Assert.DoesNotContain(result.Track.Artists, artist => artist.Contains("HWASA", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Track.Styles, style => style.Contains("K-Pop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Track.Styles, style => style.Contains("Motivation", StringComparison.OrdinalIgnoreCase));
        // Diagnostics never carry signing material.
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("oauth", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef", message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task TextRoute_DetailResponseConflictRejectsCandidateAndStopsRequests()
    {
        // The search hit passes every gate, but its detail response describes a
        // different song: the candidate dies and no page request follows.
        var backend = new StubAudiomackBackend();
        backend.AddSearch("always remember us this way", LadyGagaSearchHitJson);
        backend.AddSong("lady-gaga/always-remember-us-this-way", PhysicalSearchHitJson);
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, backend.SearchRequests);
        Assert.Equal(1, backend.SongRequests);
        Assert.Equal(0, backend.PageRequests);
    }

    [Fact]
    public async Task TextRoute_UnverifiedPageObjectIsDiscardedWithoutLosingTheMatch()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch("always remember us this way", LadyGagaSearchHitJson);
        backend.AddSong("lady-gaga/always-remember-us-this-way", LadyGagaDetailJson);
        backend.AddPage("lady-gaga/always-remember-us-this-way", RscPage(PhysicalSearchHitJson));
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("9394068", result!.Track.TrackId);
        Assert.Equal("Lady Gaga", Assert.Single(result.Track.Artists));
        Assert.Empty(result.Track.Styles);
    }

    [Fact]
    public async Task TaggedUrlRoute_ReturnsVerifiedIdentityMatch()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSong("lady-gaga/always-remember-us-this-way", LadyGagaDetailJson);
        var (matcher, _, _) = CreateMatcher(backend);
        var info = LadyGagaSourceWith(("AUDIOMACK_URL", new[] { "https://audiomack.com/lady-gaga/song/always-remember-us-this-way" }));

        var result = await matcher.MatchAsync(
            info, new AutoTagMatchingConfig(), new AudiomackMatchConfig { MatchById = true }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("id", result!.MatchStrategy);
        Assert.Equal(1.0d, result.Accuracy);
        Assert.Equal("9394068", result.Track.TrackId);
    }

    [Fact]
    public async Task TaggedIdRoute_ReturnsVerifiedIdentityMatch()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch("9394068", LadyGagaSearchHitJson);
        backend.AddSong("lady-gaga/always-remember-us-this-way", LadyGagaDetailJson);
        var (matcher, _, _) = CreateMatcher(backend);
        var info = LadyGagaSourceWith(("AUDIOMACK_TRACK_ID", new[] { "9394068" }));

        var result = await matcher.MatchAsync(
            info, new AutoTagMatchingConfig(), new AudiomackMatchConfig { MatchById = true }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("id", result!.MatchStrategy);
        Assert.Equal(1.0d, result.Accuracy);
        Assert.Equal("9394068", result.Track.TrackId);
    }

    [Fact]
    public async Task IdLookup_RejectsContradictoryTaggedIdAndUrl()
    {
        // An earlier bad run left a Lady Gaga id and an unrelated Dua Lipa URL on
        // the same file: neither may win, and text search must not paper over it.
        var backend = new StubAudiomackBackend();
        // The tagged id resolves to the Lady Gaga row; the tagged URL points at
        // Dua Lipa's song. Both cannot describe the user's recording.
        backend.AddSong("dua-lipa/physical", PhysicalSearchHitJson);
        var (matcher, _, _) = CreateMatcher(backend);
        var info = LadyGagaSourceWith(
            ("AUDIOMACK_TRACK_ID", new[] { "9394068" }),
            ("AUDIOMACK_URL", new[] { "https://audiomack.com/dua-lipa/song/physical" }));

        var result = await matcher.MatchAsync(
            info, new AutoTagMatchingConfig(), new AudiomackMatchConfig { MatchById = true }, CancellationToken.None);

        Assert.Null(result);
        // A contradiction is decided on the tagged identity alone: no text search
        // may run to paper over it.
        Assert.Equal(0, backend.SearchRequests);
    }

    [Fact]
    public async Task IdLookup_RejectsMultipleContradictoryProviderTags()
    {
        var twoIds = new StubAudiomackBackend();
        var (twoIdsMatcher, _, _) = CreateMatcher(twoIds);
        var conflictingIds = LadyGagaSourceWith(
            ("AUDIOMACK_TRACK_ID", new[] { "9394068" }),
            ("AUDIOMACK_ID", new[] { "10781857" }));

        Assert.Null(await twoIdsMatcher.MatchAsync(
            conflictingIds, new AutoTagMatchingConfig(), new AudiomackMatchConfig { MatchById = true }, CancellationToken.None));
        Assert.Equal(0, twoIds.TotalRequests);

        var twoUrls = new StubAudiomackBackend();
        var (twoUrlsMatcher, _, _) = CreateMatcher(twoUrls);
        var conflictingUrls = LadyGagaSourceWith(
            ("AUDIOMACK_URL", new[] { "https://audiomack.com/lady-gaga/song/always-remember-us-this-way" }),
            ("AUDIOMACK_URL", new[] { "https://audiomack.com/dua-lipa/song/physical" }));

        Assert.Null(await twoUrlsMatcher.MatchAsync(
            conflictingUrls, new AutoTagMatchingConfig(), new AudiomackMatchConfig { MatchById = true }, CancellationToken.None));
        Assert.Equal(0, twoUrls.TotalRequests);
    }

    [Fact]
    public async Task IdLookup_MalformedGenericUrlIsIgnoredAndSearchStillRuns()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch("always remember us this way", LadyGagaSearchHitJson);
        backend.AddSong("lady-gaga/always-remember-us-this-way", LadyGagaDetailJson);
        var (matcher, _, _) = CreateMatcher(backend);
        var info = LadyGagaSourceWith(
            ("URL", new[] { "https://open.spotify.com/track/not-audiomack" }),
            ("AUDIOMACK_URL", new[] { "definitely not a url" }));

        var result = await matcher.MatchAsync(
            info, new AutoTagMatchingConfig(), new AudiomackMatchConfig { MatchById = true }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("text", result!.MatchStrategy);
        Assert.Equal("9394068", result.Track.TrackId);
    }

    [Fact]
    public async Task IdLookup_UnavailableCoherentIdPreservesConfiguredSearchBehaviour()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch("999999", LadyGagaSearchHitJson);
        backend.AddSearch("always remember us this way", LadyGagaSearchHitJson);
        backend.AddSong("lady-gaga/always-remember-us-this-way", LadyGagaDetailJson);
        var (matcher, _, _) = CreateMatcher(backend);
        var info = LadyGagaSourceWith(("AUDIOMACK_TRACK_ID", new[] { "999999" }));

        var result = await matcher.MatchAsync(
            info, new AutoTagMatchingConfig(), new AudiomackMatchConfig { MatchById = true }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("text", result!.MatchStrategy);
        Assert.Equal("9394068", result.Track.TrackId);
    }

    [Fact]
    public async Task Ranking_MatchingIsrcOutranksWeakerEligibleTextMatch()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch(
            "always remember us this way",
            RankingCandidateJson(id: "111", slug: "tonight", album: "Deluxe", isrc: null),
            RankingCandidateJson(id: "222", slug: "forever", album: "Deluxe", isrc: "USUM71700586"));
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("222", result!.Track.TrackId);
    }

    [Fact]
    public async Task Ranking_KnownMatchingAlbumResolvesOtherwiseEqualEvidence()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch(
            "always remember us this way",
            RankingCandidateJson(id: "111", slug: "tonight", album: "Deluxe Edition", isrc: null),
            RankingCandidateJson(id: "222", slug: "forever", album: "A Star Is Born", isrc: null));
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("222", result!.Track.TrackId);
    }

    [Fact]
    public async Task Ranking_AlbumDisagreementAloneDoesNotRejectAValidRecording()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch(
            "always remember us this way",
            RankingCandidateJson(id: "222", slug: "forever", album: "A Star Is Born Deluxe", isrc: "USUM71700586"));
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("A Star Is Born Deluxe", result!.Track.Album);
    }

    [Fact]
    public async Task Ranking_DefaultAmbiguityBetweenDifferentRecordingsProducesNoMatch()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch(
            "always remember us this way",
            RankingCandidateJson(id: "111", slug: "tonight", album: "Deluxe", isrc: null, released: "2019-01-01"),
            RankingCandidateJson(id: "222", slug: "forever", album: "Deluxe Two", isrc: null, released: "2021-01-01"));
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(2, backend.SongRequests);
        Assert.Equal(2, backend.PageRequests);
    }

    [Fact]
    public async Task Ranking_ExplicitOldestPreferenceResolvesReleaseTie()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch(
            "always remember us this way",
            RankingCandidateJson(id: "111", slug: "tonight", album: "Deluxe", isrc: null, released: "2019-01-01"),
            RankingCandidateJson(id: "222", slug: "forever", album: "Deluxe Two", isrc: null, released: "2021-01-01"));
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource,
            new AutoTagMatchingConfig { MultipleMatches = MultipleMatchesSort.Oldest },
            new AudiomackMatchConfig(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("111", result!.Track.TrackId);
    }

    [Fact]
    public async Task Ranking_DuplicateResultsHydrateEachIdentityOnlyOnce()
    {
        var duplicate = RankingCandidateJson(id: "111", slug: "tonight", album: "Deluxe", isrc: null);
        var backend = new StubAudiomackBackend();
        backend.AddSearch("always remember us this way", duplicate, duplicate, duplicate);
        var (matcher, _, _) = CreateMatcher(backend);

        var result = await matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("111", result!.Track.TrackId);
        Assert.Equal(1, backend.SearchRequests);
        Assert.Equal(1, backend.SongRequests);
    }

    [Fact]
    public async Task MatchAsync_PropagatesCancellationDuringBoundedSelection()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch("always remember us this way", LadyGagaSearchHitJson);
        var (matcher, _, _) = CreateMatcher(backend);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => matcher.MatchAsync(
            LadyGagaSource, new AutoTagMatchingConfig(), new AudiomackMatchConfig(), cts.Token));
    }

    [Fact]
    public void EvaluateCandidate_RejectsConflictingIsrc()
    {
        var candidate = LadyGagaCandidate with { Isrc = "GBUM71507956" };
        Assert.False(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, candidate, new AutoTagMatchingConfig(), out var reason));
        Assert.Equal("isrc-mismatch", reason);
    }

    [Fact]
    public void EvaluateCandidate_AllowsMissingIsrc()
    {
        var info = LadyGagaSource;
        info.Isrc = null;
        var candidate = LadyGagaCandidate with { Isrc = null };
        Assert.Null(candidate.Isrc);
        Assert.True(AudiomackMatcher.EvaluateCandidate(info, candidate, new AutoTagMatchingConfig(), out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void EvaluateCandidate_RejectsVersionDrift()
    {
        var candidate = LadyGagaCandidate with { Title = "Always Remember Us This Way (Live)" };
        Assert.False(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, candidate, new AutoTagMatchingConfig(), out var reason));
        Assert.Equal("version-drift", reason);
    }

    [Fact]
    public void EvaluateCandidate_DurationBoundaryIsHonouredWhenDurationMatchingIsEnabled()
    {
        var config = new AutoTagMatchingConfig { MatchDuration = true, MaxDurationDifferenceSeconds = 5 };
        var atBoundary = LadyGagaCandidate with { DurationSeconds = 276 };
        var overBoundary = LadyGagaCandidate with { DurationSeconds = 277 };

        Assert.True(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, atBoundary, config, out _));
        Assert.False(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, overBoundary, config, out var reason));
        Assert.Equal("duration-mismatch", reason);
        // Disabled duration matching never rejects on duration.
        Assert.True(AudiomackMatcher.EvaluateCandidate(
            LadyGagaSource, overBoundary, new AutoTagMatchingConfig(), out _));
    }

    [Fact]
    public void EvaluateCandidate_MissingDurationIsNotInventedEvidence()
    {
        var config = new AutoTagMatchingConfig { MatchDuration = true, MaxDurationDifferenceSeconds = 1 };
        var candidate = LadyGagaCandidate with { DurationSeconds = null };
        Assert.True(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, candidate, config, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void EvaluateCandidate_RejectsMissingAndUploaderOnlyArtist()
    {
        var noArtist = LadyGagaCandidate with { Artist = null };
        Assert.False(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, noArtist, new AutoTagMatchingConfig(), out var reason));
        Assert.Equal("missing-recording-artist", reason);

        // The uploader account is not recording evidence.
        var uploaderOnly = new AudiomackSongCandidate(
            Id: "9394068", Title: "Always Remember Us This Way", Artist: null, Album: null,
            Genre: "pop", Mood: null, Isrc: "USUM71700586", Label: null, DurationSeconds: 271,
            ArtworkUrl: null, ReleasedDate: null,
            Url: "https://audiomack.com/lady-gaga/song/always-remember-us-this-way",
            UrlSlug: "always-remember-us-this-way", ArtistSlug: "lady-gaga",
            UploaderName: "Lady Gaga Fan Page", AlbumId: null);
        Assert.False(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, uploaderOnly, new AutoTagMatchingConfig(), out reason));
        Assert.Equal("missing-recording-artist", reason);
        Assert.Empty(AudiomackMatcher.ToAutoTagTrack(uploaderOnly)!.Artists);
    }

    [Fact]
    public void EvaluateCandidate_RejectsConflictingExplicitGuestCredits()
    {
        var info = LadyGagaSource;
        info.Title = "Always Remember Us This Way (feat. HWASA (화사))";
        var candidate = LadyGagaCandidate with { Featuring = "Another Guest" };

        Assert.False(AudiomackMatcher.EvaluateCandidate(info, candidate, new AutoTagMatchingConfig(), out var reason));
        Assert.Equal("conflicting-guest-credits", reason);
    }

    [Fact]
    public void EvaluateCandidate_EquivalentFeatFormattingAndNonLatinGuestsAreNotAConflict()
    {
        var info = LadyGagaSource;
        info.Title = "Always Remember Us This Way (ft. HWASA (화사))";
        var candidate = LadyGagaCandidate with { Featuring = "HWASA (화사)" };

        Assert.True(AudiomackMatcher.EvaluateCandidate(info, candidate, new AutoTagMatchingConfig(), out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void EvaluateCandidate_IntroducedGuestRequiresMatchingValidIsrc()
    {
        var info = LadyGagaSource;
        info.Title = "Always Remember Us This Way";
        info.Isrc = null;
        var guestCandidate = LadyGagaCandidate with { Featuring = "HWASA (화사)" };

        // No source ISRC means no ISRC evidence at all, so a newly introduced
        // guest cannot be verified and the candidate is rejected.
        Assert.False(AudiomackMatcher.EvaluateCandidate(info, guestCandidate, new AutoTagMatchingConfig(), out var reason));
        Assert.Equal("introduced-guest-requires-isrc", reason);

        // A differing candidate ISRC is not evidence either.
        Assert.False(AudiomackMatcher.EvaluateCandidate(
            LadyGagaSource, guestCandidate with { Isrc = "GBUM71507956" }, new AutoTagMatchingConfig(), out _));

        // Matching valid ISRC evidence authorizes the introduced guest.
        Assert.True(AudiomackMatcher.EvaluateCandidate(
            LadyGagaSource, guestCandidate with { Isrc = "USUM71700586" }, new AutoTagMatchingConfig(), out reason));
        Assert.Null(reason);
    }

    [Fact]
    public void RecordingIntegrity_RejectsForeignArtistWithMatchingIdentifiers()
    {
        var song = AudiomackApiClient.ParseSongResponse(RankingCandidateJson("111", "tonight", "Album", null))!;
        Assert.False(AudiomackTaxonomy.TryMerge(song, song with { Artist = "Other Artist" }, out var merged, out _));
        Assert.Equal(song, merged);
    }

    [Fact]
    public void RecordingIntegrity_OriginalUploaderAndEncodedPathAreHandled()
    {
        Assert.True(AudiomackIdNormalizer.TryExtractSongSlugs("https://audiomack.com/lixn_lk/song/always-remember-us-this-way-1", out var artist, out _));
        Assert.Equal("lixn_lk", artist);
        Assert.False(AudiomackIdNormalizer.TryExtractSongSlugs("https://audiomack.com/uploader/song/other%2Fphysical-2", out _, out _));
    }

    [Fact]
    public void RecordingIntegrity_IdGateRejectsUnrelatedTitleAndArtist()
    {
        var song = AudiomackApiClient.ParseSongResponse(RankingCandidateJson("111", "tonight", "Album", null))!;
        Assert.False(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, song with { Title = "Physical", Artist = "Dua Lipa" }, new(), out _));
        Assert.False(AudiomackMatcher.EvaluateCandidate(LadyGagaSource, song with { Artist = "Other Artist" }, new(), out _));
    }

    [Fact]
    public async Task RecordingIntegrity_ReranksEvidenceDiscoveredDuringHydration()
    {
        var backend = new StubAudiomackBackend();
        backend.AddSearch("always remember us this way",
            RankingCandidateJson("111", "tonight", "Deluxe", null),
            RankingCandidateJson("222", "forever", "Deluxe", null));
        backend.AddSong("lady-gaga/forever-version", RankingCandidateJson("222", "forever", "Deluxe", "USUM71700586"));
        var (matcher, _, _) = CreateMatcher(backend);
        var result = await matcher.MatchAsync(LadyGagaSource, new(), new(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("222", result!.Track.TrackId);
    }

    private static string RankingCandidateJson(
        string id,
        string slug,
        string album,
        string? isrc,
        string? released = null)
    {
        var suffix = slug is "tonight" ? "Tonight" : "Forever";
        var isrcField = isrc is null ? string.Empty : ",\"isrc\":\"" + isrc + "\"";
        var releasedField = released is null ? string.Empty : ",\"released_date\":\"" + released + "\"";
        return "{\"id\":\"" + id
            + "\",\"title\":\"Always Remember Us This Way " + suffix
            + "\",\"artist\":\"Lady Gaga\",\"album\":\"" + album
            + "\",\"genre\":\"pop\",\"duration\":271"
            + isrcField + releasedField
            + ",\"url\":\"https://audiomack.com/lady-gaga/song/" + slug
            + "-version\",\"url_slug\":\"" + slug
            + "-version\",\"artist_slug\":\"lady-gaga\",\"type\":\"song\",\"uploader\":{\"id\":\"4482381\",\"name\":\"Lady Gaga\",\"url_slug\":\"lady-gaga\"}}";
    }

    private static string RscPage(params string[] jsonObjects)
    {
        var builder = new StringBuilder("<html><body>");
        foreach (var json in jsonObjects)
        {
            builder.Append("<script>self.__next_f.push([1,")
                .Append(System.Text.Json.JsonSerializer.Serialize(json))
                .Append("])</script>");
        }

        return builder.Append("</body></html>").ToString();
    }

    private sealed class StubAudiomackBackend : IHttpClientFactory
    {
        private const string DiscoveryHtml = """
        <html><body><script>window.env={API_PUBLIC_API_URL:"https://api.audiomack.com/v1/",API_CONSUMER_KEY:"audiomack-web",API_CONSUMER_SECRET:"0123456789abcdef0123456789abcdef"};</script></body></html>
        """;

        private readonly List<(string Key, string Json)> _searches = new();
        private readonly Dictionary<string, string> _songs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pages = new(StringComparer.Ordinal);

        public int DiscoveryRequests { get; private set; }

        public int SearchRequests { get; private set; }

        public int SongRequests { get; private set; }

        public int PageRequests { get; private set; }

        public int TotalRequests => DiscoveryRequests + SearchRequests + SongRequests + PageRequests;

        public void AddSearch(string queryKey, params string[] resultObjects)
            => _searches.Add((queryKey, $"{{\"results\":[{string.Join(",", resultObjects)}]}}"));

        public void AddSong(string key, string json) => _songs[key] = json;

        public void AddPage(string key, string html) => _pages[key] = html;

        public HttpClient CreateClient(string name) => new(new RoutingHandler(this));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath.Trim('/');
            if (uri.Host.Equals("audiomack.com", StringComparison.OrdinalIgnoreCase))
            {
                if (path.Equals("search", StringComparison.OrdinalIgnoreCase))
                {
                    DiscoveryRequests++;
                    return Text(DiscoveryHtml, "text/html");
                }

                PageRequests++;
                var key = SlugKey(path);
                return _pages.TryGetValue(key, out var page)
                    ? Text(page, "text/html")
                    : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }

            if (uri.Host.Equals("api.audiomack.com", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2 && segments[1].Equals("search", StringComparison.OrdinalIgnoreCase))
                {
                    SearchRequests++;
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("q") ?? string.Empty;
                    foreach (var (key, json) in _searches)
                    {
                        if (query.Contains(key, StringComparison.OrdinalIgnoreCase))
                        {
                            return Text(json, "application/json");
                        }
                    }

                    return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                }

                if (segments.Length >= 5 && segments[1].Equals("music", StringComparison.OrdinalIgnoreCase))
                {
                    SongRequests++;
                    var songKey = $"{segments[2]}/{segments[4]}";
                    return _songs.TryGetValue(songKey, out var json)
                        ? Text(json, "application/json")
                        : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }

        private static string SlugKey(string path)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 3 && segments[1].Equals("song", StringComparison.OrdinalIgnoreCase)
                ? $"{segments[0]}/{segments[2]}"
                : path;
        }

        private static HttpResponseMessage Text(string body, string mediaType)
            => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };

        private sealed class RoutingHandler : HttpMessageHandler
        {
            private readonly StubAudiomackBackend _owner;

            public RoutingHandler(StubAudiomackBackend owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                // A real transport observes the token; the stub must too.
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_owner.Respond(request));
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + " " + state);
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
