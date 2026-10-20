using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Jellyfin;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class JellyfinApiClientArtistLookupTest
{
    [Theory]
    [InlineData("{}") ]
    [InlineData("null")]
    public async Task StrictLookupRejectsUnexpectedEnvelope(string body)
    {
        using var handler = new StubHandler(_ => Json(body));
        var client = new JellyfinApiClient(new HttpClient(handler));
        await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistIdsAsync("http://jellyfin.local", "key", "Artist", CancellationToken.None, requireSuccessfulResponse: true));
    }

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

    [Fact]
    public async Task FindArtistIdsAsync_WithStrictCaller_ClassifiesFailedSearchInsteadOfAbsence()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new JellyfinApiClient(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistIdsAsync(
            "http://jellyfin.local",
            "api-key",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
        Assert.Contains("401", failure.Message, StringComparison.Ordinal);

        // Legacy callers keep their empty-success behavior for the same server failure.
        var legacy = await client.FindArtistIdsAsync(
            "http://jellyfin.local",
            "api-key",
            "Artist",
            CancellationToken.None);
        Assert.Empty(legacy);
    }

    [Fact]
    public async Task FindArtistIdsAsync_WithStrictCaller_ClassifiesMalformedPayload()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ Items: ", System.Text.Encoding.UTF8, "application/json")
        });
        var client = new JellyfinApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistIdsAsync(
            "http://jellyfin.local",
            "api-key",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
    }

    [Fact]
    public async Task FindArtistIdsAsync_WithStrictCaller_MarksSuccessfulEmptySearchAsValidAbsence()
    {
        using var handler = new StubHandler(_ => Json("{\"Items\":[],\"TotalRecordCount\":0}"));
        var client = new JellyfinApiClient(new HttpClient(handler));

        var ids = await client.FindArtistIdsAsync(
            "http://jellyfin.local",
            "api-key",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetItemAsync_WithStrictCaller_ClassifiesFailedItemRead()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new JellyfinApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<TargetLookupException>(() => client.GetItemAsync(
            "http://jellyfin.local",
            "api-key",
            "u1",
            "jf-1",
            CancellationToken.None,
            requireSuccessfulResponse: true));

        var legacy = await client.GetItemAsync(
            "http://jellyfin.local",
            "api-key",
            "u1",
            "jf-1",
            CancellationToken.None);
        Assert.Null(legacy);
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
