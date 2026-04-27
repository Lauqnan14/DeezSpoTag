using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SongLinkResolverQobuzUrlGenerationTests
{
    [Fact]
    public async Task ResolveByUrlAsync_AppleSongWithoutQueryId_CreatesQobuzUrl()
    {
        const string appleTrackId = "1716080873";
        var appleUrl = $"https://music.apple.com/us/song/yoyo-feat-sewersydaa/{appleTrackId}";
        var resolver = new SongLinkResolver(
            new StubHttpClientFactory(request =>
            {
                if (request.RequestUri?.Host.Equals("itunes.apple.com", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Json("""
{
  "resultCount": 1,
  "results": [
    {
      "trackId": 1716080873,
      "trackName": "YoYo (feat. Sewersydaa)",
      "artistName": "Virusi Mbaya",
      "collectionName": "YoYo",
      "trackTimeMillis": 204000
    }
  ]
}
""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }),
            qobuzMetadataService: new StubQobuzMetadataService(query =>
            {
                return query.Contains("Virusi Mbaya", StringComparison.OrdinalIgnoreCase)
                    ? [new QobuzTrack { Id = 411245095, Title = "YoYo", Duration = 204, Performer = new QobuzArtist { Name = "Virusi Mbaya" } }]
                    : [];
            }),
            qobuzTrackResolver: null,
            qobuzOptions: Options.Create(new QobuzApiConfig { DefaultStore = "us-en" }),
            NullLogger<SongLinkResolver>.Instance);

        var result = await resolver.ResolveByUrlAsync(appleUrl, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://play.qobuz.com/track/411245095", result!.QobuzUrl);
    }

    [Fact]
    public async Task ResolveByUrlAsync_DeezerTrack_CreatesQobuzUrl()
    {
        const string deezerId = "3135556";
        var deezerUrl = $"https://www.deezer.com/track/{deezerId}";
        var resolver = new SongLinkResolver(
            new StubHttpClientFactory(request =>
            {
                if (request.RequestUri?.Host.Equals("api.deezer.com", StringComparison.OrdinalIgnoreCase) == true
                    && request.RequestUri.AbsolutePath.Contains($"/track/{deezerId}", StringComparison.Ordinal))
                {
                    return Json("""
{
  "id": 3135556,
  "title": "Harder Better Faster Stronger",
  "isrc": "GBDUW0000059",
  "duration": 224,
  "artist": { "name": "Daft Punk" },
  "album": { "title": "Discovery" }
}
""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }),
            qobuzMetadataService: new StubQobuzMetadataService(query =>
            {
                return query.Contains("Daft Punk", StringComparison.OrdinalIgnoreCase)
                    ? [new QobuzTrack { Id = 99112233, Title = "Harder, Better, Faster, Stronger", Duration = 224, Performer = new QobuzArtist { Name = "Daft Punk" } }]
                    : [];
            }),
            qobuzTrackResolver: null,
            qobuzOptions: Options.Create(new QobuzApiConfig { DefaultStore = "us-en" }),
            NullLogger<SongLinkResolver>.Instance);

        var result = await resolver.ResolveByUrlAsync(deezerUrl, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://play.qobuz.com/track/99112233", result!.QobuzUrl);
    }

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

    private sealed class StubQobuzMetadataService(Func<string, List<QobuzTrack>> searchHandler) : IQobuzMetadataService
    {
        public Task<QobuzTrack?> FindTrackByISRC(string isrc, CancellationToken ct) => Task.FromResult<QobuzTrack?>(null);

        public Task<QobuzAlbum?> FindAlbumByUPC(string upc, CancellationToken ct) => Task.FromResult<QobuzAlbum?>(null);

        public Task<QobuzArtist?> FindArtistByName(string name, CancellationToken ct) => Task.FromResult<QobuzArtist?>(null);

        public Task<List<QobuzTrack>> SearchTracks(string query, CancellationToken ct) => Task.FromResult(searchHandler(query));

        public Task<List<QobuzTrack>> SearchTracksAutosuggest(string query, string? store, CancellationToken ct)
            => Task.FromResult(new List<QobuzTrack>());

        public Task<List<QobuzAlbum>> SearchAlbums(string query, CancellationToken ct) => Task.FromResult(new List<QobuzAlbum>());

        public Task<List<QobuzArtist>> SearchArtists(string query, CancellationToken ct) => Task.FromResult(new List<QobuzArtist>());

        public Task<QobuzArtist?> GetArtistDiscography(int artistId, string store, CancellationToken ct)
            => Task.FromResult<QobuzArtist?>(null);

        public Task<List<QobuzAlbum>> GetArtistAlbums(int artistId, string store, CancellationToken ct)
            => Task.FromResult(new List<QobuzAlbum>());

        public Task<QobuzQualityInfo?> GetTrackQuality(int trackId, CancellationToken ct)
            => Task.FromResult<QobuzQualityInfo?>(null);
    }
}
