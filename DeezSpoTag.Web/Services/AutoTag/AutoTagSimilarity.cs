namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagSimilarity
{
    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        return string.Join(
            " ",
            new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static double ComputeScore(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0d;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        var distance = ShazamSharedParsing.LevenshteinDistance(left, right);
        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength <= 0)
        {
            return 1d;
        }

        return Math.Clamp(1d - (distance / (double)maxLength), 0d, 1d);
    }
}
