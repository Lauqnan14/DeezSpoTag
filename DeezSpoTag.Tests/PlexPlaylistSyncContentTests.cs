using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexPlaylistSyncContentTests
{
    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_WritesLargePlaylistWithoutTrackCountCutoff()
    {
        var ratingKeys = Enumerable.Range(1, 1000).Select(static value => value.ToString()).ToList();
        using var handler = new PlaylistSyncHandler();
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var result = await client.CreateOrUpdatePlaylistAsync(
            "http://plex.local:32400",
            "token",
            "machine",
            "Large Playlist",
            ratingKeys,
            new PlexApiClient.PlaylistUpsertOptions(ExistingPlaylistId: "900"),
            CancellationToken.None);

        Assert.Equal("900", result.PlaylistId);
        Assert.True(result.Complete);
        Assert.NotEmpty(handler.AddBatches);
        Assert.True(handler.StoredRatingKeys.SequenceEqual(ratingKeys));
        Assert.Contains(handler.RequestedUrls, url => url.Contains("X-Plex-Container-Start=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_FailsWhenPlexDoesNotRetainExpectedContent()
    {
        var ratingKeys = Enumerable.Range(1, 75).Select(static value => value.ToString()).ToList();
        using var handler = new PlaylistSyncHandler(dropLastWrittenItem: true);
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var result = await client.CreateOrUpdatePlaylistAsync(
            "http://plex.local:32400",
            "token",
            "machine",
            "Large Playlist",
            ratingKeys,
            new PlexApiClient.PlaylistUpsertOptions(ExistingPlaylistId: "900"),
            CancellationToken.None);

        Assert.Equal("900", result.PlaylistId);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_DoesNotClearExistingMembershipWhenAdditionFails()
    {
        using var handler = new PlaylistSyncHandler(initialRatingKeys: ["1", "2"], failAddRequests: true);
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var result = await client.CreateOrUpdatePlaylistAsync(
            "http://plex.local:32400",
            "token",
            "machine",
            "Large Playlist",
            ["1", "2", "3"],
            new PlexApiClient.PlaylistUpsertOptions(ExistingPlaylistId: "900"),
            CancellationToken.None);

        Assert.Equal("900", result.PlaylistId);
        Assert.False(result.Complete);
        Assert.Equal(["1", "2"], handler.StoredRatingKeys);
        Assert.False(handler.BulkClearRequested);
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_ReconcilesMembershipInPlaceAndPreservesOrder()
    {
        using var handler = new PlaylistSyncHandler(initialRatingKeys: ["3", "2", "4"]);
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var result = await client.CreateOrUpdatePlaylistAsync(
            "http://plex.local:32400",
            "token",
            "machine",
            "Large Playlist",
            ["1", "2", "3"],
            new PlexApiClient.PlaylistUpsertOptions(ExistingPlaylistId: "900"),
            CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(["1", "2", "3"], handler.StoredRatingKeys);
        Assert.False(handler.BulkClearRequested);
    }

    [Fact]
    public async Task UpdatePlaylistPosterFromFileAsync_VerifiesStoredPosterBytes()
    {
        var posterBytes = new byte[] { 0xFF, 0xD8, 0x01, 0xFF, 0xD9 };
        var posterPath = Path.Join(Path.GetTempPath(), $"plex-playlist-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(posterPath, posterBytes);
        try
        {
            using var handler = new PlaylistPosterHandler(posterBytes);
            using var httpClient = new HttpClient(handler);
            var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

            var updated = await client.UpdatePlaylistPosterFromFileAsync(
                "http://plex.local:32400",
                "token",
                "900",
                posterPath,
                "image/jpeg",
                CancellationToken.None);

            Assert.True(updated);
            Assert.True(handler.Uploaded);
            Assert.True(handler.Verified);
        }
        finally
        {
            File.Delete(posterPath);
        }
    }

    private sealed class PlaylistPosterHandler(byte[] expectedBytes) : HttpMessageHandler
    {
        public bool Uploaded { get; private set; }
        public bool Verified { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/library/metadata/900/posters")
            {
                Uploaded = request.Content != null
                    && (await request.Content.ReadAsByteArrayAsync(cancellationToken)).SequenceEqual(expectedBytes);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath == "/playlists/900")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<MediaContainer><Playlist ratingKey=\"900\" title=\"Playlist\" thumb=\"/library/metadata/900/thumb/1\" /></MediaContainer>")
                };
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath == "/library/metadata/900/thumb/1")
            {
                Verified = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class PlaylistSyncHandler : HttpMessageHandler
    {
        private readonly bool _dropLastWrittenItem;
        private readonly bool _failAddRequests;

        public PlaylistSyncHandler(
            bool dropLastWrittenItem = false,
            IReadOnlyList<string>? initialRatingKeys = null,
            bool failAddRequests = false)
        {
            _dropLastWrittenItem = dropLastWrittenItem;
            _failAddRequests = failAddRequests;
            if (initialRatingKeys != null)
            {
                StoredRatingKeys.AddRange(initialRatingKeys);
            }
        }

        public List<List<string>> AddBatches { get; } = new();
        public List<string> StoredRatingKeys { get; } = new();
        public List<string> RequestedUrls { get; } = new();
        public bool BulkClearRequested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/playlists/900")
            {
                return Xml("<MediaContainer><Playlist ratingKey=\"900\" title=\"Large Playlist\" /></MediaContainer>");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/playlists/900/items")
            {
                var keys = _dropLastWrittenItem && StoredRatingKeys.Count > 0
                    ? StoredRatingKeys.Take(StoredRatingKeys.Count - 1).ToList()
                    : StoredRatingKeys;
                return Xml(BuildPlaylistItemsXml(keys));
            }

            if (request.Method == HttpMethod.Delete && request.RequestUri.AbsolutePath == "/playlists/900/items")
            {
                BulkClearRequested = true;
                StoredRatingKeys.Clear();
                return EmptyOk();
            }

            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath == "/playlists/900/items")
            {
                if (_failAddRequests)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
                var batch = ExtractRatingKeysFromUriQuery(request.RequestUri.Query);
                AddBatches.Add(batch);
                StoredRatingKeys.AddRange(batch);
                return EmptyOk();
            }

            if (request.Method == HttpMethod.Delete
                && request.RequestUri.AbsolutePath.StartsWith("/playlists/900/items/item-", StringComparison.Ordinal))
            {
                StoredRatingKeys.Remove(request.RequestUri.AbsolutePath["/playlists/900/items/item-".Length..]);
                return EmptyOk();
            }

            if (request.Method == HttpMethod.Put
                && request.RequestUri.AbsolutePath.StartsWith("/playlists/900/items/item-", StringComparison.Ordinal)
                && request.RequestUri.AbsolutePath.EndsWith("/move", StringComparison.Ordinal))
            {
                var prefixLength = "/playlists/900/items/item-".Length;
                var key = request.RequestUri.AbsolutePath[prefixLength..^"/move".Length];
                var after = request.RequestUri.Query
                    .TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .First(part => part.StartsWith("after=", StringComparison.OrdinalIgnoreCase))["after=".Length..];
                after = Uri.UnescapeDataString(after);
                StoredRatingKeys.Remove(key);
                var insertIndex = after == "0"
                    ? 0
                    : StoredRatingKeys.IndexOf(after["item-".Length..]) + 1;
                StoredRatingKeys.Insert(insertIndex, key);
                return EmptyOk();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static List<string> ExtractRatingKeysFromUriQuery(string query)
        {
            var uriParameter = query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(static part => part.StartsWith("uri=", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(uriParameter));

            var value = Uri.UnescapeDataString(uriParameter!["uri=".Length..]);
            var marker = "/library/metadata/";
            var markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(markerIndex >= 0);
            return value[(markerIndex + marker.Length)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        private static string BuildPlaylistItemsXml(IReadOnlyList<string> keys)
        {
            var tracks = string.Concat(keys.Select(static key => $"<Track ratingKey=\"{key}\" playlistItemID=\"item-{key}\" title=\"Track {key}\" />"));
            return $"<MediaContainer totalSize=\"{keys.Count}\">{tracks}</MediaContainer>";
        }

        private static Task<HttpResponseMessage> Xml(string xml)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });

        private static Task<HttpResponseMessage> EmptyOk()
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
