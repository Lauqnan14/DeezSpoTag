namespace DeezSpoTag.Web.Services;

public static class SpotifyTextNormalizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", "-").Replace("—", "-");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\(.*?\)",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.None,
            RegexTimeout);
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"[^a-z0-9]+",
            " ",
            System.Text.RegularExpressions.RegexOptions.None,
            RegexTimeout).Trim();
        return normalized;
    }

    public static string NormalizeTrackToken(string? value)
    {
        var normalized = NormalizeToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\b(feat|ft)\b.*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.None,
            RegexTimeout).Trim();
        return normalized;
    }

    public static double ComputeCoverageScore(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0;
        }

        var rightSet = new HashSet<string>(rightTokens, StringComparer.OrdinalIgnoreCase);
        var overlap = leftTokens.Count(token => rightSet.Contains(token));
        return overlap / (double)Math.Max(leftTokens.Length, rightTokens.Length);
    }
}
