using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DailyRecommendationProviderTest
{
    [Fact]
    public async Task TidalTrackRadioUsesTrackMixWithApplicationTokenAndAllTrackModules()
    {
        using var handler = new Handler((request, index) =>
        {
            Assert.Equal("api.tidal.com", request.RequestUri!.Host);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            Assert.Contains("countryCode=GB", request.RequestUri.Query);
            if (index == 0)
            {
                Assert.Equal("/v1/tracks/99", request.RequestUri.AbsolutePath);
                return Json("{\"mixes\":{\"TRACK_MIX\":\"mix1\"}}");
            }
            Assert.Equal("/v1/pages/mix", request.RequestUri.AbsolutePath);
            Assert.Contains("mixId=mix1", request.RequestUri.Query);
            Assert.Contains("deviceType=BROWSER", request.RequestUri.Query);
            return Json("""
                {"rows":[{"modules":[{"type":"MIX_HEADER"},
                  {"type":"TRACK_LIST","supportsPaging":false,"pagedList":{"items":[
                    {"id":99,"title":"Seed","duration":100},
                    {"id":1,"title":"First","duration":180,"isrc":"GB0000000001","ai":true,"artists":[{"name":"Artist"}],"album":{"title":"Album"}}]}},
                  {"type":"TRACK_LIST","supportsPaging":false,"pagedList":{"items":[
                    {"id":1,"title":"Duplicate","duration":180},
                    {"id":2,"title":"Second","duration":200,"ai":false}]}}]}]}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(new("test-token", "GB")));
        var result = await client.GetTrackRadioAsync("99", 20);
        Assert.Equal(TidalTrackRadioOutcome.Ready, result.Outcome);
        Assert.Equal(new[] { "1", "2" }, result.Tracks.Select(t => t.Id));
        Assert.Equal(180000, result.Tracks[0].DurationMs);
        Assert.Equal("GB0000000001", result.Tracks[0].Isrc);
        Assert.True(result.Tracks[0].Ai);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mixes\":{\"TRACK_MIX\":{}}}")]
    public async Task TidalMissingMixIsAnExplicitFailureWithoutAnotherRoute(string body)
    {
        using var handler = new Handler((_, _) => Json(body));
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(new("token")));
        var result = await client.GetTrackRadioAsync("99", 20);
        Assert.Equal(TidalTrackRadioOutcome.Failed, result.Outcome);
        Assert.Empty(result.Tracks);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TidalFailedMixIsNotAnEmptySuccess(HttpStatusCode failure)
    {
        using var handler = new Handler((_, index) => index == 0 ? Json("{\"mixes\":{\"TRACK_MIX\":\"mix1\"}}") : new HttpResponseMessage(failure));
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(new("token")));
        var result = await client.GetTrackRadioAsync("99", 20);
        Assert.Equal(TidalTrackRadioOutcome.Failed, result.Outcome);
        Assert.Empty(result.Tracks);
    }

    [Fact]
    public async Task TidalUnavailableApplicationTokenMakesNoProviderCalls()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not call provider"));
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(null));
        Assert.Equal(TidalTrackRadioOutcome.Unavailable, (await client.GetTrackRadioAsync("99", 20)).Outcome);
        Assert.Equal(0, handler.Count);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTrackRadioAsync("99", 20, cts.Token));
    }

    [Theory]
    [InlineData("0eGsygTp906u18L0Oimnem", "37i9dQZF1E8IrAPFM8awVr")]
    [InlineData("4hM9jLSD1lgswviJTkHsPP", "37i9dQZF1E8zBYZSL2HCjC")]
    public async Task SpotifyUsesEachSeedSongRadioAndLoadsItsPlaylist(string seedId, string playlistId)
    {
        var seed = Track(seedId, "seed-album") with { Isrc = "US0000000001" };
        var calls = new List<string>();
        Task<SpotifyUrlMetadata?> Metadata(string url, CancellationToken token)
        {
            calls.Add(url);
            if (url == "https://open.spotify.com/track/" + seedId)
                return Task.FromResult<SpotifyUrlMetadata?>(MetadataResult(new[] { seed }));
            Assert.Equal("https://open.spotify.com/playlist/" + playlistId, url);
            return Task.FromResult<SpotifyUrlMetadata?>(MetadataResult(new[] { seed, Track("recommendation", "related-album"), Track("recommendation", "related-album") }));
        }
        Task<string?> Radio(string id, CancellationToken token)
        {
            Assert.Equal(seedId, id);
            return Task.FromResult<string?>("spotify:playlist:" + playlistId);
        }
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/" + seedId, 20, Metadata, Radio, CancellationToken.None);
        Assert.Equal("recommendation", Assert.Single(tracks).Id);
        Assert.Equal(new[] { "https://open.spotify.com/track/" + seedId, "https://open.spotify.com/playlist/" + playlistId }, calls);
    }

    [Fact]
    public async Task SpotifyUnavailableSeedDoesNotInventRecommendationContext()
    {
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/0eGsygTp906u18L0Oimnem", 20,
            (_, _) => Task.FromResult<SpotifyUrlMetadata?>(null), (_, _) => throw new InvalidOperationException("No seed context"), CancellationToken.None);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task SpotifyRadioExcludesSeedRecordingAndRespectsLimit()
    {
        var seed = Track("seed", "album");
        var alias = Track("alias", "album") with { Isrc = seed.Isrc };
        Task<SpotifyUrlMetadata?> Metadata(string url, CancellationToken token) => Task.FromResult<SpotifyUrlMetadata?>(
            MetadataResult(url.Contains("/track/", StringComparison.Ordinal) ? new[] { seed } : new[] { seed, alias, Track("one", "album"), Track("two", "album") }));
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/seed", 1,
            Metadata, (_, _) => Task.FromResult<string?>("spotify:playlist:37i9dQZF1E8IrAPFM8awVr"), CancellationToken.None);
        Assert.Equal("one", Assert.Single(tracks).Id);
    }

    [Fact]
    public async Task SpotifySongRadioSendsVerifiedAuthenticatedRequest()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://spclient.wg.spotify.com/inspiredby-mix/v2/seed_to_playlist/spotify:track:0eGsygTp906u18L0Oimnem?response-format=json", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
            Assert.Equal("client", Assert.Single(request.Headers.GetValues("Client-Token")));
            Assert.Equal("version", Assert.Single(request.Headers.GetValues("Spotify-App-Version")));
            Assert.Equal("WebPlayer", Assert.Single(request.Headers.GetValues("App-Platform")));
            return Json("{\"total\":1,\"mediaItems\":[{\"uri\":\"spotify:playlist:37i9dQZF1E8IrAPFM8awVr\"}]}");
        });
        using var http = new HttpClient(handler);
        var uri = await SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None);
        Assert.Equal("spotify:playlist:37i9dQZF1E8IrAPFM8awVr", uri);
    }

    [Fact]
    public async Task SpotifyRadioReadsUtf8JsonEvenWhenResponseCharsetAliasIsInvalid()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Json("{\"total\":1,\"mediaItems\":[{\"uri\":\"spotify:playlist:37i9dQZF1E8IrAPFM8awVr\"}]}");
            response.Content.Headers.ContentType!.CharSet = "utf8";
            return response;
        });
        using var http = new HttpClient(handler);
        Assert.Equal("spotify:playlist:37i9dQZF1E8IrAPFM8awVr", await SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SpotifyRadioHttpFailureDoesNotBecomeAlbumRecommendations(HttpStatusCode status)
    {
        using var handler = new Handler((_, _) => new(status));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mediaItems\":{}}")]
    [InlineData("{\"mediaItems\":[{\"uri\":\"https://evil.example/playlist\"}]}")]
    public async Task SpotifyRadioRejectsMalformedOrUntrustedPlaylistPayload(string json)
    {
        using var handler = new Handler((_, _) => Json(json));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
    }

    [Fact]
    public async Task SpotifyEmptyRadioDoesNotFetchAlternativeRecommendations()
    {
        using var handler = new Handler((_, _) => Json("{\"total\":0,\"mediaItems\":[]}"));
        using var http = new HttpClient(handler);
        Assert.Null(await SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/seed", 20,
            (url, _) => url.Contains("/track/", StringComparison.Ordinal) ? Task.FromResult<SpotifyUrlMetadata?>(MetadataResult(new[] { Track("seed", "album") })) : throw new InvalidOperationException("No playlist available"),
            (_, _) => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task SpotifyRadioCancellationDoesNotCallProvider()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Cancelled request must not run"));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", cancellation.Token));
        Assert.Equal(0, handler.Count);
    }

    internal static SpotifyTrackSummary Track(string id, string album) => new(id, "Song " + id, "Artist", "Album", 180000, "https://open.spotify.com/track/" + id, null, id == "seed" ? "US0000000001" : "US0000000002") { AlbumId = album };
    private static SpotifyUrlMetadata MetadataResult(IEnumerable<SpotifyTrackSummary> tracks) => new("album", "album", "Album", "", null, null, null, null, tracks.ToList(), new(), null, null, null);
    private static string Page(string id, string? next) => "{\"data\":[{\"type\":\"tracks\",\"id\":\"" + id + "\",\"attributes\":{\"title\":\"Track " + id + "\",\"duration\":\"PT3M\"}}],\"links\":{\"next\":" + (next is null ? "null" : "\"https://openapi.tidal.com/v2/tracks/99/relationships/similarTracks?page%5Bcursor%5D=" + next + "\"") + "}}";
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/vnd.api+json") };
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, Count++));
        }
    }
}
