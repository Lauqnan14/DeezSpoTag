using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

internal static class AutoTagPlatformTagContract
{
    public const string TagsKey = "tags";
    public const string GapFillTagsKey = "gapFillTags";

    public static List<string> ResolveRequestedTags(JsonObject root)
    {
        var tags = ReadStringList(root, TagsKey);
        if (tags.Count > 0)
        {
            return tags;
        }

        return ReadStringList(root, GapFillTagsKey);
    }

    public static List<string> FilterOfferedTags(
        IEnumerable<string> requested,
        IEnumerable<string> platforms,
        IReadOnlyDictionary<string, HashSet<string>> platformSupportedTags,
        Func<string?, string?> normalize)
    {
        var requestSet = requested
            .Select(normalize)
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestSet.Count == 0)
        {
            return new List<string>();
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var platformId in platforms)
        {
            if (string.IsNullOrWhiteSpace(platformId))
            {
                continue;
            }

            if (platformSupportedTags.TryGetValue(platformId.Trim(), out var supported))
            {
                allowed.UnionWith(supported);
            }
        }

        if (allowed.Count == 0)
        {
            return new List<string>();
        }

        return requestSet.Where(allowed.Contains).ToList();
    }

    public static Dictionary<string, HashSet<string>> ToSupportedTagMap<T>(
        IReadOnlyDictionary<string, T> platformCaps,
        Func<T, IEnumerable<string>> supportedTags)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (platform, caps) in platformCaps)
        {
            map[platform] = supportedTags(caps).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    private static List<string> ReadStringList(JsonObject root, string key)
    {
        if (root[key] is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(static node => node?.GetValue<string>()?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
