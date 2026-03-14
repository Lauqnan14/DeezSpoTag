namespace DeezSpoTag.Integrations.Deezer;

internal static class DeezerStringSimilarity
{
    public static int Ratio(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return 0;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return 100;
        }

        var maxLength = Math.Max(first.Length, second.Length);
        if (maxLength == 0)
        {
            return 100;
        }

        var distance = LevenshteinDistance(first, second);
        return (int)Math.Round(((double)(maxLength - distance) / maxLength) * 100d);
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= m; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}
