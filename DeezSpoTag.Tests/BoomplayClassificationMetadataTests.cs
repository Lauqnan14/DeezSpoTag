using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayClassificationMetadataTests
{
    private static readonly MethodInfo ParseSongHtml = typeof(BoomplayMetadataService).GetMethod(
        "ParseSongHtml", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseSongHtml not found.");

    private static readonly MethodInfo ApplyStreamTags = typeof(BoomplayMetadataService).GetMethod(
        "ApplyStreamTags", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ApplyStreamTags not found.");

    private static readonly MethodInfo ParsePlaylistHtml = typeof(BoomplayMetadataService).GetMethod(
        "ParsePlaylistHtml", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParsePlaylistHtml not found.");

    private static readonly MethodInfo ParseOfficialSongMetadata = typeof(BoomplayMetadataService).GetMethod(
        "ParseOfficialSongMetadata", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseOfficialSongMetadata not found.");

    private static readonly MethodInfo ParseOfficialPlaylistTracks = typeof(BoomplayMetadataService).GetMethod(
        "ParseOfficialPlaylistTracks", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseOfficialPlaylistTracks not found.");

    private static readonly MethodInfo ParseMoodPlaylistCatalog = typeof(BoomplayMetadataService).GetMethod(
        "ParseMoodPlaylistCatalog", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseMoodPlaylistCatalog not found.");

    private static readonly MethodInfo MapSingleTrack = typeof(BoomplayApiController).GetMethod(
        "MapSingleTrack", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayApiController.MapSingleTrack not found.");

    [Fact]
    public void ParseSongHtml_ExtractsGenreMoodAndDetailTagsWithProvenance()
    {
        const string html = """
        <html><head><meta property="og:title" content="Test Song" /></head><body>
          <section class="songDetailInfo"><ul>
            <li>Genre:<span>Afro Soul / R&amp;B</span></li>
            <li>Mood:<span>Happy, Energetic</span></li>
            <li>Language:<span>English</span></li>
          </ul></section>
        </body></html>
        """;

        var track = Assert.IsType<BoomplayTrackMetadata>(
            ParseSongHtml.Invoke(null, new object?[] { "256487581", html, "https://www.boomplay.com/songs/256487581" }));

        Assert.Equal(new[] { "Afro Soul", "R&B" }, track.Genres);
        Assert.Equal(new[] { "Happy", "Energetic" }, track.Moods);
        Assert.Equal("English", track.Tags["language"]);
        Assert.Equal("html", track.FieldSources["genres"]);
        Assert.Equal("html", track.FieldSources["moods"]);
    }

    [Theory]
    [InlineData("https://www.boomplay.com/songs/EQve1j5y6O5cFQswuejgba_Z", "track", "EQve1j5y6O5cFQswuejgba_Z")]
    [InlineData("https://www.boomplay.com/playlists/EQFGpOEkQenBdQefk4jpozq2", "playlist", "EQFGpOEkQenBdQefk4jpozq2")]
    [InlineData("https://www.boomplay.com/playlists/EQHxv9OfBPj-dhStUdbqizcI", "playlist", "EQHxv9OfBPj-dhStUdbqizcI")]
    [InlineData("https://www.boomplay.com/songs/256487581", "track", "256487581")]
    public void TryParseBoomplayUrl_AcceptsCurrentAndLegacyPublicIds(
        string url,
        string expectedType,
        string expectedId)
    {
        Assert.True(BoomplayMetadataService.TryParseBoomplayUrl(url, out var type, out var id));
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData("ftp://www.boomplay.com/songs/EQve1j5y6O5cFQswuejgba_Z")]
    [InlineData("https://example.com/songs/EQve1j5y6O5cFQswuejgba_Z")]
    public void TryParseBoomplayUrl_RejectsUnsupportedOrigins(string url)
    {
        Assert.False(BoomplayMetadataService.TryParseBoomplayUrl(url, out _, out _));
    }

    [Fact]
    public void ParseLinkEndpoint_RecognizesCurrentOpaquePlaylistWithoutNetworkResolution()
    {
        const string url = "https://www.boomplay.com/playlists/EQHxv9OfBPj-dhStUdbqizcI";
        var controller = new BoomplayApiController(
            boomplayMetadataService: null!,
            httpClientFactory: null!,
            NullLogger<BoomplayApiController>.Instance);

        var result = Assert.IsType<OkObjectResult>(controller.ParseLink(url));
        var json = JsonConvert.SerializeObject(result.Value);

        Assert.Contains("\"type\":\"playlist\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"EQHxv9OfBPj-dhStUdbqizcI\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"canonicalUrl\":\"{url}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"error\":\"\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSongHtml_CanonicalizesCurrentPublicUrlToNumericIdentity()
    {
        const string canonicalUrl = "https://www.boomplay.com/songs/EQve1j5y6O5cFQswuejgba_Z";
        const string html = """
        <html><head>
          <link rel="canonical" href="https://www.boomplay.com/songs/EQve1j5y6O5cFQswuejgba_Z" />
          <meta property="og:title" content="Take a look at you!" />
        </head><body>
          <div id="songsDetails" data-cid="256487581"></div>
        </body></html>
        """;

        var track = Assert.IsType<BoomplayTrackMetadata>(
            ParseSongHtml.Invoke(null, new object?[] { "EQve1j5y6O5cFQswuejgba_Z", html, canonicalUrl }));

        Assert.Equal("256487581", track.Id);
        Assert.Equal(canonicalUrl, track.Url);
    }

    [Fact]
    public void ParsePlaylistHtml_PreservesCanonicalUrlsAndNumericInternalIds()
    {
        const string canonicalUrl = "https://www.boomplay.com/playlists/EQFGpOEkQenBdQefk4jpozq2";
        const string html = """
        <html><head>
          <link rel="canonical" href="https://www.boomplay.com/playlists/EQFGpOEkQenBdQefk4jpozq2" />
          <meta property="og:title" content="Bongo Love" />
        </head><body>
          <div id="playlistsDetails" data-cid="6990547">
            <li data-data="253568340%40%2B%231%40%2B%236990547">
              <a class="songName" href="/songs/EQtzjsDa8QiPAO7NExWf46vH">Nitakesha</a>
              <a class="artistName">Mocco Genius</a>
            </li>
          </div>
        </body></html>
        """;

        var playlist = Assert.IsType<BoomplayPlaylistMetadata>(
            ParsePlaylistHtml.Invoke(null, new object?[] { "EQFGpOEkQenBdQefk4jpozq2", html, canonicalUrl }));

        Assert.Equal("6990547", playlist.Id);
        Assert.Equal(canonicalUrl, playlist.Url);
        Assert.Equal(new[] { "253568340" }, playlist.TrackIds);
        Assert.Equal(
            "https://www.boomplay.com/songs/EQtzjsDa8QiPAO7NExWf46vH",
            playlist.TrackHints["253568340"].Url);
    }

    [Fact]
    public void ParseMoodPlaylistCatalog_AcceptsCurrentOpaquePlaylistIds()
    {
        const string html = """
        <html><body>
          <a href="/playlists/EQFOEFsjM2myXi6sep7Es2PV"><strong>Happy Friday</strong></a>
          <a href="/playlists/EQGotCY_McLD4xus1knG0tgx"><strong>Relax</strong></a>
        </body></html>
        """;

        var catalog = Assert.IsAssignableFrom<IReadOnlyList<(string Id, string Name)>>(
            ParseMoodPlaylistCatalog.Invoke(null, new object?[] { html }));

        Assert.Contains(("EQFOEFsjM2myXi6sep7Es2PV", "Happy Friday"), catalog);
        Assert.Contains(("EQGotCY_McLD4xus1knG0tgx", "Relax"), catalog);
    }

    [Fact]
    public async Task SearchSongs_UsesTypedMusicSearchAndDoesNotHydrateEveryHintCandidate()
    {
        var root = Path.Join(Path.GetTempPath(), $"deezspotag-boomplay-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var environment = new TestWebHostEnvironment(root);
            var auth = new PlatformAuthService(
                environment,
                NullLogger<PlatformAuthService>.Instance,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Join(root, "keys"))));
            using var httpClientFactory = new RoutingHttpClientFactory(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.StartsWith("/search/music/", StringComparison.OrdinalIgnoreCase))
                {
                    return """
                    <html><body>
                      <ol>
                        <li class="clearfix play_one" data-id="259135741"
                            data-data="259135741%40%2B%235%40%2B%23135395273%40%2B%23https%3A%2F%2Fsource.boomplaymusic.com%2Fgroup10%2FM00%2Fcover.jpg%40%2B%23Pombe%20Niache%40%2B%23Ay%20Masta%40%2B%23EQLU2jB3HIL4wueamgx1kH9F%40%2B%2303%3A15@+#OMS@+#0@+#EQvxdsV7BtNqM5LQor545Dvb">
                          <a href="/songs/EQvxdsV7BtNqM5LQor545Dvb?from=search" class="songName">Pombe Niache</a>
                          <a class="artistName" href="/artists/EQLU2jB3HIL4wueamgx1kH9F?from=search">Ay Masta</a>
                        </li>
                      </ol>
                    </body></html>
                    """;
                }

                if (path.StartsWith("/songs/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("SearchSongsAsync must not hydrate each hinted search candidate.");
                }

                return string.Empty;
            });
            var service = new BoomplayMetadataService(
                httpClientFactory,
                auth,
                NullLogger<BoomplayMetadataService>.Instance);

            var tracks = await service.SearchSongsAsync("AY Masta Pombe Niache", 12, CancellationToken.None);

            var track = Assert.Single(tracks);
            Assert.Equal("259135741", track.Id);
            Assert.Equal("Pombe Niache", track.Title);
            Assert.Equal("Ay Masta", track.Artist);
            Assert.Equal("https://www.boomplay.com/songs/EQvxdsV7BtNqM5LQor545Dvb?from=search", track.Url);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApplyStreamTags_OverridesGenreAndRecordsOptionalMood()
    {
        var track = new BoomplayTrackMetadata { Genres = new List<string> { "Pop" } };
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TCON"] = "Afrobeats; Afro Pop",
            ["TMOO"] = "Uplifting / Party",
            ["TLAN"] = "eng"
        };

        ApplyStreamTags.Invoke(null, new object?[] { track, tags });

        Assert.Equal(new[] { "Afrobeats", "Afro Pop" }, track.Genres);
        Assert.Equal(new[] { "Uplifting", "Party" }, track.Moods);
        Assert.Equal("stream", track.FieldSources["genres"]);
        Assert.Equal("stream", track.FieldSources["moods"]);
    }

    [Fact]
    public void ParseOfficialPlaylistTracks_ReadsCurrentArtistAndAlbumObjects()
    {
        using var document = JsonDocument.Parse("""
        [
          {
            "musicID": 120387978,
            "colID": 66252730,
            "name": "Don't Let Me Go",
            "beArtist": { "name": "Etana" },
            "beAlbum": { "name": "Don't Let Me Go" },
            "deaution": "00:04:24",
            "seq": 1
          }
        ]
        """);

        var tracks = Assert.IsAssignableFrom<IReadOnlyList<BoomplayTrackMetadata>>(
            ParseOfficialPlaylistTracks.Invoke(null, new object?[] { document.RootElement }));
        var track = Assert.Single(tracks);

        Assert.Equal("120387978", track.Id);
        Assert.Equal("Etana", track.Artist);
        Assert.Equal("Don't Let Me Go", track.Album);
    }

    [Fact]
    public void SingleTrackResponse_ExposesSameClassificationShapeAsOtherTracks()
    {
        var track = new BoomplayTrackMetadata
        {
            Id = "256487581",
            Genres = new List<string> { "Afro Soul" },
            Moods = new List<string> { "Happy" },
            Tags = new Dictionary<string, string> { ["language"] = "English" },
            FieldSources = new Dictionary<string, string> { ["genres"] = "html" }
        };

        var payload = MapSingleTrack.Invoke(null, new object?[] { track });
        var json = JsonConvert.SerializeObject(payload);

        Assert.Contains("\"genres\":[\"Afro Soul\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"moods\":[\"Happy\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"fieldSources\":{\"genres\":\"html\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveBoomplaySong_ParsesCurrentHtmlAndOfficialApi()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOOMPLAY_LIVE_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const string songId = "256487581";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BoomplayMetadataLiveTest/1.0");

        var html = await client.GetStringAsync($"https://www.boomplay.com/songs/{songId}");
        var parsedHtml = Assert.IsType<BoomplayTrackMetadata>(ParseSongHtml.Invoke(
            null, new object?[] { songId, html, $"https://www.boomplay.com/songs/{songId}" }));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.boomplaymusic.com/BoomPlayer/music/getMusicInfo?musicID={songId}");
        request.Headers.TryAddWithoutValidation("x-boomplay-ref", "Boomplay_ANDROID");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var official = Assert.IsType<BoomplayTrackMetadata>(
            ParseOfficialSongMetadata.Invoke(null, new object?[] { document.RootElement }));

        Assert.Equal(songId, parsedHtml.Id);
        Assert.Equal("https://www.boomplay.com/songs/EQve1j5y6O5cFQswuejgba_Z", parsedHtml.Url);
        Assert.NotEmpty(parsedHtml.Genres);
        Assert.Equal("html", parsedHtml.FieldSources["genres"]);
        Assert.Equal(songId, official.Id);
        Assert.False(string.IsNullOrWhiteSpace(official.Title));
        Assert.False(string.IsNullOrWhiteSpace(official.Artist));
        Assert.True(official.DurationMs > 0);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveBoomplayPlaylist_ParsesCurrentOpaqueUrlAndNumericTrackIds()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOOMPLAY_LIVE_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const string publicId = "EQHxv9OfBPj-dhStUdbqizcI";
        const string playlistUrl = $"https://www.boomplay.com/playlists/{publicId}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BoomplayMetadataLiveTest/1.0");

        var html = await client.GetStringAsync(playlistUrl);
        var playlist = Assert.IsType<BoomplayPlaylistMetadata>(
            ParsePlaylistHtml.Invoke(null, new object?[] { publicId, html, playlistUrl }));

        Assert.Equal("17565916", playlist.Id);
        Assert.Equal(playlistUrl, playlist.Url);
        Assert.NotEmpty(playlist.TrackIds);
        Assert.All(playlist.TrackIds, static id => Assert.All(id, static character => Assert.True(char.IsDigit(character))));
        Assert.All(playlist.TrackHints.Values, static hint => Assert.StartsWith("https://www.boomplay.com/songs/", hint.Url));
        Assert.All(playlist.TrackHints.Values, static hint => Assert.False(string.IsNullOrWhiteSpace(hint.Artist)));

        const string numericPlaylistId = "17565916";
        var legacyUrl = $"https://www.boomplay.com/playlists/{numericPlaylistId}";
        var legacyHtml = await client.GetStringAsync(legacyUrl);
        var legacyPlaylist = Assert.IsType<BoomplayPlaylistMetadata>(
            ParsePlaylistHtml.Invoke(null, new object?[] { numericPlaylistId, legacyHtml, legacyUrl }));
        Assert.Equal(numericPlaylistId, legacyPlaylist.Id);
        Assert.Equal(playlistUrl, legacyPlaylist.Url);
        Assert.All(legacyPlaylist.TrackHints.Values, static hint => Assert.False(string.IsNullOrWhiteSpace(hint.Artist)));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.boomplaymusic.com/BoomPlayer/music/getMusicsByColID?colID={numericPlaylistId}");
        request.Headers.TryAddWithoutValidation("x-boomplay-ref", "Boomplay_ANDROID");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var officialDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var officialTracks = Assert.IsAssignableFrom<IReadOnlyList<BoomplayTrackMetadata>>(
            ParseOfficialPlaylistTracks.Invoke(null, new object?[] { officialDocument.RootElement.GetProperty("musics") }));
        Assert.NotEmpty(officialTracks);
        Assert.All(officialTracks, static track => Assert.False(string.IsNullOrWhiteSpace(track.Artist)));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveBoomplayService_OpensExactCurrentPlaylistUrlEndToEnd()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOOMPLAY_LIVE_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const string publicId = "EQHxv9OfBPj-dhStUdbqizcI";
        const string playlistUrl = $"https://www.boomplay.com/playlists/{publicId}";
        var root = Path.Join(Path.GetTempPath(), $"deezspotag-boomplay-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var environment = new TestWebHostEnvironment(root);
            var auth = new PlatformAuthService(
                environment,
                NullLogger<PlatformAuthService>.Instance,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Join(root, "keys"))));
            using var httpClientFactory = new LiveHttpClientFactory();
            var service = new BoomplayMetadataService(
                httpClientFactory,
                auth,
                NullLogger<BoomplayMetadataService>.Instance);

            var playlist = await service.GetPlaylistAsync(publicId, CancellationToken.None);
            Assert.NotNull(playlist);
            Assert.Equal("17565916", playlist.Id);
            Assert.Equal(playlistUrl, playlist.Url);
            Assert.NotEmpty(playlist.Tracks);
            Assert.All(playlist.Tracks, static track => Assert.False(string.IsNullOrWhiteSpace(track.Artist)));

            var controller = new BoomplayApiController(
                service,
                httpClientFactory,
                NullLogger<BoomplayApiController>.Instance);
            var tracklistResult = Assert.IsType<OkObjectResult>(await controller.GetTracklist(
                publicId,
                "playlist",
                CancellationToken.None));
            var payload = JsonConvert.SerializeObject(tracklistResult.Value);
            Assert.DoesNotContain("Boomplay Music: Not Found", payload, StringComparison.Ordinal);
            Assert.Contains("\"nb_tracks\":135", payload, StringComparison.Ordinal);
            Assert.Contains("\"name\":\"Etana\"", payload, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveBoomplayMoodCatalog_MapsEditorialMoodPlaylistToTracks()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOOMPLAY_LIVE_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BoomplayMetadataLiveTest/1.0");
        var catalogHtml = await client.GetStringAsync("https://www.boomplay.com/more/home/moods-and-activities");
        var catalog = Assert.IsAssignableFrom<IReadOnlyList<(string Id, string Name)>>(
            ParseMoodPlaylistCatalog.Invoke(null, new object?[] { catalogHtml }));
        var happyFriday = Assert.Single(catalog, static item => item.Name == "Happy Friday");

        var playlistHtml = await client.GetStringAsync($"https://www.boomplay.com/playlists/{happyFriday.Id}");
        Assert.Contains("/songs/", playlistHtml, StringComparison.Ordinal);
    }

    private sealed class LiveHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class RoutingHttpClientFactory(Func<HttpRequestMessage, string> route) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new RoutingHandler(route)) { Timeout = TimeSpan.FromSeconds(30) };

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, string> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = route(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
