using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class JunoDownloadClientTests
{
    [Fact]
    public async Task SearchAsync_ParsesReferenceReleaseShape()
    {
        var html = """
            <html>
            <body>
              <div class="row gutters-sm jd-listing-item" data-ua_location="release">
                <div class="col-auto order-2 pl-0">
                  <a href="/products/rick-versace-second-test-sicky-remixes/7526832-02/">
                    <img class="li-img img-fluid-fill" src="https://imagescdn.junodownload.com/150/CS7526832-02A.jpg" alt="Sicky (Remixes)" />
                  </a>
                </div>
                <div class="col-12 col-md order-4 order-md-3 mt-3 mt-md-0 pl-0 pl-md-2">
                  <div class="row gutters-xs jq_highlight">
                    <div class="col juno-artist"><a href="/artists/Rick+Versace/">Rick Versace</a> feat <a href="/artists/Second+Test/">Second Test</a></div>
                  </div>
                  <div class="row gutters-xs jq_highlight">
                    <div class="col"><a class="juno-title" href="/products/rick-versace-second-test-sicky-remixes/7526832-02/">Sicky (Remixes)</a></div>
                  </div>
                  <div class="row gutters-xs mb-2 jq_highlight">
                    <div class="col"><a class="juno-label" href="/labels/Boomwall/">Boomwall</a></div>
                  </div>
                  <div class="jd-listing-tracklist" data-ua_location="release tracklist">
                    <div class="row no-gutters mb-1">
                      <div class="col-auto"><button type="button">BUY</button></div>
                      <div class="col pl-2 jq_highlight">Sicky&nbsp;(Afrobeats remix)&nbsp;-&nbsp;(3:31)&nbsp;<span class="bpm-value d-none d-sm-inline">148&nbsp;BPM</span></div>
                    </div>
                  </div>
                </div>
                <div class="col col-md-2p5 col-xl-2 order-3 order-md-4 text-right pr-0" data-ua_location="release header">
                  <div class="text-sm text-muted mt-3">WBDA 1536<br />15 Apr 26<br />Dancehall/Ragga</div>
                </div>
              </div>
            </body>
            </html>
            """;

        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html)
        }));
        var client = new JunoDownloadClient(httpClient, NullLogger<JunoDownloadClient>.Instance);

        var results = await client.SearchAsync("rick versace sicky", CancellationToken.None);

        var track = Assert.Single(results);
        Assert.Equal("Sicky (Afrobeats remix)", track.Title);
        Assert.Equal(new[] { "Rick Versace", "feat", "Second Test" }, track.Artists);
        Assert.Equal(new[] { "Rick Versace", "feat", "Second Test" }, track.AlbumArtists);
        Assert.Equal("Sicky (Remixes)", track.Album);
        Assert.Equal("Boomwall", track.Label);
        Assert.Equal(new DateTime(2026, 4, 15), track.ReleaseDate);
        Assert.Equal(new[] { "Dancehall", "Ragga" }, track.Genres);
        Assert.Equal("WBDA 1536", track.CatalogNumber);
        Assert.Equal("7526832-02", track.ReleaseId);
        Assert.Equal("https://www.junodownload.com/products/rick-versace-second-test-sicky-remixes/7526832-02/", track.Url);
        Assert.Equal("https://imagescdn.junodownload.com/full/CS7526832-02A-BIG.jpg", track.Art);
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(31), track.Duration);
        Assert.Equal(148, track.Bpm);
        Assert.Equal(1, track.TrackNumber);
        Assert.Equal(1, track.TrackTotal);
    }

    [Fact]
    public async Task SearchAsync_DoesNotThrow_For451Responses()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)451)
        {
            Content = new StringContent("<html><body>blocked</body></html>")
        }));
        var client = new JunoDownloadClient(httpClient, NullLogger<JunoDownloadClient>.Instance);

        var results = await client.SearchAsync("test", CancellationToken.None);

        Assert.Empty(results);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
