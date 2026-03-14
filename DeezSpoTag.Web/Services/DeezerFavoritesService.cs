using DeezSpoTag.Integrations.Deezer;
using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class DeezerFavoritesService
{
    private readonly DeezerClient _deezerClient;
    private readonly ILogger<DeezerFavoritesService> _logger;

    public DeezerFavoritesService(DeezerClient deezerClient, ILogger<DeezerFavoritesService> logger)
    {
        _deezerClient = deezerClient;
        _logger = logger;
    }

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        if (!_deezerClient.LoggedIn || string.IsNullOrWhiteSpace(_deezerClient.CurrentUser?.Id))
        {
            return new FavoritesResult(false, "Deezer account not linked.", new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
        }

        var userId = _deezerClient.CurrentUser.Id!;

        try
        {
            var playlistsTask = GetUserPlaylistsAsync(userId, limit);
            var albumsTask = GetUserAlbumsAsync(userId, limit);
            var tracksTask = GetFavoriteTracksAsync(limit);

            await Task.WhenAll(playlistsTask, albumsTask, tracksTask);

            return new FavoritesResult(true, null, albumsTask.Result, playlistsTask.Result, tracksTask.Result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Deezer favorites.");
            return new FavoritesResult(false, "Deezer favorites unavailable.", new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
        }
    }

    private async Task<List<FavoriteItem>> GetUserAlbumsAsync(string userId, int limit)
    {
        var profile = await _deezerClient.GetUserProfilePageAsync(userId, "albums", limit);
        var data = profile.SelectToken("TAB.albums.data") as JArray;

        var items = new List<FavoriteItem>();
        if (data == null)
        {
            return items;
        }

        foreach (var entry in data)
        {
            var id = entry["ALB_ID"]?.ToString();
            var title = entry["ALB_TITLE"]?.ToString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var pictureId = entry["ALB_PICTURE"]?.ToString() ?? "";
            var picture = BuildPictureUrl("cover", pictureId, 250);
            var artistName = entry["ART_NAME"]?.ToString() ?? "Deezer";

            items.Add(new FavoriteItem(
                id,
                title,
                "album",
                $"https://www.deezer.com/album/{id}",
                picture,
                artistName,
                null));
        }

        return items;
    }

    private async Task<List<FavoriteItem>> GetUserPlaylistsAsync(string userId, int limit)
    {
        var profile = await _deezerClient.GetUserProfilePageAsync(userId, "playlists", limit);
        var blogName = profile.SelectToken("DATA.USER.BLOG_NAME")?.ToString() ?? "Deezer";
        var data = profile.SelectToken("TAB.playlists.data") as JArray;

        var items = new List<FavoriteItem>();
        if (data == null)
        {
            return items;
        }

        foreach (var entry in data)
        {
            var id = entry["PLAYLIST_ID"]?.ToString();
            var title = entry["TITLE"]?.ToString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var pictureType = entry["PICTURE_TYPE"]?.ToString() ?? "playlist";
            var pictureId = entry["PLAYLIST_PICTURE"]?.ToString() ?? "";
            var picture = BuildPictureUrl(pictureType, pictureId, 250);
            var trackCount = entry["NB_SONG"]?.ToObject<int?>() ?? entry["NB_TRACK"]?.ToObject<int?>();
            var creatorName = entry["PARENT_USERNAME"]?.ToString() ?? blogName;
            var subtitle = trackCount.HasValue ? $"{creatorName} \u2022 {trackCount.Value} tracks" : creatorName;

            items.Add(new FavoriteItem(
                id,
                title,
                "playlist",
                $"https://www.deezer.com/playlist/{id}",
                picture,
                subtitle,
                null));
        }

        return items;
    }

    private async Task<List<FavoriteItem>> GetFavoriteTracksAsync(int limit)
    {
        var favoriteIds = await _deezerClient.GetUserFavoriteIdsAsync(limit);
        var idArray = favoriteIds.SelectToken("data") as JArray;
        if (idArray == null || idArray.Count == 0)
        {
            return new List<FavoriteItem>();
        }

        var ids = idArray
            .Select(entry => entry?["SNG_ID"]?.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        if (ids.Count == 0)
        {
            return new List<FavoriteItem>();
        }

        var tracks = await _deezerClient.GetTracksAsync(ids);
        var items = new List<FavoriteItem>();
        foreach (var track in tracks)
        {
            var id = track.SngId.ToString();
            var title = track.SngTitle;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var albumTitle = track.AlbTitle;
            var artistName = track.ArtName;
            var subtitle = string.IsNullOrWhiteSpace(albumTitle) ? artistName : $"{artistName} \u2022 {albumTitle}";
            var imageUrl = BuildPictureUrl("cover", track.AlbPicture, 250);

            items.Add(new FavoriteItem(
                id,
                title,
                "track",
                $"https://www.deezer.com/track/{id}",
                imageUrl,
                subtitle,
                track.Duration > 0 ? track.Duration * 1000 : (int?)null));
        }

        return items;
    }

    private static string? BuildPictureUrl(string type, string pictureId, int size)
    {
        if (string.IsNullOrWhiteSpace(pictureId))
        {
            return null;
        }

        return $"https://e-cdns-images.dzcdn.net/images/{type}/{pictureId}/{size}x{size}-000000-80-0-0.jpg";
    }
}
