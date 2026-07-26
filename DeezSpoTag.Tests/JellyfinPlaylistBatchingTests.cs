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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
