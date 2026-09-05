using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Utils;

/// <summary>
/// Edition-aware album title helpers. Navidrome groups files into albums by their
/// album identity tags, so files of the same album must agree on album title,
/// album artist and album id — while a standard album and a deluxe edition must
/// stay separate. These helpers extract the <see cref="CoreTitle"/> (the album name
/// without edition markers) and the <see cref="EditionIntent"/> (which edition the
/// title claims to be) so matching, consensus and preservation can be edition-aware.
/// </summary>
public static class AlbumTitleNormalizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Edition markers recognized in album titles ("Album (Deluxe Edition)").</summary>
    private static readonly string[] EditionMarkers =
    {
        "deluxe", "expanded", "anniversary", "remaster", "collector", "special edition",
        "limited edition", "bonus track", "bonus disc", "super deluxe", "deluxe edition",
        "expanded edition", "edition"
    };

    private static readonly string[] RecognizedEditions =
    {
        "deluxe", "expanded", "anniversary", "remaster", "collector", "special edition",
        "limited edition", "bonus"
    };

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return normalized;
    }

    /// <summary>
    /// The album title with edition sections removed: only parenthesized/bracketed
    /// segments that carry an edition marker are stripped ("(Deluxe Edition)",
    /// "[2011 Remaster]"), while non-edition sections ("(Live)", "(Live from
    /// Nowhere)") stay part of the core because they are genuinely different albums.
    /// </summary>
    public static string CoreTitle(string? albumTitle)
    {
        var normalized = NormalizeText(albumTitle);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var core = Regex.Replace(
            normalized,
            @"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}",
            match => ContainsEditionMarker(match.Value) ? " " : match.Value,
            RegexOptions.None,
            RegexTimeout);

        var dashIndex = core.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            var tail = core[(dashIndex + 3)..];
            if (ContainsEditionMarker(tail))
            {
                core = core[..dashIndex];
            }
        }

        core = Regex.Replace(core, @"[^\p{L}\p{Nd}]+", " ", RegexOptions.None, RegexTimeout);
        return Regex.Replace(core, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    /// <summary>
    /// The set of edition markers a title claims ("deluxe", "remaster", …). Empty for
    /// a plain album title. Two titles with the same core but different edition sets
    /// are different releases of the same album.
    /// </summary>
    public static HashSet<string> EditionIntent(string? albumTitle)
    {
        var normalized = NormalizeText(albumTitle);
        var intent = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return intent;
        }

        foreach (var marker in RecognizedEditions)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
            {
                intent.Add(marker);
            }
        }

        return intent;
    }

    /// <summary>True when both titles describe the same album (core) and the same edition intent.</summary>
    public static bool IsSameEdition(string? left, string? right)
    {
        var leftCore = CoreTitle(left);
        var rightCore = CoreTitle(right);
        if (leftCore.Length == 0 || rightCore.Length == 0)
        {
            return false;
        }

        if (!string.Equals(leftCore, rightCore, StringComparison.Ordinal))
        {
            return false;
        }

        return EditionIntent(left).SetEquals(EditionIntent(right));
    }

    /// <summary>
    /// True when the two titles describe the same album core but different editions
    /// (standard vs deluxe, 2009 vs 2011 remaster) — a conflict that must never be
    /// silently resolved by rewriting the album identity.
    /// </summary>
    public static bool IsEditionConflict(string? sourceTitle, string? candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(sourceTitle) || string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        var sourceCore = CoreTitle(sourceTitle);
        var candidateCore = CoreTitle(candidateTitle);
        if (sourceCore.Length == 0 || !string.Equals(sourceCore, candidateCore, StringComparison.Ordinal))
        {
            // Different album names entirely — not an edition conflict of one album.
            return false;
        }

        return !EditionIntent(sourceTitle).SetEquals(EditionIntent(candidateTitle));
    }

    private static bool ContainsEditionMarker(string value)
    {
        var normalized = NormalizeText(value);
        foreach (var marker in EditionMarkers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
