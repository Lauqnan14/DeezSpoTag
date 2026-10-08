using System.Text.Json;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.Favorites;

/// <summary>
/// Qobuz saved content. The account's user auth token is already validated at save time by
/// QobuzAccountProfileService and is proven against user-scoped calls by QobuzPlaylistSyncTarget,
/// so this only needs the favorites reads.
/// </summary>
public sealed class QobuzFavoritesService : IFavoritesProvider
{
    private const string ApiBase = "https://www.qobuz.com/api.json/0.2";
    private const int PageSize = 50;

    private readonly HttpClient _httpClient;
    private readonly PlatformSyncConnectionProvider _connections;
    private readonly ILogger<QobuzFavoritesService> _logger;

    public QobuzFavoritesService(
        HttpClient httpClient,
        PlatformSyncConnectionProvider connections,
        ILogger<QobuzFavoritesService> logger)
    {
        _httpClient = httpClient;
        _connections = connections;
        _logger = logger;
    }

    public string Key => "qobuz";

    public string DisplayName => "Qobuz";

    public string IconPath => "/images/icons/qobuz.png";

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        var resolvedLimit = Math.Clamp(limit, 1, 100);
        try
        {
            var credentials = await _connections.GetQobuzAsync(cancellationToken);
            if (credentials is null
                || string.IsNullOrWhiteSpace(credentials.AppId)
                || string.IsNullOrWhiteSpace(credentials.AuthToken))
            {
                return Unavailable("Qobuz account not connected.");
            }

            var favoritesTask = ReadFavoritesAsync(credentials, "tracks", resolvedLimit, cancellationToken);
            var albumsTask = ReadFavoritesAsync(credentials, "albums", resolvedLimit, cancellationToken);
            var playlistsTask = ReadUserPlaylistsAsync(credentials, resolvedLimit, cancellationToken);

            await Task.WhenAll(favoritesTask, albumsTask, playlistsTask);

            return new FavoritesResult(
                true,
                null,
                await albumsTask,
                await playlistsTask,
                await favoritesTask);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Qobuz favorites.");
            return Unavailable("Qobuz favorites unavailable.");
        }
    }

    /// <summary>
    /// Qobuz serves every saved type from one endpoint, singular "favorite/", parameterized by type.
    /// </summary>
    private async Task<List<FavoriteItem>> ReadFavoritesAsync(
        QobuzTargetCredentials credentials,
        string type,
        int limit,
        CancellationToken cancellationToken)
    {
        var uri = $"{ApiBase}/favorite/getUserFavorites?type={Uri.EscapeDataString(type)}&limit={PageSize}&offset=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("x-app-id", credentials.AppId);
        request.Headers.TryAddWithoutValidation("X-User-Auth-Token", credentials.AuthToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<FavoriteItem>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new List<FavoriteItem>();
        }

        var result = new List<FavoriteItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (result.Count >= limit)
            {
                break;
            }

            var mapped = MapItem(item, type, limit);
            if (mapped is not null)
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    private async Task<List<FavoriteItem>> ReadUserPlaylistsAsync(
        QobuzTargetCredentials credentials,
        int limit,
        CancellationToken cancellationToken)
    {
        var uri = $"{ApiBase}/playlist/getUserPlaylists?limit={PageSize}&offset=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("x-app-id", credentials.AppId);
        request.Headers.TryAddWithoutValidation("X-User-Auth-Token", credentials.AuthToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<FavoriteItem>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new List<FavoriteItem>();
        }

        var result = new List<FavoriteItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (result.Count >= limit)
            {
                break;
            }

            var id = ReadInt(item, "id")?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var owner = ReadString(item, "owner", "name");
            result.Add(new FavoriteItem(
                id,
                name,
                "playlist",
                $"https://open.qobuz.com/playlist/{id}",
                ReadCoverUrl(item),
                string.IsNullOrWhiteSpace(owner) ? "Qobuz" : owner,
                null));
        }

        return result;
    }

    private static FavoriteItem? MapItem(JsonElement item, string type, int limit)
    {
        var id = ReadInt(item, "id")?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var name = ReadString(item, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var itemType = type switch
        {
            "tracks" => "track",
            "albums" => "album",
            _ => type
        };

        var subtitle = type switch
        {
            "tracks" => BuildArtistNames(item) ?? ReadString(item, "performer", "name"),
            "albums" => ReadString(item, "artist", "name"),
            _ => "Qobuz"
        };

        int? durationMs = null;
        if (itemType == "track")
        {
            var seconds = ReadInt(item, "duration");
            if (seconds is > 0)
            {
                durationMs = seconds * 1000;
            }
        }

        var url = itemType switch
        {
            "track" => $"https://open.qobuz.com/track/{id}",
            "album" => $"https://open.qobuz.com/album/{id}",
            _ => $"https://open.qobuz.com/{itemType}/{id}"
        };

        return new FavoriteItem(id, name, itemType, url, ReadCoverUrl(item), subtitle, durationMs);
    }

    private static string? BuildArtistNames(JsonElement item)
    {
        if (!item.TryGetProperty("performer", out var performer)
            || !performer.TryGetProperty("name", out var name))
        {
            return null;
        }

        return name.ToString();
    }

    private static string? ReadCoverUrl(JsonElement item)
    {
        // Qobuz image urls are ids under get.qobuz.com, not direct links.
        if (!item.TryGetProperty("image", out var image))
        {
            return null;
        }

        var raw = image.ValueKind == JsonValueKind.String
            ? image.ToString()
            : image.TryGetProperty("id", out var id) ? id.ToString() : null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 6)
        {
            return null;
        }

        var size = raw.TrimStart('l', 'm', 's', 'x');
        return $"https://static.qobuz.com/images/covers/{size}_raw.jpg";
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static FavoritesResult Unavailable(string message)
        => new(false, message, new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
}
