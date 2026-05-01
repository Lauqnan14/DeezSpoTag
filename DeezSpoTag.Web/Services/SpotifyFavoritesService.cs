using System.Linq;
using System.Text.Json;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyFavoritesService
{
    private const string PlaylistType = "playlist";
    private const string TrackType = "track";
    private const string TitleKey = "title";
    private const string ItemsKey = "items";
    private const string ProfileKey = "profile";
    private const string SectionsKey = "sections";
    private static readonly string[] PersonalSectionKeywords =
    {
        "your top playlists",
        "your top playlist",
        "your playlists",
        "your playlist",
        "liked songs",
        "loved tracks",
        "your library",
        "favorites",
        "favourites",
        "my playlists",
        "my playlist"
    };

    private readonly PlatformAuthService _platformAuthService;
    private readonly SpotifyBlobService _blobService;
    private readonly SpotifyUserAuthStore _userAuthStore;
    private readonly ISpotifyUserContextAccessor _userContext;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly ILogger<SpotifyFavoritesService> _logger;

    public SpotifyFavoritesService(
        PlatformAuthService platformAuthService,
        SpotifyBlobService blobService,
        SpotifyUserAuthStore userAuthStore,
        ISpotifyUserContextAccessor userContext,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        ILogger<SpotifyFavoritesService> logger)
    {
        _platformAuthService = platformAuthService;
        _blobService = blobService;
        _userAuthStore = userAuthStore;
        _userContext = userContext;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _logger = logger;
    }

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        var webPlayerBlobPath = await ResolveActiveWebPlayerBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(webPlayerBlobPath))
        {
            return new FavoritesResult(false, "Spotify account not linked.", new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
        }

        try
        {
            var resolvedLimit = Math.Clamp(limit, 1, 100);
            var sections = await LoadHomeSectionsAsync(cancellationToken);
            if (sections.Count == 0)
            {
                return new FavoritesResult(false, "Spotify home feed unavailable.", new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
            }

            var (playlists, tracks) = ExtractPersonalFavorites(sections, resolvedLimit);
            return new FavoritesResult(true, null, new List<FavoriteItem>(), playlists, tracks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Spotify favorites from home feed.");
            return new FavoritesResult(false, "Spotify favorites unavailable.", new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
        }
    }

    private async Task<List<HomeSection>> LoadHomeSectionsAsync(CancellationToken cancellationToken)
    {
        var sections = await TryLoadCachedHomeSectionsAsync();
        if (sections.Count > 0)
        {
            return sections;
        }

        using var doc = await _pathfinderMetadataClient.FetchHomeFeedWithBlobAsync(null, cancellationToken);
        if (doc == null)
        {
            return new List<HomeSection>();
        }

        return ParseHomeSections(doc.RootElement);
    }

    private static async Task<List<HomeSection>> TryLoadCachedHomeSectionsAsync()
    {
        await Task.CompletedTask;
        return new List<HomeSection>();
    }

    private async Task<string?> ResolveActiveWebPlayerBlobPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            var userState = await TryLoadUserSpotifyStateAsync();
            if (userState != null)
            {
                var userBlobPath = SpotifyUserAuthStore.ResolveActiveWebPlayerBlobPath(userState);
                if (!string.IsNullOrWhiteSpace(userBlobPath)
                    && _blobService.BlobExists(userBlobPath)
                    && await _blobService.IsWebPlayerBlobAsync(userBlobPath, cancellationToken))
                {
                    return userBlobPath;
                }
            }

            var state = await _platformAuthService.LoadAsync();
            var active = state.Spotify?.ActiveAccount;
            if (string.IsNullOrWhiteSpace(active))
            {
                return null;
            }

            var platformBlobPath = state.Spotify?.Accounts
                .FirstOrDefault(a => a.Name.Equals(active, StringComparison.OrdinalIgnoreCase))
                ?.WebPlayerBlobPath;
            if (string.IsNullOrWhiteSpace(platformBlobPath) || !_blobService.BlobExists(platformBlobPath))
            {
                return null;
            }

            return await _blobService.IsWebPlayerBlobAsync(platformBlobPath, cancellationToken)
                ? platformBlobPath
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify web-player blob path for favorites.");
            return null;
        }
    }

    private async Task<SpotifyUserAuthState?> TryLoadUserSpotifyStateAsync()
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return await _userAuthStore.LoadAsync(userId);
    }

    private static (List<FavoriteItem> Playlists, List<FavoriteItem> Tracks) ExtractPersonalFavorites(
        IReadOnlyList<HomeSection> sections,
        int limit)
    {
        var playlists = new List<FavoriteItem>();
        var tracks = new List<FavoriteItem>();
        var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            if (!IsPersonalSectionTitle(section.Title))
            {
                continue;
            }

            foreach (var item in section.Items)
            {
                TryAddFavoritePlaylist(item, playlists, seenPlaylistIds, limit);
                TryAddFavoriteTrack(item, tracks, seenTrackIds, limit);
            }

            if (HasReachedFavoritesLimit(playlists, tracks, limit))
            {
                break;
            }
        }

        return (playlists, tracks);
    }

    private static void TryAddFavoritePlaylist(
        HomeItem item,
        List<FavoriteItem> playlists,
        HashSet<string> seenPlaylistIds,
        int limit)
    {
        if (!item.Type.Equals(PlaylistType, StringComparison.OrdinalIgnoreCase)
            || playlists.Count >= limit
            || !seenPlaylistIds.Add(item.Id))
        {
            return;
        }

        playlists.Add(new FavoriteItem(
            item.Id,
            item.Name,
            PlaylistType,
            $"https://open.spotify.com/playlist/{item.Id}",
            item.CoverUrl,
            string.IsNullOrWhiteSpace(item.Artists) ? "Spotify" : item.Artists,
            null));
    }

    private static void TryAddFavoriteTrack(
        HomeItem item,
        List<FavoriteItem> tracks,
        HashSet<string> seenTrackIds,
        int limit)
    {
        if (!item.Type.Equals(TrackType, StringComparison.OrdinalIgnoreCase)
            || tracks.Count >= limit
            || !seenTrackIds.Add(item.Id))
        {
            return;
        }

        tracks.Add(new FavoriteItem(
            item.Id,
            item.Name,
            TrackType,
            $"https://open.spotify.com/track/{item.Id}",
            item.CoverUrl,
            item.Artists,
            item.DurationMs));
    }

    private static bool HasReachedFavoritesLimit(
        List<FavoriteItem> playlists,
        List<FavoriteItem> tracks,
        int limit)
    {
        return playlists.Count >= limit && tracks.Count >= limit;
    }

    private static bool IsPersonalSectionTitle(string? title)
    {
        return ContainsAnyToken(title, PersonalSectionKeywords);
    }

    private static bool ContainsAnyToken(string? value, string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(value) || tokens.Length == 0)
        {
            return false;
        }

        var normalized = value.Trim();
        return tokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static List<HomeSection> ParseHomeSections(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataElement)
            || !dataElement.TryGetProperty("home", out var homeElement)
            || homeElement.ValueKind != JsonValueKind.Object)
        {
            return new List<HomeSection>();
        }

        var sections = new List<HomeSection>();
        var sectionCandidates = TryGetSectionItemsElement(homeElement, out var sectionItemsElement)
            ? sectionItemsElement.EnumerateArray()
            : FindSectionCandidates(homeElement);

        foreach (var sectionItem in sectionCandidates)
        {
            if (!sectionItem.TryGetProperty("data", out var sectionData) || sectionData.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = TryGetString(sectionData, TitleKey, "transformedLabel")
                        ?? TryGetString(sectionData, TitleKey, "text")
                        ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var items = ParseRawSectionItems(sectionItem);
            if (items.Count > 0)
            {
                sections.Add(new HomeSection(title, items));
            }
        }

        return sections;
    }

    private static bool TryGetSectionItemsElement(JsonElement homeElement, out JsonElement sectionItemsElement)
    {
        sectionItemsElement = default;
        return homeElement.TryGetProperty("sectionContainer", out var sectionContainer)
               && sectionContainer.ValueKind == JsonValueKind.Object
               && sectionContainer.TryGetProperty(SectionsKey, out var sectionsElement)
               && sectionsElement.ValueKind == JsonValueKind.Object
               && sectionsElement.TryGetProperty(ItemsKey, out sectionItemsElement)
               && sectionItemsElement.ValueKind == JsonValueKind.Array;
    }

    private static IEnumerable<JsonElement> FindSectionCandidates(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty("sectionItems", out var sectionItems)
                    && sectionItems.ValueKind == JsonValueKind.Object
                    && sectionItems.TryGetProperty(ItemsKey, out var itemsElement)
                    && itemsElement.ValueKind == JsonValueKind.Array)
                {
                    yield return current;
                }

                foreach (var property in current.EnumerateObject())
                {
                    stack.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    stack.Push(item);
                }
            }
        }
    }

    private static List<HomeItem> ParseRawSectionItems(JsonElement sectionItem)
    {
        var items = new List<HomeItem>();
        if (!TryGetRawSectionItemsElement(sectionItem, out var itemsElement))
        {
            return items;
        }

        foreach (var item in itemsElement.EnumerateArray())
        {
            if (TryParseRawHomeItem(item, out var parsedItem))
            {
                items.Add(parsedItem);
            }
        }

        return items;
    }

    private static bool TryGetRawSectionItemsElement(JsonElement sectionItem, out JsonElement itemsElement)
    {
        itemsElement = default;
        return sectionItem.TryGetProperty("sectionItems", out var sectionItems)
               && sectionItems.ValueKind == JsonValueKind.Object
               && sectionItems.TryGetProperty(ItemsKey, out itemsElement)
               && itemsElement.ValueKind == JsonValueKind.Array;
    }

    private static bool TryParseRawHomeItem(JsonElement itemElement, out HomeItem homeItem)
    {
        homeItem = default!;
        if (!TryGetContentData(itemElement, out var contentData))
        {
            return false;
        }

        var candidates = ExpandContentCandidates(contentData);
        if (!TryResolveHomeItemIdentity(itemElement, candidates, out var itemType, out var itemId))
        {
            return false;
        }

        var name = ResolveHomeItemName(candidates);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var metadata = ResolveHomeItemMetadata(itemType, candidates, contentData);
        if (metadata is null)
        {
            return false;
        }

        homeItem = new HomeItem(itemId, itemType, name, metadata.Artists, metadata.Description, metadata.CoverUrl, metadata.DurationMs);
        return true;
    }

    private static bool TryGetContentData(JsonElement itemElement, out JsonElement contentData)
    {
        contentData = default;
        return itemElement.TryGetProperty("content", out var content)
               && content.ValueKind == JsonValueKind.Object
               && content.TryGetProperty("data", out contentData)
               && contentData.ValueKind == JsonValueKind.Object;
    }

    private static bool TryResolveHomeItemIdentity(
        JsonElement itemElement,
        IEnumerable<JsonElement> candidates,
        out string itemType,
        out string itemId)
    {
        itemType = string.Empty;
        itemId = string.Empty;

        var uri = TryGetString(itemElement, "uri")
            ?? TryGetStringFromCandidates(candidates, "uri")
            ?? TryGetStringAtFromCandidates(candidates, ProfileKey, "uri")
            ?? TryGetStringAtFromCandidates(candidates, ProfileKey, "data", "uri");

        if (TryParseSpotifyUri(uri, out var uriType, out var uriId))
        {
            itemType = uriType ?? string.Empty;
            itemId = uriId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(itemType))
        {
            itemType = NormalizeItemType(
                TryGetStringFromCandidates(candidates, "type")
                ?? TryGetStringFromCandidates(candidates, "__typename")) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            itemId = TryGetStringFromCandidates(candidates, "id")
                ?? TryGetStringAtFromCandidates(candidates, ProfileKey, "id")
                ?? TryGetStringAtFromCandidates(candidates, ProfileKey, "data", "id")
                ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(itemType) && !string.IsNullOrWhiteSpace(itemId);
    }

    private static string ResolveHomeItemName(IEnumerable<JsonElement> candidates)
    {
        return TryGetStringFromCandidates(candidates, "name")
               ?? TryGetStringFromCandidates(candidates, TitleKey)
               ?? TryGetStringAtFromCandidates(candidates, TitleKey, "transformedLabel")
               ?? TryGetStringAtFromCandidates(candidates, TitleKey, "text")
               ?? string.Empty;
    }

    private static HomeItemMetadata? ResolveHomeItemMetadata(
        string itemType,
        IEnumerable<JsonElement> candidates,
        JsonElement contentData)
    {
        if (itemType.Equals(PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeItemMetadata(
                TryGetStringFromCandidates(candidates, "ownerV2", "data", "name"),
                TryGetStringFromCandidates(candidates, "description"),
                TryGetStringAtFromCandidates(candidates, "images", ItemsKey, 0, "sources", 0, "url")
                ?? TryGetStringAtFromCandidates(candidates, "images", 0, "url")
                ?? TryGetStringAtFromCandidates(candidates, "image", "sources", 0, "url")
                ?? FindFirstImageUrl(contentData),
                null);
        }

        if (itemType.Equals(TrackType, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeItemMetadata(
                ExtractArtistsFromItems(contentData, "artists", ItemsKey)
                ?? ExtractArtistsFromItems(contentData, "firstArtist", ItemsKey),
                TryGetStringFromCandidates(candidates, "description"),
                TryGetStringAtFromCandidates(candidates, "albumOfTrack", "coverArt", "sources", 0, "url")
                ?? TryGetStringAtFromCandidates(candidates, "album", "images", 0, "url")
                ?? FindFirstImageUrl(contentData),
                TryGetIntAt(contentData, "duration", "totalMilliseconds")
                ?? TryGetIntAt(contentData, "trackDuration", "totalMilliseconds"));
        }

        return null;
    }

    private static IEnumerable<JsonElement> ExpandContentCandidates(JsonElement contentData)
    {
        yield return contentData;

        if (contentData.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            yield return inner;

            if (inner.TryGetProperty("data", out var innerData) && innerData.ValueKind == JsonValueKind.Object)
            {
                yield return innerData;
            }
        }
    }

    private static string? TryGetStringFromCandidates(IEnumerable<JsonElement> candidates, params string[] path)
    {
        foreach (var candidate in candidates)
        {
            var value = TryGetString(candidate, path);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? TryGetStringAtFromCandidates(IEnumerable<JsonElement> candidates, params object[] path)
    {
        foreach (var candidate in candidates)
        {
            var value = TryGetStringAt(candidate, path);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
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

    private static string? TryGetStringAt(JsonElement element, params object[] path)
    {
        if (!TryNavigateToElement(element, path, out var current))
        {
            return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int? TryGetIntAt(JsonElement element, params object[] path)
    {
        if (!TryNavigateToElement(element, path, out var current))
        {
            return null;
        }

        if (current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var number))
        {
            return number;
        }

        if (current.ValueKind == JsonValueKind.String && int.TryParse(current.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static bool TryNavigateToElement(JsonElement element, object[] path, out JsonElement current)
    {
        current = element;
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var segment in path)
        {
            if (segment is string name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return false;
                }

                continue;
            }

            if (segment is int index)
            {
                if (!TryGetArrayElement(current, index, out current))
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryGetArrayElement(JsonElement element, int index, out JsonElement item)
    {
        item = default;
        if (index < 0 || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var currentIndex = 0;
        foreach (var candidate in element.EnumerateArray())
        {
            if (currentIndex == index)
            {
                item = candidate;
                return true;
            }

            currentIndex++;
        }

        return false;
    }

    private static bool TryParseSpotifyUri(string? uri, out string? type, out string? id)
    {
        type = null;
        id = null;

        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        var parts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        type = NormalizeItemType(parts[1]);
        id = parts[2];
        return !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(id);
    }

    private static string? NormalizeItemType(string? rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
        {
            return null;
        }

        var normalized = rawType.Trim().ToLowerInvariant();
        if (normalized.Contains(PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistType;
        }

        if (normalized.Contains(TrackType, StringComparison.OrdinalIgnoreCase))
        {
            return TrackType;
        }

        return normalized is PlaylistType or TrackType ? normalized : null;
    }

    private static string? ExtractArtistsFromItems(JsonElement element, string containerName, string itemsName)
    {
        if (!element.TryGetProperty(containerName, out var container)
            || container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty(itemsName, out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var artist in items.EnumerateArray())
        {
            var name = TryGetString(artist, "profile", "name") ?? TryGetString(artist, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static string? FindFirstImageUrl(JsonElement element)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(element);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (TryGetImageUrlFromObject(current, stack, out var imageUrl))
            {
                return imageUrl;
            }

            PushArrayItems(current, stack);
        }

        return null;
    }

    private static bool TryGetImageUrlFromObject(JsonElement element, Stack<JsonElement> stack, out string imageUrl)
    {
        imageUrl = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (TryExtractSpotifyImageUrl(property, out imageUrl))
            {
                return true;
            }

            stack.Push(property.Value);
        }

        return false;
    }

    private static bool TryExtractSpotifyImageUrl(JsonProperty property, out string imageUrl)
    {
        imageUrl = string.Empty;
        if (!property.Name.Equals("url", StringComparison.OrdinalIgnoreCase)
            || property.Value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var url = property.Value.GetString();
        if (!IsSpotifyImageHost(url))
        {
            return false;
        }

        imageUrl = url!;
        return true;
    }

    private static void PushArrayItems(JsonElement element, Stack<JsonElement> stack)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            stack.Push(item);
        }
    }

    private static bool IsSpotifyImageHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("i.scdn.co", StringComparison.OrdinalIgnoreCase)
            || value.Contains("spotifycdn.com", StringComparison.OrdinalIgnoreCase)
            || value.Contains("scdn.co", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record HomeItemMetadata(
        string? Artists,
        string? Description,
        string? CoverUrl,
        int? DurationMs);

    private sealed record HomeSection(string Title, List<HomeItem> Items);

    private sealed record HomeItem(
        string Id,
        string Type,
        string Name,
        string? Artists,
        string? Description,
        string? CoverUrl,
        int? DurationMs);
}
