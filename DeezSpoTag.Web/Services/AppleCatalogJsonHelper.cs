using DeezSpoTag.Services.Apple;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public static class AppleCatalogJsonHelper
{
    private const string DataField = "data";
    private const string ArtworkField = "artwork";
    private const string UrlField = "url";
    private const string PreviewsField = "previews";

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
