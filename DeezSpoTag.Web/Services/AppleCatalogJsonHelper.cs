using DeezSpoTag.Services.Apple;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

public static class AppleCatalogJsonHelper
{
    private const string AppleDisabledEnvironmentVariable = "DEEZSPOTAG_APPLE_DISABLED";
    private const string DataField = "data";
    private const string ArtworkField = "artwork";
    private const string UrlField = "url";
    private const string PreviewsField = "previews";
    private const string AudioTraitsField = "audioTraits";

    private static JsonElement BrowseProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;

    private static string BrowseString(JsonElement value, string name)
    {
        var child = BrowseProperty(value, name);
        return child.ValueKind == JsonValueKind.String ? child.GetString() ?? "" : "";
    }

    public static JsonArray MapBrowseSections(JsonElement root, bool categoriesOnly)
    {
        var output = new JsonArray();
        if (!TryGetDataArray(root, out var pages)) throw new JsonException("Missing Apple Music browse pages.");
        foreach (var page in pages.EnumerateArray())
        {
            var sections = BrowseProperty(BrowseProperty(page, "data"), "sections");
            if (sections.ValueKind != JsonValueKind.Array) continue;
            foreach (var section in sections.EnumerateArray())
            {
                var id = BrowseString(section, "id");
                var isCategories = id.StartsWith("browse-category-bricks", StringComparison.Ordinal);
                if (categoriesOnly && !isCategories) continue;
                var sourceItems = BrowseProperty(section, "items");
                if (sourceItems.ValueKind != JsonValueKind.Array) continue;
                var items = new JsonArray();
                foreach (var item in sourceItems.EnumerateArray())
                {
                    var mapped = MapBrowseItem(item, isCategories);
                    if (mapped != null) items.Add(mapped);
                }
                if (items.Count == 0) continue;
                var title = BrowseString(BrowseProperty(BrowseProperty(BrowseProperty(section, "header"), "item"), "titleLink"), "title");
                var metrics = BrowseProperty(BrowseProperty(section, "impressionMetrics"), "fields");
                var identity = BrowseString(metrics, "name");
                if (identity.Length == 0) identity = title;
                var kind = BrowseString(section, "itemKind");
                var role = kind switch
                {
                    "trackLockup" when identity == "Trending Songs" => "trending",
                    "squareLockup" when identity == "New This Week" => "new-this-week",
                    "squareLockup" when identity == "Recent Releases" => "recent-releases",
                    "squareLockup" when identity == "Updated Playlists" => "updated-playlists",
                    "verticalVideoLockup" when (identity is "Music Videos" or "New Music Videos") && items.All(i => i?["type"]?.GetValue<string>() == "video") => "videos",
                    _ => ""
                };
                var titleLink = BrowseProperty(BrowseProperty(BrowseProperty(section, "header"), "item"), "titleLink");
                var intent = BrowseProperty(BrowseProperty(BrowseProperty(titleLink, "segue"), "destination"), "intent");
                var pagePath = BrowseString(BrowseProperty(intent, "contentDescriptor"), "url");
                if (pagePath.Length == 0 && BrowseString(intent, "$kind") == "RoomPageIntent")
                    pagePath = "/room/" + BrowseString(intent, "id");
                output.Add(new JsonObject
                {
                    ["id"] = id, ["title"] = title, ["source"] = "apple",
                    ["role"] = role, ["items"] = items, ["pagePath"] = pagePath
                });
            }
        }
        return output;
    }

