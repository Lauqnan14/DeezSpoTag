namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagReleaseCategory
{
    internal const string Album = "album";
    internal const string Single = "single";
    internal const string Compilation = "compilation";

    public static string? Resolve(string? explicitReleaseType, int? trackTotal)
    {
        var normalized = Normalize(explicitReleaseType);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized == Compilation ? Album : normalized;
        }

        return trackTotal switch
        {
            1 => Single,
            > 1 => Album,
            _ => null
        };
    }

    public static bool MatchesPreference(string? explicitReleaseType, int? trackTotal, string? preference)
    {
        var preferred = Normalize(preference);
        if (preferred is not Album and not Single)
        {
            return true;
        }

        var explicitNormalized = Normalize(explicitReleaseType);
        if (explicitNormalized == Compilation)
        {
            return false;
        }

        return string.Equals(
            explicitNormalized ?? Resolve(null, trackTotal),
            preferred,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("single", StringComparison.Ordinal))
        {
            return Single;
        }

        if (normalized.Contains("compilation", StringComparison.Ordinal))
        {
            return Compilation;
        }

        if (normalized.Contains("album", StringComparison.Ordinal)
            || normalized.Contains("ep", StringComparison.Ordinal)
            || normalized.Contains("lp", StringComparison.Ordinal))
        {
            return Album;
        }

        return null;
    }
}
