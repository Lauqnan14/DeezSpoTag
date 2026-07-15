namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagReleaseCategory
{
    internal const string Album = "album";
    internal const string Single = "single";
    internal const string Ep = "ep";
    internal const string Compilation = "compilation";

    public static string? Resolve(string? explicitReleaseType, int? trackTotal)
    {
        var normalized = Normalize(explicitReleaseType);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return trackTotal switch
        {
            1 => Single,
            > 1 => Album,
            _ => null
        };
    }

    public static string? Resolve(
        string? primaryReleaseType,
        IEnumerable<string>? additionalReleaseTypes,
        int? trackTotal)
    {
        var resolvedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddResolvedType(resolvedTypes, primaryReleaseType);
        if (additionalReleaseTypes != null)
        {
            foreach (var releaseType in additionalReleaseTypes)
            {
                AddResolvedType(resolvedTypes, releaseType);
            }
        }

        if (resolvedTypes.Contains(Compilation))
        {
            return Compilation;
        }

        if (resolvedTypes.Contains(Ep))
        {
            return Ep;
        }

        if (resolvedTypes.Contains(Single))
        {
            return Single;
        }

        if (resolvedTypes.Contains(Album))
        {
            return Album;
        }

        return Resolve(null, trackTotal);
    }

    public static bool MatchesPreference(string? explicitReleaseType, int? trackTotal, string? preference)
    {
        var preferred = Normalize(preference);
        if (preferred is not Album and not Single and not Ep and not Compilation)
        {
            return true;
        }

        return string.Equals(
            Resolve(explicitReleaseType, trackTotal),
            preferred,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AddResolvedType(HashSet<string> resolvedTypes, string? value)
    {
        var normalized = Normalize(value);
        if (normalized != null)
        {
            resolvedTypes.Add(normalized);
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("compilation", StringComparison.Ordinal))
        {
            return Compilation;
        }

        var tokens = normalized.Split(
            [' ', '-', '_', '/', '.', ',', ';', ':', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (normalized.Contains("extended play", StringComparison.Ordinal)
            || tokens.Contains(Ep, StringComparer.Ordinal))
        {
            return Ep;
        }

        if (normalized.Contains("single", StringComparison.Ordinal))
        {
            return Single;
        }

        if (normalized.Contains("album", StringComparison.Ordinal)
            || tokens.Contains("lp", StringComparer.Ordinal))
        {
            return Album;
        }

        return null;
    }
}
