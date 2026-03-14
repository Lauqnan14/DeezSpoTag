using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Qobuz;

namespace DeezSpoTag.Web.Services;

public static class QobuzTrackMatchingService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static QobuzTrack? FindBestMatch(
        string? title,
        string? artist,
        int? expectedDuration,
        IReadOnlyList<QobuzTrack> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var expectedTitle = Normalize(title);
        var expectedArtist = Normalize(artist);

        QobuzTrack? best = null;
        var bestScore = 0.0;

        foreach (var candidate in candidates)
        {
            var score = ScoreCandidate(candidate, expectedTitle, expectedArtist, expectedDuration);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= 0.7 ? best : null;
    }

    private static double ScoreCandidate(
        QobuzTrack candidate,
        string expectedTitle,
        string expectedArtist,
        int? expectedDuration)
    {
        var score = 0.0;
        score += ScoreName(Normalize(candidate.Title), expectedTitle, 0.6, 0.3);
        score += ScoreName(Normalize(GetTrackArtist(candidate)), expectedArtist, 0.3, 0.15);

        var duration = expectedDuration.GetValueOrDefault();
        if (duration > 0 && candidate.Duration > 0)
        {
            var diff = Math.Abs(duration - candidate.Duration);
            if (diff <= 2)
            {
                score += 0.1;
            }
        }

        return score;
    }

    private static double ScoreName(string candidate, string expected, double exactScore, double fuzzyScore)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(expected))
        {
            return 0.0;
        }

        if (candidate == expected)
        {
            return exactScore;
        }

        if (candidate.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
            expected.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return fuzzyScore;
        }

        return 0.0;
    }

    private static string GetTrackArtist(QobuzTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Performer?.Name))
        {
            return track.Performer.Name;
        }

        var albumArtist = track.Album?.Artists?.FirstOrDefault()?.Name;
        return albumArtist ?? string.Empty;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        return Regex.Replace(trimmed, @"[^a-z0-9]+", string.Empty, RegexOptions.None, RegexTimeout);
    }
}
