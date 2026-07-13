using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexApiClientHistoryTests
{
    [Fact]
    public async Task GetHistoryAsync_UsesMusicSectionsIncrementalFilterAndPagination()
    {
        var requests = new List<RequestSnapshot>();
        using var handler = new StubHandler(request =>
        {
            requests.Add(RequestSnapshot.Create(request));
            if (request.RequestUri!.AbsolutePath == "/library/sections")
            {
                return Xml("<MediaContainer><Directory key=\"7\" title=\"Music\" type=\"artist\"/><Directory key=\"2\" title=\"TV\" type=\"show\"/></MediaContainer>");
            }

            var start = request.Headers.GetValues("X-Plex-Container-Start").Single();
            return start == "0"
                ? Xml("<MediaContainer size=\"1\" totalSize=\"201\" offset=\"0\"><Track ratingKey=\"101\" librarySectionID=\"7\" title=\"First\" grandparentTitle=\"Artist\" parentTitle=\"Album\" viewedAt=\"1700000000\"/></MediaContainer>")
                : Xml("<MediaContainer size=\"1\" totalSize=\"201\" offset=\"200\"><Track ratingKey=\"102\" librarySectionID=\"7\" title=\"Second\" grandparentTitle=\"Artist\" parentTitle=\"Album\" viewedAt=\"1700000060\"/></MediaContainer>");
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);
        var since = DateTimeOffset.FromUnixTimeSeconds(1699999999);

        var history = await client.GetHistoryAsync("http://plex.local:32400", "secret", since);

        Assert.Equal(2, history.Count);
        Assert.All(history, item => Assert.Equal("7", item.LibrarySectionId));
        var historyRequests = requests.Where(item => item.Path == "/status/sessions/history/all").ToList();
        Assert.Equal(2, historyRequests.Count);
        Assert.All(historyRequests, request =>
        {
            Assert.Equal("secret", request.PlexToken);
            Assert.Contains("librarySectionID=7", request.Uri, StringComparison.Ordinal);
            Assert.Contains("viewedAt%3E=1699999999", request.Uri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", request.Uri, StringComparison.Ordinal);
        });
        Assert.Equal(new[] { "0", "200" }, historyRequests.Select(item => item.ContainerStart));
    }

    [Fact]
    public async Task GetHistoryAsync_FallsBackToLegacyEndpoint_WhenAllEndpointIsUnavailable()
    {
        var paths = new List<string>();
        using var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/library/sections")
            {
                return Xml("<MediaContainer><Directory key=\"7\" title=\"Music\" type=\"artist\"/></MediaContainer>");
            }
            if (path == "/status/sessions/history/all")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Xml("<MediaContainer size=\"1\" totalSize=\"1\"><Track ratingKey=\"legacy\" title=\"Legacy\" viewedAt=\"1700000000\"/></MediaContainer>");
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var history = await client.GetHistoryAsync(
            "http://plex.local:32400",
            "secret",
            DateTimeOffset.FromUnixTimeSeconds(1));

        var item = Assert.Single(history);
        Assert.Equal("legacy", item.RatingKey);
        Assert.Contains("/status/sessions/history/all", paths);
        Assert.Contains("/status/sessions/history", paths);
    }

    [Fact]
    public async Task GetHistoryAsync_DoesNotUseUnsupportedTypeFilter()
    {
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/library/sections")
            {
                return Xml("<MediaContainer><Directory key=\"29\" title=\"Music\" type=\"artist\"/></MediaContainer>");
            }

            Assert.DoesNotContain("type=10", request.RequestUri.OriginalString, StringComparison.OrdinalIgnoreCase);
            return Xml("<MediaContainer size=\"0\" totalSize=\"0\"/>");
        });
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var history = await client.GetHistoryAsync("http://plex.local:32400", "secret");

        Assert.Empty(history);
    }

    private static HttpResponseMessage Xml(string xml)
        => new(HttpStatusCode.OK) { Content = new StringContent(xml) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed record RequestSnapshot(
        string Path,
        string Uri,
        string? PlexToken,
        string? ContainerStart)
    {
        public static RequestSnapshot Create(HttpRequestMessage request)
        {
            request.Headers.TryGetValues("X-Plex-Token", out var tokenValues);
            request.Headers.TryGetValues("X-Plex-Container-Start", out var startValues);
            return new RequestSnapshot(
                request.RequestUri!.AbsolutePath,
                request.RequestUri.OriginalString,
                tokenValues?.SingleOrDefault(),
                startValues?.SingleOrDefault());
        }
    }
}
