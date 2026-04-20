using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BoomplayMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] TrackIdTagKeys =
    {
        "BOOMPLAY_TRACK_ID",
        "BOOMPLAY_ID",
        "BOOMPLAYID",
        "MUSICID",
        "MUSIC_ID"
    };

    private static readonly string[] UrlTagKeys =
    {
        "URL",
        "WWW",
        "SOURCEURL",
        "SOURCE_URL",
        "BOOMPLAY_URL"
    };

    private static readonly Regex SongIdRegex = CreateRegex(@"(?:^|/)songs/(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MultiWhitespaceRegex = CreateRegex(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TrackNumberPrefixRegex = CreateRegex(
        @"^\s*(?:track\s*no\.?\s*)?\d{1,3}\s*[_\-\.:)]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NoiseSuffixRegex = CreateRegex(
        @"\s*(?:" +
        @"official\s+(?:audio|video|lyrics?|visualizer)|" +
        @"audio|video|lyrics?|visualizer|" +
        @"final|finished|" +
        @"master(?:\s*\d+)?|" +
        @"\.(?:mp3|wav|m4a|aac)" +
        @")\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FeaturingTailRegex = CreateRegex(
        @"\s*(?:\(|\[)?\s*(?:feat\.?|ft\.?|featuring|with|x)\s+.+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistDashPrefixRegex = CreateRegex(
        @"^\s*(?<artist>[^-]{2,80}?)\s*[-–]\s*(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SplitArtistsRegex = CreateRegex(
        @"\s*(?:,|&|/|;|\band\b|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b|\bx\b)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] VariantQualifierMarkers =
    {
        "instrumental",
        "acapella",
        "karaoke",
        "remix",
        "edit",
        "live"
    };

    private readonly BoomplayMetadataService _metadataService;
    private readonly ILogger<BoomplayMatcher> _logger;
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);

    public BoomplayMatcher(
        BoomplayMetadataService metadataService,
        ILogger<BoomplayMatcher> logger)
    {
        _metadataService = metadataService;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        BoomplayConfig boomplayConfig,
        CancellationToken cancellationToken)
    {
        boomplayConfig ??= new BoomplayConfig();
        var searchLimit = Math.Clamp(boomplayConfig.SearchLimit, 5, 30);

        if (boomplayConfig.MatchById)
        {
            var byId = await TryMatchByIdAsync(info, config, cancellationToken);
            if (byId != null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Isrc))
        {
            var byIsrc = await TryMatchByIsrcAsync(info, config, info.Isrc!, searchLimit, cancellationToken);
            if (byIsrc != null)
            {
                return byIsrc;
            }
        }

        var queries = BuildQueries(info);
        if (queries.Count == 0)
        {
            return null;
        }

        return await TryMatchByQueryAsync(info, config, queries, searchLimit, cancellationToken);
    }

    private async Task<AutoTagMatchResult?> TryMatchByIdAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        CancellationToken cancellationToken)
    {
        foreach (var id in CollectCandidateIds(info))
        {
            try
            {
                var track = await _metadataService.GetSongAsync(id, cancellationToken);
                if (track == null || !IsUsableTrack(track))
                {
                    continue;
                }

                if (!IsIdMatchCandidateConsistent(info, track, config))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Boomplay ID candidate rejected due to metadata mismatch. id={TrackId}, inputTitle={InputTitle}, inputArtist={InputArtist}, candidateTitle={CandidateTitle}, candidateArtist={CandidateArtist}",
                            id,
                            info.Title,
                            info.Artist,
                            track.Title,
                            track.Artist);
                    }
                    continue;
                }

                return new AutoTagMatchResult
                {
                    Accuracy = 1.0,
                    Track = ToAutoTagTrack(track)
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Boomplay ID match failed for {TrackId}", id);
                }
            }
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> TryMatchByIsrcAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        string isrc,
        int searchLimit,
        CancellationToken cancellationToken)
    {
        var tracks = await _metadataService.SearchSongsAsync(isrc, searchLimit, cancellationToken);
        var exact = tracks.FirstOrDefault(track =>
            !string.IsNullOrWhiteSpace(track.Isrc)
            && string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase)
            && IsUsableTrack(track));
        if (exact != null)
        {
            return new AutoTagMatchResult
            {
                Accuracy = 1.0,
                Track = ToAutoTagTrack(exact)
            };
        }

        return TrySelectBySimilarityWithFallback(info, config, tracks);
    }

    private async Task<AutoTagMatchResult?> TryMatchByQueryAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        IReadOnlyList<string> queries,
        int searchLimit,
        CancellationToken cancellationToken)
    {
        var allTracks = new List<BoomplayTrackMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var query in queries)
        {
            var tracks = await _metadataService.SearchSongsAsync(query, searchLimit, cancellationToken);
            foreach (var track in tracks)
            {
                TryAddTrack(track, seen, allTracks);
            }
        }

        return TrySelectBySimilarityWithFallback(info, config, allTracks);
    }

    private static List<string> CollectCandidateIds(AutoTagAudioInfo info)
    {
        var candidateIds = new List<string>();
        candidateIds.AddRange(ReadTagValues(info, TrackIdTagKeys)
            .Select(value => TryExtractSongId(value, out var parsed) ? parsed : null)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!));

        foreach (var key in UrlTagKeys)
        {
            if (!info.Tags.TryGetValue(key, out var values) || values.Count == 0)
            {
                continue;
            }

            foreach (var value in values)
            {
                var normalized = Normalize(value);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (BoomplayMetadataService.TryParseBoomplayUrl(normalized, out var type, out var id)
                    && string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(id))
                {
                    candidateIds.Add(id);
                    continue;
                }

                // Only allow raw numeric fallback for explicit Boomplay URL tags.
                if (key.Equals("BOOMPLAY_URL", StringComparison.OrdinalIgnoreCase)
                    && TryExtractSongId(normalized, out var parsed))
                {
                    candidateIds.Add(parsed);
                }
            }
        }

        return candidateIds.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsIdMatchCandidateConsistent(
        AutoTagAudioInfo info,
        BoomplayTrackMetadata track,
        AutoTagMatchingConfig config)
    {
        if (HasConflictingIsrc(info.Isrc, track.Isrc))
        {
            return false;
        }

        var sourceArtists = ResolveSourceArtists(info);
        var hasSourceTitle = !string.IsNullOrWhiteSpace(Normalize(info.Title));
        var hasSourceArtists = sourceArtists.Count > 0;
        if (!hasSourceTitle && !hasSourceArtists)
        {
            // If source tags are too sparse, keep ID-first behavior as last-resort.
            return true;
        }

        if (HasIncompatibleTitleQualifier(info.Title, track.Title))
        {
            return false;
        }

        if (!hasSourceTitle)
        {
            return OneTaggerMatching.MatchArtist(
                sourceArtists,
                ParseArtists(track.Artist),
                Math.Max(config.Strictness, 0.92));
        }

        var validationConfig = new AutoTagMatchingConfig
        {
            Strictness = Math.Max(config.Strictness, 0.92),
            MatchDuration = false,
            MaxDurationDifferenceSeconds = config.MaxDurationDifferenceSeconds,
            MultipleMatches = config.MultipleMatches
        };

        var candidate = new Candidate(track);
        var match = OneTaggerMatching.MatchTrack(
            info,
            new[] { candidate },
            validationConfig,
            new OneTaggerMatching.TrackSelectors<Candidate>(
                c => c.Track.Title,
                _ => null,
                c => ParseArtists(c.Track.Artist),
                c => c.Track.DurationMs > 0 ? TimeSpan.FromMilliseconds(c.Track.DurationMs) : null,
                c => ParseReleaseDate(c.Track.ReleaseDate)),
            matchArtist: hasSourceArtists);
        return match != null;
    }

    private static bool HasConflictingIsrc(string? sourceIsrc, string? candidateIsrc)
    {
        var source = Normalize(sourceIsrc);
        var candidate = Normalize(candidateIsrc);
        return !string.IsNullOrWhiteSpace(source)
               && !string.IsNullOrWhiteSpace(candidate)
               && !string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasIncompatibleTitleQualifier(string? sourceTitle, string? candidateTitle)
    {
        var source = Normalize(sourceTitle);
        var candidate = Normalize(candidateTitle);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        foreach (var marker in VariantQualifierMarkers)
        {
            var sourceHasMarker = ContainsWholeWord(source, marker);
            var candidateHasMarker = ContainsWholeWord(candidate, marker);
            if (sourceHasMarker != candidateHasMarker)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWholeWord(string value, string marker)
    {
        return Regex.IsMatch(
            value,
            $@"\b{Regex.Escape(marker)}\b",
            RegexOptions.IgnoreCase,
            RegexTimeout);
    }

    private static List<string> ResolveSourceArtists(AutoTagAudioInfo info)
    {
        var sourceArtists = info.Artists
            .SelectMany(ParseArtists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceArtists.Count > 0)
        {
            return sourceArtists;
        }

        return ParseArtists(info.Artist);
    }

    private static void TryAddTrack(
        BoomplayTrackMetadata? track,
        HashSet<string> seen,
        List<BoomplayTrackMetadata> allTracks)
    {
        if (track == null)
        {
            return;
        }

        var key = Normalize(track.Id);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = $"{Normalize(track.Title)}::{Normalize(track.Artist)}::{Normalize(track.Album)}";
        }

        if (!seen.Add(key))
        {
            return;
        }

        allTracks.Add(track);
    }

    private static AutoTagMatchResult? TrySelectBySimilarityWithFallback(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        IReadOnlyList<BoomplayTrackMetadata> tracks)
    {
        var strict = TrySelectBySimilarity(info, config, tracks);
        if (strict != null)
        {
            return strict;
        }

        var relaxedConfig = BuildRelaxedConfig(config);
        if (ReferenceEquals(relaxedConfig, config))
        {
            return null;
        }

        return TrySelectBySimilarity(info, relaxedConfig, tracks);
    }

    private static AutoTagMatchResult? TrySelectBySimilarity(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        IReadOnlyList<BoomplayTrackMetadata> tracks)
    {
        var candidates = tracks
            .Where(IsUsableTrack)
            .Select(static track => new Candidate(track))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var match = OneTaggerMatching.MatchTrack(
            info,
            candidates,
            config,
            new OneTaggerMatching.TrackSelectors<Candidate>(
                candidate => candidate.Track.Title,
                _ => null,
                candidate => ParseArtists(candidate.Track.Artist),
                candidate => candidate.Track.DurationMs > 0 ? TimeSpan.FromMilliseconds(candidate.Track.DurationMs) : null,
                candidate => ParseReleaseDate(candidate.Track.ReleaseDate)),
            matchArtist: true);
        if (match == null)
        {
            return null;
        }

        return new AutoTagMatchResult
        {
            Accuracy = match.Accuracy,
            Track = ToAutoTagTrack(match.Track.Track)
        };
    }

    private static AutoTagMatchingConfig BuildRelaxedConfig(AutoTagMatchingConfig config)
    {
        var strictness = Math.Min(config.Strictness, 0.56);
        var matchDuration = false;

        if (Math.Abs(strictness - config.Strictness) < 0.0001
            && config.MatchDuration == matchDuration)
        {
            return config;
        }

        return new AutoTagMatchingConfig
        {
            Strictness = strictness,
            MatchDuration = matchDuration,
            MaxDurationDifferenceSeconds = config.MaxDurationDifferenceSeconds,
            MultipleMatches = config.MultipleMatches
        };
    }

    private static AutoTagTrack ToAutoTagTrack(BoomplayTrackMetadata track)
    {
        var artist = Normalize(track.Artist);
        var artists = string.IsNullOrWhiteSpace(artist) ? new List<string>() : new List<string> { artist };

        var albumArtist = Normalize(track.AlbumArtist);
        var albumArtists = !string.IsNullOrWhiteSpace(albumArtist)
            ? new List<string> { albumArtist }
            : artists.ToList();

        // Prefer stream-tag genres; fall back to HTML-scraped genres.
        var genres = track.Genres
            .Select(Normalize)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        other["source"] = new List<string> { "boomplay" };

        var composer = Normalize(track.Composer);
        if (!string.IsNullOrWhiteSpace(composer))
        {
            other["composer"] = new List<string> { composer };
        }

        var language = Normalize(track.Language);
        if (!string.IsNullOrWhiteSpace(language))
        {
            other["language"] = new List<string> { language };
        }

        return new AutoTagTrack
        {
            Title = Normalize(track.Title),
            Artists = artists,
            AlbumArtists = albumArtists,
            Album = Normalize(track.Album),
            Url = Normalize(track.Url),
            TrackId = Normalize(track.Id),
            Duration = track.DurationMs > 0 ? TimeSpan.FromMilliseconds(track.DurationMs) : null,
            TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null,
            DiscNumber = track.DiscNumber > 0 ? track.DiscNumber : null,
            Isrc = Normalize(track.Isrc),
            ReleaseDate = ParseReleaseDate(track.ReleaseDate),
            Genres = genres,
            Label = Normalize(track.Publisher),
            Bpm = track.Bpm > 0 ? track.Bpm : null,
            Key = Normalize(track.Key),
            Art = Normalize(track.CoverUrl),
            Other = other
        };
    }

    private static List<string> ParseArtists(string? artist)
    {
        var value = Normalize(artist);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        return SplitArtistsRegex
            .Split(value)
            .Select(Normalize)
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateTime? ParseReleaseDate(string? raw)
    {
        var value = Normalize(raw);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        if (value.Length == 4
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            && year >= 1900
            && year <= 2100)
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }

    private static bool IsUsableTrack(BoomplayTrackMetadata track)
    {
        var title = Normalize(track.Title).ToLowerInvariant();
        var artist = Normalize(track.Artist).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }

        return !IsPlaceholder(title) && !IsPlaceholder(artist);
    }

    private static bool IsPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value == "unknown"
               || value == "boomplay music"
               || value == "boomplay"
               || value.StartsWith("unknown ", StringComparison.Ordinal);
    }

    private static List<string> BuildQueries(AutoTagAudioInfo info)
    {
        var normalizedArtist = Normalize(info.Artist);
        if (string.IsNullOrWhiteSpace(normalizedArtist) && info.Artists.Count > 0)
        {
            normalizedArtist = Normalize(info.Artists.FirstOrDefault());
        }

        var sourceTitle = Normalize(info.Title);
        var primaryArtist = StripFeaturing(normalizedArtist);
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            primaryArtist = ParseArtists(normalizedArtist).FirstOrDefault() ?? string.Empty;
        }

        var titleCandidates = BuildTitleCandidates(sourceTitle, primaryArtist);
        var queries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var title in titleCandidates)
        {
            AddQuery(queries, seen, BuildCombinedQuery(primaryArtist, title));
            AddQuery(queries, seen, BuildCombinedQuery(normalizedArtist, title));
            AddQuery(queries, seen, title);
        }

        if (!string.IsNullOrWhiteSpace(primaryArtist))
        {
            AddQuery(queries, seen, primaryArtist);
        }

        return queries;
    }

    private static List<string> BuildTitleCandidates(string sourceTitle, string artist)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            var normalized = NormalizeQueryToken(value);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                candidates.Add(normalized);
            }
        }

        var cleanedTitle = OneTaggerMatching.CleanTitle(sourceTitle);
        var sanitizedTitle = SanitizeTitle(sourceTitle, artist);
        var cleanedSanitized = OneTaggerMatching.CleanTitle(sanitizedTitle);

        Add(cleanedTitle);
        Add(sanitizedTitle);
        Add(cleanedSanitized);
        Add(sourceTitle);

        return candidates;
    }

    private static string SanitizeTitle(string? rawTitle, string? artist)
    {
        var title = Normalize(rawTitle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        title = TrackNumberPrefixRegex.Replace(title, string.Empty).Trim();
        title = NoiseSuffixRegex.Replace(title, string.Empty).Trim();
        title = FeaturingTailRegex.Replace(title, string.Empty).Trim();

        var match = ArtistDashPrefixRegex.Match(title);
        if (match.Success)
        {
            var titleArtist = StripFeaturing(match.Groups["artist"].Value);
            var normalizedArtist = StripFeaturing(artist);
            if (!string.IsNullOrWhiteSpace(titleArtist)
                && !string.IsNullOrWhiteSpace(normalizedArtist)
                && string.Equals(titleArtist, normalizedArtist, StringComparison.OrdinalIgnoreCase))
            {
                title = match.Groups["title"].Value;
            }
        }

        return NormalizeQueryToken(title);
    }

    private static string BuildCombinedQuery(string? artist, string? title)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            builder.Append(artist.Trim());
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(title.Trim());
        }

        return NormalizeQueryToken(builder.ToString());
    }

    private static void AddQuery(List<string> queries, HashSet<string> seen, string? query)
    {
        var normalized = NormalizeQueryToken(query);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (seen.Add(normalized))
        {
            queries.Add(normalized);
        }
    }

    private static string StripFeaturing(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return ReplaceWithTimeout(
            normalized,
            @"\s*(?:feat\.?|ft\.?|featuring|with|x)\s+.*$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
    }

    private static string NormalizeQueryToken(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim(' ', '-', '_', ':', '.');
        return normalized;
    }

    private static IEnumerable<string> ReadTagValues(AutoTagAudioInfo info, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!info.Tags.TryGetValue(key, out var values) || values.Count == 0)
            {
                continue;
            }

            foreach (var normalized in values
                .Select(Normalize)
                .Where(static normalized => !string.IsNullOrWhiteSpace(normalized)))
            {
                yield return normalized!;
            }
        }
    }

    private static bool TryExtractSongId(string? value, out string id)
    {
        id = string.Empty;
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.All(char.IsDigit) && normalized.Length >= 6)
        {
            id = normalized;
            return true;
        }

        var match = SongIdRegex.Match(normalized);
        if (match.Success)
        {
            id = match.Groups["id"].Value;
            return !string.IsNullOrWhiteSpace(id);
        }

        return false;
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
    }

    private sealed record Candidate(BoomplayTrackMetadata Track);
}
