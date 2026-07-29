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
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ApplePlaylistPaginationTests
{
    [Theory]
    [InlineData("gb", "us", "ke", "gb")]
    [InlineData(null, "us", "ke", "us")]
    [InlineData(null, null, "ca", "ca")]
    [InlineData(null, null, null, "us")]
    [InlineData(null, null, "invalid", "us")]
    public void TracklistStorefront_UsesExplicitThenPersistedThenConfiguredThenUs(
        string? explicitStorefront,
        string? persistedStorefront,
        string? configuredStorefront,
        string expected)
    {
        var actual = AppleTracklistApiController.ResolveStorefrontPrecedence(
            explicitStorefront,
            persistedStorefront,
            configuredStorefront);

        Assert.Equal(expected, actual);
    }

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
    public async Task CompletePlaylistTracks_ReturnsAll250PositionsIncludingRepeatedTrackIds()
    {
        const string secondPage = "https://amp-api.music.apple.com/v1/catalog/us/playlists/list/tracks?offset=100";
        const string thirdPage = "https://amp-api.music.apple.com/v1/catalog/us/playlists/list/tracks?offset=200";
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(
            "apple:playlist:tracks:us:list:100:0:en-US",
            BuildPage(1, 100, secondPage));
        cache.Set(
            $"apple:playlist:page:{secondPage}",
            BuildPage(101, 100, thirdPage));
        cache.Set(
            $"apple:playlist:page:{thirdPage}",
            BuildPage(201, 50, null, repeatedId: "25"));
        var service = CreateService(cache);

        var result = await service.GetCompletePlaylistTracksAsync(
            "list",
            "us",
            "en-US",
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(250, result.Tracks.Count);
        Assert.Equal("25", result.Tracks[24].GetProperty("id").GetString());
        Assert.Equal("25", result.Tracks[249].GetProperty("id").GetString());
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
        Assert.Contains("GetPlaylistWatchlistEntryAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("ResolveStorefrontPrecedence(", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog.ResolveStorefrontAsync(", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveStorefrontAsync(", watchlist, StringComparison.Ordinal);
        Assert.Contains("GetPersistedAppleStorefrontAsync(", watchlist, StringComparison.Ordinal);
        var mapper = ExtractMethod(
            watchlist,
            "private static IReadOnlyList<PlaylistTrackCandidate> MapWatchIntentTrackCandidates");
        Assert.DoesNotContain("!seen.Add(trackId)", mapper, StringComparison.Ordinal);
        Assert.Contains("SourcePosition", mapper, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleWatchlistUpgrade_RepairsStorefrontWithoutACompetingFetchPath()
    {
        var watchlist = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var coordinator = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var repository = ReadSource("DeezSpoTag.Services/Library/LibraryRepository.cs");
        var database = ReadSource("DeezSpoTag.Services/Library/LibraryDbService.cs");

        Assert.Contains("BackfillLegacyApplePlaylistStorefrontAsync(", coordinator, StringComparison.Ordinal);
        Assert.Contains("EnqueueWatchlistReconciliationRequestAsync(", coordinator, StringComparison.Ordinal);
        Assert.Contains("BackfillLegacyApplePlaylistStorefrontAsync(", repository, StringComparison.Ordinal);
        Assert.Contains("apple_storefront_not_persisted", watchlist, StringComparison.Ordinal);
        Assert.Contains("apple_playlist_unavailable", watchlist, StringComparison.Ordinal);
        Assert.Contains("apple_playlist_incomplete", watchlist, StringComparison.Ordinal);
        Assert.DoesNotContain("apple_storefront_or_snapshot_unavailable", watchlist, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AND (schema_version<>4 OR is_complete=0);\n\nDELETE FROM playlist_track_candidate_cache",
            database,
            StringComparison.Ordinal);
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

    private static string ExtractMethod(string source, string methodName)
    {
        var start = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method {methodName} was not found.");
        var nextMethod = source.IndexOf("\n    private ", start + methodName.Length, StringComparison.Ordinal);
        return nextMethod > start ? source[start..nextMethod] : source[start..];
    }

    private static string BuildPage(int firstId, int count, string? next, string? repeatedId = null)
    {
        var tracks = Enumerable.Range(firstId, count)
            .Select((id, index) => new
            {
                id = index == count - 1 && !string.IsNullOrWhiteSpace(repeatedId)
                    ? repeatedId
                    : id.ToString(),
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
