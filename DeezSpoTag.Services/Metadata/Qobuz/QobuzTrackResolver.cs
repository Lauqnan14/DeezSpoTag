using System.Net;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Core.Utils;
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

    private static readonly string[] AlbumReleaseTypeSuffixes =
    {
        "single", "ep"
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
            var exact = await TryFindTrackByISRCAsync(isrc, cancellationToken);
            if (exact != null
                && IsExactIsrcMatch(exact, isrc)
                && !HasContradictoryMetadata(exact, title, artist, expectedDurationSec))
            {
                var exactScore = ScoreCandidate(exact, title, artist, album, expectedDurationSec, preferHiRes: true);
                return BuildResolution(exact, "isrc", Math.Max(exactScore, 20));
            }
        }

        var candidates = new Dictionary<int, QobuzTrack>();
        await CollectAlbumCandidatesAsync(candidates, title, artist, album, cancellationToken);
        await CollectCandidatesAsync(candidates, title, artist, album, requireArtist: !string.IsNullOrWhiteSpace(isrc), cancellationToken);

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            await CollectQueryAsync(candidates, $"isrc:{isrc.Trim()}", cancellationToken);
        }

        var best = PickBestCandidate(candidates.Values, isrc, title, artist, album, expectedDurationSec);
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

    public async Task<QobuzTrackResolution?> ValidateTrackIdAsync(
        int trackId,
        string? isrc,
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            return null;
        }

        var track = await TryGetTrackAsync(trackId, cancellationToken);
        if (track == null || track.Id <= 0)
        {
            return null;
        }

        var expectedDurationSec = durationMs.HasValue && durationMs.Value > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : 0;
        if (!string.IsNullOrWhiteSpace(isrc)
            && !IsExactIsrcMatch(track, isrc))
        {
            return null;
        }

        if (HasContradictoryMetadata(track, title, artist, expectedDurationSec))
        {
            return null;
        }

        var score = ScoreCandidate(track, title, artist, album, expectedDurationSec, preferHiRes: true);
        if (string.IsNullOrWhiteSpace(isrc)
            && !HasAuthoritativeMetadataMatch(track, title, artist, album, expectedDurationSec, score))
        {
            return null;
        }

        return BuildResolution(track, "direct_url", score);
    }

    private async Task CollectCandidatesAsync(
        Dictionary<int, QobuzTrack> candidates,
        string? title,
        string? artist,
        string? album,
        bool requireArtist,
        CancellationToken cancellationToken)
    {
        foreach (var query in BuildQueries(title, artist, album, requireArtist))
        {
            await CollectQueryAsync(candidates, query, cancellationToken);

            if (requireArtist)
            {
                continue;
            }

            foreach (var store in ResolveStores())
            {
                var autosuggest = await SearchAutosuggestSafeAsync(query, store, cancellationToken);
                foreach (var track in autosuggest.Where(static t => t.Id > 0))
                {
                    candidates[track.Id] = track;
                }
            }
        }
    }

    private async Task CollectAlbumCandidatesAsync(
        Dictionary<int, QobuzTrack> candidates,
        string? title,
        string? artist,
        string? album,
        CancellationToken cancellationToken)
    {
        foreach (var query in BuildAlbumQueries(title, artist, album))
        {
            var albumTracks = await SearchAlbumTracksSafeAsync(query, cancellationToken);
            foreach (var track in albumTracks.Where(static track => track.Id > 0))
            {
                candidates[track.Id] = track;
            }
        }
    }

    private async Task<QobuzTrack?> TryFindTrackByISRCAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            return await _metadataService.FindTrackByISRC(isrc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsRateLimit(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Qobuz ISRC lookup throttled for {Isrc}; continuing with alternate Qobuz resolution.",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(isrc));
                return null;
            }

            _logger.LogWarning(ex, "Qobuz ISRC lookup failed for {Isrc}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(isrc));
            return null;
        }
    }

    private async Task<QobuzTrack?> TryGetTrackAsync(int trackId, CancellationToken cancellationToken)
    {
        try
        {
            return await _metadataService.GetTrack(trackId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsRateLimit(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Qobuz track lookup throttled for id {TrackId}; continuing with alternate Qobuz resolution.",
                    trackId);
                return null;
            }

            _logger.LogWarning(ex, "Qobuz track lookup failed for id {TrackId}", trackId);
            return null;
        }
    }

    private async Task CollectQueryAsync(
        Dictionary<int, QobuzTrack> candidates,
        string query,
        CancellationToken cancellationToken)
    {
        var results = await SearchTracksSafeAsync(query, cancellationToken);
        foreach (var track in results.Where(static t => t.Id > 0))
        {
            candidates[track.Id] = track;
        }
    }

    private async Task<List<QobuzTrack>> SearchTracksSafeAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            return await _metadataService.SearchTracks(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsRateLimit(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Qobuz track search throttled for query {Query}; continuing with alternate Qobuz resolution.",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(query));
                return new List<QobuzTrack>();
            }

            _logger.LogWarning(ex, "Qobuz track search failed for query {Query}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(query));
            return new List<QobuzTrack>();
        }
    }

    private async Task<List<QobuzTrack>> SearchAlbumTracksSafeAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            return await _metadataService.SearchAlbumTracks(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsRateLimit(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Qobuz album track search throttled for query {Query}; continuing with alternate Qobuz resolution.",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(query));
                return new List<QobuzTrack>();
            }

            _logger.LogWarning(ex, "Qobuz album track search failed for query {Query}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(query));
            return new List<QobuzTrack>();
        }
    }

    private async Task<List<QobuzTrack>> SearchAutosuggestSafeAsync(
        string query,
        string store,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _metadataService.SearchTracksAutosuggest(query, store, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsRateLimit(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Qobuz autosuggest throttled for query {Query} store {Store}; continuing with alternate Qobuz resolution.",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(query),
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(store));
                return new List<QobuzTrack>();
            }

            _logger.LogWarning(ex, "Qobuz autosuggest failed for query {Query} store {Store}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(query), DeezSpoTag.Core.Security.LogSanitizer.OneLine(store));
            return new List<QobuzTrack>();
        }
    }

    private static bool IsRateLimit(Exception exception)
        => exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };

    private QobuzTrackResolution? PickBestCandidate(
        IEnumerable<QobuzTrack> candidates,
        string? expectedIsrc,
        string? expectedTitle,
        string? expectedArtist,
        string? expectedAlbum,
        int expectedDurationSec)
    {
        QobuzTrack? bestTrack = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(expectedIsrc)
                && !string.IsNullOrWhiteSpace(candidate.ISRC)
                && !IsExactIsrcMatch(candidate, expectedIsrc))
            {
                continue;
            }

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

        var hasStrictTitle = StrictTitlesMatch(expectedTitle, bestTrack.Title);
        var hasStrictArtist = StrictArtistsMatch(expectedArtist, GetTrackArtist(bestTrack));
        var accepted = IsAcceptedResolvedTrack(new QobuzTrackAcceptanceInput(
            bestTrack,
            expectedIsrc,
            expectedTitle,
            expectedArtist,
            expectedAlbum,
            expectedDurationSec,
            bestScore,
            hasStrictTitle,
            hasStrictArtist));
        if (!accepted)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Rejected Qobuz candidate id={TrackId} score={Score} titleMatch={TitleMatch} artistMatch={ArtistMatch}",
                    bestTrack.Id,
                    bestScore,
                    hasStrictTitle,
                    hasStrictArtist);
            }
            return null;
        }

        return BuildResolution(bestTrack, "metadata", bestScore);
    }

    private static bool HasAuthoritativeMetadataMatch(
        QobuzTrack candidate,
        string? expectedTitle,
        string? expectedArtist,
        string? expectedAlbum,
        int expectedDurationSec,
        int score)
    {
        if (!StrictTitlesMatch(expectedTitle, candidate.Title)
            || !StrictArtistsMatch(expectedArtist, GetTrackArtist(candidate)))
        {
            return false;
        }

        if (expectedDurationSec > 0)
        {
            if (candidate.Duration <= 0)
            {
                return false;
            }

            if (Math.Abs(candidate.Duration - expectedDurationSec) > 10)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedAlbum)
            && !string.IsNullOrWhiteSpace(candidate.Album?.Title)
            && !StrictAlbumMatches(expectedAlbum, candidate.Album.Title))
        {
            return false;
        }

        return score >= 14;
    }

    private sealed record QobuzTrackAcceptanceInput(
        QobuzTrack Candidate,
        string? ExpectedIsrc,
        string? ExpectedTitle,
        string? ExpectedArtist,
        string? ExpectedAlbum,
        int ExpectedDurationSec,
        int BestScore,
        bool HasStrictTitle,
        bool HasStrictArtist);

    private static bool IsAcceptedResolvedTrack(QobuzTrackAcceptanceInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.ExpectedIsrc))
        {
            return input.BestScore >= (input.HasStrictTitle ? 11 : 8) && input.HasStrictArtist;
        }

        return HasAuthoritativeMetadataMatch(
            input.Candidate,
            input.ExpectedTitle,
            input.ExpectedArtist,
            input.ExpectedAlbum,
            input.ExpectedDurationSec,
            input.BestScore);
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

    private static bool IsExactIsrcMatch(QobuzTrack track, string isrc)
    {
        return track.Id > 0
            && !string.IsNullOrWhiteSpace(track.ISRC)
            && string.Equals(track.ISRC.Trim(), isrc.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasContradictoryMetadata(
        QobuzTrack track,
        string? expectedTitle,
        string? expectedArtist,
        int expectedDurationSec)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(expectedTitle);
        var hasArtist = !string.IsNullOrWhiteSpace(expectedArtist);
        if (hasTitle && !TitlesMatch(expectedTitle, track.Title))
        {
            return true;
        }

        if (hasArtist && !ArtistsMatch(expectedArtist, GetTrackArtist(track)))
        {
            return true;
        }

        if (expectedDurationSec <= 0 || track.Duration <= 0)
        {
            return false;
        }

        return Math.Abs(track.Duration - expectedDurationSec) > 20;
    }

    private static HashSet<string> BuildQueries(string? title, string? artist, string? album, bool requireArtist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            seen.Add($"{artist.Trim()} {title.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(artist)
            && !string.IsNullOrWhiteSpace(title)
            && !string.IsNullOrWhiteSpace(album))
        {
            seen.Add($"{artist.Trim()} {title.Trim()} {album.Trim()}");
            seen.Add($"{title.Trim()} {artist.Trim()} {album.Trim()}");
        }

        if (!requireArtist && !string.IsNullOrWhiteSpace(title))
        {
            seen.Add(title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            seen.Add($"{title.Trim()} {artist.Trim()}");
        }

        return seen;
    }

    private static HashSet<string> BuildAlbumQueries(string? title, string? artist, string? album)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var albumVariants = BuildAlbumTitleVariants(album);
        if (albumVariants.Count == 0 && !string.IsNullOrWhiteSpace(title))
        {
            albumVariants = BuildAlbumTitleVariants(title);
        }

        if (albumVariants.Count == 0)
        {
            return seen;
        }

        foreach (var albumVariant in albumVariants)
        {
            if (!string.IsNullOrWhiteSpace(artist))
            {
                seen.Add($"{artist.Trim()} {albumVariant}");
                seen.Add($"{albumVariant} {artist.Trim()}");
            }
            else
            {
                seen.Add(albumVariant);
            }
        }

        return seen;
    }

    private static List<string> BuildAlbumTitleVariants(string? album)
    {
        var normalized = TrackTitleMatcher.NormalizeText(album);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new List<string>();
        }

        var variants = new List<string> { normalized };
        foreach (var suffix in AlbumReleaseTypeSuffixes)
        {
            AddAlbumVariant(variants, RemoveReleaseTypeSuffix(normalized, suffix));
        }

        return variants
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddAlbumVariant(List<string> variants, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !variants.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            variants.Add(value);
        }
    }

    private static string RemoveReleaseTypeSuffix(string value, string suffix)
    {
        var escapedSuffix = Regex.Escape(suffix);
        var withoutBracketedSuffix = Regex.Replace(
            value,
            $@"\s*[\(\[]\s*{escapedSuffix}\s*[\)\]]\s*$",
            string.Empty,
            RegexOptions.IgnoreCase,
            RegexTimeout).Trim();

        return Regex.Replace(
            withoutBracketedSuffix,
            $@"\s*[-–—]\s*{escapedSuffix}\s*$",
            string.Empty,
            RegexOptions.IgnoreCase,
            RegexTimeout).Trim();
    }

    private static bool TitlesMatch(string? expected, string? actual)
    {
        var normalizedExpected = TrackTitleMatcher.NormalizeText(expected);
        var normalizedActual = TrackTitleMatcher.NormalizeText(actual);
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

    private static bool StrictTitlesMatch(string? expected, string? actual)
    {
        var normalizedExpected = NormalizeStrictComparableTitle(expected);
        var normalizedActual = NormalizeStrictComparableTitle(actual);
        return !string.IsNullOrWhiteSpace(normalizedExpected)
            && normalizedExpected == normalizedActual;
    }

    private static bool ArtistsMatch(string? expected, string? actual)
    {
        return TrackTitleMatcher.ArtistsMatch(expected, actual);
    }

    private static bool StrictArtistsMatch(string? expected, string? actual)
    {
        return TrackTitleMatcher.StrictArtistsMatch(expected, actual);
    }

    private static bool AlbumMatches(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var normalizedExpected = CleanTitle(TrackTitleMatcher.NormalizeText(expected));
        var normalizedActual = CleanTitle(TrackTitleMatcher.NormalizeText(actual));
        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedActual))
        {
            return false;
        }

        return normalizedExpected == normalizedActual
            || normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal)
            || normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal);
    }

    private static bool StrictAlbumMatches(string? expected, string? actual)
    {
        var normalizedExpected = NormalizeStrictComparableTitle(expected);
        var normalizedActual = NormalizeStrictComparableTitle(actual);
        return !string.IsNullOrWhiteSpace(normalizedExpected)
            && !string.IsNullOrWhiteSpace(normalizedActual)
            && string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal);
    }

    private static string NormalizeStrictComparableTitle(string? value)
    {
        var normalized = CleanTitle(TrackTitleMatcher.NormalizeText(value));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ", RegexOptions.None, RegexTimeout);
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    private static string GetTrackArtist(QobuzTrack track)
        => track.Performer?.Name
           ?? track.Album?.Artists?.FirstOrDefault()?.Name
           ?? string.Empty;

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
