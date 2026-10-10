using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Integrations.Apple;
using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the streaming-platform identity resolver.
/// <para>
/// What matters here is not the happy path. A resolver that records a wrong catalog id writes the
/// wrong track into a user's playlist and looks like it worked, so the tests pin the two things that
/// guard against it: a search result is only recorded once the app's own candidate validator
/// accepts it, and a track the platform does not have stays unresolved rather than being guessed at.
/// They also pin the property that makes the feature affordable - a recorded identity is never
/// searched for again.
/// </para>
/// </summary>
public sealed class PlatformTrackIdentityResolverTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private MemoryCache _cache = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-platform-identity-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = string.Concat("Data Source=", Path.Join(_tempRoot, "library.db"))
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        _cache?.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private PlatformTrackIdentityResolver CreateResolver() => new(_repository);

    private static PlatformIdentityTrack Track(
        long localTrackId,
        string? isrc = "USRC17607839",
        string name = "Paranoid Android",
        string artists = "Radiohead",
        int? durationMs = 383_000)
        => new(localTrackId, isrc, name, artists, "OK Computer", durationMs);

    [Fact]
    public async Task AnAlreadyRecordedIdentityIsReturnedWithoutSearching()
    {
        await _repository.UpsertMediaServerTrackMetadataAsync([
            new MediaServerTrackMetadataUpsertDto(1, "spotify", "recorded-id", string.Empty, DateTimeOffset.UtcNow)
        ]);

        // This resolver has no Spotify client at all, so a search would be impossible. Reaching the
        // recorded id at all proves the stored one was used.
        var resolved = await CreateResolver().ResolveAsync("spotify", [Track(1)], CancellationToken.None);

        Assert.Equal("recorded-id", resolved[1]);
    }

    [Fact]
    public async Task AServiceWithNoSearchWiredLeavesTracksUnresolvedRatherThanFailing()
    {
        var resolved = await CreateResolver().ResolveAsync("tidal", [Track(1)], CancellationToken.None);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task TracksWithNoLocalIdentityAreSkipped()
    {
        // Local track id 0 means the track is not in this library, so there is no row to attach an
        // identity to and nothing to write.
        var resolved = await CreateResolver().ResolveAsync("spotify", [Track(0)], CancellationToken.None);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task SupportsIsFalseForEveryServiceWhenNoClientIsWired()
    {
        var resolver = CreateResolver();

        Assert.False(resolver.Supports("spotify"));
        Assert.False(resolver.Supports("deezer"));
        Assert.False(resolver.Supports("qobuz"));
        Assert.False(resolver.Supports("applemusic"));
        Assert.False(resolver.Supports("ytmusic"));
        Assert.False(resolver.Supports("tidal"));
    }

    [Fact]
    public async Task AnEmptyTrackListResolvesToNothing()
    {
        Assert.Empty(await CreateResolver().ResolveAsync("spotify", [], CancellationToken.None));
    }

    [Fact]
    public void TheAppleRowReaderReturnsNothingForAnEmptyLookup()
    {
        // An ISRC that matches nothing comes back as an empty data array, not an error. Treating
        // that as a match would record an empty catalog id and the write would address nothing.
        using var empty = System.Text.Json.JsonDocument.Parse("""{"data":[]}""");

        Assert.Null(PlatformTrackIdentityResolver.ReadAppleSongId(empty.RootElement));
    }

    // ------------------------------------------------------------------ YouTube Music

    private sealed class SearchHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Func<HttpRequestMessage, string> _body;

        public SearchHandler(Func<HttpRequestMessage, string> body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body(request), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static YouTubeMusicSearchClient SearchClient(SearchHandler handler)
        => new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) });

    /// <summary>
    /// The endpoint's row shape as ytmusicapi walks it: one shelf of responsive rows, each with a
    /// title column, an "artist • album" column, a duration column, and the video id.
    /// </summary>
    private static string RowResponse(string title = "Paranoid Android")
    {
        static object? Column(params object?[] runs)
            => new Dictionary<string, object?>
            {
                ["musicResponsiveListItemFlexColumnRenderer"] = new Dictionary<string, object?>
                {
                    ["text"] = new Dictionary<string, object?> { ["runs"] = runs },
                },
            };

        static object? LinkedRun(string text, string browseId)
            => new Dictionary<string, object?>
            {
                ["text"] = text,
                ["navigationEndpoint"] = new Dictionary<string, object?>
                {
                    ["browseEndpoint"] = new Dictionary<string, object?> { ["browseId"] = browseId },
                },
            };

        var row = new Dictionary<string, object?>
        {
            ["musicResponsiveListItemRenderer"] = new Dictionary<string, object?>
            {
                ["flexColumns"] = new object?[]
                {
                    Column(new Dictionary<string, object?> { ["text"] = title }),
                    Column(
                        LinkedRun("Radiohead", "UCgw8vQvV"),
                        new Dictionary<string, object?> { ["text"] = " \u2022 " },
                        LinkedRun("OK Computer", "MPREbYykK")),
                    Column(new Dictionary<string, object?> { ["text"] = "6:23" }),
                },
                ["playlistItemData"] = new Dictionary<string, object?> { ["videoId"] = "abc123XYZ" },
            },
        };

        var body = new Dictionary<string, object?>
        {
            ["contents"] = new Dictionary<string, object?>
            {
                ["tabbedSearchResultsRenderer"] = new Dictionary<string, object?>
                {
                    ["tabs"] = new object?[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["tabRenderer"] = new Dictionary<string, object?>
                            {
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["sectionListRenderer"] = new Dictionary<string, object?>
                                    {
                                        ["contents"] = new object?[]
                                        {
                                            new Dictionary<string, object?>
                                            {
                                                ["musicShelfRenderer"] = new Dictionary<string, object?>
                                                {
                                                    ["contents"] = new object?[] { row },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(body);
    }

    [Fact]
    public async Task YouTubeMusicResolvesAnIdentityFromTheInnertubeSearch()
    {
        // YouTube Music exposes no ISRC, so the search is the only route this platform's tracks
        // have to a destination id at all.
        var handler = new SearchHandler(_ => RowResponse());
        var resolver = new PlatformTrackIdentityResolver(_repository, youtubeMusic: SearchClient(handler));

        var resolved = await resolver.ResolveAsync("ytmusic", [Track(1)], CancellationToken.None);

        Assert.Equal("abc123XYZ", resolved[1]);
        Assert.True(handler.Calls > 0);
    }

    [Fact]
    public async Task AYouTubeMusicRowDescribingADifferentTrackIsNotRecorded()
    {
        // A wrong id writes a different song into the user's playlist and looks exactly like a
        // success, so the candidate has to survive the validator rather than be taken first.
        var handler = new SearchHandler(_ => RowResponse(title: "Karma Police"));
        var resolver = new PlatformTrackIdentityResolver(_repository, youtubeMusic: SearchClient(handler));

        var resolved = await resolver.ResolveAsync("ytmusic", [Track(1)], CancellationToken.None);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task AFailedYouTubeMusicSearchLeavesTheTrackUnresolvedRatherThanInventingOne()
    {
        // The client throws on a non-success status. That has to read as "no answer" and not be
        // confused with a search that answered and found nothing.
        var handler = new SearchHandler(_ => RowResponse(), HttpStatusCode.InternalServerError);
        var resolver = new PlatformTrackIdentityResolver(_repository, youtubeMusic: SearchClient(handler));

        var resolved = await resolver.ResolveAsync("ytmusic", [Track(1)], CancellationToken.None);

        Assert.Empty(resolved);
    }

    // ------------------------------------------------------------------------ Apple

    private PlatformTrackIdentityResolver AppleResolver(
        AppleHandler handler,
        Func<CancellationToken, Task<AppleTargetCredentials?>>? credentials = null)
        => new(
            _repository,
            apple: new AppleMusicCatalogService(
                new AppleClientFactory(handler),
                new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance),
                NullLogger<AppleMusicCatalogService>.Instance,
                _cache),
            logger: NullLogger<PlatformTrackIdentityResolver>.Instance,
            appleCredentials: credentials);

    /// <summary>
    /// A search answer in the shape the catalog actually returns: rows are nested under
    /// <c>results.songs.data</c>, not the top-level <c>data</c> array an ISRC filter answers with.
    /// </summary>
    private static string AppleSearchPayload(string title = "Paranoid Android")
        => $$"""
        {
          "results": {
            "songs": {
              "data": [
                {
                  "id": "1440871064",
                  "type": "songs",
                  "attributes": {
                    "name": "{{title}}",
                    "artistName": "Radiohead",
                    "albumName": "OK Computer",
                    "durationInMillis": 383000
                  }
                }
              ]
            }
          }
        }
        """;

    [Fact]
    public async Task AnAppleTrackWithNoIsrcFallsBackToTheCatalogSearch()
    {
        // A track carrying no ISRC - most of what a non-library rip contains - used to bail out
        // before any request was made, so it stayed unresolved for ever while every other
        // destination already fell back to a query search.
        var handler = new AppleHandler(AppleSearchPayload());

        var resolved = await AppleResolver(handler)
            .ResolveAsync("applemusic", [Track(1, isrc: null)], CancellationToken.None);

        Assert.Equal("1440871064", resolved[1]);
        Assert.Contains(handler.Requests, url => url.Contains("/search?term=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAppleStorefrontComesFromTheConfiguredCredential()
    {
        // The storefront decides which catalog every lookup is scoped to. It used to come from the
        // catalog service's own default - always "us" - so an account configured for anything else
        // searched a catalog its own tracks are not in.
        var handler = new AppleHandler(AppleSearchPayload());
        var resolver = AppleResolver(handler, _ => Task.FromResult<AppleTargetCredentials?>(
            new AppleTargetCredentials("authorization-token", "media-user-token", "gb")));

        var resolved = await resolver
            .ResolveAsync("applemusic", [Track(1, isrc: null)], CancellationToken.None);

        Assert.Equal("1440871064", resolved[1]);
        Assert.Contains(handler.Requests, url => url.Contains("/v1/catalog/gb/search?", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("/v1/catalog/us/search?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAppleStorefrontIsReadOnceForTheWholePass()
    {
        // Reading it per track would pay a settings read for every unresolved row of a playlist.
        var calls = 0;
        var handler = new AppleHandler(AppleSearchPayload());
        var resolver = AppleResolver(handler, _ =>
        {
            calls++;
            return Task.FromResult<AppleTargetCredentials?>(
                new AppleTargetCredentials("authorization-token", "media-user-token", "gb"));
        });

        var resolved = await resolver.ResolveAsync(
            "applemusic", [Track(1, isrc: null), Track(2, isrc: null)], CancellationToken.None);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AnAppleSearchRowDescribingADifferentTrackIsNotRecorded()
    {
        // A search returns relevance-ranked rows; taking the first one writes the wrong song into
        // the user's playlist and looks exactly like a success.
        var handler = new AppleHandler(AppleSearchPayload(title: "Karma Police"));

        var resolved = await AppleResolver(handler)
            .ResolveAsync("applemusic", [Track(1, isrc: null)], CancellationToken.None);

        Assert.Empty(resolved);
    }

    /// <summary>
    /// Serves the two hosts an Apple lookup touches: the token page the service scrapes for its
    /// catalog token, and the amp-api catalog itself. Anything amp-api asks for that this test does
    /// not care about comes back as an empty ISRC answer rather than an error, so a future extra
    /// request does not turn these tests red for the wrong reason.
    /// </summary>
    private sealed class AppleHandler : HttpMessageHandler
    {
        private readonly string _searchPayload;

        public AppleHandler(string searchPayload) => _searchPayload = searchPayload;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add(url);

            return Task.FromResult(url.Contains("amp-api.music.apple.com", StringComparison.Ordinal)
                ? Respond(_searchPayload, "application/json")
                : Respond(FakeCatalogJwt(), "text/html"));
        }

        private static HttpResponseMessage Respond(string body, string contentType)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };

        /// <summary>
        /// A token shaped the way the scraper expects - three base64url segments, an AMP issuer and
        /// an unexpired <c>exp</c> - so the scrape succeeds on the first page without a network
        /// dependency or a change to anyone's real settings file.
        /// </summary>
        private static string FakeCatalogJwt()
        {
            static string Segment(object payload)
                => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');

            return string.Join(
                '.',
                Segment(new { alg = "ES256", typ = "JWT" }),
                Segment(new { iss = "AMP", exp = 4102444800 }),
                Segment(new { d = "test-signature" }));
        }
    }

    private sealed class AppleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public AppleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
