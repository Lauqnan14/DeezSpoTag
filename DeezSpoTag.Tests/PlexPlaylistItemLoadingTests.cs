using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexPlaylistItemLoadingTests
{
    [Fact]
    public async Task GetPlaylistAsync_CapturesSmartPlaylistKeyAndFlag()
    {
        using var handler = new PlexPlaylistItemHandler();
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var playlist = await client.GetPlaylistAsync("http://plex.local:32400", "token", "85289");

        Assert.NotNull(playlist);
        Assert.True(playlist.Smart);
        Assert.Equal("/playlists/85289/items", playlist.Key);
        Assert.Contains("/library/sections/29/all", playlist.Content, StringComparison.Ordinal);
        Assert.Equal(150, playlist.TrackCount);
    }

    [Fact]
    public async Task GetPlaylistItemsDetailedAsync_UsesPlaylistKeyAndDoesNotExposeTokenInDiagnostics()
    {
        using var handler = new PlexPlaylistItemHandler();
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);
        var playlist = new PlexPlaylist
        {
            Id = "85289",
            Key = "/playlists/85289/items",
            Smart = true
        };

        var result = await client.GetPlaylistItemsDetailedAsync("http://plex.local:32400", "secret-token", playlist);

        Assert.True(result.Success, string.Join(" | ", handler.RequestedUrls));
        Assert.Single(result.Tracks);
        Assert.Contains("/playlists/85289/items", handler.LastItemsUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", result.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPlaylistItemsDetailedAsync_ReportsFailureInsteadOfSuccessfulEmptyPlaylist()
    {
        using var handler = new PlexPlaylistItemHandler(failItems: true);
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var result = await client.GetPlaylistItemsDetailedAsync("http://plex.local:32400", "secret-token", "85289");

        Assert.False(result.Success);
        Assert.Empty(result.Tracks);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("/playlists/85289/items", result.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", result.Endpoint, StringComparison.Ordinal);
    }

    private sealed class PlexPlaylistItemHandler(bool failItems = false) : HttpMessageHandler
    {
        public string LastItemsUrl { get; private set; } = string.Empty;
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            if (request.RequestUri?.AbsolutePath == "/playlists/85289")
            {
                return Xml("""
                    <MediaContainer size="1">
                        <Playlist ratingKey="85289" key="/playlists/85289/items" content="server://machine/com.plexapp.plugins.library/library/sections/29/all?type=10&amp;sort=titleSort" title="Kenyan Hip Hop" playlistType="audio" leafCount="150" smart="1" />
                    </MediaContainer>
                    """);
            }

            if (request.RequestUri?.AbsolutePath == "/playlists/85289/items")
            {
                LastItemsUrl = request.RequestUri.ToString();
                if (failItems)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return PlaylistTrackXml();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Xml(string xml)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });

        private static Task<HttpResponseMessage> PlaylistTrackXml()
            => Xml("""
                <MediaContainer totalSize="1">
                    <Track ratingKey="1" title="Track One" grandparentTitle="Artist" parentTitle="Album" duration="180000">
                        <Media><Part key="/library/parts/1/file.flac" file="/music/Artist/Album/Track One.flac" /></Media>
                    </Track>
                </MediaContainer>
                """);
    }
}
