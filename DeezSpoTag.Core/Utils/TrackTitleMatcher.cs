using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Utils;

public static class TrackTitleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string[] RemovableVersionMarkers =
    {
        "remaster", "remastered", "radio edit", "single version", "album version",
        "original mix", "edit", "mono", "stereo", "clean", "explicit",
        "dolby atmos version", "atmos version"
    };

    private static readonly string[] ToxicVariantMarkers =
    {
        "cover", "karaoke", "parody", "tribute"
    };

    private static readonly string[] StrictVariantMarkers =
    {
        "instrumental", "live", "acoustic", "remix", "demo", "sped up", "slowed", "nightcore",
        "acapella", "a cappella", "made famous by", "made popular by", "as made famous by"
    };

    public static bool HasVersionDrift(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        var expectedSignature = BuildSignature(expected);
        var actualSignature = BuildSignature(actual);
        return HasIncompatibleVariants(expectedSignature, actualSignature);
    }

    public static bool TitlesMatch(string? expected, string? actual)
    {
        if (HasVersionDrift(expected, actual))
        {
            return false;
        }

        var expectedSignature = BuildSignature(expected);
        var actualSignature = BuildSignature(actual);

        if (string.IsNullOrWhiteSpace(expectedSignature.BaseTitle) || string.IsNullOrWhiteSpace(actualSignature.BaseTitle))
        {
            return false;
        }

        return expectedSignature.BaseTitle == actualSignature.BaseTitle
            || (!string.IsNullOrWhiteSpace(expectedSignature.CompactTitle)
                && expectedSignature.CompactTitle == actualSignature.CompactTitle)
            || HasSafeContainmentMatch(expectedSignature.BaseTitle, actualSignature.BaseTitle);
    }

    /// <summary>
    /// True when a candidate title is the same work as the source title.
    /// Punctuation, edition markers, and featured-artist containment are allowed.
    /// Near-miss alternative titles from the same artist (Close vs Closer) are not.
    /// Weak or empty source titles are treated as compatible so they can be filled in.
    /// </summary>
    public static bool HasCompatibleTitleIdentity(string? sourceTitle, string? candidateTitle)
    {
        if (TrackIdentityTrust.IsWeakMetadataValue(sourceTitle))
        {
            return true;
        }

        return TitlesMatch(sourceTitle, candidateTitle);
    }

    public static bool ArtistsMatch(string? expected, string? actual)
    {
        var expectedArtists = ExpandComparableArtists(expected);
        var actualArtists = ExpandComparableArtists(actual);
        if (expectedArtists.Count == 0 || actualArtists.Count == 0)
        {
            return false;
        }

        return expectedArtists.Any(exp => actualArtists.Any(act =>
            exp == act
            || exp.Contains(act, StringComparison.Ordinal)
            || act.Contains(exp, StringComparison.Ordinal)));
    }

    public static bool StrictArtistsMatch(string? expected, string? actual)
    {
        var expectedArtists = ExpandComparableArtists(expected);
        var actualArtists = ExpandComparableArtists(actual);
        if (expectedArtists.Count == 0 || actualArtists.Count == 0)
        {
            return false;
        }

        return expectedArtists.Any(expectedArtist => actualArtists.Contains(expectedArtist, StringComparer.Ordinal));
    }

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

    public static string RemoveAtmosVersionMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(
            value.Trim(),
            @"\s*[\(\[]\s*(?:dolby\s+)?atmos(?:\s+version)?\s*[\)\]]\s*$",
            string.Empty,
            RegexOptions.IgnoreCase,
            RegexTimeout).Trim();
    }

    private static TrackTitleSignature BuildSignature(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return TrackTitleSignature.Empty;
        }

        var toxicVariants = ExtractVariants(normalized, ToxicVariantMarkers);
        var strictVariants = ExtractVariants(normalized, StrictVariantMarkers);
        var cleaned = RemoveTrailingVersionSection(normalized, '(', ')');
        cleaned = RemoveTrailingVersionSection(cleaned, '[', ']');
        cleaned = Regex.Replace(
            cleaned,
            @"\s+-\s+(remaster(?:ed)?|radio edit|single version|album version|original mix|edit|mono|stereo|clean|explicit)$",
            string.Empty,
            RegexOptions.IgnoreCase,
            RegexTimeout);
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{Nd}]+", " ", RegexOptions.None, RegexTimeout);
        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
        var compact = Regex.Replace(cleaned, @"\s+", string.Empty, RegexOptions.None, RegexTimeout);

        return new TrackTitleSignature(cleaned, compact, toxicVariants, strictVariants);
    }

    private static HashSet<string> ExpandComparableArtists(string? artists)
    {
        return ArtistNameNormalizer
            .ExpandArtistNames(new[] { NormalizeText(artists) })
            .Select(NormalizeComparableIdentity)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeComparableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return Regex.Replace(
            builder.ToString().Normalize(NormalizationForm.FormC),
            @"\s+",
            " ",
            RegexOptions.None,
            RegexTimeout).Trim();
    }

    private static bool HasIncompatibleVariants(TrackTitleSignature expected, TrackTitleSignature actual)
    {
        if (actual.ToxicVariants.Count > 0 && expected.ToxicVariants.Count == 0)
        {
            return true;
        }

        if (expected.ToxicVariants.Count != actual.ToxicVariants.Count
            || !expected.ToxicVariants.SetEquals(actual.ToxicVariants))
        {
            return true;
        }

        return expected.StrictVariants.Count != actual.StrictVariants.Count
            || !expected.StrictVariants.SetEquals(actual.StrictVariants);
    }

    private static HashSet<string> ExtractVariants(string title, IEnumerable<string> markers)
    {
        return markers
            .Where(marker => title.Contains(marker, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool HasSafeContainmentMatch(string expectedTitle, string actualTitle)
    {
        var shorter = expectedTitle.Length <= actualTitle.Length ? expectedTitle : actualTitle;
        var longer = expectedTitle.Length > actualTitle.Length ? expectedTitle : actualTitle;
        if (shorter.Length < 4)
        {
            return false;
        }

        // Require the shorter title as a whole-word phrase so "close" does not match "closer".
        var pattern = $@"\b{Regex.Escape(shorter)}\b";
        return Regex.IsMatch(longer, pattern, RegexOptions.None, RegexTimeout);
    }

    private static string RemoveTrailingVersionSection(string value, char startChar, char endChar)
    {
        var cleaned = value;
        while (true)
        {
            var startIndex = cleaned.LastIndexOf(startChar);
            var endIndex = cleaned.LastIndexOf(endChar);
            if (startIndex < 0 || endIndex <= startIndex)
            {
                return cleaned.Trim();
            }

            var content = cleaned[(startIndex + 1)..endIndex].ToLowerInvariant();
            if (!RemovableVersionMarkers.Any(marker => content.Contains(marker, StringComparison.Ordinal)))
            {
                return cleaned.Trim();
            }

            cleaned = (cleaned[..startIndex] + cleaned[(endIndex + 1)..]).Trim();
        }
    }

    private sealed record TrackTitleSignature(
        string BaseTitle,
        string CompactTitle,
        HashSet<string> ToxicVariants,
        HashSet<string> StrictVariants)
    {
        public static TrackTitleSignature Empty { get; } = new(
            string.Empty,
            string.Empty,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }
}
