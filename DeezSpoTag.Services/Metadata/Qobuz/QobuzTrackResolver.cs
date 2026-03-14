using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Services.Metadata.Qobuz;

public sealed class QobuzTrackResolver
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] VersionMarkers =
    {
        "remaster", "remastered", "deluxe", "bonus", "single",
        "album version", "radio edit", "original mix", "extended",
        "club mix", "remix", "live", "acoustic", "demo"
    };

    private readonly IQobuzMetadataService _metadataService;
    private readonly QobuzApiConfig _config;
    private readonly ILogger<QobuzTrackResolver> _logger;

    public QobuzTrackResolver(
        IQobuzMetadataService metadataService,
        IOptions<QobuzApiConfig> options,
        ILogger<QobuzTrackResolver> logger)
    {
        _metadataService = metadataService;
        _config = options.Value;
        _logger = logger;
    }

    public async Task<QobuzTrackResolution?> ResolveTrackAsync(
        string? isrc,
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        var expectedDurationSec = durationMs.HasValue && durationMs.Value > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : 0;

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            var exact = await _metadataService.FindTrackByISRC(isrc, cancellationToken);
            if (exact != null)
            {
                var exactScore = ScoreCandidate(exact, title, artist, album, expectedDurationSec, preferHiRes: true);
                if (exactScore >= 11)
                {
                    return BuildResolution(exact, "isrc", exactScore);
                }
            }
        }

        var candidates = new Dictionary<int, QobuzTrack>();
        await CollectCandidatesAsync(candidates, title, artist, cancellationToken);

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            await CollectQueryAsync(candidates, $"isrc:{isrc.Trim()}", cancellationToken);
        }

        var best = PickBestCandidate(candidates.Values, title, artist, album, expectedDurationSec);
        return best;
    }

    public async Task<string?> ResolveTrackUrlAsync(
        string? isrc,
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveTrackAsync(isrc, title, artist, album, durationMs, cancellationToken);
        return resolved?.Track.Id > 0
            ? $"https://play.qobuz.com/track/{resolved.Track.Id}"
            : null;
    }

    private async Task CollectCandidatesAsync(
        Dictionary<int, QobuzTrack> candidates,
        string? title,
        string? artist,
        CancellationToken cancellationToken)
    {
        foreach (var query in BuildQueries(title, artist))
        {
            await CollectQueryAsync(candidates, query, cancellationToken);

            foreach (var store in ResolveStores())
            {
                var autosuggest = await _metadataService.SearchTracksAutosuggest(query, store, cancellationToken);
                foreach (var track in autosuggest.Where(static t => t.Id > 0))
                {
                    candidates[track.Id] = track;
                }
            }
        }
    }

    private async Task CollectQueryAsync(
        Dictionary<int, QobuzTrack> candidates,
        string query,
        CancellationToken cancellationToken)
    {
        var results = await _metadataService.SearchTracks(query, cancellationToken);
        foreach (var track in results.Where(static t => t.Id > 0))
        {
            candidates[track.Id] = track;
        }
    }

    private QobuzTrackResolution? PickBestCandidate(
        IEnumerable<QobuzTrack> candidates,
        string? expectedTitle,
        string? expectedArtist,
        string? expectedAlbum,
        int expectedDurationSec)
    {
        QobuzTrack? bestTrack = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            var score = ScoreCandidate(candidate, expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, preferHiRes: true);
            if (score > bestScore)
            {
                bestScore = score;
                bestTrack = candidate;
            }
        }

        if (bestTrack == null)
        {
            return null;
        }

        var hasStrictTitle = TitlesMatch(expectedTitle, bestTrack.Title);
        var hasStrictArtist = ArtistsMatch(expectedArtist, GetTrackArtist(bestTrack));
        var minimumScore = hasStrictTitle ? 11 : 8;
        if (bestScore < minimumScore || !hasStrictArtist)
        {
            _logger.LogDebug(
                "Rejected Qobuz candidate id={TrackId} score={Score} titleMatch={TitleMatch} artistMatch={ArtistMatch}",
                bestTrack.Id,
                bestScore,
                hasStrictTitle,
                hasStrictArtist);
            return null;
        }

        return BuildResolution(bestTrack, "metadata", bestScore);
    }

    private static QobuzTrackResolution BuildResolution(QobuzTrack track, string source, int score)
        => new(track, source, score);

    private static int ScoreCandidate(
        QobuzTrack candidate,
        string? expectedTitle,
        string? expectedArtist,
        string? expectedAlbum,
        int expectedDurationSec,
        bool preferHiRes)
    {
        var score = 0;

        if (TitlesMatch(expectedTitle, candidate.Title))
        {
            score += 8;
        }

        if (ArtistsMatch(expectedArtist, GetTrackArtist(candidate)))
        {
            score += 6;
        }

        if (AlbumMatches(expectedAlbum, candidate.Album?.Title))
        {
            score += 4;
        }

        if (expectedDurationSec > 0 && candidate.Duration > 0)
        {
            var delta = Math.Abs(candidate.Duration - expectedDurationSec);
            if (delta <= 2)
            {
                score += 4;
            }
            else if (delta <= 5)
            {
                score += 2;
            }
            else if (delta <= 10)
            {
                score += 1;
            }
            else
            {
                score -= 4;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ISRC))
        {
            score += 1;
        }

        if (preferHiRes && candidate.MaximumBitDepth >= 24)
        {
            score += 1;
        }

        if (preferHiRes && candidate.MaximumSamplingRate >= 96)
        {
            score += 1;
        }

        return score;
    }

    private IEnumerable<string> ResolveStores()
    {
        var configured = _config.PreferredStores ?? new List<string>();
        if (configured.Count == 0)
        {
            yield return QobuzStoreManager.NormalizeStore(_config.DefaultStore, "us-en");
            yield break;
        }

        foreach (var store in configured
                     .Where(static s => !string.IsNullOrWhiteSpace(s))
                     .Select(store => QobuzStoreManager.NormalizeStore(store, _config.DefaultStore))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return store;
        }
    }

    private static HashSet<string> BuildQueries(string? title, string? artist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            seen.Add($"{artist.Trim()} {title.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            seen.Add(title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            seen.Add($"{title.Trim()} {artist.Trim()}");
        }

        return seen;
    }

    private static bool TitlesMatch(string? expected, string? actual)
    {
        var normalizedExpected = NormalizeText(expected);
        var normalizedActual = NormalizeText(actual);
        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedActual))
        {
            return false;
        }

        if (normalizedExpected == normalizedActual)
        {
            return true;
        }

        var cleanExpected = CleanTitle(normalizedExpected);
        var cleanActual = CleanTitle(normalizedActual);
        if (cleanExpected == cleanActual)
        {
            return true;
        }

        return cleanExpected.Contains(cleanActual, StringComparison.Ordinal)
            || cleanActual.Contains(cleanExpected, StringComparison.Ordinal);
    }

    private static bool ArtistsMatch(string? expected, string? actual)
    {
        var normalizedExpected = NormalizeText(expected);
        var normalizedActual = NormalizeText(actual);
        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedActual))
        {
            return false;
        }

        if (normalizedExpected == normalizedActual)
        {
            return true;
        }

        if (normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal)
            || normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        var expectedParts = SplitArtists(normalizedExpected);
        var actualParts = SplitArtists(normalizedActual);
        return expectedParts.Any(exp => actualParts.Any(act =>
            exp == act
            || exp.Contains(act, StringComparison.Ordinal)
            || act.Contains(exp, StringComparison.Ordinal)));
    }

    private static bool AlbumMatches(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var normalizedExpected = CleanTitle(NormalizeText(expected));
        var normalizedActual = CleanTitle(NormalizeText(actual));
        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedActual))
        {
            return false;
        }

        return normalizedExpected == normalizedActual
            || normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal)
            || normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal);
    }

    private static string GetTrackArtist(QobuzTrack track)
        => track.Performer?.Name
           ?? track.Album?.Artists?.FirstOrDefault()?.Name
           ?? string.Empty;

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return normalized;
    }

    private static string CleanTitle(string title)
    {
        var cleaned = title;
        cleaned = RemoveTrailingVersionSection(cleaned, '(', ')');
        cleaned = RemoveTrailingVersionSection(cleaned, '[', ']');
        cleaned = Regex.Replace(cleaned, @"\s+-\s+(remaster(?:ed)?|single version|radio edit|live|acoustic|demo|remix)$", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return cleaned.Trim();
    }

    private static string RemoveTrailingVersionSection(string value, char startChar, char endChar)
    {
        var cleaned = value;
        while (true)
        {
            var startIdx = cleaned.LastIndexOf(startChar);
            var endIdx = cleaned.LastIndexOf(endChar);
            if (startIdx < 0 || endIdx <= startIdx)
            {
                return cleaned.Trim();
            }

            var content = cleaned[(startIdx + 1)..endIdx].ToLowerInvariant();
            if (!VersionMarkers.Any(pattern => content.Contains(pattern, StringComparison.Ordinal)))
            {
                return cleaned.Trim();
            }

            cleaned = (cleaned[..startIdx] + cleaned[(endIdx + 1)..]).Trim();
        }
    }

    private static List<string> SplitArtists(string artists)
    {
        var normalized = artists
            .Replace(" feat. ", "|", StringComparison.Ordinal)
            .Replace(" feat ", "|", StringComparison.Ordinal)
            .Replace(" ft. ", "|", StringComparison.Ordinal)
            .Replace(" ft ", "|", StringComparison.Ordinal)
            .Replace(" & ", "|", StringComparison.Ordinal)
            .Replace(", ", "|", StringComparison.Ordinal)
            .Replace(" and ", "|", StringComparison.Ordinal);

        return normalized
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

public sealed record QobuzTrackResolution(QobuzTrack Track, string Source, int Score);
