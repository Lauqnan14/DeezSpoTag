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
        @"(?:\s*(?:\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b|&|,|;|/|\+)\s*|\s+\bx\b\s+)",
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
    /// Split a combined artist name into parts AND the separators between them,
    /// so a rewrite can replace individual parts while keeping the original
    /// joiners ("feat.", "&", "," …) intact. parts.Count == separators.Count + 1.
    /// </summary>
    public static List<string> SplitCombinedNameForRewrite(string? artistName, out List<string> separators)
    {
        separators = new List<string>();
        var credit = artistName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(credit))
        {
            return new List<string>();
        }

        var matches = CollaborationSplitRegex.Matches(credit);
        if (matches.Count == 0)
        {
            return new List<string> { credit.Trim() };
        }

        var parts = new List<string>();
        var lastIndex = 0;
        foreach (Match match in matches)
        {
            var part = credit[lastIndex..match.Index].Trim();
            if (part.Length > 0 || parts.Count > 0)
            {
                parts.Add(part);
            }

            separators.Add(match.Value);
            lastIndex = match.Index + match.Length;
        }

        var tail = credit[lastIndex..].Trim();
        if (tail.Length > 0)
        {
            parts.Add(tail);
        }

        // A separator with no following part (trailing "feat.") is dropped.
        while (separators.Count >= parts.Count)
        {
            separators.RemoveAt(separators.Count - 1);
        }

        return parts;
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