    private static JsonObject? MapBrowseItem(JsonElement item, bool category)
    {
        var descriptor = BrowseProperty(item, "contentDescriptor");
        var kind = BrowseString(descriptor, "kind");
        var type = kind switch { "song" => "track", "album" => "album", "playlist" => "playlist", "musicVideo" => "video", "appleCurator" or "editorialItem" or "room" or "grouping" or "topCharts" => "browse-category", _ => "" };
        if (type.Length == 0) return null;
        var destination = BrowseProperty(BrowseProperty(item, "segue"), "destination");
        var intent = BrowseProperty(destination, "intent");
        var canonical = BrowseString(BrowseProperty(intent, "contentDescriptor"), "url");
        if (canonical.Length == 0) canonical = BrowseString(BrowseProperty(destination, "contentDescriptor"), "url");
        var url = canonical.Length > 0 ? canonical : BrowseString(descriptor, "url");
        if (url.Length == 0 && BrowseString(intent, "$kind") == "RoomPageIntent") url = "/room/" + BrowseString(intent, "id");
        if (!url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host != "music.apple.com") return null;
        }
        else if (!url.StartsWith('/') || url.StartsWith("//")) return null;
        var id = BrowseString(BrowseProperty(descriptor, "identifiers"), "storeAdamID");
        if (id.Length == 0) id = BrowseString(intent, "id");
        var title = BrowseString(item, "title");
        var titleLinks = BrowseProperty(item, "titleLinks");
        if (title.Length == 0 && titleLinks.ValueKind == JsonValueKind.Array) title = BrowseString(titleLinks.EnumerateArray().FirstOrDefault(), "title");
        var subtitle = BrowseString(item, "artistName");
        var subtitles = BrowseProperty(item, "subtitleLinks");
        if (subtitle.Length == 0 && subtitles.ValueKind == JsonValueKind.Array) subtitle = string.Join(", ", subtitles.EnumerateArray().Select(link => BrowseString(link, "title")).Where(t => t.Length > 0));
        var artwork = BrowseProperty(item, "artwork");
        var dictionary = BrowseProperty(artwork, "dictionary");
        if (dictionary.ValueKind == JsonValueKind.Object) artwork = dictionary;
        var image = AppleArtworkRenderHelper.BuildArtworkUrl(BrowseString(artwork, "url"), BrowseProperty(artwork, "width").TryGetInt32Safe(), BrowseProperty(artwork, "height").TryGetInt32Safe());
        return new JsonObject { ["id"] = id, ["appleId"] = id, ["title"] = title, ["name"] = title, ["subtitle"] = subtitle, ["artist"] = subtitle, ["source"] = "apple", ["type"] = category ? "browse-category" : type, ["appleUrl"] = url, ["target"] = category || type == "browse-category" ? url : "", ["image"] = image, ["coverUrl"] = image, ["previewUrl"] = ReadPreviewUrl(item), ["albumName"] = BrowseString(item, "albumName"), ["durationSeconds"] = BrowseProperty(item, "durationInMillis").TryGetInt32Safe() / 1000 };
    }

    private static int TryGetInt32Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    public static bool IsAppleDisabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(AppleDisabledEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetDataArray(JsonElement root, out JsonElement dataArr)
    {
        if (root.TryGetProperty(DataField, out dataArr) && dataArr.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        dataArr = default;
        return false;
    }

    public static string ResolveArtwork(JsonElement attributes)
    {
        if (!attributes.TryGetProperty(ArtworkField, out var art) || art.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!art.TryGetProperty(UrlField, out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var raw = urlEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var width = art.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = art.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        return AppleArtworkRenderHelper.BuildArtworkUrl(raw, width, height);
    }

    public static string ReadPreviewUrl(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty(PreviewsField, out var previews)
            || previews.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var first = previews.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Object
            && first.TryGetProperty(UrlField, out var previewEl)
            && previewEl.ValueKind == JsonValueKind.String)
        {
            return previewEl.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    public static bool HasAtmos(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!attributes.TryGetProperty(AudioTraitsField, out var traits)
            || traits.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return traits.EnumerateArray().Any(static trait =>
            trait.ValueKind == JsonValueKind.String
            && trait.GetString()?.IndexOf("atmos", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static bool HasAppleDigitalMaster(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (attributes.TryGetProperty("isAppleDigitalMaster", out var admEl)
            && admEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return admEl.GetBoolean();
        }

        if (attributes.TryGetProperty("isMasteredForItunes", out var mfiEl)
            && mfiEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return mfiEl.GetBoolean();
        }

        return false;
    }

    public static List<string> ReadStringArray(JsonElement attributes, string propertyName)
    {
        if (attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty(propertyName, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return values.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool RootHasNext(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String)
        {
            return !string.IsNullOrWhiteSpace(nextEl.GetString());
        }

        return false;
    }
}
