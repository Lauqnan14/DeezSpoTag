using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Utils;

/// <summary>
/// Splits combined artist names (e.g. "Ayra Starr & Wizkid") into primary + additional artists.
/// Used by the Deezer download pipeline to enforce single main artist per track/album.
/// </summary>
public static class ArtistNameNormalizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex CollaborationSplitRegex = new(
        @"\s*(?:\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b|\bx\b|&|,|;|/|\+)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// Split a combined artist name into primary and additional artist names.
    /// Tries " &amp; " first, then ", " if no ampersand found.
    /// </summary>
    public static (string Primary, List<string> Additional) SplitCombinedName(string? artistName)
    {
        var expanded = ExpandArtistNames(new[] { artistName ?? string.Empty });
        if (expanded.Count == 0)
        {
            return (artistName ?? string.Empty, new List<string>());
        }

        return (expanded[0], expanded.Skip(1).ToList());
    }

    /// <summary>
    /// Extract the first/main artist from a potentially combined artist credit.
    /// </summary>
    public static string ExtractPrimaryArtist(string? artistName)
    {
        var (primary, _) = SplitCombinedName(artistName);
        return string.IsNullOrWhiteSpace(primary)
            ? (artistName ?? string.Empty).Trim()
            : primary.Trim();
    }

    /// <summary>
    /// Expand artist credits into a unique list of normalized artist names.
    /// </summary>
    public static List<string> ExpandArtistNames(IEnumerable<string> credits)
    {
        var results = new List<string>();
        foreach (var credit in credits)
        {
            if (string.IsNullOrWhiteSpace(credit))
            {
                continue;
            }

            var parts = CollaborationSplitRegex.Split(credit);
            foreach (var normalized in parts
                         .Select(static part => part?.Trim())
                         .Where(static normalized => !string.IsNullOrWhiteSpace(normalized))
                         .Where(normalized => !results.Contains(normalized, StringComparer.OrdinalIgnoreCase)))
            {
                results.Add(normalized!);
            }
        }

        return results;
    }

    /// <summary>
    /// Check if an artist name appears to be a combined/collaboration name.
    /// </summary>
    public static bool IsCombinedName(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return false;

        return ExpandArtistNames(new[] { artistName }).Count > 1;
    }
}
