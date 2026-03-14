using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Utils;

public static class TrackTitleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string[] RemovableVersionMarkers =
    {
        "remaster", "remastered", "radio edit", "single version", "album version",
        "original mix", "edit", "mono", "stereo", "clean", "explicit"
    };

    private static readonly string[] ToxicVariantMarkers =
    {
        "cover", "karaoke", "parody", "tribute"
    };

    private static readonly string[] StrictVariantMarkers =
    {
        "instrumental", "live", "acoustic", "remix", "demo", "sped up", "slowed", "nightcore"
    };

    public static bool TitlesMatch(string? expected, string? actual)
    {
        var expectedSignature = BuildSignature(expected);
        var actualSignature = BuildSignature(actual);

        if (string.IsNullOrWhiteSpace(expectedSignature.BaseTitle) || string.IsNullOrWhiteSpace(actualSignature.BaseTitle))
        {
            return false;
        }

        if (actualSignature.ToxicVariants.Count > 0 && expectedSignature.ToxicVariants.Count == 0)
        {
            return false;
        }

        if (expectedSignature.ToxicVariants.Count != actualSignature.ToxicVariants.Count
            || !expectedSignature.ToxicVariants.SetEquals(actualSignature.ToxicVariants))
        {
            return false;
        }

        if (expectedSignature.StrictVariants.Count != actualSignature.StrictVariants.Count
            || !expectedSignature.StrictVariants.SetEquals(actualSignature.StrictVariants))
        {
            return false;
        }

        return expectedSignature.BaseTitle == actualSignature.BaseTitle
            || expectedSignature.BaseTitle.Contains(actualSignature.BaseTitle, StringComparison.Ordinal)
            || actualSignature.BaseTitle.Contains(expectedSignature.BaseTitle, StringComparison.Ordinal);
    }

    public static bool ArtistsMatch(string? expected, string? actual)
    {
        var expectedArtists = ExpandArtists(expected);
        var actualArtists = ExpandArtists(actual);
        if (expectedArtists.Count == 0 || actualArtists.Count == 0)
        {
            return false;
        }

        return expectedArtists.Any(exp => actualArtists.Any(act =>
            exp == act
            || exp.Contains(act, StringComparison.Ordinal)
            || act.Contains(exp, StringComparison.Ordinal)));
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
        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();

        return new TrackTitleSignature(cleaned, toxicVariants, strictVariants);
    }

    private static HashSet<string> ExpandArtists(string? artists)
    {
        return ArtistNameNormalizer
            .ExpandArtistNames(new[] { NormalizeText(artists) })
            .Select(NormalizeText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractVariants(string title, IEnumerable<string> markers)
    {
        return markers
            .Where(marker => title.Contains(marker, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
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
        HashSet<string> ToxicVariants,
        HashSet<string> StrictVariants)
    {
        public static TrackTitleSignature Empty { get; } = new(string.Empty, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
    }
}
