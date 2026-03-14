using System.Text.Json;
using System.Linq;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Queue;

public static class FinalDestinationTracker
{
    public static Dictionary<string, string> EnsureMap(Dictionary<string, string>? existing)
        => existing ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static void SeedIdentityEntries(
        Dictionary<string, string> map,
        string? filePath,
        IEnumerable<Dictionary<string, object>>? files)
    {
        RecordPathTransition(map, filePath, filePath);
        if (files == null)
        {
            return;
        }

        foreach (var path in files.Select(entry => ReadPath(entry, "path")))
        {
            RecordPathTransition(map, path, path);
        }
    }

    public static void RecordPathTransition(
        Dictionary<string, string> map,
        string? sourcePath,
        string? destinationPath)
    {
        var source = NormalizePath(sourcePath);
        var destination = NormalizePath(destinationPath);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        var matchedPair = map.FirstOrDefault(pair =>
            string.Equals(pair.Key, source, StringComparison.OrdinalIgnoreCase)
            || string.Equals(pair.Value, source, StringComparison.OrdinalIgnoreCase));
        var matchedKey = matchedPair.Key;

        if (!string.IsNullOrWhiteSpace(matchedKey))
        {
            map[matchedKey] = destination;
            return;
        }

        map[source] = destination;
    }

    public static string? Serialize(Dictionary<string, string>? map)
    {
        if (map == null || map.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map)
        {
            var key = NormalizePath(pair.Key);
            var value = NormalizePath(pair.Value);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            normalized[key] = value;
        }

        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
    }

    private static string? ReadPath(Dictionary<string, object> entry, string key)
    {
        if (!entry.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string str => str,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return DownloadPathResolver.NormalizeDisplayPath(path);
    }
}
