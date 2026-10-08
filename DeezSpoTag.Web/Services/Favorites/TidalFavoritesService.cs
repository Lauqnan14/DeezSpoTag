using System.Text.Json;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.Favorites;

/// <summary>
/// Tidal library. The login form already collects the user access/refresh token and user id, and
/// TidalPlaylistSyncTarget already reads the account's playlists, so this adds the two collection
/// reads the Favorites tab needs.
/// </summary>
public sealed class TidalFavoritesService : IFavoritesProvider
{
    private const string ApiBase = "https://openapi.tidal.com/v2";
    private const int PageSize = 50;

    private readonly HttpClient _httpClient;
    private readonly PlatformSyncConnectionProvider _connections;
    private readonly ILogger<TidalFavoritesService> _logger;

    public TidalFavoritesService(
        HttpClient httpClient,
        PlatformSyncConnectionProvider connections,
        ILogger<TidalFavoritesService> logger)
    {
        _httpClient = httpClient;
        _connections = connections;
        _logger = logger;
    }

    public string Key => "tidal";

    public string DisplayName => "Tidal";

    public string IconPath => "/images/icons/tidal.png";

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        var resolvedLimit = Math.Clamp(limit, 1, 100);
        try
        {
            var credentials = await _connections.GetTidalAsync(cancellationToken);
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.AccessToken))
            {
                return Unavailable("Tidal account not connected.");
            }

            // A client-credentials token authenticates the app but has no user behind it, so the
            // collection reads answer 401/403. Report that plainly rather than an empty library.
            var tracksTask = ReadCollectionAsync(credentials, "userCollectionTracks", "track", resolvedLimit, cancellationToken);
            var albumsTask = ReadCollectionAsync(credentials, "userCollectionAlbums", "album", resolvedLimit, cancellationToken);
            var playlistsTask = ReadOwnPlaylistsAsync(credentials, resolvedLimit, cancellationToken);

            await Task.WhenAll(tracksTask, albumsTask, playlistsTask);

            var tracks = await tracksTask;
            var albums = await albumsTask;
            var playlists = await playlistsTask;

            if (tracks.Count == 0 && albums.Count == 0 && playlists.Count == 0)
            {
                return new FavoritesResult(
                    false,
                    "Tidal token cannot read your library. Save a user access token in the Tidal login form.",
                    new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
            }

            return new FavoritesResult(true, null, albums, playlists, tracks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Tidal library.");
            return Unavailable("Tidal library unavailable.");
        }
    }

    private async Task<List<FavoriteItem>> ReadCollectionAsync(
        TidalTargetCredentials credentials,
        string collection,
        string itemType,
        int limit,
        CancellationToken cancellationToken)
    {
        var include = itemType == "track"
            ? "items.artists,items.albums,items.albums.coverArt"
            : "items.artists,items.coverArt";

        var uri = $"{ApiBase}/{collection}/me/relationships/items"
            + $"?countryCode={ResolveCountryCode(credentials)}"
            + $"&include={Uri.EscapeDataString(include)}";

        return await ReadItemsAsync(credentials, uri, itemType, limit, cancellationToken);
    }

    private async Task<List<FavoriteItem>> ReadOwnPlaylistsAsync(
        TidalTargetCredentials credentials,
        int limit,
        CancellationToken cancellationToken)
    {
        var uri = $"{ApiBase}/playlists?filter[owners.id]=me&countryCode={ResolveCountryCode(credentials)}";
        return await ReadItemsAsync(credentials, uri, "playlist", limit, cancellationToken);
    }

    private async Task<List<FavoriteItem>> ReadItemsAsync(
        TidalTargetCredentials credentials,
        string uri,
        string itemType,
        int limit,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.AccessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.api+json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<FavoriteItem>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var result = new List<FavoriteItem>();
        foreach (var entry in EnumerateIncluded(doc.RootElement))
        {
            if (result.Count >= limit)
            {
                break;
            }

            var mapped = MapItem(entry, itemType);
            if (mapped is not null)
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    /// <summary>
    /// Tidal v2 returns the entity in "included" and only its id in "data". Walking both covers a
    /// response that inlines either.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateIncluded(JsonElement root)
    {
        if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray()
                .Where(item => item.TryGetProperty("id", out _) && item.TryGetProperty("type", out _)))
            {
                yield return item;
            }
        }

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out _)))
                {
                    yield return item;
                }
            }
            else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _))
            {
                yield return data;
            }
        }
    }

    private static FavoriteItem? MapItem(JsonElement item, string itemType)
    {
        var id = ReadString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = ReadString(item, "title");
        var name = itemType == "track"
            ? title ?? ReadNestedName(item, "artists")
            : title;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var subtitle = itemType switch
        {
            "track" => ReadArtistNames(item) ?? ReadNestedName(item, "artists") ?? "Tidal",
            "album" => ReadArtistNames(item) ?? ReadNestedName(item, "artists") ?? "Tidal",
            _ => "Tidal"
        };

        var url = itemType switch
        {
            "track" => $"https://tidal.com/browse/track/{id}",
            "album" => $"https://tidal.com/browse/album/{id}",
            _ => $"https://tidal.com/browse/playlist/{id}"
        };

        int? durationMs = null;
        if (item.TryGetProperty("duration", out var duration)
            && duration.ValueKind == JsonValueKind.String
            && int.TryParse(duration.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
        {
            durationMs = milliseconds;
        }

        return new FavoriteItem(id, name, itemType, url, ReadCoverUrl(item), subtitle, durationMs);
    }

    private static string? ReadArtistNames(JsonElement item)
    {
        if (!item.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var name in artists.EnumerateArray()
            .Select(artist => ReadString(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            names.Add(name!);
        }

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static string? ReadNestedName(JsonElement item, string container)
    {
        if (!item.TryGetProperty(container, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(nested, "name");
    }

    private static string? ReadCoverUrl(JsonElement item)
    {
        if (!item.TryGetProperty("coverArt", out var coverArt) || coverArt.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadString(coverArt, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return $"https://resources.tidal.com/images/{id.Replace(" ", string.Empty, StringComparison.Ordinal)}/300x300.jpg";
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static string ResolveCountryCode(TidalTargetCredentials credentials)
        => string.IsNullOrWhiteSpace(credentials.CountryCode) ? "US" : credentials.CountryCode;

    private static FavoritesResult Unavailable(string message)
        => new(false, message, new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
}
