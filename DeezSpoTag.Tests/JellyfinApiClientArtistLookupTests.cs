using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class JellyfinApiClientArtistLookupTests
{
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
