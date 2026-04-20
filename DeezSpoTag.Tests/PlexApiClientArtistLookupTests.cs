using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexApiClientArtistLookupTests
{
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

    private static HttpResponseMessage Xml(string xml)
        => new(HttpStatusCode.OK) { Content = new StringContent(xml) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
