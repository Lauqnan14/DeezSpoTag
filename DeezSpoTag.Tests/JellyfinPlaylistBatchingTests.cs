using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class JellyfinPlaylistBatchingTests
{
    [Fact]
    public async Task AddPlaylistItemsAsync_BatchesLargeOrderedWrites()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var api = new JellyfinApiClient(client);
        var ids = Enumerable.Range(1, 250).Select(index => $"item-{index}").ToList();

        var added = await api.AddPlaylistItemsAsync(
            "http://jellyfin.local",
            "key",
            "user",
            "playlist",
            ids,
            CancellationToken.None);

        Assert.True(added);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(100, ReadIds(handler.Requests[0]).Count);
        Assert.Equal(100, ReadIds(handler.Requests[1]).Count);
        Assert.Equal(50, ReadIds(handler.Requests[2]).Count);
        Assert.Equal("item-1", ReadIds(handler.Requests[0])[0]);
        Assert.Equal("item-250", ReadIds(handler.Requests[2])[^1]);
    }

    [Fact]
    public async Task MovePlaylistItemAsync_UsesPlaylistEntryIdAndClampsIndex()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var api = new JellyfinApiClient(client);

        var moved = await api.MovePlaylistItemAsync(
            "http://jellyfin.local",
            "key",
            "user",
            "playlist",
            "entry-9",
            newIndex: 99,
            itemCount: 3,
            CancellationToken.None);

        Assert.Equal(JellyfinPlaylistMoveStatus.Moved, moved.Status);
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/Playlists/playlist/Items/entry-9/Move/2", uri.AbsolutePath);
        Assert.Equal(2, JellyfinApiClient.ClampPlaylistMoveIndex(99, 3));
        Assert.DoesNotContain("item-9", uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovePlaylistItemAsync_Http500IsNotSupportedAndDoesNotThrow()
    {
        using var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);
        var api = new JellyfinApiClient(client);

        var moved = await api.MovePlaylistItemAsync(
            "http://jellyfin.local",
            "key",
            "user",
            "playlist",
            "entry-1",
            newIndex: 0,
            itemCount: 3,
            CancellationToken.None);

        Assert.Equal(JellyfinPlaylistMoveStatus.NotSupported, moved.Status);
        Assert.Equal(500, moved.HttpStatusCode);
    }

    [Fact]
    public void JellyfinMoveFailures_DoNotIncrementTargetCircuit()
    {
        var service = System.IO.File.ReadAllText(System.IO.Path.Combine(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            "DeezSpoTag.Web",
            "Services",
            "PlaylistSyncService.cs"));
        var worker = System.IO.File.ReadAllText(System.IO.Path.Combine(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            "DeezSpoTag.Web",
            "Services",
            "WatchlistPostDownloadSyncService.cs"));

        Assert.Contains("JellyfinPlaylistMoveStatus.NotSupported", service, StringComparison.Ordinal);
        Assert.Contains("JellyfinPlaylistMoveStatus.Transient", service, StringComparison.Ordinal);
        Assert.Contains("PersistTargetCapabilityAsync", service, StringComparison.Ordinal);
        Assert.Contains("SyncFailureClass.ReorderUnsupported", worker, StringComparison.Ordinal);
        Assert.Contains("ShouldIncrementTargetCircuit(SyncFailureClass failureClass)", worker, StringComparison.Ordinal);
        Assert.Contains("failureClass is SyncFailureClass.Transport or SyncFailureClass.Auth", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordTargetCircuitFailureAsync(\n                JellyfinPlaylistMoveStatus", worker, StringComparison.Ordinal);
    }

    private static List<string> ReadIds(Uri uri)
    {
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .First(parts => Uri.UnescapeDataString(parts[0]) == "Ids")[1];
        return Uri.UnescapeDataString(query)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
