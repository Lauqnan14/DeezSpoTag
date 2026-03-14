namespace DeezSpoTag.Web.Controllers.Api;

internal static class SearchApiControllerCommon
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "track",
        "album",
        "artist",
        "playlist"
    };

    public static string? ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Query is required.";
        }

        if (IsUrl(query))
        {
            return "Search expects text, not a URL.";
        }

        return null;
    }

    public static string? ValidateType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Type is required.";
        }

        var normalized = type.Trim().ToLowerInvariant();
        return AllowedTypes.Contains(normalized) ? null : "Invalid type.";
    }

    public static string NormalizeType(string type)
    {
        return type.Trim().ToLowerInvariant();
    }

    public static object BuildUnavailablePayload()
    {
        return new { available = false };
    }

    public static object BuildSearchPayload(DeezSpoTag.Web.Services.DeezSpoTagSearchResponse result)
    {
        return new
        {
            available = true,
            tracks = result.Tracks,
            albums = result.Albums,
            artists = result.Artists,
            playlists = result.Playlists,
            totals = result.Totals ?? new Dictionary<string, int>()
        };
    }

    public static object BuildTypedPayload(DeezSpoTag.Web.Services.DeezSpoTagSearchTypeResponse result)
    {
        return new
        {
            available = true,
            type = result.Type,
            items = result.Items,
            total = result.Total
        };
    }

    private static bool IsUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
