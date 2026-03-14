using System.Text.Json;
using System.Linq;

namespace DeezSpoTag.Web.Services;

public sealed record SpotifyRecommendationItem(
    string Id,
    string Uri,
    string Type,
    string Name,
    string? Artists,
    string? Description,
    string? CoverUrl);

public sealed record SpotifyRecommendationSection(string Title, List<SpotifyRecommendationItem> Items);

public sealed class SpotifyRecommendationService
{
    private const string AlbumType = "album";
    private const string TitleField = "title";
    private const string ItemsField = "items";
    private static readonly string[] RecommendationKeys =
    {
        "recommend", "related", "similar", "morelike", "suggest"
    };
    private static readonly string[] DataPath = { "data" };
    private static readonly string[] ItemV2DataPath = { "itemV2", "data" };
    private static readonly string[] ContentDataPath = { "content", "data" };

    private readonly SpotifyPathfinderMetadataClient _pathfinder;
    private readonly ILogger<SpotifyRecommendationService> _logger;

    public SpotifyRecommendationService(
        SpotifyPathfinderMetadataClient pathfinder,
        ILogger<SpotifyRecommendationService> logger)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    public async Task<List<SpotifyRecommendationSection>> FetchRecommendationsAsync(
        string url,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = await FetchRecommendationsInternalAsync(url, limit, cancellationToken);
        return result.Sections;
    }

    public async Task<RecommendationDebugResult> FetchRecommendationsDebugAsync(
        string url,
        int limit,
        CancellationToken cancellationToken)
    {
        return await FetchRecommendationsInternalAsync(url, limit, cancellationToken);
    }

    private async Task<RecommendationDebugResult> FetchRecommendationsInternalAsync(
        string url,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!SpotifyMetadataService.TryParseSpotifyUrl(url, out var type, out var id))
        {
            return new RecommendationDebugResult(new List<SpotifyRecommendationSection>(), null, null, null);
        }

