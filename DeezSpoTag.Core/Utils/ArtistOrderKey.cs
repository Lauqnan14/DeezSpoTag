using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Utils;

/// <summary>
/// Ordering key for enhancement runs: files are processed alphabetically by their
/// main artist — the alphabetically-first main artist of the track ("21 Savage,
/// Drake" sorts under "21 Savage"), with albums and track numbers as tie-breakers.
/// Ordinal comparison puts numbers ("21 Savage") before letters, as intended.
/// </summary>
public static class ArtistOrderKey
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex SplitSeparatorsRegex = new(
        @"\s*(?:;|/|,|&|\bx\b|\bvs\.?\b|\bwith\b|\bfeat\.?\b|\bft\.?\b)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    public static string NormalizeArtistName(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        var normalized = artist.Trim().ToLowerInvariant();
        normalized = normalized.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character);
        }

        var folded = builder.ToString().Normalize(NormalizationForm.FormC);
        return Regex.Replace(folded, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    /// <summary>
    /// Expands a raw artist credit into individual artist names using the common
    /// separators (",", "&amp;", ";", "/", "feat.", "with", "vs.").
    /// </summary>
    public static IReadOnlyList<string> ExpandArtists(string? artistCredit)
    {
        if (string.IsNullOrWhiteSpace(artistCredit))
        {
            return Array.Empty<string>();
        }

        return SplitSeparatorsRegex
            .Split(artistCredit)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    /// <summary>
    /// The alphabetical artist an enhancement run orders this file under: the
    /// alphabetically-first main artist of the track. Multi-artist credits sort
    /// under their first artist ("21 Savage, Drake" → "21 Savage").
    /// </summary>
    public static string ResolveMainArtistKey(IEnumerable<string>? artists, string? artistFallback)
    {
        var candidates = new List<string>();
        foreach (var artist in artists ?? Array.Empty<string>())
        {
            candidates.AddRange(ExpandArtists(artist));
        }

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(artistFallback))
        {
            candidates.AddRange(ExpandArtists(artistFallback));
        }

        string? best = null;
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeArtistName(candidate);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (best is null || string.Compare(normalized, best, StringComparison.Ordinal) < 0)
            {
                best = normalized;
            }
        }

        return best ?? string.Empty;
    }
}
