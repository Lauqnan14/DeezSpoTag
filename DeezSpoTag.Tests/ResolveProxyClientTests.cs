using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ResolveProxyClientTests
{
    private const string SpotifyTrackId = "4uLU6hMCjMI75M1A2tKUQC";

    [Fact]
    public async Task ResolveUrlAsync_MapsResolveProxySongUrls()
    {
        string? requestBody = null;
        var client = CreateClient(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.zarz.moe", request.RequestUri?.Host);
            return Json("""
{
  "success": true,
  "isrc": "USRC17607839",
  "songUrls": {
    "Spotify": "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC",
    "Deezer": "https://www.deezer.com/track/3135556",
    "Tidal": "https://listen.tidal.com/track/202",
    "Qobuz": "https://open.qobuz.com/track/303",
    "AmazonMusic": "https://music.amazon.com/tracks/amz1",
    "AppleMusic": "https://music.apple.com/us/song/name/1440857781?i=1440857781",
    "YouTubeMusic": "https://music.youtube.com/watch?v=ytm1"
  }
}
""");
        });

        var result = await client.ResolveUrlAsync($"https://open.spotify.com/track/{SpotifyTrackId}", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("\"url\"", requestBody, StringComparison.Ordinal);
        Assert.Equal(SpotifyTrackId, result!.SpotifyId);
        Assert.Equal("3135556", result.DeezerId);
        Assert.Equal("https://www.deezer.com/track/3135556", result.DeezerUrl);
        Assert.Equal("https://listen.tidal.com/track/202", result.TidalUrl);
        Assert.Equal("https://open.qobuz.com/track/303", result.QobuzUrl);
        Assert.Equal("https://music.amazon.com/tracks/amz1", result.AmazonUrl);
        Assert.Equal("https://music.apple.com/us/song/name/1440857781?i=1440857781", result.AppleMusicUrl);
        Assert.Equal("ytm1", result.YouTubeId);
        Assert.Equal("USRC17607839", result.Isrc);
    }

    [Fact]
    public async Task ResolvePlatformIdAsync_SendsPlatformPayload()
    {
        string? requestBody = null;
        var client = CreateClient(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            return Json("""
{
  "success": true,
  "songUrls": {
    "Spotify": "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC",
    "Qobuz": ["https://play.qobuz.com/track/303"]
  }
}
""");
        });

        var result = await client.ResolvePlatformIdAsync("spotify", "song", SpotifyTrackId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("\"platform\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"id\"", requestBody, StringComparison.Ordinal);
        Assert.Equal("https://play.qobuz.com/track/303", result!.QobuzUrl);
    }

    [Fact]
    public async Task ResolveByPlatformIdAsync_UsesResolveProxyPlatformLookup()
    {
        string? requestBody = null;
        var resolver = new SongLinkResolver(new SongLinkResolver.Dependencies
        {
            HttpClientFactory = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
            Logger = NullLogger<SongLinkResolver>.Instance,
            ResolveProxyClient = CreateClient(request =>
            {
                requestBody = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                return Json("""
{
  "success": true,
  "isrc": "USRC17607839",
  "songUrls": {
    "Spotify": "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC",
    "Deezer": "https://www.deezer.com/track/3135556",
    "Tidal": "https://listen.tidal.com/track/202",
    "Qobuz": "https://open.qobuz.com/track/303",
    "AmazonMusic": "https://music.amazon.com/tracks/amz1",
    "AppleMusic": "https://music.apple.com/us/song/name/1440857781?i=1440857781"
  }
}
""");
            })
        });

        var result = await resolver.ResolveByPlatformIdAsync("qobuz", "song", "303", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("\"platform\":\"qobuz\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"song\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"303\"", requestBody, StringComparison.Ordinal);
        Assert.Equal(SpotifyTrackId, result!.SpotifyId);
        Assert.Equal("3135556", result.DeezerId);
        Assert.Equal("https://listen.tidal.com/track/202", result.TidalUrl);
        Assert.Equal("https://music.amazon.com/tracks/amz1", result.AmazonUrl);
        Assert.Equal("https://music.apple.com/us/song/name/1440857781?i=1440857781", result.AppleMusicUrl);
        Assert.Equal("USRC17607839", result.Isrc);
    }

    [Fact]
    public async Task ResolveByUrlAsync_UsesResolveProxyForDeezerUrl()
    {
        string? requestBody = null;
        var resolver = new SongLinkResolver(new SongLinkResolver.Dependencies
        {
            HttpClientFactory = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
            Logger = NullLogger<SongLinkResolver>.Instance,
            ResolveProxyClient = CreateClient(request =>
            {
                requestBody = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                return Json("""
{
  "success": true,
  "songUrls": {
    "Spotify": "https://open.spotify.com/track/4S8PxReB1UiDR2F5x1lyIR",
    "Deezer": "https://www.deezer.com/track/3021278461",
    "AmazonMusic": "https://music.amazon.com/tracks/B0D4QF7ABC"
  }
}
""");
            })
        });

        var result = await resolver.ResolveByUrlAsync("https://www.deezer.com/track/3021278461", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("\"url\":\"https://www.deezer.com/track/3021278461\"", requestBody, StringComparison.Ordinal);
        Assert.Equal("3021278461", result!.DeezerId);
        Assert.Equal("4S8PxReB1UiDR2F5x1lyIR", result.SpotifyId);
        Assert.Equal("https://music.amazon.com/tracks/B0D4QF7ABC", result.AmazonUrl);
    }

    [Fact]
    public async Task ResolveUrlAsync_ReturnsNull_WhenProxyFails()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await client.ResolveUrlAsync($"https://open.spotify.com/track/{SpotifyTrackId}", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveByUrlAsync_FallsBackToSongLink_WhenResolveProxyFails()
    {
        var resolver = new SongLinkResolver(new SongLinkResolver.Dependencies
        {
            HttpClientFactory = new StubHttpClientFactory(request =>
            {
                if (request.RequestUri?.Host.Equals("api.zarz.moe", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                }

                if (request.RequestUri?.Host.Equals("api.song.link", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Json("""
{
  "linksByPlatform": {
    "spotify": { "url": "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC" },
    "tidal": { "url": "https://listen.tidal.com/track/202" },
    "qobuz": { "url": "https://open.qobuz.com/track/303" },
    "deezer": { "url": "https://www.deezer.com/track/3135556" }
  }
}
""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }),
            Logger = NullLogger<SongLinkResolver>.Instance,
            ResolveProxyClient = CreateClient(request =>
            {
                Assert.Equal("api.zarz.moe", request.RequestUri?.Host);
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            })
        });

        var result = await resolver.ResolveByUrlAsync($"https://open.spotify.com/track/{SpotifyTrackId}", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://listen.tidal.com/track/202", result!.TidalUrl);
        Assert.Equal("https://open.qobuz.com/track/303", result.QobuzUrl);
        Assert.Equal("3135556", result.DeezerId);
    }

    private static ResolveProxyClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StubHttpClientFactory(responder),
            NullLogger<ResolveProxyClient>.Instance);

    private static HttpResponseMessage Json(string payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        };
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(responder), disposeHandler: true);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
