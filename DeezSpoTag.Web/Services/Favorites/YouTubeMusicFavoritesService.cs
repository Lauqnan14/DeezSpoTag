using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.Favorites;

/// <summary>
/// YouTube Music library. The Data API has no likes endpoint: a "Likes" list is an ordinary
/// playlist with a stable id, so liked tracks are read by finding that playlist and listing its
/// items with the already-implemented playlistItems call.
/// </summary>
public sealed class YouTubeMusicFavoritesService : IFavoritesProvider
{
    private const string LikesPlaylistIdPrefix = "PL";
    private const int PageSize = 50;

    private readonly YouTubeDataApiClient _client;
    private readonly YouTubeMusicTokenProvider _tokenProvider;
    private readonly ILogger<YouTubeMusicFavoritesService> _logger;

    public YouTubeMusicFavoritesService(
        YouTubeDataApiClient client,
        YouTubeMusicTokenProvider tokenProvider,
        ILogger<YouTubeMusicFavoritesService> logger)
    {
        _client = client;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Key => "ytmusic";

    public string DisplayName => "YouTube Music";

    public string IconPath => "/images/icons/youtube-music.png";

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        var resolvedLimit = Math.Clamp(limit, 1, 100);
        try
        {
            if (!await _tokenProvider.IsConfiguredAsync())
            {
                return Unavailable("YouTube Music account not connected.");
            }

            var accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Unavailable("YouTube Music access token unavailable.");
            }

            var ownPlaylists = await _client.GetMyPlaylistsAsync(accessToken, PageSize, null, cancellationToken);
            if (ownPlaylists.Count == 0)
            {
                return Unavailable("YouTube Music library unavailable.");
            }

            var playlists = new List<FavoriteItem>();
            YouTubePlaylist? likesPlaylist = null;

            foreach (var playlist in ownPlaylists)
            {
                if (IsLikesPlaylist(playlist))
                {
                    likesPlaylist ??= playlist;
                    continue;
                }

                playlists.Add(new FavoriteItem(
                    playlist.Id,
                    playlist.Title,
                    "playlist",
                    $"https://music.youtube.com/playlist?list={playlist.Id}",
                    playlist.CoverUrl,
                    "YouTube Music",
                    null));
            }

            var tracks = new List<FavoriteItem>();
            if (likesPlaylist is not null)
            {
                tracks = await ReadLikesAsync(accessToken, likesPlaylist.Id, resolvedLimit, cancellationToken);
            }

            return new FavoritesResult(true, null, new List<FavoriteItem>(), playlists, tracks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load YouTube Music library.");
            return Unavailable("YouTube Music library unavailable.");
        }
    }

    private async Task<List<FavoriteItem>> ReadLikesAsync(
        string accessToken,
        string playlistId,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = await _client.GetPlaylistItemsAsync(accessToken, playlistId, PageSize, null, cancellationToken);
        var tracks = new List<FavoriteItem>();
        foreach (var item in items)
        {
            if (tracks.Count >= limit)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.VideoId) || string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            tracks.Add(new FavoriteItem(
                item.VideoId,
                item.Title,
                "track",
                $"https://music.youtube.com/watch?v={item.VideoId}",
                null,
                "YouTube Music",
                null));
        }

        return tracks;
    }

    /// <summary>
    /// The Likes list is an ordinary playlist. Its title is the reliable signal; the id prefix
    /// alone would also match ordinary user playlists that happen to start with "PL".
    /// </summary>
    private static bool IsLikesPlaylist(YouTubePlaylist playlist)
    {
        var title = (playlist.Title ?? string.Empty).Trim();
        return title.Equals("Likes", StringComparison.OrdinalIgnoreCase)
            || title.Equals("Liked videos", StringComparison.OrdinalIgnoreCase)
            || (playlist.Id.StartsWith(LikesPlaylistIdPrefix, StringComparison.Ordinal)
                && title.Contains("like", StringComparison.OrdinalIgnoreCase));
    }

    private static FavoritesResult Unavailable(string message)
        => new(false, message, new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
}
