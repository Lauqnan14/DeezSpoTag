using System.Text.Json;
using DeezSpoTag.Integrations.Apple;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.Favorites;

/// <summary>
/// Apple Music library. Both tokens are already stored and resolved by
/// PlatformSyncConnectionProvider, and /me/library/playlists is already read by the playlist sync
/// target; this adds the saved songs and albums reads.
/// </summary>
public sealed class AppleMusicFavoritesService : IFavoritesProvider
{
    private const string ApiBase = "https://amp-api.music.apple.com/v1/me/library";
    private const int PageSize = 100;

    private readonly HttpClient _httpClient;
    private readonly PlatformSyncConnectionProvider _connections;
    private readonly ILogger<AppleMusicFavoritesService> _logger;

    public AppleMusicFavoritesService(
        HttpClient httpClient,
        PlatformSyncConnectionProvider connections,
        ILogger<AppleMusicFavoritesService> logger)
    {
        _httpClient = httpClient;
        _connections = connections;
        _logger = logger;
    }

    public string Key => "appleMusic";

    public string DisplayName => "Apple Music";

    public string IconPath => "/images/icons/apple-music.png";

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        var resolvedLimit = Math.Clamp(limit, 1, 100);
        try
        {
            var credentials = await _connections.GetAppleAsync(cancellationToken);
            if (credentials is null
                || string.IsNullOrWhiteSpace(credentials.AuthorizationToken)
                || string.IsNullOrWhiteSpace(credentials.MediaUserToken))
            {
                return Unavailable("Apple Music account not connected.");
            }

            var songsTask = ReadLibraryAsync(credentials, "songs", resolvedLimit, cancellationToken);
            var albumsTask = ReadLibraryAsync(credentials, "albums", resolvedLimit, cancellationToken);
            var playlistsTask = ReadLibraryAsync(credentials, "playlists", resolvedLimit, cancellationToken);

            await Task.WhenAll(songsTask, albumsTask, playlistsTask);

            return new FavoritesResult(
                true,
                null,
                await albumsTask,
                await playlistsTask,
                await songsTask);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Apple Music library.");
            return Unavailable("Apple Music library unavailable.");
        }
    }

    private async Task<List<FavoriteItem>> ReadLibraryAsync(
        AppleTargetCredentials credentials,
        string collection,
        int limit,
        CancellationToken cancellationToken)
    {
        var uri = $"{ApiBase}/{collection}?limit={PageSize}&offset=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.AuthorizationToken}");
        request.Headers.TryAddWithoutValidation("Media-User-Token", credentials.MediaUserToken);
        request.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<FavoriteItem>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new List<FavoriteItem>();
        }

        var result = new List<FavoriteItem>();
        foreach (var entry in data.EnumerateArray())
        {
            if (result.Count >= limit)
            {
                break;
            }

            var mapped = MapEntry(entry, collection);
            if (mapped is not null)
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    private static FavoriteItem? MapEntry(JsonElement entry, string collection)
    {
        if (!entry.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var attributes = entry.TryGetProperty("attributes", out var attrs) ? attrs : default;
        var name = ReadString(attributes, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var (itemType, url) = collection switch
        {
            "songs" => ("track", $"https://music.apple.com/us/song/{id}"),
            "albums" => ("album", $"https://music.apple.com/us/album/{id}"),
            _ => ("playlist", $"https://music.apple.com/us/playlist/{id}")
        };

        var subtitle = itemType == "track"
            ? ReadString(attributes, "artistName")
            : ReadString(attributes, "artistName") ?? ReadString(attributes, "curatorName");

        int? durationMs = null;
        if (itemType == "track"
            && attributes.TryGetProperty("durationInMillis", out var duration)
            && duration.TryGetInt32(out var durationNumber))
        {
            durationMs = durationNumber;
        }

        return new FavoriteItem(id, name, itemType, url, ReadArtworkUrl(attributes), subtitle, durationMs);
    }

    private static string? ReadArtworkUrl(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("artwork", out var artwork) || artwork.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The library listing carries the smallest variant; request a usable width from the template.
        if (!artwork.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return urlElement.GetString()?.Replace("{w}", "300", StringComparison.Ordinal)
                                  .Replace("{h}", "300", StringComparison.Ordinal)
                                  .Replace("{f}", "jpg", StringComparison.Ordinal);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static FavoritesResult Unavailable(string message)
        => new(false, message, new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
}
