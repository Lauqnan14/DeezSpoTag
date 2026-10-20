using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Navidrome;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Navidrome artist lookup/read responses: legacy callers keep their empty-success behavior, while
/// strict callers (the artwork audits) classify failed HTTP, malformed JSON and Subsonic application
/// errors that arrive under HTTP 200 as lookup errors instead of trusted absence.
/// </summary>
public sealed class NavidromeApiClientArtistLookupTest
{
    [Theory]
    [InlineData("{}") ]
    [InlineData("null")]
    public async Task StrictLookupRejectsUnexpectedEnvelope(string body)
    {
        using var handler = new StubHandler(_ => Json(body));
        var client = new NavidromeApiClient(new HttpClient(handler));
        await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistIdsAsync("http://navidrome.local", "user", "password", "Artist", CancellationToken.None, requireSuccessfulResponse: true));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FindArtistIdsAsync_WithStrictCaller_ClassifiesFailedHttp(HttpStatusCode status)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status));
        var client = new NavidromeApiClient(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistIdsAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
        Assert.Contains(((int)status).ToString(), failure.Message, StringComparison.Ordinal);

        // Legacy callers keep their empty-success behavior for the same server failure.
        var legacy = await client.FindArtistIdsAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "Artist",
            CancellationToken.None);
        Assert.Empty(legacy);
    }

    [Fact]
    public async Task FindArtistIdsAsync_WithStrictCaller_ClassifiesSubsonicErrorEnvelopeUnderHttp200()
    {
        using var handler = new StubHandler(_ => Json(
            "{\"subsonic-response\":{\"status\":\"failed\",\"version\":\"1.16.1\",\"error\":{\"code\":40,\"message\":\"wrong username or password\"}}}"));
        var client = new NavidromeApiClient(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistIdsAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
        Assert.Contains("wrong username or password", failure.Message, StringComparison.Ordinal);

        var legacy = await client.FindArtistIdsAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "Artist",
            CancellationToken.None);
        Assert.Empty(legacy);
    }

    [Fact]
    public async Task FindArtistIdsAsync_WithStrictCaller_ClassifiesMalformedJson()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ subsonic-response: ", Encoding.UTF8, "application/json")
        });
        var client = new NavidromeApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistIdsAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
    }

    [Fact]
    public async Task FindArtistIdsAsync_WithStrictCaller_MarksSuccessfulEmptySearchAsValidAbsence()
    {
        using var handler = new StubHandler(_ => Json(
            "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{\"artist\":[]}}}"));
        var client = new NavidromeApiClient(new HttpClient(handler));

        // An authoritative empty search is genuine absence, not an error, so strict mode returns it.
        var ids = await client.FindArtistIdsAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task FindArtistIdsAsync_WithStrictCaller_ReturnsExactMatches()
    {
        using var handler = new StubHandler(_ => Json(
            "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{\"artist\":[{\"id\":\"n1\",\"name\":\"Artist\"},{\"id\":\"n2\",\"name\":\"Artist Tribute\"}]}}}\n"));
        var client = new NavidromeApiClient(new HttpClient(handler));

        var ids = await client.FindArtistIdsAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true);
        Assert.Equal(["n1"], ids);
    }

    [Fact]
    public async Task GetArtistInfoAsync_WithStrictCaller_ClassifiesSubsonicErrorEnvelopeUnderHttp200()
    {
        using var handler = new StubHandler(_ => Json(
            "{\"subsonic-response\":{\"status\":\"failed\",\"error\":{\"code\":0,\"message\":\"generic\"}}}"));
        var client = new NavidromeApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<TargetLookupException>(() => client.GetArtistInfoAsync(
            "http://navidrome.local:4533",
            "admin",
            "password",
            "n1",
            CancellationToken.None,
            requireSuccessfulResponse: true));
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
