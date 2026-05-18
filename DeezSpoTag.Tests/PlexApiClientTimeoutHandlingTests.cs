using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexApiClientTimeoutHandlingTests
{
    [Fact]
    public async Task SearchTracksAsync_ReturnsEmpty_WhenRequestTimesOut()
    {
        using var httpClient = new HttpClient(new TimeoutHandler());
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var result = await client.SearchTracksAsync(
            "http://plex.local:32400",
            "token",
            "kendrick lamar",
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchTracksAsync_Throws_WhenCallerCancellationIsRequested()
    {
        using var httpClient = new HttpClient(new CancellationAwareHandler());
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.SearchTracksAsync(
                "http://plex.local:32400",
                "token",
                "kendrick lamar",
                cts.Token));
    }

    [Fact]
    public async Task LockArtistArtworkAsync_SetsThumbAndArtLockedFlags()
    {
        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var locked = await client.LockArtistArtworkAsync(
            "http://plex.local:32400",
            "token",
            "7",
            "1234",
            lockPoster: true,
            lockBackground: true,
            CancellationToken.None);

        Assert.True(locked);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        var uri = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("/library/sections/7/all", uri, StringComparison.Ordinal);
        Assert.Contains("type=8", uri, StringComparison.Ordinal);
        Assert.Contains("id=1234", uri, StringComparison.Ordinal);
        Assert.Contains("thumb.locked=1", uri, StringComparison.Ordinal);
        Assert.Contains("art.locked=1", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshMetadataAsync_RefreshesSpecificMetadataRatingKey()
    {
        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var refreshed = await client.RefreshMetadataAsync(
            "http://plex.local:32400",
            "token",
            "album-123",
            CancellationToken.None);

        Assert.True(refreshed);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        var uri = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("/library/metadata/album-123/refresh", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("/library/sections/", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMetadataParentKeysAsync_ReadsAlbumAndArtistRatingKeys()
    {
        using var httpClient = new HttpClient(new XmlHandler(
            "<MediaContainer><Track ratingKey=\"track-1\" parentRatingKey=\"album-1\" grandparentRatingKey=\"artist-1\" /></MediaContainer>"));
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var parentKeys = await client.GetMetadataParentKeysAsync(
            "http://plex.local:32400",
            "token",
            "track-1",
            CancellationToken.None);

        Assert.Equal("album-1", parentKeys.AlbumRatingKey);
        Assert.Equal("artist-1", parentKeys.ArtistRatingKey);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new TaskCanceledException("The request timed out.");
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CaptureRequestHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class XmlHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml)
            });
        }
    }
}
