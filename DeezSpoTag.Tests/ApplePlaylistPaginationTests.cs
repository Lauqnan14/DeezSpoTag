using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ApplePlaylistPaginationTests
{
    [Fact]
    public async Task CompletePlaylistTracks_FollowsNextLinksAndPreservesOrder()
    {
        const string nextUrl = "https://amp-api.music.apple.com/v1/catalog/us/playlists/list/tracks?offset=100";
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(
            "apple:playlist:tracks:us:list:100:0:en-US",
            BuildPage(1, 100, nextUrl));
        cache.Set(
            $"apple:playlist:page:{nextUrl}",
            BuildPage(101, 50, null));
        var service = CreateService(cache);

        var result = await service.GetCompletePlaylistTracksAsync(
            "list",
            "us",
            "en-US",
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(150, result.Tracks.Count);
        Assert.Equal("1", result.Tracks[0].GetProperty("id").GetString());
        Assert.Equal("150", result.Tracks[^1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task CompletePlaylistTracks_RejectsRepeatedNextLink()
    {
        const string nextUrl = "https://amp-api.music.apple.com/v1/catalog/us/playlists/list/tracks?offset=100";
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(
            "apple:playlist:tracks:us:list:100:0:en-US",
            BuildPage(1, 100, nextUrl));
        cache.Set(
            $"apple:playlist:page:{nextUrl}",
            BuildPage(101, 100, nextUrl));
        var service = CreateService(cache);

        var result = await service.GetCompletePlaylistTracksAsync(
            "list",
            "us",
            "en-US",
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(200, result.Tracks.Count);
        Assert.Contains("repeated", result.IncompleteReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplePlaylistConsumers_UseOnlyCatalogPaginationPath()
    {
        var controller = ReadSource("DeezSpoTag.Web/Controllers/Api/AppleTracklistApiController.cs");
        var watchlist = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var catalog = ReadSource("DeezSpoTag.Services/Apple/AppleMusicCatalogService.cs");

        Assert.Contains("GetCompletePlaylistTracksAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("GetCompletePlaylistTracksAsync(", watchlist, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPlaylistTracksByFeedAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetApplePlaylistTracksData", watchlist, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSafety", catalog, StringComparison.Ordinal);
    }

    private static AppleMusicCatalogService CreateService(IMemoryCache cache)
        => new(
            new ThrowingHttpClientFactory(),
            new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance),
            NullLogger<AppleMusicCatalogService>.Instance,
            cache);

    private static string ReadSource(string relativePath)
    {
        var root = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            root = Directory.GetParent(root)?.FullName;
        }

        throw new FileNotFoundException("Unable to locate source file.", relativePath);
    }

    private static string BuildPage(int firstId, int count, string? next)
    {
        var tracks = Enumerable.Range(firstId, count)
            .Select(id => new
            {
                id = id.ToString(),
                type = "songs",
                attributes = new
                {
                    name = $"Track {id}",
                    artistName = "Artist",
                    albumName = "Album"
                }
            })
            .ToList();
        return System.Text.Json.JsonSerializer.Serialize(new { data = tracks, next });
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new ThrowingHandler(), disposeHandler: true);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
