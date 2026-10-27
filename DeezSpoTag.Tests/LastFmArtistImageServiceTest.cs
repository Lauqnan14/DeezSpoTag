using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LastFmArtistImageServiceTest
{
    [Fact]
    public async Task FullGalleryLookupUsesExistingExactArtistGalleryWhenApiHasNoPhotos()
    {
        var calls = 0;
        var service = Create(request => {
            calls++;
            return Ok(request.RequestUri!.Host.StartsWith("ws.")
                ? "{\"artist\":{\"name\":\"Alicios\",\"image\":[]}}"
                : "<a href=\"/music/Alicios/+images/abc\"><img src=\"https://lastfm.freetls.fastly.net/i/u/300x300/abc.jpg\"></a>");
        });
        var images = await service.SearchArtistImagesAsync("Alicios", 8, CancellationToken.None, propagateRateLimits: true);
        Assert.Single(images);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(200, "<title>Client Challenge</title>")]
    [InlineData(429, "limited")]
    [InlineData(503, "unavailable")]
    public async Task GalleryAccessFailureCannotBecomeVerifiedNoData(int status, string html)
    {
        var service = Create(request => request.RequestUri!.Host.StartsWith("ws.")
            ? Ok("{\"artist\":{\"name\":\"Alicios\",\"image\":[]}}")
            : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(html) });
        await Assert.ThrowsAnyAsync<Exception>(() => service.SearchArtistImagesAsync("Alicios", 8, CancellationToken.None, propagateRateLimits: true));
    }

    [Fact]
    public async Task ApiPhotosAreRetainedBeforeGalleryFailure()
    {
        var service = Create(request => request.RequestUri!.Host.StartsWith("ws.")
            ? Ok("{\"artist\":{\"name\":\"Alicios\",\"image\":[{\"#text\":\"https://lastfm.freetls.fastly.net/i/u/300x300/photo.jpg\",\"size\":\"large\"}]}}")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var retained = new List<LastFmArtistImageCandidate>();
        var method = typeof(LastFmArtistImageService).GetMethod("SearchArtistImagesAsync");
        Assert.Equal(5, method!.GetParameters().Length);
        Func<IReadOnlyList<LastFmArtistImageCandidate>, CancellationToken, Task> save = (items, _) => {retained.AddRange(items); return Task.CompletedTask;};
        await Assert.ThrowsAsync<HttpRequestException>(() => (Task)method.Invoke(service, new object[] {"Alicios",8,CancellationToken.None,true,save})!);
        Assert.Single(retained);
    }

    [Fact]
    public async Task ApiRateLimitStopsBeforeGalleryRequest()
    {
        var calls=0;
        var service=Create(_=>{calls++;return Ok("{\"error\":29,\"message\":\"rate limited\"}");});
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>service.SearchArtistImagesAsync("Alicios",8,CancellationToken.None,propagateRateLimits:true));
        Assert.Equal(HttpStatusCode.TooManyRequests,error.StatusCode);
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task ConfirmedMissingExactGalleryIsNoData()
    {
        var service=Create(request=>request.RequestUri!.Host.StartsWith("ws.")
            ? Ok("{\"error\":6,\"message\":\"Artist not found\"}")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty(await service.SearchArtistImagesAsync("Alicios",8,CancellationToken.None,propagateRateLimits:true));
    }

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) {Content = new StringContent(content)};
    private static LastFmArtistImageService Create(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new Factory(response), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Lastfm:ApiKey"] = "fixture" }).Build(), null!, NullLogger<LastFmArtistImageService>.Instance);
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
}
