using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Navidrome;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NavidromeApiClientHistoryTests
{
    [Fact]
    public async Task GetPlayHistoryAsync_AuthenticatesPagesAndStopsAtIncrementalBoundary()
    {
        using var handler = new HistoryHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);
        var sinceUtc = new DateTimeOffset(2026, 7, 12, 10, 30, 0, TimeSpan.Zero);

        var history = await client.GetPlayHistoryAsync(
            "http://navidrome.local",
            "listener",
            "secret",
            sinceUtc,
            pageSize: 2,
            CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.Equal("song-1", history[0].ItemId);
        Assert.Equal("/music/Artist/Album/Track One.flac", history[0].FilePath);
        Assert.Equal(181500, history[0].DurationMs);
        Assert.Equal("song-2", history[1].ItemId);
        Assert.Equal(2, handler.SongRequests.Count);
        Assert.Contains("_start=0", handler.SongRequests[0], StringComparison.Ordinal);
        Assert.Contains("_end=2", handler.SongRequests[0], StringComparison.Ordinal);
        Assert.Contains("_sort=playDate", handler.SongRequests[0], StringComparison.Ordinal);
        Assert.Contains("_start=2", handler.SongRequests[1], StringComparison.Ordinal);
        Assert.All(handler.SongRequests, request =>
            Assert.DoesNotContain("library_id=", request, StringComparison.OrdinalIgnoreCase));
        Assert.All(handler.NativeAuthorization, value => Assert.Equal("Bearer jwt-token", value));
    }

    [Fact]
    public async Task GetPlayHistoryAsync_WithLibraryId_FiltersNativeSongRequest()
    {
        var songRequests = new List<Uri>();
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return Json("""{"token":"jwt-token"}""");
            }

            songRequests.Add(request.RequestUri);
            return Json("""
                [
                  {
                    "id":"song-27",
                    "title":"Scoped Track",
                    "artist":"Artist",
                    "duration":200,
                    "path":"Artist/Scoped.flac",
                    "libraryPath":"/gold",
                    "libraryId":27,
                    "playDate":"2026-07-12T12:00:00Z",
                    "playCount":3
                  }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var history = await client.GetPlayHistoryAsync(
            "http://navidrome.local",
            "listener",
            "secret",
            " 27 ",
            pageSize: 10,
            cancellationToken: CancellationToken.None);

        var item = Assert.Single(history);
        Assert.Equal("27", item.LibraryId);
        var request = Assert.Single(songRequests);
        Assert.Contains("library_id=27", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLibrariesAsync_ReturnsAccessibleMusicFolderIdsAndNames()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.EndsWith("/rest/getMusicFolders.view", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("u=listener", request.RequestUri.Query, StringComparison.Ordinal);
            return Json("""
                {
                  "subsonic-response": {
                    "status": "ok",
                    "musicFolders": {
                      "musicFolder": [
                        { "id": 1, "name": "Music" },
                        { "id": 27, "name": "Gold" }
                      ]
                    }
                  }
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var libraries = await client.GetLibrariesAsync(
            "http://navidrome.local",
            "listener",
            "secret",
            CancellationToken.None);

        Assert.Equal(
            new[] { new NavidromeLibrary("1", "Music"), new NavidromeLibrary("27", "Gold") },
            libraries);
    }

    private sealed class HistoryHandler : HttpMessageHandler
    {
        public List<string> SongRequests { get; } = new();
        public List<string> NativeAuthorization { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return JsonAsync("""{"token":"jwt-token"}""");
            }

            Assert.EndsWith("/api/song", path, StringComparison.Ordinal);
            SongRequests.Add(request.RequestUri.ToString());
            NativeAuthorization.Add(request.Headers.GetValues("X-ND-Authorization").Single());
            var isSecondPage = request.RequestUri.Query.Contains("_start=2", StringComparison.Ordinal);
            return JsonAsync(isSecondPage
                ? """
                  [
                    {
                      "id":"song-old",
                      "title":"Old Track",
                      "artist":"Artist",
                      "duration":200,
                      "path":"Artist/Album/Old.flac",
                      "libraryPath":"/music",
                      "playDate":"2026-07-12T10:00:00Z",
                      "playCount":1
                    },
                    {
                      "id":"song-never-played",
                      "title":"Never Played",
                      "artist":"Artist",
                      "duration":200,
                      "path":"Artist/Album/Never.flac",
                      "libraryPath":"/music",
                      "playCount":0
                    }
                  ]
                  """
                : """
                  [
                    {
                      "id":"song-1",
                      "title":"Track One",
                      "artist":"Artist",
                      "duration":181.5,
                      "path":"Artist/Album/Track One.flac",
                      "libraryPath":"/music",
                      "playDate":"2026-07-12T12:00:00Z",
                      "playCount":4
                    },
                    {
                      "id":"song-2",
                      "title":"Track Two",
                      "artist":"Artist",
                      "duration":182,
                      "path":"/already/absolute/Track Two.flac",
                      "libraryPath":"/music",
                      "playDate":"2026-07-12T11:00:00Z",
                      "playCount":2
                    }
                  ]
                  """);
        }

        private static Task<HttpResponseMessage> JsonAsync(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
