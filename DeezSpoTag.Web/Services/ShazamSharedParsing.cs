namespace DeezSpoTag.Web.Services;

internal static class ShazamSharedParsing
{
    // Levenshtein lives in DeezSpoTag.Core.Utils.TextMatchUtils. A second copy used to sit
    // here; it was verified identical over 300k random pairs and removed so there is one.

    public static bool? ParseExplicitFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized.Contains("not explicit", StringComparison.Ordinal) ||
            normalized.Contains("clean", StringComparison.Ordinal) ||
            normalized is "none" or "false" or "no" or "0")
        {
            return false;
        }

        if (normalized.Contains("explicit", StringComparison.Ordinal) ||
            normalized is "true" or "yes" or "1")
        {
            return true;
        }

        return bool.TryParse(normalized, out var parsed) ? parsed : null;
    }
}
