using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Navidrome;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NavidromeApiClientPlaylistTests
{
    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_AppendsWithUpdatePlaylistSongIdToAdd()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlistId = await client.CreateOrUpdatePlaylistAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "Gold School",
            new[] { "song-1", "song-2" },
            existingPlaylistId: "playlist-1",
            appendMissingOnly: true,
            CancellationToken.None,
            "A reliable playlist description");

        Assert.Equal("playlist-1", playlistId);
        var update = Assert.Single(handler.RequestedUrls, url => url.Contains("/rest/updatePlaylist.view?", StringComparison.Ordinal));
        Assert.Contains("playlistId=playlist-1", update, StringComparison.Ordinal);
        Assert.Equal("A reliable playlist description", GetQueryValues(update, "comment").Single());
        Assert.Contains("songIdToAdd=song-2", update, StringComparison.Ordinal);
        Assert.DoesNotContain("songId=song-2", update, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_ResolvesCreatedPlaylistIdWhenCreateReturnsEmptySuccess()
    {
        using var handler = new NavidromePlaylistHandler(createReturnsPlaylist: false, playlistExistsInitially: false);
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlistId = await client.CreateOrUpdatePlaylistAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "Gold School",
            new[] { "song-1" },
            existingPlaylistId: null,
            appendMissingOnly: false,
            CancellationToken.None,
            "A reliable playlist description");

        Assert.Equal("playlist-1", playlistId);
        var create = Assert.Single(handler.RequestedUrls, url => url.Contains("/rest/createPlaylist.view?", StringComparison.Ordinal));
        Assert.Equal("Gold School", GetQueryValues(create, "name").Single());
        Assert.Equal("song-1", GetQueryValues(create, "songId").Single());
        var metadataUpdate = Assert.Single(handler.RequestedUrls, url => url.Contains("/rest/updatePlaylist.view?", StringComparison.Ordinal));
        Assert.Equal("A reliable playlist description", GetQueryValues(metadataUpdate, "comment").Single());
    }

    [Fact]
    public async Task UpdatePlaylistImageFromFileAsync_LogsInAndUploadsMultipartImage()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"navidrome-playlist-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        try
        {
            using var handler = new NavidromePlaylistHandler();
            using var httpClient = new HttpClient(handler);
            var client = new NavidromeApiClient(httpClient);

            var updated = await client.UpdatePlaylistImageFromFileAsync(
                "http://navidrome.local",
                "user",
                "pass",
                "playlist-1",
                imagePath,
                "image/png",
                CancellationToken.None);

            Assert.True(updated);
            Assert.Contains(handler.RequestedUrls, url => url.EndsWith("/auth/login", StringComparison.Ordinal));
            Assert.Contains(handler.RequestedUrls, url => url.EndsWith("/api/playlist/playlist-1/image", StringComparison.Ordinal));
            Assert.Equal("Bearer jwt-token", handler.UploadAuthorization);
            Assert.Contains("Content-Disposition", handler.UploadBody);
            Assert.Contains("image", handler.UploadBody);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task GetPlaylistAsync_ReturnsDescriptionAndEntries()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlist = await client.GetPlaylistAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "playlist-1",
            CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.Equal("playlist-1", playlist.Id);
        Assert.Equal("Gold School", playlist.Name);
        Assert.Equal("A reliable playlist description", playlist.Comment);
        Assert.Equal("song-1", Assert.Single(playlist.Entries).ItemId);
    }

    [Fact]
    public async Task SearchTracksAsync_ReturnsEmptyWhenServerReturnsHtml()
    {
        using var httpClient = new HttpClient(new HtmlResponseHandler());
        var client = new NavidromeApiClient(httpClient);

        var results = await client.SearchTracksAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "Gold School",
            CancellationToken.None);

        Assert.Empty(results);
    }

    private sealed class NavidromePlaylistHandler(
        bool createReturnsPlaylist = true,
        bool playlistExistsInitially = true) : HttpMessageHandler
    {
        private bool _playlistCreated;
        public List<string> RequestedUrls { get; } = new();
        public string UploadAuthorization { get; private set; } = string.Empty;
        public string UploadBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            var path = request.RequestUri.AbsolutePath;

            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return await Json("""
                    {
                      "token": "jwt-token"
                    }
                    """);
            }

            if (path.EndsWith("/api/playlist/playlist-1/image", StringComparison.Ordinal))
            {
                UploadAuthorization = request.Headers.Authorization?.ToString() ?? string.Empty;
                if (request.Headers.TryGetValues("X-ND-Authorization", out var nativeAuth))
                {
                    UploadAuthorization = nativeAuth.Single();
                }
                UploadBody = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return await Json("""
                    {
                      "status": "ok"
                    }
                    """);
            }

            if (path.EndsWith("/getPlaylists.view", StringComparison.Ordinal))
            {
                var playlists = playlistExistsInitially || _playlistCreated
                    ? """
                              "playlist": [
                                { "id": "playlist-1", "name": "Gold School", "songCount": 1 }
                              ]
                      """
                    : """
                              "playlist": []
                      """;
                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok",
                        "playlists": {
                    """ + playlists + """
                        }
                      }
                    }
                    """);
            }

            if (path.EndsWith("/getPlaylist.view", StringComparison.Ordinal))
            {
                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok",
                        "playlist": {
                          "id": "playlist-1",
                          "name": "Gold School",
                          "comment": "A reliable playlist description",
                          "entry": [
                            { "id": "song-1", "title": "Song 1", "artist": "Artist" }
                          ]
                        }
                      }
                    }
                    """);
            }

            if (path.EndsWith("/createPlaylist.view", StringComparison.Ordinal))
            {
                _playlistCreated = true;
                return await Json(createReturnsPlaylist
                    ? """
                      {
                        "subsonic-response": {
                          "status": "ok",
                          "playlist": { "id": "playlist-1", "name": "Gold School", "songCount": 1 }
                        }
                      }
                      """
                    : """
                      {
                        "subsonic-response": {
                          "status": "ok"
                        }
                      }
                      """);
            }

            if (path.EndsWith("/updatePlaylist.view", StringComparison.Ordinal))
            {
                var query = request.RequestUri.Query;
                if (GetQueryValues(url, "songIdToAdd").Count > 0)
                {
                    Assert.Contains("songIdToAdd=song-2", query, StringComparison.Ordinal);
                    Assert.DoesNotContain("songId=song-2", query, StringComparison.Ordinal);
                }
                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok"
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }

    private sealed class HtmlResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not json</html>")
            });
    }

    private static List<string> GetQueryValues(string url, string name)
    {
        var uri = new Uri(url);
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.Ordinal))
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .ToList();
    }
}
