using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Shared text normalization for artist-identity matching across platforms.
/// Ported from the Spotify matcher's normalization rules (SpotifyArtistService)
/// so every platform matcher compares titles and artist names with the same rules.
/// </summary>
public static partial class ArtistIdentityTextNormalizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string[] AliasSuffixes =
    [
        " tell em",
        " tell'em",
        " tell em official"
    ];

    private static readonly string[] CompilationAlbumMarkers =
    [
        "greatest hits",
        "best of",
        "anthology",
        "collection",
        "essentials",
        "now that's what i call",
        "top hits",
        "compilation",
        "various artists"
    ];

    [GeneratedRegex(@"\(.*?\)|\[.*?]|\{.*?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroupPattern();

    [GeneratedRegex(@"\bfeat\.?\b|\bft\.?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeatPattern();

    [GeneratedRegex(@"\s+\b(?:feat|ft|featuring)\b\.?\s*.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFeatPattern();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumericPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    /// <summary>
    /// Normalizes an album title: lowercase, dashes unified, bracketed groups and
    /// featuring markers stripped, non-alphanumerics collapsed to single spaces.
    /// Ported from SpotifyArtistService.NormalizeAlbumTitle.
    /// </summary>
    public static string NormalizeAlbumTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", "-").Replace("—", "-");
        normalized = BracketGroupPattern().Replace(normalized, string.Empty);
        normalized = FeatPattern().Replace(normalized, string.Empty);
        normalized = NonAlphanumericPattern().Replace(normalized, " ").Trim();
        return normalized;
    }

    /// <summary>
    /// Normalizes a track title: bracketed groups stripped, trailing
    /// "feat. …"/"featuring …" segments removed, non-alphanumerics collapsed.
    /// Platform suffixes ("Song [Remastered]", "Song (feat. X)") compare equal to the
    /// bare local title.
    /// </summary>
    public static string NormalizeTrackTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant().Replace("–", "-").Replace("—", "-");
        normalized = BracketGroupPattern().Replace(normalized, " ");
        normalized = TrailingFeatPattern().Replace(normalized, " ");
        normalized = NonAlphanumericPattern().Replace(normalized, " ").Trim();
        return normalized;
    }

    /// <summary>
    /// True when either side is a compilation-like local album title that should not
    /// be used as matching evidence. Markers ported from SpotifyArtistService.
    /// </summary>
    public static bool IsLikelyCompilationTitle(string? normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        var value = normalizedTitle.ToLowerInvariant();
        return CompilationAlbumMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Filters compilation-like titles; keeps the original set when everything would
    /// be filtered out (same rule as SpotifyArtistService.FilterResolvableAlbumTitles).
    /// </summary>
    public static List<string> FilterResolvableTitles(IEnumerable<string> titles)
    {
        var all = titles
            .Select(title => title?.Trim() ?? string.Empty)
            .Where(title => title.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolvable = all
            .Where(title => !IsLikelyCompilationTitle(title))
            .ToList();
        return resolvable.Count > 0 ? resolvable : all;
    }

    /// <summary>
    /// Overlap is required when the library has any resolvable album title.
    /// A name hit with no album in common is not a match, including for a one-album artist.
    /// An artist with no resolvable albums is not blocked here.
    /// </summary>
    public static bool ShouldRequireAlbumOverlap(IReadOnlyCollection<string> filteredLocalAlbumTitles)
        => filteredLocalAlbumTitles.Count >= 1;

    /// <summary>
    /// Counts how many normalized local album titles appear in the candidate's
    /// normalized album-title set.
    /// </summary>
    public static int CountAlbumOverlap(IEnumerable<string> localAlbumTitles, IEnumerable<string> candidateAlbumTitles)
    {
        var candidateSet = candidateAlbumTitles
            .Select(NormalizeAlbumTitle)
            .Where(title => title.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidateSet.Count == 0)
        {
            return 0;
        }

        return localAlbumTitles
            .Select(NormalizeAlbumTitle)
            .Where(title => title.Length > 0)
            .Count(candidateSet.Contains);
    }

    /// <summary>
    /// Builds the variant set for an artist name (whitespace-normalized, canonical
    /// diacritic-folded key, connector-word-stripped, leading-"the" and alias-suffix
    /// variants). Ported from SpotifyArtistService.ExpandArtistNameVariants.
    /// </summary>
    public static HashSet<string> BuildArtistNameVariants(string? artistName)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = NormalizeNameWhitespace(artistName);
        if (normalized.Length == 0)
        {
            return variants;
        }

        variants.Add(normalized);
        variants.Add(NormalizeArtistCanonicalKey(normalized));
        variants.Add(RemoveConnectorWords(normalized));
        if (normalized.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(normalized[4..].Trim());
        }

        variants.UnionWith(
            AliasSuffixes
                .Where(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(suffix => normalized[..^suffix.Length].Trim())
                .Where(alias => !string.IsNullOrWhiteSpace(alias)));

        variants.RemoveWhere(static item => string.IsNullOrWhiteSpace(item));
        return variants;
    }

    /// <summary>
    /// True when two artist names are equivalent under the shared alias rules.
    /// </summary>
    public static bool NamesEquivalent(string? localName, string? candidateName)
    {
        var localVariants = BuildArtistNameVariants(localName);
        if (localVariants.Count == 0)
        {
            return false;
        }

        var candidateVariants = BuildArtistNameVariants(candidateName);
        return localVariants.Overlaps(candidateVariants);
    }

    /// <summary>
    /// Lowercases and collapses non-alphanumerics to single spaces.
    /// Ported from SpotifyArtistService.NormalizeTitle.
    /// </summary>
    public static string NormalizeNameWhitespace(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var chars = new char[input.Length];
        var index = 0;
        var lastWasSpace = false;
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                chars[index++] = char.ToLowerInvariant(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                chars[index++] = ' ';
                lastWasSpace = true;
            }
        }

        return new string(chars, 0, index).Trim();
    }

    private static string RemoveConnectorWords(string value)
        => string.Join(
            ' ',
            value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !token.Equals("and", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Diacritic-folded canonical key with special-character mapping.
    /// Ported from SpotifyArtistService.NormalizeArtistCanonicalKey.
    /// </summary>
    private static string NormalizeArtistCanonicalKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKD);
        var buffer = new StringBuilder(normalized.Length);
        foreach (var rawChar in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(rawChar) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            foreach (var mapped in MapArtistCanonicalCharacters(rawChar))
            {
                if (!char.IsLetterOrDigit(mapped))
                {
                    continue;
                }

                buffer.Append(char.ToLowerInvariant(mapped));
            }
        }

        return buffer.Length == 0 ? string.Empty : buffer.ToString();
    }

    private static IEnumerable<char> MapArtistCanonicalCharacters(char value)
    {
        return value switch
        {
            'ø' or 'Ø' => ['o'],
            'đ' or 'Đ' => ['d'],
            'ł' or 'Ł' => ['l'],
            'þ' or 'Þ' => ['t', 'h'],
            'ß' => ['s', 's'],
            'æ' or 'Æ' => ['a', 'e'],
            'œ' or 'Œ' => ['o', 'e'],
            _ => [value]
        };
    }

    /// <summary>
    /// True when a normalized track/album title from a platform result equals one of
    /// the local titles (order-insensitive, suffix-insensitive).
    /// </summary>
    public static bool TitleMatchesAny(string? platformTitle, IEnumerable<string> localTitles, bool isTrackTitle)
        => localTitles.Any(local => TitleEquals(platformTitle, local, isTrackTitle));

    private static bool TitleEquals(string? platformTitle, string localTitle, bool isTrackTitle)
    {
        var normalizedPlatform = isTrackTitle
            ? NormalizeTrackTitle(platformTitle)
            : NormalizeAlbumTitle(platformTitle);
        var normalizedLocal = isTrackTitle
            ? NormalizeTrackTitle(localTitle)
            : NormalizeAlbumTitle(localTitle);
        return normalizedPlatform.Length > 0
            && normalizedLocal.Length > 0
            && string.Equals(normalizedPlatform, normalizedLocal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collapses whitespace runs (helper for queries built from local names).
    /// </summary>
    public static string CollapseWhitespace(string? value)
        => value is null ? string.Empty : WhitespacePattern().Replace(value, " ").Trim();
}
