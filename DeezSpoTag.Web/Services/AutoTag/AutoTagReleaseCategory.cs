namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagReleaseCategory
{
    internal const string Album = "album";
    internal const string Single = "single";

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

        if (normalized.Contains("album", StringComparison.Ordinal)
            || normalized.Contains("ep", StringComparison.Ordinal)
            || normalized.Contains("compilation", StringComparison.Ordinal)
            || normalized.Contains("lp", StringComparison.Ordinal))
        {
            return Album;
        }

        return null;
    }
}
