using System.Text;

namespace DeezSpoTag.Web.Services.CoverPort;

internal static class CoverTextNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(ch);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                buffer.Append(' ');
                previousWasSpace = true;
            }
        }

        return buffer.ToString().Trim();
    }

    public static double ComputeMatchConfidence(CoverSearchQuery query, string? artist, string? album)
    {
        var queryArtist = Normalize(query.Artist);
        var queryAlbum = Normalize(query.Album);
        var candidateArtist = Normalize(artist);
        var candidateAlbum = Normalize(album);

        var artistScore = ComputeFieldScore(queryArtist, candidateArtist);
        var albumScore = ComputeFieldScore(queryAlbum, candidateAlbum);
        return (artistScore + albumScore) / 2d;
    }

    private static double ComputeFieldScore(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate))
        {
            return 0d;
        }

        if (string.Equals(query, candidate, StringComparison.Ordinal))
        {
            return 1d;
        }

        if (candidate.Contains(query, StringComparison.Ordinal) || query.Contains(candidate, StringComparison.Ordinal))
        {
            return 0.8d;
        }

        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (queryTokens.Length == 0 || candidateTokens.Length == 0)
        {
            return 0d;
        }

        var overlaps = queryTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
        return overlaps <= 0 ? 0d : (double)overlaps / Math.Max(queryTokens.Length, candidateTokens.Length);
    }
}
