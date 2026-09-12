using System;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Utils;

/// <summary>
/// Single definition of how a title credits featured artists, so that adding a credit and
/// removing one can never disagree.
///
/// Previously <c>Track.GetFeatTitle</c> only looked for the literal "feat." while the removal
/// logic stripped "feat", "ft" and "featuring", so a title that already said "(ft. X)" had the
/// credit appended a second time. Detection and removal now share the same patterns.
///
/// "with" is deliberately excluded: it is common in ordinary titles and is not treated as a
/// featured credit elsewhere in the codebase.
/// </summary>
public static class FeaturedTitleMarker
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Patterns that remove a featured-artist credit. Bracketed forms first, then a bare inline
    /// credit. These are the same patterns used to detect a credit, so anything detected can be
    /// removed and vice versa.
    /// </summary>
    private static readonly string[] CreditPatterns =
    {
        @"\s*\((feat|ft|featuring)\.?\s+.*?\)",
        @"\s*\[(feat|ft|featuring)\.?\s+.*?\]",
        @"\s*(feat|ft|featuring)\.?\s+.*$"
    };

    private static readonly Regex CreditRegex = new(
        @"(?s)\s*\((feat|ft|featuring)\.?\s+.*?\)|\s*\[(feat|ft|featuring)\.?\s+.*?\]|\s*(feat|ft|featuring)\.?\s+.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// True when the title already credits a featured artist in any accepted spelling
    /// ("feat.", "ft.", "featuring", bracketed or inline).
    /// </summary>
    public static bool HasCredit(string? title)
        => !string.IsNullOrWhiteSpace(title) && CreditRegex.IsMatch(title);

    /// <summary>
    /// Removes a featured-artist credit from a title. Returns the trimmed remainder.
    /// </summary>
    public static string StripCredit(string? title)
    {
        var result = title ?? string.Empty;
        if (result.Length == 0)
        {
            return result;
        }

        foreach (var pattern in CreditPatterns)
        {
            result = Regex.Replace(
                result,
                pattern,
                string.Empty,
                RegexOptions.IgnoreCase,
                RegexTimeout);
        }

        return result.Trim();
    }

    /// <summary>
    /// The canonical spelling the app writes when it adds a credit, matching
    /// <c>Track.FeatArtistsString</c> and therefore the artist tag.
    /// </summary>
    private const string CanonicalKeyword = "feat.";

    /// <summary>
    /// Single regex covering every accepted spelling, capturing the credited names and the whole
    /// credit so it can be rewritten in canonical form. The bare form stops at a trailing
    /// qualifier such as " - Remastered" so the qualifier is not swallowed into the credit.
    /// </summary>
    private static readonly Regex NormalizeRegex = new(
        @"(?<m1>\(\s*(?:feat|ft|featuring)\.?\s+(?<n1>[^)]*?)\s*\))|(?<m2>\[\s*(?:feat|ft|featuring)\.?\s+(?<n2>[^\]]*?)\s*\])|(?<m3>\b(?:feat|ft|featuring)\.?\s+(?<n3>.+?)(?=\s+[-–—]\s+|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// Rewrites an existing featured credit into the canonical "(feat. X)" form, so a title the
    /// app touched looks the same whether it added the credit or found one already there.
    ///
    /// The credited names are preserved verbatim; only the keyword and the brackets change.
    /// Titles with no credit, and titles with no credited names, are returned unchanged.
    /// </summary>
    public static string NormalizeCredit(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !HasCredit(title))
        {
            return title ?? string.Empty;
        }

        return NormalizeRegex.Replace(title, match =>
        {
            var creditedNames = match.Groups["n1"].Success ? match.Groups["n1"].Value
                : match.Groups["n2"].Success ? match.Groups["n2"].Value
                : match.Groups["n3"].Value;

            if (string.IsNullOrWhiteSpace(creditedNames))
            {
                return match.Value;
            }

            return $"({CanonicalKeyword} {creditedNames.Trim()})";
        });
    }
}
