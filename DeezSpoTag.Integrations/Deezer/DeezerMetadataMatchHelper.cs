using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Utils;
using Newtonsoft.Json;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Integrations.Deezer;

internal static class DeezerMetadataMatchHelper
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static List<ApiTrack> ConvertSearchResults(object[]? data)
    {
        if (data == null || data.Length == 0)
        {
            return new List<ApiTrack>();
        }

        var candidates = new List<ApiTrack>(data.Length);
        foreach (var item in data)
        {
            ApiTrack? track;
            try
            {
                track = JsonConvert.DeserializeObject<ApiTrack>(JsonConvert.SerializeObject(item));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (track == null || string.IsNullOrWhiteSpace(track.Id) || track.Id == "0")
            {
                continue;
            }

            candidates.Add(track);
        }

        return candidates;
    }

    public static bool IsTitleMatch(string normalizedTitle, string normalizedTitleNoFeat, ApiTrack track)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        var candidateTitle = NormalizeMatchToken(track.Title);
        if (candidateTitle == normalizedTitle || candidateTitle == normalizedTitleNoFeat)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(track.TitleShort))
        {
            var shortTitle = NormalizeMatchToken(track.TitleShort);
            if (shortTitle == normalizedTitle || shortTitle == normalizedTitleNoFeat)
            {
                return true;
            }
        }

        var candidateTitleNoFeat = NormalizeMatchToken(RemoveFeaturing(track.Title));
        return candidateTitleNoFeat == normalizedTitle || candidateTitleNoFeat == normalizedTitleNoFeat;
    }

    public static bool IsArtistMatch(string normalizedArtist, ApiTrack track)
    {
        if (string.IsNullOrWhiteSpace(normalizedArtist))
        {
            return false;
        }

        return GetArtistCandidates(track)
            .Select(NormalizeMatchToken)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Any(candidate => normalizedArtist == candidate || ContainsToken(normalizedArtist, candidate));
    }

    public static bool IsDurationMatch(int sourceSeconds, int candidateSeconds)
    {
        if (sourceSeconds <= 0 || candidateSeconds <= 0)
        {
            return false;
        }

        var tolerance = Math.Min(5, (int)Math.Ceiling(sourceSeconds * 0.05));
        return Math.Abs(sourceSeconds - candidateSeconds) <= tolerance;
    }

    public static bool IsExactNormalizedMatch(string normalizedSource, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return normalizedSource == NormalizeMatchToken(candidate);
    }

    public static bool ContainsToken(string haystack, string needle)
    {
        if (haystack == needle)
        {
            return true;
        }

        return haystack.StartsWith($"{needle} ", StringComparison.Ordinal)
            || haystack.EndsWith($" {needle}", StringComparison.Ordinal)
            || haystack.Contains($" {needle} ", StringComparison.Ordinal);
    }

    public static string NormalizeMatchToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = StripMatchDecorations(value);

        var formD = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark))
        {
            sb.Append(ch);
        }

        var normalized = sb.ToString().ToLowerInvariant();
        normalized = normalized.Replace("&", "and");
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ", RegexOptions.None, RegexTimeout).Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return normalized;
    }

    public static string RemoveFeaturing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = Regex.Replace(value, @"\s*[\(\[]?\s*(feat|ft|featuring)\b.*$", "", RegexOptions.IgnoreCase, RegexTimeout);
        return trimmed.Trim();
    }

    public static IEnumerable<string> GetArtistCandidates(ApiTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Artist?.Name))
        {
            yield return track.Artist.Name;
        }

        if (track.Contributors == null)
        {
            yield break;
        }

        foreach (var contributorName in track.Contributors
                     .Where(contributor => !string.IsNullOrWhiteSpace(contributor.Name))
                     .Select(contributor => contributor.Name))
        {
            yield return contributorName;
        }
    }

    public static double GetBestArtistSimilarity(string normalizedArtist, ApiTrack track)
    {
        if (string.IsNullOrWhiteSpace(normalizedArtist))
        {
            return 0d;
        }

        return GetArtistCandidates(track)
            .Select(NormalizeMatchToken)
            .Where(normalizedCandidate => !string.IsNullOrWhiteSpace(normalizedCandidate))
            .Select(normalizedCandidate => ComputeSimilarity(normalizedArtist, normalizedCandidate))
            .DefaultIfEmpty(0d)
            .Max();
    }

    public static double GetBestTitleSimilarity(string normalizedTitle, string normalizedTitleNoFeat, ApiTrack track)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedTitleNoFeat))
        {
            return 0d;
        }

        var candidates = new List<string>(5);
        AddCandidate(candidates, track.Title);
        AddCandidate(candidates, track.TitleShort);
        AddCandidate(candidates, track.TitleVersion);
        AddCandidate(candidates, RemoveFeaturing(track.Title));
        AddCandidate(candidates, RemoveFeaturing(track.TitleShort));

        return candidates
            .Select(NormalizeMatchToken)
            .Where(normalizedCandidate => !string.IsNullOrWhiteSpace(normalizedCandidate))
            .Select(normalizedCandidate =>
            {
                var bestForCandidate = 0d;
                if (!string.IsNullOrWhiteSpace(normalizedTitle))
                {
                    bestForCandidate = Math.Max(bestForCandidate, ComputeSimilarity(normalizedTitle, normalizedCandidate));
                }

                if (!string.IsNullOrWhiteSpace(normalizedTitleNoFeat))
                {
                    bestForCandidate = Math.Max(bestForCandidate, ComputeSimilarity(normalizedTitleNoFeat, normalizedCandidate));
                }

                return bestForCandidate;
            })
            .DefaultIfEmpty(0d)
            .Max();
    }

    public static void AddCandidate(List<string> candidates, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!candidates.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(value);
        }
    }

    public static string StripMatchDecorations(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.ToLowerInvariant();
        var markers = new[]
        {
            " (remastered)", " (remaster)", " - remastered", " - remaster",
            " (deluxe)", " (deluxe edition)", " - deluxe", " - deluxe edition",
            " (explicit)", " (clean)", " [explicit]", " [clean]",
            " (album version)", " (single version)", " (radio edit)", " (edit)",
            " (mono)", " (stereo)", " (live)", " (bonus track)",
            " (extended mix)", " (original mix)",
            " - live", " - edit", " - mono", " - stereo"
        };

        var cutIndex = markers
            .Select(marker => lowered.IndexOf(marker, StringComparison.Ordinal))
            .Where(idx => idx >= 0)
            .DefaultIfEmpty(value.Length)
            .Min();

        return cutIndex < value.Length ? value[..cutIndex] : value;
    }

    public static double ComputeSimilarity(string source, string candidate)
    {
        return TextMatchUtils.ComputeNormalizedSimilarity(source, candidate);
    }

    public static int LevenshteinDistance(string s1, string s2)
    {
        return TextMatchUtils.LevenshteinDistance(s1, s2);
    }
}
