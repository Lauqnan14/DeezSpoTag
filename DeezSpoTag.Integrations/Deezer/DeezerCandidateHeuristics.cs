using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Utils;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Integrations.Deezer;

internal static class DeezerCandidateHeuristics
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] DerivativeMarkers =
    {
        "cover",
        "karaoke",
        "tribute",
        "instrumental",
        "re recorded",
        "as made famous by"
    };

    private static readonly string[] CompilationMarkers =
    {
        "greatest hits",
        "best of",
        "compilation",
        "essentials",
        "anthology",
        "collection",
        "top hits",
        "playlist",
        "mix",
        "songs of",
        "ultimate hits"
    };

    public static bool SourceAllowsDerivative(string title, string artist, string album)
    {
        var normalized = NormalizeDescriptorToken($"{title} {artist} {album}");
        return ContainsDerivativeMarker(normalized);
    }

    public static bool IsDerivativeCandidate(ApiTrack track)
    {
        var descriptor = new StringBuilder();
        descriptor.Append(track.Title);
        descriptor.Append(' ');
        descriptor.Append(track.TitleShort);
        descriptor.Append(' ');
        descriptor.Append(track.TitleVersion);
        descriptor.Append(' ');
        descriptor.Append(track.Artist?.Name);
        if (track.Contributors != null)
        {
            foreach (var contributor in track.Contributors)
            {
                descriptor.Append(' ');
                descriptor.Append(contributor?.Name);
            }
        }

        var normalized = NormalizeDescriptorToken(descriptor.ToString());
        return ContainsDerivativeMarker(normalized);
    }

    public static bool IsCompilationLikeCandidate(ApiTrack track)
    {
        if (track?.Album == null)
        {
            return false;
        }

        var recordType = track.Album.RecordType?.Trim().ToLowerInvariant();
        if (recordType is "compile" or "compilation")
        {
            return true;
        }

        var normalizedAlbumTitle = NormalizeDescriptorToken(track.Album.Title);
        if (string.IsNullOrWhiteSpace(normalizedAlbumTitle))
        {
            return false;
        }

        return CompilationMarkers.Any(marker => TextMatchUtils.ContainsWholeMarker(normalizedAlbumTitle, marker));
    }

    public static int ScoreFastMatch(ApiTrack candidate, string title, string artist, int? durationSeconds, bool sourceAllowsDerivative, int compilationPenalty)
    {
        if (!sourceAllowsDerivative && IsDerivativeCandidate(candidate))
        {
            return 0;
        }

        var normalizedTitle = NormalizeFastToken(title);
        var normalizedArtist = NormalizeFastToken(artist);
        var candidateArtist = NormalizeFastToken(candidate.Artist?.Name ?? string.Empty);
        var candidateTitle = NormalizeFastToken(candidate.Title ?? string.Empty);
        var score = CalculateBaseScore(normalizedTitle, normalizedArtist, candidateTitle, candidateArtist);
        score += CalculateDurationScore(candidate.Duration, durationSeconds);

        if (IsCompilationLikeCandidate(candidate))
        {
            score -= compilationPenalty;
        }

        return (int)Math.Round(score);
    }

    private static double CalculateBaseScore(
        string normalizedTitle,
        string normalizedArtist,
        string candidateTitle,
        string candidateArtist)
    {
        if (HasComparableArtist(normalizedArtist, candidateArtist))
        {
            var artistSimilarity = CalculateSimilarity(normalizedArtist, candidateArtist);
            var titleSimilarity = CalculateSimilarity(normalizedTitle, candidateTitle);
            return artistSimilarity * 0.55 + titleSimilarity * 0.45;
        }

        return CalculateTitleOnlyScore(normalizedTitle, candidateTitle);
    }

    private static bool HasComparableArtist(string normalizedArtist, string candidateArtist) =>
        !string.IsNullOrWhiteSpace(normalizedArtist) && !string.IsNullOrWhiteSpace(candidateArtist);

    private static double CalculateTitleOnlyScore(string normalizedTitle, string candidateTitle)
    {
        var titleSimilarity = CalculateSimilarity(normalizedTitle, candidateTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(candidateTitle))
        {
            return titleSimilarity;
        }

        var isPartial = candidateTitle.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains(candidateTitle, StringComparison.OrdinalIgnoreCase);
        return isPartial ? titleSimilarity * 1.2 : titleSimilarity;
    }

    private static double CalculateDurationScore(int candidateDuration, int? durationSeconds)
    {
        if (!durationSeconds.HasValue || candidateDuration <= 0)
        {
            return 0d;
        }

        var diff = Math.Abs(candidateDuration - durationSeconds.Value);
        if (diff < 5)
        {
            return 5d;
        }

        return diff < 15 ? 2d : 0d;
    }

    private static string NormalizeFastToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = ReplaceWithTimeout(normalized, @"\s*\(.*?\)\s*", string.Empty);
        normalized = ReplaceWithTimeout(normalized, @"\s*\[.*?\]\s*", string.Empty);
        normalized = ReplaceWithTimeout(normalized, @"[^\w\s]", string.Empty);
        normalized = ReplaceWithTimeout(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static int CalculateSimilarity(string normalized1, string normalized2)
    {
        return DeezerStringSimilarity.Ratio(normalized1, normalized2);
    }

    private static string NormalizeDescriptorToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", " ").Replace("—", " ");
        normalized = ReplaceWithTimeout(normalized, @"[^\p{L}\p{Nd}]+", " ").Trim();
        normalized = ReplaceWithTimeout(normalized, @"\s+", " ");
        return normalized;
    }

    private static bool ContainsDerivativeMarker(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return DerivativeMarkers.Any(marker => TextMatchUtils.ContainsWholeMarker(normalized, marker));
    }

    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);
}
