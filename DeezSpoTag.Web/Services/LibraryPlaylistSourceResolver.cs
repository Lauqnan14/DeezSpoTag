using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Reads one library playlist in the shape the sync service expects, from whichever self-hosted
/// server holds it.
/// <para>
/// Extracted from the controller's own reader so a scheduled pass and a manual one read the playlist
/// the same way. Two readers would be two things to keep in step, and they would eventually disagree
/// about what a playlist's name or track count is - which matters because the name is what a
/// destination is matched by.
/// </para>
/// </summary>
public sealed class LibraryPlaylistSourceResolver
{
    public const string PlexServer = MediaServerTargetServices.Plex;
    public const string JellyfinServer = MediaServerTargetServices.Jellyfin;
    public const string NavidromeServer = MediaServerTargetServices.Navidrome;

    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;

    public LibraryPlaylistSourceResolver(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        NavidromeApiClient navidromeApiClient)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _navidromeApiClient = navidromeApiClient;
    }

    /// <summary>
    /// The playlist as it stands now, or null when the server is not connected or the playlist is
    /// gone. A null result is a normal outcome, not an error: a playlist deleted from the source
    /// server has nothing left to copy.
    /// </summary>
    public async Task<PlaylistWatchlistDto?> ResolveAsync(
        string server,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var state = await _authService.LoadAsync();
        var now = DateTimeOffset.UtcNow;
        var normalized = server.Trim().ToLowerInvariant();

        if (normalized == JellyfinServer)
        {
            var jellyfin = state.Jellyfin;
            if (string.IsNullOrWhiteSpace(jellyfin?.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
            {
                return null;
            }

            var playlist = await _jellyfinApiClient.GetPlaylistAsync(
                jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId ?? string.Empty, id, cancellationToken);
            if (playlist is null || string.IsNullOrWhiteSpace(playlist.Id))
            {
                return null;
            }

            var items = await _jellyfinApiClient.GetPlaylistItemsAsync(
                jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId ?? string.Empty, id, cancellationToken);
            return new PlaylistWatchlistDto(
                0,
                JellyfinServer,
                playlist.Id!,
                playlist.Name ?? "Jellyfin Playlist",
                // No cover URL here. The controller's own reader builds these through
                // Url.ActionLink, which needs a request context a service does not have. A sync
                // never uses the cover, and a hand-rolled URL that the action route would not match
                // would be worse than none.
                ImageUrl: null,
                playlist.Overview,
                items.Count,
                now);
        }

        if (normalized == NavidromeServer)
        {
            var navidrome = state.Navidrome;
            if (string.IsNullOrWhiteSpace(navidrome?.Url)
                || string.IsNullOrWhiteSpace(navidrome.Username)
                || string.IsNullOrWhiteSpace(navidrome.Password))
            {
                return null;
            }

            var (playlist, tracks) = await _navidromeApiClient.GetPlaylistWithTracksAsync(
                navidrome.Url, navidrome.Username, navidrome.Password, id, cancellationToken);
            return playlist is null
                ? null
                : new PlaylistWatchlistDto(
                    0,
                    NavidromeServer,
                    playlist.Id,
                    playlist.Name,
                    null,
                    playlist.Comment,
                    tracks.Count,
                    now);
        }

        if (normalized != PlexServer)
        {
            return null;
        }

        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return null;
        }

        var plexPlaylist = await _plexApiClient.GetPlaylistAsync(plex.Url, plex.Token, id, cancellationToken);
        if (plexPlaylist is null)
        {
            return null;
        }

        var plexItems = await _plexApiClient.GetPlaylistItemsDetailedAsync(
            plex.Url, plex.Token, plexPlaylist, cancellationToken);
        return new PlaylistWatchlistDto(
            0,
            PlexServer,
            plexPlaylist.Id,
            plexPlaylist.Title,
            ImageUrl: null,
            plexPlaylist.Summary,
            plexItems.Tracks.Count,
            now);
    }
}
