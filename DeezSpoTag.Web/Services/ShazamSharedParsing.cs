namespace DeezSpoTag.Web.Services;

internal static class ShazamSharedParsing
{
    public static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            dp[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            dp[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[a.Length, b.Length];
    }

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
