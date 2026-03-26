using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LrclibMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly char[] LyricsLineSeparators = ['\r', '\n'];
    private static readonly Regex LrcLineRegex = new(
        @"^\[(\d{1,3}):([0-5]?\d)\.(\d{1,3})\](.*)$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly string[] TitleCleanupPatterns =
    {
        @"\s*\(feat\..*?\)",
        @"\s*\(ft\..*?\)",
        @"\s*\(featuring.*?\)",
        @"\s*\(with.*?\)",
        @"\s*-\s*Remaster(ed)?.*$",
        @"\s*-\s*\d{4}\s*Remaster.*$",
        @"\s*\(Remaster(ed)?.*?\)",
        @"\s*\(Deluxe.*?\)",
        @"\s*\(Bonus.*?\)",
        @"\s*\(Live.*?\)",
        @"\s*\(Acoustic.*?\)",
        @"\s*\(Radio Edit\)",
        @"\s*\(Single Version\)"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LrclibMatcher> _logger;

    public LrclibMatcher(IHttpClientFactory httpClientFactory, ILogger<LrclibMatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        LrclibConfig config,
        CancellationToken cancellationToken)
    {
        var title = OneTaggerMatching.CleanTitle(info.Title).Trim();
        var artistRaw = FirstNonEmpty(info.Artist, info.Artists.FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name)));
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artistRaw))
        {
            return null;
        }

        var primaryArtist = NormalizeArtistName(artistRaw);
        var artistCandidates = BuildArtistCandidates(primaryArtist, artistRaw, info.Artists);
        var titleCandidates = BuildTitleCandidates(title);
        var durationSeconds = info.DurationSeconds.HasValue && info.DurationSeconds.Value > 0
            ? info.DurationSeconds.Value
            : (int?)null;
        var effectiveConfig = NormalizeConfig(config);

        var metadataMatch = await TryMatchByMetadataAsync(
            info,
            artistCandidates,
            titleCandidates,
            durationSeconds,
            effectiveConfig,
            cancellationToken);
        if (metadataMatch != null)
        {
            return metadataMatch;
        }

        if (!effectiveConfig.SearchFallback)
        {
            return null;
        }

        foreach (var query in BuildQueryCandidates(artistCandidates, titleCandidates))
        {
            var bySearch = await FetchBySearchAsync(query, durationSeconds, effectiveConfig, cancellationToken);
            var searchMatch = BuildAutoTagMatch(info, bySearch);
            if (searchMatch != null)
            {
                return searchMatch;
            }
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> TryMatchByMetadataAsync(
        AutoTagAudioInfo info,
        IEnumerable<string> artistCandidates,
        IEnumerable<string> titleCandidates,
        int? durationSeconds,
        LrclibConfig config,
        CancellationToken cancellationToken)
    {
        foreach (var artist in artistCandidates)
        {
            foreach (var titleCandidate in titleCandidates)
            {
                var byMetadata = await FetchByMetadataAsync(
                    artist,
                    titleCandidate,
                    durationSeconds,
                    config,
                    cancellationToken);
                var metadataMatch = BuildAutoTagMatch(info, byMetadata);
                if (metadataMatch != null)
                {
                    return metadataMatch;
                }
            }
        }

        return null;
    }

    private static List<string> BuildQueryCandidates(
        IEnumerable<string> artistCandidates,
        IEnumerable<string> titleCandidates)
    {
        var queries = new List<string>();
        foreach (var artist in artistCandidates)
        {
            foreach (var titleCandidate in titleCandidates)
            {
                var query = $"{artist} {titleCandidate}".Trim();
                if (string.IsNullOrWhiteSpace(query)
                    || queries.Contains(query, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                queries.Add(query);
            }
        }

        return queries;
    }

    private static LrclibConfig NormalizeConfig(LrclibConfig? config)
    {
        var normalized = config ?? new LrclibConfig();
        normalized.DurationToleranceSeconds = Math.Clamp(normalized.DurationToleranceSeconds, 0, 60);
        return normalized;
    }

    private async Task<LrclibApiTrack?> FetchByMetadataAsync(
        string artistName,
        string trackName,
        int? durationSeconds,
        LrclibConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackName))
        {
            return null;
        }

        var query = new List<string>
        {
            $"artist_name={Uri.EscapeDataString(artistName)}",
            $"track_name={Uri.EscapeDataString(trackName)}"
        };

        if (config.UseDurationHint && durationSeconds.HasValue && durationSeconds.Value > 0)
        {
            query.Add($"duration={durationSeconds.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var url = $"https://lrclib.net/api/get?{string.Join("&", query)}";
        try
        {
            using var response = await SendGetAsync(url, cancellationToken);
            if (response == null || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "LRCLIB metadata request failed with status {Status} for {Artist} - {Title}",
                    (int)response.StatusCode,
                    artistName,
                    trackName);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ParseApiTrack(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LRCLIB metadata request failed for {Artist} - {Title}", artistName, trackName);
            return null;
        }
    }

    private async Task<LrclibApiTrack?> FetchBySearchAsync(
        string query,
        int? durationSeconds,
        LrclibConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
        try
        {
            using var response = await SendGetAsync(url, cancellationToken);
            if (response == null || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("LRCLIB search request failed with status {Status} for query {Query}", (int)response.StatusCode, query);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var candidates = new List<LrclibApiTrack>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var parsed = ParseApiTrack(element);
                if (parsed != null)
                {
                    candidates.Add(parsed);
                }
            }

            return SelectBestCandidate(candidates, durationSeconds, config);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LRCLIB search request failed for query {Query}", query);
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendGetAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("LyricsService");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(request, cancellationToken);
    }

    private static LrclibApiTrack? SelectBestCandidate(
        List<LrclibApiTrack> candidates,
        int? durationSeconds,
        LrclibConfig config)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        IEnumerable<LrclibApiTrack> filtered = candidates;
        if (durationSeconds.HasValue && config.DurationToleranceSeconds > 0)
        {
            var tolerance = config.DurationToleranceSeconds;
            var byDuration = candidates
                .Where(static candidate => candidate.Duration.HasValue)
                .Where(candidate => Math.Abs(candidate.Duration!.Value - durationSeconds.Value) <= tolerance)
                .ToList();
            if (byDuration.Count > 0)
            {
                filtered = byDuration;
            }
        }

        return filtered
            .OrderByDescending(candidate => ComputeScore(candidate, durationSeconds, config))
            .ThenBy(candidate =>
            {
                if (!durationSeconds.HasValue || !candidate.Duration.HasValue)
                {
                    return double.MaxValue;
                }

                return Math.Abs(candidate.Duration.Value - durationSeconds.Value);
            })
            .FirstOrDefault();
    }

    private static double ComputeScore(LrclibApiTrack candidate, int? durationSeconds, LrclibConfig config)
    {
        var score = 0d;
        var hasSynced = !string.IsNullOrWhiteSpace(candidate.SyncedLyrics);
        var hasPlain = !string.IsNullOrWhiteSpace(candidate.PlainLyrics);

        if (config.PreferSynced && hasSynced)
        {
            score += 100d;
        }
        else if (hasSynced)
        {
            score += 25d;
        }

        if (hasPlain)
        {
            score += 20d;
        }

        if (candidate.Instrumental)
        {
            score -= 30d;
        }

        if (durationSeconds.HasValue && candidate.Duration.HasValue)
        {
            var delta = Math.Abs(candidate.Duration.Value - durationSeconds.Value);
            score += Math.Max(0d, 20d - delta);
        }

        return score;
    }

    private static AutoTagMatchResult? BuildAutoTagMatch(AutoTagAudioInfo info, LrclibApiTrack? payload)
    {
        if (payload == null)
        {
            return null;
        }

        var syncedLines = ParseSyncedLines(payload.SyncedLyrics);
        var unsyncedLines = ParseUnsyncedLines(payload.PlainLyrics);
        if (syncedLines.Count == 0 && unsyncedLines.Count == 0)
        {
            return null;
        }

        var artists = BuildTrackArtists(info, payload);

        var title = FirstNonEmpty(info.Title, payload.TrackName);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var duration = ResolveTrackDuration(info, payload);

        var track = new AutoTagTrack
        {
            Title = title.Trim(),
            Artists = artists,
            AlbumArtists = artists.ToList(),
            Album = FirstNonEmpty(info.Album, payload.AlbumName),
            Duration = duration,
            Isrc = string.IsNullOrWhiteSpace(info.Isrc) ? null : info.Isrc.Trim(),
            TrackId = payload.Id.HasValue ? payload.Id.Value.ToString(CultureInfo.InvariantCulture) : null
        };

        AddLyricsToTrackPayload(track, syncedLines, unsyncedLines);

        return new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = track
        };
    }

    private static List<string> BuildTrackArtists(AutoTagAudioInfo info, LrclibApiTrack payload)
    {
        var artists = info.Artists
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count > 0)
        {
            return artists;
        }

        var fallbackArtist = FirstNonEmpty(info.Artist, payload.ArtistName);
        if (!string.IsNullOrWhiteSpace(fallbackArtist))
        {
            artists.Add(fallbackArtist.Trim());
        }

        return artists;
    }

    private static TimeSpan? ResolveTrackDuration(AutoTagAudioInfo info, LrclibApiTrack payload)
    {
        if (info.DurationSeconds.HasValue && info.DurationSeconds.Value > 0)
        {
            return TimeSpan.FromSeconds(info.DurationSeconds.Value);
        }

        if (payload.Duration.HasValue && payload.Duration.Value > 0)
        {
            return TimeSpan.FromSeconds(payload.Duration.Value);
        }

        return null;
    }

    private static void AddLyricsToTrackPayload(
        AutoTagTrack track,
        IReadOnlyCollection<string> syncedLines,
        IReadOnlyCollection<string> unsyncedLines)
    {
        if (syncedLines.Count > 0)
        {
            track.Other["syncedLyrics"] = syncedLines.ToList();
        }

        if (unsyncedLines.Count == 0)
        {
            return;
        }

        track.Other["unsyncedLyrics"] = unsyncedLines.ToList();
        if (syncedLines.Count == 0)
        {
            track.Other["lyrics"] = unsyncedLines.ToList();
        }
    }

    private static List<string> ParseSyncedLines(string? syncedLyrics)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(syncedLyrics))
        {
            return lines;
        }

        foreach (var raw in syncedLyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = LrcLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var minutesRaw = match.Groups[1].Value;
            var secondsRaw = match.Groups[2].Value;
            var fractionRaw = match.Groups[3].Value;
            var text = match.Groups[4].Value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!int.TryParse(minutesRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                !int.TryParse(secondsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
                !int.TryParse(fractionRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fraction))
            {
                continue;
            }

            var milliseconds = fractionRaw.Length switch
            {
                1 => fraction * 100,
                2 => fraction * 10,
                _ => fraction
            };

            var timestamp = $"[{minutes:D2}:{seconds:D2}.{milliseconds / 10:D2}]";
            var formatted = $"{timestamp}{text}";
            if (!lines.Contains(formatted, StringComparer.Ordinal))
            {
                lines.Add(formatted);
            }
        }

        return lines;
    }

    private static List<string> ParseUnsyncedLines(string? plainLyrics)
    {
        if (string.IsNullOrWhiteSpace(plainLyrics))
        {
            return new List<string>();
        }

        return plainLyrics
            .Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static LrclibApiTrack? ParseApiTrack(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new LrclibApiTrack
        {
            Id = TryReadInt(source, "id"),
            TrackName = TryReadString(source, "trackName", "track_name", "name"),
            ArtistName = TryReadString(source, "artistName", "artist_name"),
            AlbumName = TryReadString(source, "albumName", "album_name"),
            Duration = TryReadDouble(source, "duration"),
            Instrumental = TryReadBool(source, "instrumental"),
            PlainLyrics = TryReadString(source, "plainLyrics", "plain_lyrics"),
            SyncedLyrics = TryReadString(source, "syncedLyrics", "synced_lyrics")
        };
    }

    private static int? TryReadInt(JsonElement source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!source.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? TryReadDouble(JsonElement source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!source.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryReadBool(JsonElement source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!source.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsedBool))
            {
                return parsedBool;
            }
        }

        return false;
    }

    private static string? TryReadString(JsonElement source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!source.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var parsed = value.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    return parsed.Trim();
                }
            }
        }

        return null;
    }

    private static List<string> BuildTitleCandidates(string title)
    {
        var candidates = new List<string>();
        AddDistinctCandidate(candidates, title);
        AddDistinctCandidate(candidates, SimplifyTrackName(title));
        AddDistinctCandidate(candidates, NormalizeLooseTitle(title));

        return candidates;
    }

    private static List<string> BuildArtistCandidates(string primaryArtist, string originalArtist, IEnumerable<string> allArtists)
    {
        var candidates = new List<string>();
        AddDistinctCandidate(candidates, primaryArtist);
        AddDistinctCandidate(candidates, originalArtist);

        foreach (var artist in allArtists)
        {
            AddDistinctCandidate(candidates, artist);
            AddDistinctCandidate(candidates, NormalizeArtistName(artist));
        }

        return candidates;
    }

    private static void AddDistinctCandidate(List<string> candidates, string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalized);
        }
    }

    private static string SimplifyTrackName(string title)
    {
        var result = title;
        foreach (var pattern in TitleCleanupPatterns)
        {
            result = Regex.Replace(result, pattern, string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        }

        result = result.Trim();
        return string.IsNullOrWhiteSpace(result) ? title : result;
    }

    private static string NormalizeLooseTitle(string value)
    {
        var loose = Regex.Replace(value, @"[^\p{L}\p{N}]+", " ", RegexOptions.None, RegexTimeout);
        loose = Regex.Replace(loose, @"\s{2,}", " ", RegexOptions.None, RegexTimeout).Trim();
        return string.IsNullOrWhiteSpace(loose) ? value : loose;
    }

    private static string NormalizeArtistName(string value)
    {
        var separators = new[] { ", ", "; ", " & ", " feat. ", " ft. ", " featuring ", " with " };
        foreach (var separator in separators)
        {
            var index = value.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                value = value[..index];
                break;
            }
        }

        return value.Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values
            .Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
    }

    private sealed class LrclibApiTrack
    {
        public int? Id { get; init; }
        public string? TrackName { get; init; }
        public string? ArtistName { get; init; }
        public string? AlbumName { get; init; }
        public double? Duration { get; init; }
        public bool Instrumental { get; init; }
        public string? PlainLyrics { get; init; }
        public string? SyncedLyrics { get; init; }
    }
}
