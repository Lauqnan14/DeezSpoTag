using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Integrations.Discogs;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.Favorites;

/// <summary>
/// Discogs library. The personal access token is already stored and the username is resolved from
/// it at save time. Discogs has no concept of liked tracks, so this exposes the user's playlists
/// and their collection items; there is deliberately no tracks list.
/// </summary>
public sealed class DiscogsFavoritesService : IFavoritesProvider
{
    private const string ApiBase = "https://api.discogs.com";
    private const int PageSize = 50;

    private readonly HttpClient _httpClient;
    private readonly PlatformAuthService _platformAuth;
    private readonly ILogger<DiscogsFavoritesService> _logger;

    public DiscogsFavoritesService(
        HttpClient httpClient,
        PlatformAuthService platformAuth,
        ILogger<DiscogsFavoritesService> logger)
    {
        _httpClient = httpClient;
        _platformAuth = platformAuth;
        _logger = logger;
    }

    public string Key => "discogs";

    public string DisplayName => "Discogs";

    public string IconPath => "/images/icons/discogs.png";

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        var resolvedLimit = Math.Clamp(limit, 1, 100);
        try
        {
            var state = await _platformAuth.LoadAsync();
            var discogs = state.Discogs;
            if (discogs is null
                || string.IsNullOrWhiteSpace(discogs.Token)
                || string.IsNullOrWhiteSpace(discogs.Username))
            {
                return Unavailable("Discogs account not connected.");
            }

            var username = Uri.EscapeDataString(discogs.Username);
            var playlistsTask = ReadJsonAsync(
                $"{ApiBase}/users/{username}/playlists?per_page={PageSize}&page=1", discogs.Token, cancellationToken);
            var collectionTask = ReadJsonAsync(
                $"{ApiBase}/users/{username}/collection?per_page={PageSize}&page=1", discogs.Token, cancellationToken);

            await Task.WhenAll(playlistsTask, collectionTask);

            var playlists = MapPlaylists(await playlistsTask, resolvedLimit);
            var collection = MapCollection(await collectionTask, resolvedLimit);

            return new FavoritesResult(true, null, collection, playlists, new List<FavoriteItem>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Discogs library.");
            return Unavailable("Discogs library unavailable.");
        }
    }

    private async Task<JsonDocument?> ReadJsonAsync(
        string uri,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={token}");
        request.Headers.TryAddWithoutValidation("User-Agent", "DeezSpoTag");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static List<FavoriteItem> MapPlaylists(JsonDocument? doc, int limit)
    {
        var playlists = new List<FavoriteItem>();
        if (doc is null || !TryGetItems(doc.RootElement, out var items))
        {
            return playlists;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (playlists.Count >= limit)
            {
                break;
            }

            var id = ReadInt(entry, "id")?.ToString(CultureInfo.InvariantCulture);
            var name = ReadString(entry, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            playlists.Add(new FavoriteItem(
                id,
                name,
                "playlist",
                $"https://www.discogs.com/playlist/{id}",
                null,
                "Discogs",
                null));
        }

        return playlists;
    }

    private static List<FavoriteItem> MapCollection(JsonDocument? doc, int limit)
    {
        var albums = new List<FavoriteItem>();
        if (doc is null || !TryGetItems(doc.RootElement, out var items))
        {
            return albums;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (albums.Count >= limit)
            {
                break;
            }

            var id = ReadInt(entry, "id")?.ToString(CultureInfo.InvariantCulture);
            var title = ReadString(entry, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var cover = ReadString(entry, "cover_image") ?? ReadString(entry, "thumb");
            var artists = ReadString(entry, "artists");
            albums.Add(new FavoriteItem(
                id,
                title,
                "album",
                $"https://www.discogs.com/release/{id}",
                string.IsNullOrWhiteSpace(cover) ? null : cover,
                string.IsNullOrWhiteSpace(artists) ? "Discogs" : artists,
                null));
        }

        return albums;
    }

    private static bool TryGetItems(JsonElement root, out JsonElement items)
    {
        items = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("items", out items)
            && items.ValueKind == JsonValueKind.Array;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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
            && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static FavoritesResult Unavailable(string message)
        => new(false, message, new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
}
