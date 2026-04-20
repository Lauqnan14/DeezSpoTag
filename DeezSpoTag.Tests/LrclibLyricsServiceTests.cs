using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LrclibLyricsServiceTests
{
    [Fact]
    public async Task ResolveLyricsAsync_ParsesTimestampedAndUnsyncedLines_FromMetadataEndpoint()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Contains("/api/get", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, """
                {
                  "plainLyrics": null,
                  "syncedLyrics": "[00:10.00] Hello\nThis is plain",
                  "duration": 180
                }
                """);
        });
        var service = CreateService(handler);
        var track = CreateTrack("Track A", "Artist A", durationSeconds: 180);

        var lyrics = await service.ResolveLyricsAsync(track);

        Assert.True(lyrics.IsLoaded());
        Assert.NotNull(lyrics.SyncedLyrics);
        Assert.Single(lyrics.SyncedLyrics!);
        Assert.Equal("Hello", lyrics.SyncedLyrics![0].Text);
        Assert.Equal("This is plain", lyrics.UnsyncedLyrics);
    }

    [Fact]
    public async Task ResolveLyricsAsync_FallsBackToSearch_AndPrefersSyncedResultWithinTolerance()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/api/get", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri!.AbsoluteUri.Contains("/api/search", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """
                    [
                      { "plainLyrics": "fallback plain", "syncedLyrics": null, "duration": 300 },
                      { "plainLyrics": "fallback plain 2", "syncedLyrics": "[00:01.00] Synced pick", "duration": 205 }
                    ]
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var service = CreateService(handler);
        var track = CreateTrack("Track B", "Artist B", durationSeconds: 200);

        var lyrics = await service.ResolveLyricsAsync(track);

        Assert.True(lyrics.IsLoaded());
        Assert.NotNull(lyrics.SyncedLyrics);
        Assert.Single(lyrics.SyncedLyrics!);
        Assert.Equal("Synced pick", lyrics.SyncedLyrics![0].Text);
    }

    [Fact]
    public async Task ResolveLyricsAsync_RetriesWithSimplifiedTitle_WhenInitialLookupFails()
    {
        var requestedUris = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            requestedUris.Add(uri);

            if (uri.Contains("track_name=Song%20%28Radio%20Edit%29%20-%20Mix", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (uri.Contains("track_name=Song", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "plainLyrics": "Simplified success",
                      "syncedLyrics": null,
                      "duration": 180
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var track = CreateTrack("Song (Radio Edit) - Mix", "Artist C", durationSeconds: 180);

        var lyrics = await service.ResolveLyricsAsync(track);

        Assert.True(lyrics.IsLoaded());
        Assert.Equal("Simplified success", lyrics.UnsyncedLyrics);
        Assert.Contains(requestedUris, uri => uri.Contains("track_name=Song%20%28Radio%20Edit%29%20-%20Mix", StringComparison.Ordinal));
        Assert.Contains(requestedUris, uri => uri.Contains("track_name=Song&", StringComparison.Ordinal) || uri.EndsWith("track_name=Song", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveLyricsAsync_RespectsSearchFallbackFalse_AndReturnsError()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(handler);
        var track = CreateTrack("Track C", "Artist C", durationSeconds: 200);

        var lyrics = await service.ResolveLyricsAsync(
            track,
            new LrclibLyricsService.LrclibRequestOptions { SearchFallback = false });

        Assert.False(lyrics.IsLoaded());
        Assert.Equal("LRCLIB lyrics not found.", lyrics.ErrorMessage);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ResolveLyricsAsync_ClampsOptionsAndCanPreferPlainLyrics()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/api/get", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Json(HttpStatusCode.OK, """
                [
                  { "plainLyrics": "plain preferred", "syncedLyrics": null, "duration": 300 },
                  { "plainLyrics": null, "syncedLyrics": "[00:01.00] synced", "duration": 300 }
                ]
                """);
        });
        var service = CreateService(handler);
        var track = CreateTrack("Track D", "Artist D", durationSeconds: 300);

        var lyrics = await service.ResolveLyricsAsync(
            track,
            new LrclibLyricsService.LrclibRequestOptions
            {
                DurationToleranceSeconds = 999,
                PreferSynced = false
            });

        Assert.True(lyrics.IsLoaded());
        Assert.Equal("plain preferred", lyrics.UnsyncedLyrics);
    }

    private static LrclibLyricsService CreateService(HttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(handler);
        return new LrclibLyricsService(factory, NullLogger<LrclibLyricsService>.Instance);
    }

    private static Track CreateTrack(string title, string artist, int durationSeconds)
    {
        return new Track
        {
            Title = title,
            Duration = durationSeconds,
            MainArtist = new Artist(artist)
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string payload)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
