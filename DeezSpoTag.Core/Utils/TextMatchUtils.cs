namespace DeezSpoTag.Core.Utils;

public static class TextMatchUtils
{
    public static bool ContainsWholeMarker(string text, string marker)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        var paddedText = $" {text} ";
        var paddedMarker = $" {marker} ";
        return paddedText.Contains(paddedMarker, StringComparison.Ordinal);
    }

    public static double ComputeNormalizedSimilarity(string source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0d;
        }

        if (string.Equals(source, candidate, StringComparison.Ordinal))
        {
            return 1d;
        }

        var distance = LevenshteinDistance(source, candidate);
        var maxLen = Math.Max(source.Length, candidate.Length);
        return maxLen == 0 ? 0d : 1d - (double)distance / maxLen;
    }

    public static int LevenshteinDistance(string s1, string s2)
    {
        if (s1.Length == 0)
        {
            return s2.Length;
        }

        if (s2.Length == 0)
        {
            return s1.Length;
        }

        var rows = s1.Length + 1;
        var cols = s2.Length + 1;
        var matrix = new int[rows, cols];

        for (var i = 0; i < rows; i++)
        {
            matrix[i, 0] = i;
        }

        for (var j = 0; j < cols; j++)
        {
            matrix[0, j] = j;
        }

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[rows - 1, cols - 1];
    }
}
