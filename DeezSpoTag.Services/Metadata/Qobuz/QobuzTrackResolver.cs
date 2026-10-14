using System.Net;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Matching;
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
        CancellationToken cancellationToken, bool rejectAmbiguous = false, string? conversionQuery = null)
    {
        var failures = new List<Exception>();
        QobuzTrackResolution? PreserveFailureIfUnresolved(QobuzTrackResolution? result)
        {
            if (result == null && failures.Count > 0)
            {
                throw new AggregateException("Qobuz catalog lookup failed: "
                    + string.Join("; ", failures.Select(static failure => failure.Message)), failures);
            }
            return result;
        }

        var expectedDurationSec = durationMs.HasValue && durationMs.Value > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : 0;

        if (!string.IsNullOrWhiteSpace(conversionQuery))
        {
            var links = await SearchTracksSafeAsync(conversionQuery, cancellationToken, failures);
            var converted = DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.ConvertCandidate(links.Take(4), conversionQuery,
                item => item.Title ?? "", GetTrackArtist, item => item.Id.ToString());
            if (converted is not null)
            {
                var returnedId = converted.Id;
                converted = await TryGetTrackAsync(returnedId, cancellationToken);
                if (converted?.Id != returnedId) return null;
            }
            return PreserveFailureIfUnresolved(converted is null ? null : PickBestCandidate([converted], isrc, title, artist, album, expectedDurationSec, rejectAmbiguous));
        }

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            var metadataQuery = string.Join(' ', new[] { title, artist }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
            var exactFailures = new List<Exception>();
            var metadataFailures = new List<Exception>();
            using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var lookupToken = lookupCancellation.Token;

            async Task<QobuzTrackResolution?> ResolveExactAsync()
            {
                var exact = await TryFindTrackByISRCAsync(isrc, lookupToken, exactFailures);
                return exact != null && IsExactIsrcMatch(exact, isrc)
                    && !HasContradictoryMetadata(exact, title, artist, expectedDurationSec)
                    ? BuildResolution(exact, "isrc", Math.Max(ScoreCandidate(exact, title, artist, album, expectedDurationSec, preferHiRes: true), 20))
                    : null;
            }

            async Task<QobuzTrackResolution?> ResolveMetadataAsync()
                => PickBestCandidate(await SearchTracksSafeAsync(metadataQuery, lookupToken, metadataFailures),
                    isrc, title, artist, album, expectedDurationSec, rejectAmbiguous);

            var exactTask = ResolveExactAsync();
            var pending = new List<Task<QobuzTrackResolution?>> { exactTask };
            try
            {
                // Cached/synchronous exact matches retain their cheap short-circuit. For a
                // pending catalog request, run the already-supported metadata lookup alongside
                // it so a slow ISRC miss cannot consume the entire shared identity deadline.
                if (exactTask.IsCompleted)
                {
                    var exact = await exactTask;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (exact is not null) return exact;
                    pending.Clear();
                }
                if (!string.IsNullOrWhiteSpace(metadataQuery)) pending.Add(ResolveMetadataAsync());
                _ = Task.WhenAll(pending).ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending).WaitAsync(cancellationToken);
                    pending.Remove(completed);
                    var result = await completed;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result is not null) return result;
                }
            }
            finally { lookupCancellation.Cancel(); }
            failures.AddRange(exactFailures);
            failures.AddRange(metadataFailures);

            if (string.IsNullOrWhiteSpace(metadataQuery)) return PreserveFailureIfUnresolved(null);
            var albumVariant = BuildAlbumTitleVariants(album).OrderBy(static value => value.Length).FirstOrDefault();
            var albumQuery = string.Join(' ', new[] { albumVariant, artist }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(albumQuery)) return PreserveFailureIfUnresolved(null);
            var albumCandidates = await SearchAlbumTracksSafeAsync(albumQuery, cancellationToken, failures);
            return PreserveFailureIfUnresolved(PickBestCandidate(albumCandidates,
                isrc, title, artist, album, expectedDurationSec, rejectAmbiguous));
        }

        var candidates = new Dictionary<int, QobuzTrack>();
        await CollectAlbumCandidatesAsync(candidates, title, artist, album, cancellationToken, failures);
        await CollectCandidatesAsync(candidates, title, artist, album, requireArtist: !string.IsNullOrWhiteSpace(isrc), cancellationToken, failures);

        var best = PickBestCandidate(candidates.Values, isrc, title, artist, album, expectedDurationSec, rejectAmbiguous);
        return PreserveFailureIfUnresolved(best);
    }

    public async Task<QobuzTrackResolution?> ValidateTrackIdAsync(
        QobuzTrackId trackId,
        string? isrc,
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            return await ResolveTrackAsync(isrc, title, artist, album, durationMs, cancellationToken);
        }

        var track = await TryGetTrackAsync(trackId.Value, cancellationToken);
        if (track == null || track.Id <= 0)
        {
            return null;
        }

        var expectedDurationSec = durationMs.HasValue && durationMs.Value > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : 0;
        if (HasContradictoryMetadata(track, title, artist, expectedDurationSec))
        {
            return null;
        }

        var score = ScoreCandidate(track, title, artist, album, expectedDurationSec, preferHiRes: true);
        var hasAuthoritativeMetadataMatch = HasAuthoritativeMetadataMatch(
            track,
            title,
            artist,
            album,
            expectedDurationSec,
            score);
        if (!hasAuthoritativeMetadataMatch)
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
        CancellationToken cancellationToken,
        ICollection<Exception>? failures = null)
    {
        foreach (var query in BuildQueries(title, artist, album, requireArtist))
        {
            await CollectQueryAsync(candidates, query, cancellationToken, failures);

            if (requireArtist)
            {
                continue;
            }

            foreach (var store in ResolveStores())
            {
                var autosuggest = await SearchAutosuggestSafeAsync(query, store, cancellationToken, failures);
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
        CancellationToken cancellationToken,
        ICollection<Exception>? failures = null)
    {
        foreach (var query in BuildAlbumQueries(title, artist, album))
        {
            var albumTracks = await SearchAlbumTracksSafeAsync(query, cancellationToken, failures);
            foreach (var track in albumTracks.Where(static track => track.Id > 0))
            {
                candidates[track.Id] = track;
            }
        }
    }

    private async Task<QobuzTrack?> TryFindTrackByISRCAsync(string isrc, CancellationToken cancellationToken,
        ICollection<Exception>? failures = null)
    {
        try
        {
            return await _metadataService.FindTrackByISRC(isrc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures?.Add(ex);
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
        CancellationToken cancellationToken,
        ICollection<Exception>? failures = null)
    {
        var results = await SearchTracksSafeAsync(query, cancellationToken, failures);
        foreach (var track in results.Where(static t => t.Id > 0))
        {
            candidates[track.Id] = track;
        }
    }

    private async Task<List<QobuzTrack>> SearchTracksSafeAsync(string query, CancellationToken cancellationToken,
        ICollection<Exception>? failures = null)
    {
        try
        {
            return await _metadataService.SearchTracks(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures?.Add(ex);
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

    private async Task<List<QobuzTrack>> SearchAlbumTracksSafeAsync(string query, CancellationToken cancellationToken,
        ICollection<Exception>? failures = null)
    {
        try
        {
            return await _metadataService.SearchAlbumTracks(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures?.Add(ex);
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
        CancellationToken cancellationToken,
        ICollection<Exception>? failures = null)
    {
        try
        {
            return await _metadataService.SearchTracksAutosuggest(query, store, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures?.Add(ex);
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
        int expectedDurationSec, bool rejectAmbiguous = false)
    {
        var acceptedRecordings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var validation = TrackCandidateValidator.Validate(
                new TrackMatchSource(
                    expectedIsrc,
                    expectedTitle,
                    expectedArtist,
                    expectedAlbum,
                    expectedDurationSec > 0 ? expectedDurationSec * 1000 : null),
                BuildTrackMatchCandidate(candidate),
                new TrackCandidateValidationOptions(
                    StrictWithoutIsrc: true,
                    AllowMissingCandidateArtist: false,
                    RequireCandidateDurationWhenSourceHasDuration: true,
                    MaxIsrcDurationDifferenceMs: 20_000,
                    MaxMetadataDurationDifferenceMs: 8_000));
            if (!validation.Accepted)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Rejected Qobuz candidate id={TrackId} reason={Reason} score={Score}",
                        candidate.Id,
                        validation.Reason,
                        score);
                }
                continue;
            }

            acceptedRecordings.Add(!string.IsNullOrWhiteSpace(candidate.ISRC) ? candidate.ISRC : candidate.Id.ToString());
            if (score > bestScore)
            {
                bestScore = score;
                bestTrack = candidate;
            }
        }

        if (bestTrack == null || (rejectAmbiguous && acceptedRecordings.Count > 1))
        {
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
        var validation = TrackCandidateValidator.Validate(
            new TrackMatchSource(
                null,
                expectedTitle,
                expectedArtist,
                expectedAlbum,
                expectedDurationSec > 0 ? expectedDurationSec * 1000 : null),
            BuildTrackMatchCandidate(candidate),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxMetadataDurationDifferenceMs: 8_000));
        return validation.Accepted && score >= 14;
    }

    private static QobuzTrackResolution BuildResolution(QobuzTrack track, string source, int score)
        => new(track, source, score);

    private static TrackMatchCandidate BuildTrackMatchCandidate(QobuzTrack track)
        => new(
            track.Id > 0 ? track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            track.ISRC,
            track.Title,
            GetTrackArtist(track),
            track.Album?.Title,
            track.Duration > 0 ? track.Duration * 1000 : null);

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
        if (TrackTitleMatcher.HasVersionDrift(expected, actual))
        {
            return false;
        }

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

    private static bool ArtistsMatch(string? expected, string? actual)
    {
        return TrackTitleMatcher.ArtistsMatch(expected, actual);
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

}

public sealed record QobuzTrackResolution(QobuzTrack Track, string Source, int Score);

public readonly struct QobuzTrackId
{
    private QobuzTrackId(int value)
    {
        Value = value;
    }

    public int Value { get; }

    public static bool TryCreate(long value, out QobuzTrackId trackId)
    {
        if (value <= 0 || value > int.MaxValue)
        {
            trackId = default;
            return false;
        }

        trackId = new QobuzTrackId((int)value);
        return true;
    }
}
