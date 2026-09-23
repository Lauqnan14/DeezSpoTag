using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class JellyfinApiClientArtistLookupTest
{
    [Fact]
    public async Task GetSystemInfoAsync_UsesJellyfin12MediaBrowserAuthorization()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("MediaBrowser", request.Headers.Authorization!.Scheme);
            Assert.Equal("Token=\"api-key\"", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            return Json("{\"ServerName\":\"Jellyfin\",\"Version\":\"12.0.0\"}");
        });
        var client = new JellyfinApiClient(new HttpClient(handler));

        var systemInfo = await client.GetSystemInfoAsync(
            "http://jellyfin.local",
            "api-key",
            CancellationToken.None);

        Assert.NotNull(systemInfo);
    }

    [Fact]
    public async Task FindArtistIdsAsync_ReturnsAllExactNameMatches()
    {
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/Artists")
            {
                return Json("{\"Items\":[{\"Id\":\"jf-1\",\"Name\":\"Artist\"},{\"Id\":\"jf-2\",\"Name\":\"Artist\"},{\"Id\":\"jf-3\",\"Name\":\"Artist X\"}]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new JellyfinApiClient(new HttpClient(handler));

        var ids = await client.FindArtistIdsAsync(
            "http://jellyfin.local",
            "api-key",
            "Artist",
            CancellationToken.None);

        Assert.Equal(2, ids.Count);
        Assert.Contains("jf-1", ids);
        Assert.Contains("jf-2", ids);
        Assert.DoesNotContain("jf-3", ids);
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
