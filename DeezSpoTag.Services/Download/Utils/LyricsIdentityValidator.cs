using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Services.Download.Utils;

public sealed record LyricsCandidateIdentity(
    string Provider,
    string? ProviderTrackId,
    string? Title,
    string? Artist,
    string? Album,
    int? DurationSeconds,
    string? Isrc = null);

public sealed record LyricsIdentityValidationResult(bool IsMatch, string Reason, int Score);

public static class LyricsIdentityValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static LyricsIdentityValidationResult ValidateSearchCandidate(
        Track expected,
        LyricsCandidateIdentity candidate,
        int durationToleranceSeconds = 10,
        bool requireArtist = true)
    {
        if (expected == null)
        {
            return Reject("Expected track is missing.");
        }

        if (candidate == null)
        {
            return Reject("Candidate identity is missing.");
        }

        if (IsSameIsrc(expected.ISRC, candidate.Isrc))
        {
            return new LyricsIdentityValidationResult(true, "ISRC matched.", 100);
        }

        if (!IsTitleMatch(expected.Title, candidate.Title))
        {
            return Reject("Title mismatch.");
        }

        if (requireArtist && !IsArtistMatch(ResolveExpectedArtist(expected), candidate.Artist))
        {
            return Reject("Primary artist mismatch.");
        }

        var score = 75;
        if (!requireArtist || IsArtistMatch(ResolveExpectedArtist(expected), candidate.Artist))
        {
            score += 15;
        }

        if (IsDurationMatch(expected.Duration, candidate.DurationSeconds, durationToleranceSeconds))
        {
            score += 10;
        }

        if (IsAlbumMatch(expected.Album?.Title, candidate.Album))
        {
            score += 5;
        }

        return new LyricsIdentityValidationResult(true, "Title and primary artist matched.", Math.Min(score, 100));
    }

    public static LyricsIdentityValidationResult ValidateResolvedMapping(
        Track expected,
        string provider,
        string? sourceTitle,
        string? sourceArtist,
        string? sourceIsrc)
    {
        if (IsSameIsrc(expected.ISRC, sourceIsrc))
        {
            return new LyricsIdentityValidationResult(true, "ISRC matched.", 100);
        }

        if (string.IsNullOrWhiteSpace(sourceTitle) && string.IsNullOrWhiteSpace(sourceArtist))
        {
            return new LyricsIdentityValidationResult(true, $"{provider} mapping did not expose identity metadata.", 70);
        }

        return ValidateSearchCandidate(
            expected,
            new LyricsCandidateIdentity(provider, null, sourceTitle, sourceArtist, null, null, sourceIsrc),
            durationToleranceSeconds: 10,
            requireArtist: true);
    }

    public static bool IsTitleMatch(string? expected, string? actual)
    {
        var expectedNormalized = NormalizeTitle(expected);
        var actualNormalized = NormalizeTitle(actual);
        if (string.IsNullOrWhiteSpace(expectedNormalized) || string.IsNullOrWhiteSpace(actualNormalized))
        {
            return false;
        }

        if (string.Equals(expectedNormalized, actualNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedBase = StripVersionMarkers(expectedNormalized);
        var actualBase = StripVersionMarkers(actualNormalized);
        return !string.IsNullOrWhiteSpace(expectedBase)
            && !string.IsNullOrWhiteSpace(actualBase)
            && string.Equals(expectedBase, actualBase, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsArtistMatch(string? expected, string? actual)
    {
        var expectedPrimary = NormalizeArtist(ResolvePrimaryArtist(expected));
        var actualPrimary = NormalizeArtist(ResolvePrimaryArtist(actual));
        if (string.IsNullOrWhiteSpace(expectedPrimary) || string.IsNullOrWhiteSpace(actualPrimary))
        {
            return false;
        }

        if (string.Equals(expectedPrimary, actualPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TokenSetContains(expectedPrimary, actualPrimary)
            || TokenSetContains(actualPrimary, expectedPrimary);
    }

    public static bool IsDurationMatch(int expectedSeconds, int? actualSeconds, int toleranceSeconds)
    {
        if (expectedSeconds <= 0 || !actualSeconds.HasValue || actualSeconds.Value <= 0)
        {
            return false;
        }

        return Math.Abs(expectedSeconds - actualSeconds.Value) <= Math.Max(0, toleranceSeconds);
    }

    public static bool IsAlbumMatch(string? expected, string? actual)
    {
        var expectedNormalized = NormalizeText(expected);
        var actualNormalized = NormalizeText(actual);
        return !string.IsNullOrWhiteSpace(expectedNormalized)
            && !string.IsNullOrWhiteSpace(actualNormalized)
            && string.Equals(expectedNormalized, actualNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static LyricsIdentityValidationResult Reject(string reason)
        => new(false, reason, 0);

    private static string? ResolveExpectedArtist(Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.MainArtist?.Name))
        {
            return track.MainArtist.Name;
        }

        if (track.Artists?.Count > 0)
        {
            return track.Artists.FirstOrDefault(static artist => !string.IsNullOrWhiteSpace(artist));
        }

        if (!string.IsNullOrWhiteSpace(track.ArtistString))
        {
            return track.ArtistString;
        }

        return track.Artist.TryGetValue("Main", out var mainArtists)
            ? mainArtists.FirstOrDefault(static artist => !string.IsNullOrWhiteSpace(artist))
            : null;
    }

    private static bool IsSameIsrc(string? expected, string? actual)
    {
        var expectedNormalized = NormalizeIsrc(expected);
        var actualNormalized = NormalizeIsrc(actual);
        return !string.IsNullOrWhiteSpace(expectedNormalized)
            && !string.IsNullOrWhiteSpace(actualNormalized)
            && string.Equals(expectedNormalized, actualNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIsrc(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim(), @"[^A-Za-z0-9]", string.Empty, RegexOptions.None, RegexTimeout).ToUpperInvariant();

    private static string NormalizeTitle(string? value)
    {
        var normalized = NormalizeText(value);
        normalized = Replace(normalized, @"\b(feat|ft|featuring)\b.*$", string.Empty);
        return normalized.Trim();
    }

    private static string NormalizeArtist(string? value)
        => NormalizeText(value);

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = Replace(normalized, @"\((feat|ft|featuring)\.?.*?\)", string.Empty);
        normalized = Replace(normalized, @"\[.*?\]", string.Empty);
        normalized = Replace(normalized, @"\b(remastered|explicit|clean|radio edit|single version)\b", string.Empty);
        normalized = Replace(normalized, @"[^a-z0-9\s&,+]", " ");
        normalized = Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static string StripVersionMarkers(string value)
    {
        var stripped = Replace(value, @"\b(radio edit|single version|album version|remix|remaster(ed)?)\b.*$", string.Empty);
        stripped = Replace(stripped, @"\s+", " ");
        return stripped.Trim();
    }

    private static string? ResolvePrimaryArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var normalized = Replace(artist, @"\b(feat|ft|featuring)\b.*$", string.Empty, RegexOptions.IgnoreCase);
        var separators = new[] { ",", "&", " x ", " X ", " and " };
        foreach (var separator in separators)
        {
            var index = normalized.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                normalized = normalized[..index];
            }
        }

        return normalized.Trim();
    }

    private static bool TokenSetContains(string expected, string actual)
    {
        var expectedTokens = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualTokens = actual.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedTokens.Count > 0 && actualTokens.Count > 0 && expectedTokens.IsSubsetOf(actualTokens);
    }

    private static string Replace(string value, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(value, pattern, replacement, options, RegexTimeout);
}
