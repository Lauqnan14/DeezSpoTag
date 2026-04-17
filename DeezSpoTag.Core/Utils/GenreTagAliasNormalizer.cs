using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Core.Utils;

public static class GenreTagAliasNormalizer
{
    private static readonly char[] CompositeGenreSeparators = ['/', '\\'];
    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> BuildAliasMap(IEnumerable<GenreTagAliasRule>? rules)
    {
        if (rules == null)
        {
            return EmptyMap;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule == null)
            {
                continue;
            }

            var canonical = rule.Canonical?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(canonical))
            {
                continue;
            }

            var key = ToLookupKey(rule.Alias);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            map[key] = canonical;
        }

        return map.Count == 0
            ? EmptyMap
            : map;
    }

    public static List<GenreTagAliasRule> NormalizeRules(IEnumerable<GenreTagAliasRule>? rules)
    {
        var normalized = new List<GenreTagAliasRule>();
        if (rules == null)
        {
            return normalized;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule == null)
            {
                continue;
            }

            var alias = rule.Alias?.Trim() ?? string.Empty;
            var canonical = rule.Canonical?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(canonical))
            {
                continue;
            }

            var key = ToLookupKey(alias);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            normalized.Add(new GenreTagAliasRule
            {
                Alias = alias,
                Canonical = canonical
            });
        }

        return normalized;
    }

    public static string NormalizeValue(string value, IReadOnlyDictionary<string, string>? aliasMap)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (TryNormalizeAliasMatch(trimmed, aliasMap, out var normalized))
        {
            return normalized;
        }

        return trimmed;
    }

    public static List<string> NormalizeAndExpandValues(
        IEnumerable<string>? values,
        IReadOnlyDictionary<string, string>? aliasMap,
        bool splitComposite)
    {
        var output = new List<string>();
        if (values == null)
        {
            return output;
        }

        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var trimmed = rawValue.Trim();
            if (TryNormalizeAliasMatch(trimmed, aliasMap, out var normalizedWholeValue))
            {
                output.Add(normalizedWholeValue);
                continue;
            }

            if (!splitComposite)
            {
                output.Add(trimmed);
                continue;
            }

            var tokens = trimmed.Split(CompositeGenreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var token in tokens)
            {
                var normalized = NormalizeValue(token, aliasMap);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    output.Add(normalized);
                }
            }
        }

        return output;
    }

    private static bool TryNormalizeAliasMatch(
        string value,
        IReadOnlyDictionary<string, string>? aliasMap,
        out string normalized)
    {
        normalized = value;
        if (aliasMap == null || aliasMap.Count == 0)
        {
            return false;
        }

        var key = ToLookupKey(value);
        if (key.Length == 0
            || !aliasMap.TryGetValue(key, out var canonical)
            || string.IsNullOrWhiteSpace(canonical))
        {
            return false;
        }

        normalized = canonical.Trim();
        return true;
    }

    public static string ToLookupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Trim()
            .Where(static character => char.IsLetterOrDigit(character))
            .Select(static character => char.ToLowerInvariant(character))
            .ToArray();
        return new string(normalized);
    }
}