        if (!string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, AlbumType, StringComparison.OrdinalIgnoreCase))
        {
            return new RecommendationDebugResult(new List<SpotifyRecommendationSection>(), null, null, null);
        }

        var contextUri = $"spotify:{type}:{id}";
        var payload = await _pathfinder.FetchRecommendationsPayloadAsync(
            contextUri,
            type,
            limit,
            cancellationToken);

        if (payload == null)
        {
            _logger.LogInformation("Spotify recommendations unavailable for ContextUri.");
            return new RecommendationDebugResult(new List<SpotifyRecommendationSection>(), null, null, null);
        }

        using var doc = payload.Document;
        var sections = ExtractRecommendationSections(doc.RootElement, limit);
        var rawJson = doc.RootElement.GetRawText();
        return new RecommendationDebugResult(sections, payload.OperationName, payload.VariablesJson, rawJson);
    }

    private static List<SpotifyRecommendationSection> ExtractRecommendationSections(
        JsonElement root,
        int limit)
    {
        var sections = new List<SpotifyRecommendationSection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkRecommendationTree(root, limit, seen, sections);
        TryAddFallbackRecommendationSections(root, limit, seen, sections);
        return sections;
    }

    private static void WalkRecommendationTree(
        JsonElement element,
        int limit,
        HashSet<string> seen,
        List<SpotifyRecommendationSection> sections)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsRecommendationKey(prop.Name))
                    {
                        TryAddRecommendationSection(prop.Name, prop.Value, limit, seen, sections);
                    }

                    WalkRecommendationTree(prop.Value, limit, seen, sections);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkRecommendationTree(item, limit, seen, sections);
                }
                break;
        }
    }

    private static void TryAddRecommendationSection(
        string key,
        JsonElement element,
        int limit,
        HashSet<string> seen,
        List<SpotifyRecommendationSection> sections)
    {
        var items = ExtractItems(element, limit, seen);
        if (items.Count == 0)
        {
            return;
        }

        var title = TryGetString(element, TitleField, "text")
                    ?? TryGetString(element, TitleField)
                    ?? TryGetString(element, "name")
                    ?? HumanizeKey(key)
                    ?? "Recommended";
        sections.Add(new SpotifyRecommendationSection(title, items));
    }

    private static void TryAddFallbackRecommendationSections(
        JsonElement root,
        int limit,
        HashSet<string> seen,
        List<SpotifyRecommendationSection> sections)
    {
        if (sections.Count == 0)
        {
            TryAddMoreLikeSection(root, limit, seen, sections);
        }

        if (sections.Count == 0)
        {
            TryAddHomeSections(root, limit, seen, sections);
        }
    }

    private static void TryAddMoreLikeSection(
        JsonElement root,
        int limit,
        HashSet<string> seen,
        List<SpotifyRecommendationSection> sections)
    {
        if (!TryGetNested(root, out var moreLike, "data", "moreLikeThisPlaylist"))
        {
            return;
        }

        var items = ExtractItems(moreLike, limit, seen);
        if (items.Count > 0)
        {
            sections.Add(new SpotifyRecommendationSection("You might also like", items));
        }
    }

    private static void TryAddHomeSections(
        JsonElement root,
        int limit,
        HashSet<string> seen,
        List<SpotifyRecommendationSection> sections)
    {
        if (!TryGetNested(root, out var homeSections, "data", "homeSections", "sections")
            || homeSections.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var section in homeSections.EnumerateArray())
        {
            var items = ExtractItems(section, limit, seen);
            if (items.Count == 0)
            {
                continue;
            }

            var title = TryGetString(section, TitleField, "text")
                        ?? TryGetString(section, TitleField)
                        ?? "You might also like";
            sections.Add(new SpotifyRecommendationSection(title, items));
        }
    }

    private static List<SpotifyRecommendationItem> ExtractItems(
        JsonElement element,
        int limit,
        HashSet<string> seen)
    {
        var items = new List<SpotifyRecommendationItem>();
        foreach (var candidate in EnumerateItemCandidates(element))
        {
            if (items.Count >= limit)
            {
                break;
            }

            var item = TryParseRecommendationItem(candidate);
            if (item == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Uri) || !seen.Add(item.Uri))
            {
                continue;
            }

            items.Add(item);
        }

        return items;
    }

    private static IEnumerable<JsonElement> EnumerateItemCandidates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                yield return item;
            }
            yield break;
        }

        if ((TryGetNested(element, out var items, ItemsField) ||
             TryGetNested(element, out items, "sectionItems", ItemsField) ||
             TryGetNested(element, out items, "content", ItemsField) ||
             TryGetNested(element, out items, "data", ItemsField) ||
             TryGetNested(element, out items, "section", ItemsField) ||
             TryGetNested(element, out items, "playlists", ItemsField)) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static SpotifyRecommendationItem? TryParseRecommendationItem(JsonElement element)
    {
        var candidate = element;
        if (TryGetNestedAny(
                element,
                out var data,
                DataPath,
                ItemV2DataPath,
                ContentDataPath))
        {
            candidate = data;
        }

        var uri = TryGetString(candidate, "uri")
                  ?? TryGetString(element, "uri");
        var type = NormalizeSpotifyType(uri)
                   ?? NormalizeSpotifyType(TryGetString(candidate, "__typename"))
                   ?? NormalizeSpotifyType(TryGetString(candidate, "type"));
        var id = ExtractIdFromUri(uri) ?? TryGetString(candidate, "id") ?? string.Empty;

        var name = TryGetString(candidate, "name")
                   ?? TryGetString(candidate, TitleField)
                   ?? TryGetString(candidate, "displayName")
                   ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(uri) && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(type))
        {
            uri = $"spotify:{type}:{id}";
        }

        var artists = ExtractArtists(candidate);
        var description = TryGetString(candidate, "description") ?? TryGetString(candidate, "subtitle");
        var coverUrl = ExtractCoverUrl(candidate);

        return new SpotifyRecommendationItem(
            id,
            uri ?? string.Empty,
            type,
            name,
            artists,
            description,
            coverUrl);
    }

    private static bool IsRecommendationKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Replace("_", string.Empty).ToLowerInvariant();
        return RecommendationKeys.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeSpotifyType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("spotify:"))
        {
            var parts = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 ? parts[1] : null;
        }

        return normalized switch
        {
            "playlist" or AlbumType or "track" or "artist" or "show" or "episode" => normalized,
            "playlistresponse" or "playlistv2" or "playlistunion" => "playlist",
            "albumresponse" or "albumunion" => AlbumType,
            "trackresponse" or "trackunion" => "track",
            _ => null
        };
    }

    private static string? ExtractArtists(JsonElement element)
    {
        var names = new List<string>();
        if (TryGetNested(element, out var artistsInItems, "artists", ItemsField) &&
            artistsInItems.ValueKind == JsonValueKind.Array)
        {
            AddArtistNames(artistsInItems, names, prioritizeProfileName: true);
        }
        else if (TryGetNested(element, out var artists, "artists") &&
                 artists.ValueKind == JsonValueKind.Array)
        {
            AddArtistNames(artists, names, prioritizeProfileName: false);
        }

        TryAddOwnerName(element, names);

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static void AddArtistNames(
        JsonElement artists,
        List<string> names,
        bool prioritizeProfileName)
    {
        foreach (var artist in artists.EnumerateArray())
        {
            var name = prioritizeProfileName
                ? TryGetString(artist, "profile", "name") ?? TryGetString(artist, "name")
                : TryGetString(artist, "name") ?? TryGetString(artist, "profile", "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }
    }

    private static void TryAddOwnerName(JsonElement element, List<string> names)
    {
        if (names.Count > 0 || !TryGetNested(element, out var owner, "ownerV2", "data"))
        {
            return;
        }

        var name = TryGetString(owner, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            names.Add(name);
        }
    }

    private static string? ExtractCoverUrl(JsonElement element)
    {
        if (TryExtractCoverArtUrl(element, out var coverUrl)
            || TryExtractAlbumCoverArtUrl(element, out coverUrl)
            || TryExtractImageUrl(element, out coverUrl)
            || TryExtractImageItemsUrl(element, out coverUrl))
        {
            return coverUrl;
        }

        return TryGetString(element, "imageUrl")
               ?? TryGetString(element, "image")
               ?? TryGetString(element, "cover")
               ?? TryGetString(element, "picture");
    }

    private static bool TryExtractCoverArtUrl(JsonElement element, out string? coverUrl)
    {
        coverUrl = null;
        if (!TryGetNested(element, out var coverArt, "coverArt"))
        {
            return false;
        }

        coverUrl = ExtractCoverUrlFromSources(coverArt);
        return !string.IsNullOrWhiteSpace(coverUrl);
    }

    private static bool TryExtractAlbumCoverArtUrl(JsonElement element, out string? coverUrl)
    {
        coverUrl = null;
        if (!(TryGetNested(element, out var album, "albumOfTrack") ||
              TryGetNested(element, out album, AlbumType)) ||
            !TryGetNested(album, out var coverArtAlbum, "coverArt"))
        {
            return false;
        }

        coverUrl = ExtractCoverUrlFromSources(coverArtAlbum);
        return !string.IsNullOrWhiteSpace(coverUrl);
    }

    private static bool TryExtractImageUrl(JsonElement element, out string? coverUrl)
    {
        coverUrl = null;
        if (!TryGetNested(element, out var images, "images") || images.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        coverUrl = images.EnumerateArray()
            .Select(image => TryGetString(image, "url"))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        return !string.IsNullOrWhiteSpace(coverUrl);
    }

    private static bool TryExtractImageItemsUrl(JsonElement element, out string? coverUrl)
    {
        coverUrl = null;
        if (!TryGetNested(element, out var imageItems, "images", ItemsField) ||
            imageItems.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        coverUrl = imageItems.EnumerateArray()
            .SelectMany(GetImageSourceUrls)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        return !string.IsNullOrWhiteSpace(coverUrl);
    }

    private static string? ExtractCoverUrlFromSources(JsonElement coverArt)
    {
        if (!TryGetNested(coverArt, out var sources, "sources") ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        var bestSize = 0;
        foreach (var source in sources.EnumerateArray())
        {
            var url = TryGetString(source, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            fallback ??= url;
            var width = source.TryGetProperty("width", out var widthProp) && widthProp.ValueKind == JsonValueKind.Number
                ? widthProp.GetInt32()
                : 0;
            if (width > bestSize)
            {
                bestSize = width;
                fallback = url;
            }
        }

        return fallback;
    }

    private static IEnumerable<string> GetImageSourceUrls(JsonElement image)
    {
        if (!TryGetNested(image, out var sources, "sources") || sources.ValueKind != JsonValueKind.Array)
        {
            return Enumerable.Empty<string>();
        }

        return sources.EnumerateArray()
            .Select(source => TryGetString(source, "url"))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!);
    }

    private static bool TryGetNested(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            if (!value.TryGetProperty(segment, out var next))
            {
                value = default;
                return false;
            }

            value = next;
        }
        return true;
    }

    private static bool TryGetNestedAny(JsonElement element, out JsonElement value, params string[][] paths)
    {
        foreach (var path in paths)
        {
            if (TryGetNested(element, out value, path))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        if (!TryGetNested(element, out var value, path))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ExtractIdFromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }
        if (uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 ? parts[2] : null;
        }
        return null;
    }

    private static string? HumanizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        var cleaned = key.Replace("_", " ").Replace("-", " ");
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}

public sealed record RecommendationDebugResult(
    List<SpotifyRecommendationSection> Sections,
    string? OperationName,
    string? VariablesJson,
    string? RawJson);
