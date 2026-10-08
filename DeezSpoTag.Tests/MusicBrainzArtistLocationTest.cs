using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.ArtistLocation;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers MusicBrainz as the default artist-location source.
/// </summary>
/// <remarks>
/// Two rules carry the weight and are tested hardest:
/// <list type="bullet">
/// <item>an MBID is identity, so it resolves without a cross-check;</item>
/// <item>a name search is only a match with album overlap — none means no match,
/// and there is deliberately no name-only escape.</item>
/// </list>
///
/// A real SQLite library is used rather than a stub, because the identity routes
/// read it and the SQL is part of what is being verified.
/// </remarks>
public sealed class MusicBrainzArtistLocationTest : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"musicbrainz-location-{Guid.NewGuid():N}.db");

    private const long ArtistId = 7401;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    // ── Area mapping ──────────────────────────────────────────────────────

    [Fact]
    public void CityLevelBeginAreaBecomesTheCity()
    {
        var location = MusicBrainzArtistLocationService.ToLocation(new MusicBrainzArtist
        {
            Id = "mbid-1",
            Name = "Alikiba",
            CountryCode = "TZ",
            BeginArea = new Area { Name = "Dar es Salaam", Type = "City" },
            Area = new Area { Name = "Tanzania", Type = "Country" }
        });

        Assert.NotNull(location);
        Assert.Equal("Dar es Salaam", location!.City);
        Assert.Equal("Tanzania", location.Country);
        // Straight from MusicBrainz's own ISO value, so no lookup table is involved.
        Assert.Equal("TZ", location.CountryCode);
        Assert.Equal("musicbrainz", location.Source);
    }

    /// <summary>
    /// A country-level begin area is not a city. Putting "Tanzania" in the city
    /// field would be wrong, so it is left out rather than guessed at.
    /// </summary>
    [Fact]
    public void CountryLevelBeginAreaIsNotReportedAsACity()
    {
        var location = MusicBrainzArtistLocationService.ToLocation(new MusicBrainzArtist
        {
            Id = "mbid-1",
            Name = "Someone",
            CountryCode = "US",
            BeginArea = new Area { Name = "United States", Type = "Country" }
        });

        Assert.NotNull(location);
        Assert.Null(location!.City);
        Assert.Equal("US", location.CountryCode);
    }

    [Fact]
    public void MunicipalityCountsAsCityLevel()
    {
        var location = MusicBrainzArtistLocationService.ToLocation(new MusicBrainzArtist
        {
            Id = "mbid-1",
            Name = "Someone",
            CountryCode = "NG",
            BeginArea = new Area { Name = "Lagos", Type = "Municipality" }
        });

        Assert.Equal("Lagos", location!.City);
    }

    /// <summary>
    /// MusicBrainz knows the artist but not where they are from. Returning null
    /// lets the next source try; returning an empty location would leave the page
    /// blank instead of falling back.
    /// </summary>
    [Fact]
    public void AnArtistWithNoGeographyYieldsNull()
        => Assert.Null(MusicBrainzArtistLocationService.ToLocation(new MusicBrainzArtist
        {
            Id = "mbid-1",
            Name = "Obscure Artist"
        }));

    [Theory]
    [InlineData("ke", "KE")]
    [InlineData(" KE ", "KE")]
    [InlineData("USA", null)]
    [InlineData("K", null)]
    [InlineData("", null)]
    public void OnlyRealIsoAlpha2CodesAreAccepted(string input, string? expected)
    {
        var location = MusicBrainzArtistLocationService.ToLocation(new MusicBrainzArtist
        {
            Id = "mbid-1",
            Name = "Someone",
            CountryCode = input
        });

        Assert.Equal(expected, location?.CountryCode);
    }

    // ── Name search requires album overlap ────────────────────────────────

    /// <summary>
    /// The rule that matters most: a same-name artist with no album in common is
    /// not a match, and resolution must fall through to the next source.
    /// </summary>
    [Fact]
    public async Task ASearchHitWithNoAlbumOverlapIsNotAMatch()
    {
        var harness = await CreateHarnessAsync(["Zogo Album"]);
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("aaaa", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" })));
        // The candidate's catalogue shares nothing with the library.
        harness.Http.WhenContains("release-group?artist=aaaa", ReleaseGroupsJson("Totally Different Album"));

        Assert.Null(await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None));
        // A rejected candidate must not be persisted as the artist's identity.
        Assert.Null(await harness.Repository.GetArtistSourceIdAsync(ArtistId, "musicbrainz"));
    }

    [Fact]
    public async Task ASearchHitWithAlbumOverlapResolves()
    {
        var harness = await CreateHarnessAsync(["Zogo Album", "Unrelated Local"]);
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("bbbb", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" })));
        harness.Http.WhenContains("release-group?artist=bbbb", ReleaseGroupsJson("Zogo Album", "Another One"));

        var result = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Dar es Salaam", result!.City);
        Assert.Equal("TZ", result.CountryCode);
        Assert.Equal("musicbrainz", result.Source);
        // A confirmed identity is worth keeping, so the search is paid once.
        Assert.Equal("bbbb", await harness.Repository.GetArtistSourceIdAsync(ArtistId, "musicbrainz"));
    }

    /// <summary>
    /// Among same-name hits the one sharing the most albums wins — the whole point
    /// of grading the overlap rather than accepting the first plausible hit.
    /// </summary>
    [Fact]
    public async Task TheCandidateSharingTheMostAlbumsWins()
    {
        var harness = await CreateHarnessAsync(["Zogo Album", "Another One", "Third One"]);
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("weak", "Alikiba", "KE", new Area { Name = "Nairobi", Type = "City" }),
            Candidate("strong", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" })));
        harness.Http.WhenContains("release-group?artist=weak", ReleaseGroupsJson("Zogo Album"));
        harness.Http.WhenContains("release-group?artist=strong", ReleaseGroupsJson("Zogo Album", "Another One", "Third One"));

        var result = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);

        Assert.Equal("TZ", result?.CountryCode);
        Assert.Equal("strong", await harness.Repository.GetArtistSourceIdAsync(ArtistId, "musicbrainz"));
    }

    /// <summary>
    /// An artist with no albums in the library cannot be cross-checked, and the
    /// rule has no name-only escape — so there is deliberately no match.
    /// </summary>
    [Fact]
    public async Task AnArtistWithNoLibraryAlbumsIsNotResolvedByNameSearch()
    {
        // No albums at all, not even a carrier: this is the case the rule exists
        // for, so the library genuinely has nothing to cross-check against.
        var harness = await CreateHarnessAsync([], includeCarrierAlbum: false);
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("cccc", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" })));

        Assert.Null(await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None));
        // The catalogue must not even be fetched: there is nothing to compare.
        Assert.False(harness.Http.Saw("release-group?artist=cccc"));
    }

    [Fact]
    public async Task ACandidateWithADifferentNameIsSkippedBeforeAnyCatalogueCall()
    {
        var harness = await CreateHarnessAsync(["Zogo Album"]);
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("dddd", "Someone Else Entirely", "US", new Area { Name = "Austin", Type = "City" })));

        Assert.Null(await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None));
        Assert.False(harness.Http.Saw("release-group?artist=dddd"));
    }

    [Fact]
    public async Task AnEmptySearchResultYieldsNoMatch()
    {
        var harness = await CreateHarnessAsync(["Zogo Album"]);
        harness.Http.WhenContains("artist?query=", SearchJson([]));

        Assert.Null(await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None));
    }

    [Fact]
    public async Task ABlankArtistNameSkipsTheSearchEntirely()
    {
        var harness = await CreateHarnessAsync(["Zogo Album"]);

        Assert.Null(await harness.Service.ResolveAsync(ArtistId, "   ", CancellationToken.None));
        Assert.False(harness.Http.Saw("artist?query="));
    }

    // ── MBID identity routes ──────────────────────────────────────────────

    /// <summary>
    /// A stored MBID is identity, so it resolves with no album cross-check and no
    /// search — even for an artist whose library holds no albums at all.
    /// </summary>
    [Fact]
    public async Task AStoredMbidResolvesWithoutAnySearchOrAlbumCheck()
    {
        var harness = await CreateHarnessAsync([], storedMbid: "stored-mbid");
        harness.Http.WhenContains("artist/stored-mbid", ArtistJson(
            "stored-mbid", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" }));

        var result = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);

        Assert.Equal("Dar es Salaam", result?.City);
        Assert.Equal("TZ", result?.CountryCode);
        Assert.False(harness.Http.Saw("artist?query="));
    }

    /// <summary>
    /// A stored MBID whose artist has no geography falls through to the search
    /// rather than reporting an empty location.
    /// </summary>
    [Fact]
    public async Task AStoredMbidWithNoGeographyFallsThroughToSearch()
    {
        var harness = await CreateHarnessAsync(["Zogo Album"], storedMbid: "stored-mbid");
        harness.Http.WhenContains("artist/stored-mbid", ArtistJson("stored-mbid", "Alikiba", null, null));
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("eeee", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" })));
        harness.Http.WhenContains("release-group?artist=eeee", ReleaseGroupsJson("Zogo Album"));

        var result = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);

        Assert.Equal("Dar es Salaam", result?.City);
    }

    [Fact]
    public async Task AnMbidReadFromTagsResolvesWithoutASearch()
    {
        var harness = await CreateHarnessAsync([], taggedMbid: "tagged-mbid");
        harness.Http.WhenContains("artist/tagged-mbid", ArtistJson(
            "tagged-mbid", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" }));

        var result = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);

        Assert.Equal("Dar es Salaam", result?.City);
        Assert.False(harness.Http.Saw("artist?query="));
        // Discovered from the library's own tags, so it is worth persisting.
        Assert.Equal("tagged-mbid", await harness.Repository.GetArtistSourceIdAsync(ArtistId, "musicbrainz"));
    }

    /// <summary>
    /// The MBID tag route reads the value most tracks carry, so files that
    /// disagree do not send the lookup to the wrong artist.
    /// </summary>
    [Fact]
    public async Task TheMajorityTaggedMbidIsUsed()
    {
        // Two albums so there are two tracks: the majority MBID is on the first
        // and the minority on the second, so the counts differ.
        var harness = await CreateHarnessAsync(
            ["Album One", "Album Two"],
            taggedMbid: "tagged-mbid",
            extraTaggedMbid: "minority");
        harness.Http.WhenContains("artist/tagged-mbid", ArtistJson(
            "tagged-mbid", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" }));
        harness.Http.WhenContains("artist/minority", ArtistJson(
            "minority", "Alikiba", "KE", new Area { Name = "Nairobi", Type = "City" }));

        var result = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);

        Assert.Equal("TZ", result?.CountryCode);
        Assert.False(harness.Http.Saw("artist/minority"));
    }

    [Fact]
    public async Task BothMusicBrainzArtistIdTagSpellingsAreRead()
    {
        var harness = await CreateHarnessAsync([], taggedMbid: "spelling-two", useUnderscoreSpelling: true);
        harness.Http.WhenContains("artist/spelling-two", ArtistJson(
            "spelling-two", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" }));

        Assert.Equal("Dar es Salaam", (await harness.Service.ResolveAsync(ArtistId, "Alikiba", default))?.City);
    }

    // ── Caching ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CorrectedMusicBrainzId_RefreshesLocationAfterCacheInvalidation()
    {
        var harness = await CreateHarnessAsync(["Album"], storedMbid: "old-mbid");
        harness.Http.WhenContains("artist/old-mbid", ArtistJson("old-mbid", "Artist", "TZ", new Area { Name = "Old City", Type = "City" }));
        harness.Http.WhenContains("artist/new-mbid", ArtistJson("new-mbid", "Artist", "KE", new Area { Name = "New City", Type = "City" }));
        Assert.Equal("Old City", (await harness.Service.ResolveAsync(ArtistId, "Artist", default))?.City);
        await harness.Repository.UpsertArtistSourceIdAsync(ArtistId, "musicbrainz", "new-mbid", default);
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, harness.Service);
        resolver.InvalidateMusicBrainzArtist(ArtistId);
        Assert.Equal("New City", (await resolver.ResolveAsync(ArtistId, "Artist", default))?.City);
        Assert.Equal(1, harness.Http.CountContaining("artist/new-mbid"));
    }

    [Fact]
    public async Task ASecondResolveDoesNotRepeatTheLookup()
    {
        var harness = await CreateHarnessAsync(["Zogo Album"]);
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("ffff", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" })));
        harness.Http.WhenContains("release-group?artist=ffff", ReleaseGroupsJson("Zogo Album"));

        var first = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);
        var second = await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, harness.Http.CountContaining("artist?query="));
    }

    /// <summary>
    /// A miss is cached too. Without this, an artist MusicBrainz does not know
    /// would cost a full rate-limited search on every single page load.
    /// </summary>
    [Fact]
    public async Task ARejectionIsCachedSoItIsNotRetriedEveryLoad()
    {
        var harness = await CreateHarnessAsync(["Zogo Album"]);
        harness.Http.WhenContains("artist?query=", SearchJson(
            Candidate("gggg", "Alikiba", "TZ", new Area { Name = "Dar es Salaam", Type = "City" })));
        harness.Http.WhenContains("release-group?artist=gggg", ReleaseGroupsJson("Unrelated"));

        Assert.Null(await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None));
        Assert.Null(await harness.Service.ResolveAsync(ArtistId, "Alikiba", CancellationToken.None));

        Assert.Equal(1, harness.Http.CountContaining("artist?query="));
    }

    [Fact]
    public void TheSourceNameIsStable()
        => Assert.Equal("musicbrainz", MusicBrainzArtistLocationService.MusicBrainzSourceName);

    // ── Harness ───────────────────────────────────────────────────────────

    private sealed record Harness(
        MusicBrainzArtistLocationService Service,
        LibraryRepository Repository,
        StubHandler Http);

    private async Task<Harness> CreateHarnessAsync(
        IReadOnlyList<string> albumTitles,
        string? storedMbid = null,
        string? taggedMbid = null,
        string? extraTaggedMbid = null,
        bool useUnderscoreSpelling = false,
        bool includeCarrierAlbum = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_databasePath}"
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        await ExecuteAsync(connection, "INSERT INTO artist (id, name) VALUES ($id, 'Alikiba');", ("$id", ArtistId));

        if (!string.IsNullOrWhiteSpace(storedMbid))
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO artist_source (artist_id, source, source_id) VALUES ($id, 'musicbrainz', $mbid);",
                ("$id", ArtistId), ("$mbid", storedMbid));
        }

        // An album only counts as held when it has a local audio file in an enabled
        // folder, which is what GetArtistAlbumsAsync joins on. A bare album row
        // would be invisible to the very query the cross-check depends on.
        await ExecuteAsync(
            connection,
            "INSERT INTO folder (id, root_path, display_name, enabled) VALUES (1, '/music', 'Music', 1);");

        // The artist normally gets one playable track even when the test names no
        // albums, so the identity-tag routes have something to read. A test that
        // is specifically about an artist holding nothing opts out.
        var effective = albumTitles.Count > 0
            ? albumTitles
            : includeCarrierAlbum ? ["Carrier Album"] : [];
        var trackId = 1L;
        for (var index = 0; index < effective.Count; index++)
        {
            var albumId = (long)(index + 1);
            await ExecuteAsync(
                connection,
                "INSERT INTO album (id, artist_id, title) VALUES ($id, $artistId, $title);",
                ("$id", albumId), ("$artistId", ArtistId), ("$title", effective[index]));
            await ExecuteAsync(
                connection,
                "INSERT INTO track (id, album_id, title) VALUES ($id, $albumId, $title);",
                ("$id", trackId), ("$albumId", albumId), ("$title", $"Track {trackId}"));
            await ExecuteAsync(
                connection,
                "INSERT INTO audio_file (id, path, folder_id) VALUES ($id, $path, 1);",
                ("$id", trackId), ("$path", $"/music/track{trackId}.flac"));
            await ExecuteAsync(
                connection,
                "INSERT INTO track_local (track_id, audio_file_id) VALUES ($trackId, $fileId);",
                ("$trackId", trackId), ("$fileId", trackId));
            trackId++;
        }

        // The majority MBID goes on every track; a minority value goes on the last
        // one only, so the two counts genuinely differ rather than tying and
        // falling back to alphabetical order.
        if (!string.IsNullOrWhiteSpace(taggedMbid))
        {
            var key = useUnderscoreSpelling ? "MUSICBRAINZ_ARTIST_ID" : "MUSICBRAINZ_ARTISTID";
            for (var id = 1L; id <= trackId - 1; id++)
            {
                await TagAsync(connection, id, key, taggedMbid);
            }
        }

        if (!string.IsNullOrWhiteSpace(extraTaggedMbid))
        {
            await TagAsync(connection, trackId - 1, "MUSICBRAINZ_ARTISTID", extraTaggedMbid);
        }

        var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        var handler = new StubHandler();
        var service = new MusicBrainzArtistLocationService(
            // The client sets its own base address, so the test supplies only
            // transport. Its one-request-per-second limiter stays in force, which
            // is deliberate: these tests run through the real throttling.
            new MusicBrainzClient(new HttpClient(handler), NullLogger<MusicBrainzClient>.Instance),
            NullLogger<MusicBrainzArtistLocationService>.Instance,
            repository);
        return new Harness(service, repository, handler);
    }

    /// <summary>Tags one track with an MBID.</summary>
    private static async Task TagAsync(SqliteConnection connection, long trackId, string key, string value)
    {
        await ExecuteAsync(
            connection,
            "INSERT OR IGNORE INTO track_other_tag (track_id, tag_key, tag_value) VALUES ($trackId, $key, $value);",
            ("$trackId", trackId), ("$key", key), ("$value", value));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static MusicBrainzArtist Candidate(string mbid, string name, string? country, Area? beginArea)
        => new()
        {
            Id = mbid,
            Name = name,
            CountryCode = country,
            Score = 100,
            BeginArea = beginArea,
            Area = country is null ? null : new Area { Name = country, Type = "Country" }
        };

    /// <summary>
    /// A search response shaped like the real one, including the area objects —
    /// a hit without them would under-report the location and hide a mapping bug.
    /// </summary>
    private static string SearchJson(params MusicBrainzArtist[] artists)
    {
        var entries = artists.Select(a => "{" + string.Join(",",
            $"\"id\":{JsonValue(a.Id)}",
            $"\"name\":{JsonValue(a.Name)}",
            $"\"country\":{JsonValue(a.CountryCode)}",
            $"\"begin-area\":{AreaJson(a.BeginArea)}",
            $"\"area\":{AreaJson(a.Area)}",
            "\"score\":100") + "}");
        return "{\"count\":" + artists.Length + ",\"offset\":0,\"artists\":[" + string.Join(",", entries) + "]}";
    }

    private static string ArtistJson(string mbid, string name, string? country, Area? beginArea)
        => "{\"id\":" + JsonValue(mbid)
            + ",\"name\":" + JsonValue(name)
            + ",\"country\":" + JsonValue(country)
            + ",\"begin-area\":" + AreaJson(beginArea) + "}";

    private static string AreaJson(Area? area)
        => area is null
            ? "null"
            : "{\"name\":" + JsonValue(area.Name) + ",\"type\":" + JsonValue(area.Type) + "}";

    private static string ReleaseGroupsJson(params string[] titles)
    {
        var entries = titles.Select(t => "{\"id\":" + JsonValue("rg-" + t) + ",\"title\":" + JsonValue(t) + "}");
        return "{\"count\":" + titles.Length + ",\"release-groups\":[" + string.Join(",", entries) + "]}";
    }

    private static string JsonValue(string? value) => value is null ? "null" : "\"" + value + "\"";

    [Fact]
    public async Task MatchedArtistWithoutLocalRecordResolvesExactAreaTypes()
    {
        var handler = new StubHandler();
        const string mbid = "0ab49580-c84f-44d4-875f-d83760ea2cfe";
        handler.WhenContains("artist/" + mbid, """
            {"id":"0ab49580-c84f-44d4-875f-d83760ea2cfe","name":"Maroon 5","country":"US",
             "area":{"id":"country-id","name":"United States","type":null},
             "begin-area":{"id":"city-id","name":"Los Angeles","type":null}}
            """);
        handler.WhenContains("area/city-id", """{"id":"city-id","name":"Los Angeles","type":"City"}""");
        handler.WhenContains("area/country-id", """{"id":"country-id","name":"United States","type":"Country"}""");
        var service = new MusicBrainzArtistLocationService(
            new MusicBrainzClient(new HttpClient(handler), NullLogger<MusicBrainzClient>.Instance),
            NullLogger<MusicBrainzArtistLocationService>.Instance);
        var result = await service.ResolveMatchedArtistAsync(mbid, "Maroon 5", CancellationToken.None);
        Assert.Equal("Los Angeles", result?.City);
        Assert.Equal("United States", result?.Country);
        Assert.Null(result?.ArtistId);
        Assert.Null(await service.ResolveMatchedArtistAsync(mbid, "Wiz Khalifa", CancellationToken.None));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(string Match, string Body)> _routes = [];
        private readonly List<string> _seen = [];

        public void WhenContains(string match, string body) => _routes.Add((match, body));

        public bool Saw(string match) => _seen.Exists(url => url.Contains(match, StringComparison.Ordinal));

        public int CountContaining(string match)
            => _seen.Count(url => url.Contains(match, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            _seen.Add(url);

            foreach (var (match, body) in _routes)
            {
                if (url.Contains(match, StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

}
