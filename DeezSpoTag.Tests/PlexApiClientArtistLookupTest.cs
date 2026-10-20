using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexApiClientArtistLookupTest
{
    [Theory]
    [InlineData("<Error />")]
    [InlineData("<html />")]
    public async Task StrictLookupRejectsUnexpectedEnvelope(string body)
    {
        using var handler = new StubHandler(_ => Xml(body));
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, new HttpClient(handler));
        await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistLocationsAsync("http://plex.local", "token", "Artist", CancellationToken.None, requireSuccessfulResponse: true));
    }

    [Fact]
    public async Task FindArtistLocationsAsync_ReturnsAllTopMatchesAcrossSections()
    {
        using var handler = new StubHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /><Directory type=\"artist\" key=\"2\" /></MediaContainer>");
            }

            if (path.Contains("/library/search", StringComparison.Ordinal)
                || path.Contains("/library/sections/1/all", StringComparison.Ordinal)
                || path.Contains("/library/sections/2/all", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist\" librarySectionID=\"1\" /><Directory type=\"artist\" ratingKey=\"rk2\" title=\"Artist\" librarySectionID=\"2\" /></MediaContainer>");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var matches = await client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, location => location.SectionKey == "1" && location.RatingKey == "rk1");
        Assert.Contains(matches, location => location.SectionKey == "2" && location.RatingKey == "rk2");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FindArtistLocationsAsync_WithStrictCaller_ClassifiesServerFailuresAsLookupErrors(HttpStatusCode status)
    {
        using var handler = new StubHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            return new HttpResponseMessage(status) { Content = new StringContent("<MediaContainer />") };
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        // A strict lookup surfaces the failure: a section or search rejection after the section list
        // succeeded is not evidence that the artist is absent.
        var failure = await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
        Assert.Contains(((int)status).ToString(), failure.Message, StringComparison.Ordinal);

        // Legacy callers keep their empty-success behavior.
        var matches = await client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task FindArtistLocationsAsync_WithStrictCaller_ClassifiesSectionListFailure()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var failure = await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
        Assert.Contains("401", failure.Message, StringComparison.Ordinal);
        Assert.Contains("section", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindArtistLocationsAsync_WithStrictCaller_ClassifiesMalformedSectionPayload()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<MediaContainer><Directory type=\"artist\" ")
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var failure = await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
        Assert.Contains("not valid XML", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindArtistLocationsAsync_WithStrictCaller_ClassifiesPartialSectionFailureAsUnknown()
    {
        using var handler = new StubHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /><Directory type=\"artist\" key=\"2\" /></MediaContainer>");
            }

            if (path.Contains("/library/sections/1/all", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist\" librarySectionID=\"1\" /></MediaContainer>");
            }

            if (path.Contains("/library/sections/2/all", StringComparison.Ordinal))
            {
                // One section rejects the request: the whole lookup cannot be a confident answer.
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return Xml("<MediaContainer size=\"0\" />");
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var failure = await Assert.ThrowsAsync<TargetLookupException>(() => client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true));
        Assert.Contains("500", failure.Message, StringComparison.Ordinal);

        // The legacy lookup keeps its best-effort answer across the surviving sections.
        var matches = await client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None);
        Assert.NotEmpty(matches);
    }

    [Fact]
    public async Task FindArtistLocationsAsync_WithStrictCaller_MarksSuccessfulEmptySearchAsValidAbsence()
    {
        using var handler = new StubHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            return Xml("<MediaContainer size=\"0\" />");
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        // An authoritative empty search is genuine absence, not an error, so strict mode returns it.
        var matches = await client.FindArtistLocationsAsync(
            "http://plex.local:32400",
            "token",
            "Artist",
            CancellationToken.None,
            requireSuccessfulResponse: true);
        Assert.Empty(matches);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetArtistMetadataAsync_WithStrictCaller_ClassifiesFailedRead(HttpStatusCode status)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        await Assert.ThrowsAsync<TargetLookupException>(() => client.GetArtistMetadataAsync(
            "http://plex.local:32400",
            "token",
            "rk1",
            CancellationToken.None,
            requireSuccessfulResponse: true));

        var legacy = await client.GetArtistMetadataAsync(
            "http://plex.local:32400",
            "token",
            "rk1",
            CancellationToken.None);
        Assert.Null(legacy);
    }

    private static HttpResponseMessage Xml(string xml)
        => new(HttpStatusCode.OK) { Content = new StringContent(xml) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
