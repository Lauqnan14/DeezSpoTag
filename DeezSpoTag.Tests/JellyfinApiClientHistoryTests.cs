using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class JellyfinApiClientHistoryTests
{
    [Fact]
    public async Task GetAudioPlayHistoryAsync_WithLibraryId_AddsParentIdScope()
    {
        Uri? requestUri = null;
        string? apiToken = null;
        using var handler = new StubHandler(request =>
        {
            requestUri = request.RequestUri;
            apiToken = request.Headers.GetValues("X-Emby-Token").Single();
            return Json("""
                {
                  "Items": [
                    {
                      "Id": "track-1",
                      "Name": "Track One",
                      "Artists": ["Artist"],
                      "Album": "Album",
                      "Path": "/media/Gold/Artist/Track One.flac",
                      "RunTimeTicks": 1815000000,
                      "UserData": { "LastPlayedDate": "2026-07-12T12:00:00Z" }
                    }
                  ]
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new JellyfinApiClient(httpClient);

        var history = await client.GetAudioPlayHistoryAsync(
            "http://jellyfin.local",
            "api-secret",
            "user-1",
            "library/gold",
            limit: 25,
            cancellationToken: CancellationToken.None);

        Assert.Single(history);
        Assert.Equal("api-secret", apiToken);
        Assert.NotNull(requestUri);
        Assert.Contains("ParentId=library%2Fgold", requestUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Limit=25", requestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAudioPlayHistoryAsync_LegacyOverload_RemainsUnscoped()
    {
        Uri? requestUri = null;
        using var handler = new StubHandler(request =>
        {
            requestUri = request.RequestUri;
            return Json("""{"Items":[]}""");
        });
        using var httpClient = new HttpClient(handler);
        var client = new JellyfinApiClient(httpClient);

        var history = await client.GetAudioPlayHistoryAsync(
            "http://jellyfin.local",
            "api-secret",
            "user-1",
            limit: 10,
            cancellationToken: CancellationToken.None);

        Assert.Empty(history);
        Assert.NotNull(requestUri);
        Assert.DoesNotContain("ParentId=", requestUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
