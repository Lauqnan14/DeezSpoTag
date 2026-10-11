using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Core.Utils;

public static class GenreTagAliasNormalizer
{
    private static readonly char[] CompositeGenreSeparators = ['/', '\\', ',', ';'];
    public static readonly string[] DefaultBlockedGenres = ["other", "others", "Worldwide"];
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

    public static List<string> NormalizeBlockedValues(IEnumerable<string>? values)
    {
        return DedupeValues(values ?? DefaultBlockedGenres);
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
        bool splitComposite,
        Func<string, bool>? isAtomic = null)
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

            output.AddRange(ExpandOne(rawValue.Trim(), aliasMap, splitComposite, isAtomic, blocked: null));
        }

        return output;
    }

    public static List<string> NormalizeExpandFilterAndDedupeValues(
        IEnumerable<string>? values,
        IReadOnlyDictionary<string, string>? aliasMap,
        bool splitComposite,
        IEnumerable<string>? blockedValues = null,
        Func<string, bool>? isAtomic = null)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blocked = BuildLookupKeySet(blockedValues);

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

            foreach (var expanded in ExpandOne(rawValue.Trim(), aliasMap, splitComposite, isAtomic, blocked))
            {
                var key = ToLookupKey(expanded);
                if (key.Length == 0 || blocked.Contains(key) || !seen.Add(key))
                {
                    continue;
                }

                output.Add(expanded.Trim());
            }
        }

        return output;
    }

    /// <summary>
    /// Applies an alias and, when the value is not one recognized term, splits it.
    /// Tokens are separated before the alias runs. A blocked source key therefore
    /// cannot be renamed into a different term, and a recognized term that contains
    /// a slash or a comma is never torn apart.
    /// </summary>
    private static List<string> ExpandOne(
        string trimmed,
        IReadOnlyDictionary<string, string>? aliasMap,
        bool splitComposite,
        Func<string, bool>? isAtomic,
        HashSet<string>? blocked,
        HashSet<string>? expanding = null,
        bool honorWholeAlias = true)
    {
        expanding ??= new HashSet<string>(StringComparer.Ordinal);
        var tokens = RawTokens(trimmed, aliasMap, splitComposite, isAtomic, honorWholeAlias);
        var output = new List<string>();
        foreach (var token in tokens)
        {
            var sourceKey = ToLookupKey(token);
            if (sourceKey.Length == 0 || blocked?.Contains(sourceKey) == true)
            {
                continue;
            }

            // A token already being expanded must not follow its alias again.
            // Rap → "Rap, HipHop" would otherwise split back into Rap and recurse.
            if (!expanding.Add(sourceKey))
            {
                output.Add(token);
                continue;
            }

            try
            {
                var aliased = NormalizeValue(token, aliasMap);
                if (string.IsNullOrWhiteSpace(aliased))
                {
                    continue;
                }

                if (splitComposite
                    && !string.Equals(aliased, token, StringComparison.Ordinal)
                    && isAtomic?.Invoke(aliased) != true
                    && aliased.IndexOfAny(CompositeGenreSeparators) >= 0)
                {
                    output.AddRange(ExpandOne(aliased, aliasMap, splitComposite, isAtomic, blocked, expanding, honorWholeAlias: false));
                    continue;
                }

                output.Add(aliased);
            }
            finally
            {
                expanding.Remove(sourceKey);
            }
        }

        return output;
    }

    private static List<string> RawTokens(
        string trimmed,
        IReadOnlyDictionary<string, string>? aliasMap,
        bool splitComposite,
        Func<string, bool>? isAtomic,
        bool honorWholeAlias)
    {
        // A saved alias for the complete value wins before any separator split.
        // An alias target is expanded with this switch off, so "Rap, HipHop"
        // still splits even when its letters match the source alias key.
        var key = ToLookupKey(trimmed);
        if (!splitComposite
            || isAtomic?.Invoke(trimmed) == true
            || (honorWholeAlias && key.Length > 0 && aliasMap?.ContainsKey(key) == true))
        {
            return [trimmed];
        }

        return GenreValueTokenizer.Tokenize(trimmed, extension: null, normalizationEnabled: true, isAtomic);
    }

    public static List<string> DedupeValues(
        IEnumerable<string>? values,
        IEnumerable<string>? blockedValues = null)
    {
        return NormalizeExpandFilterAndDedupeValues(
            values,
            aliasMap: null,
            splitComposite: false,
            blockedValues);
    }

    private static HashSet<string> BuildLookupKeySet(IEnumerable<string>? values)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        if (values == null)
        {
            return output;
        }

        foreach (var key in values
            .Select(ToLookupKey)
            .Where(key => key.Length > 0))
        {
            output.Add(key);
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
