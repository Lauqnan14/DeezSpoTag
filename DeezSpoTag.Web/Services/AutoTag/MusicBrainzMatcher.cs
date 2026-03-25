using System.Globalization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusicBrainzMatcher
{
    private readonly MusicBrainzClient _client;
    private readonly ILogger<MusicBrainzMatcher> _logger;

    public MusicBrainzMatcher(MusicBrainzClient client, ILogger<MusicBrainzMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        MusicBrainzMatchConfig config,
        CancellationToken cancellationToken)
    {
        var resolvedConfig = NormalizeConfig(config);
        var preferences = MusicBrainzPreferences.FromConfig(resolvedConfig);

        if (resolvedConfig.MatchById)
        {
            var byIdResult = await TryMatchRecordingIdAsync(info, preferences, cancellationToken);
            if (byIdResult != null)
            {
                return byIdResult;
            }
        }

        if (resolvedConfig.UseIsrcFirst && !string.IsNullOrWhiteSpace(info.Isrc))
        {
            var isrcResult = await TryMatchIsrcAsync(info, matchingConfig, resolvedConfig, preferences, cancellationToken);
            if (isrcResult != null)
            {
                return isrcResult;
            }
        }

        var queries = BuildQueries(info).ToList();
        for (var queryIndex = 0; queryIndex < queries.Count; queryIndex++)
        {
            var results = await _client.SearchAsync(queries[queryIndex], cancellationToken);
            if (results?.Recordings is null || results.Recordings.Count == 0)
            {
                continue;
            }

            var tracks = results.Recordings
                .Take(resolvedConfig.SearchLimit)
                .Select(r => ToTrack(r, preferences))
                .ToList();
            var match = MatchTracks(info, tracks, matchingConfig);
            if (match != null)
            {
                await ExtendTrackAsync(match.Track, preferences, cancellationToken);
                return new AutoTagMatchResult
                {
                    Accuracy = match.Accuracy,
                    Track = ToAutoTagTrack(match.Track)
                };
            }
        }

        if (!resolvedConfig.UseIsrcFirst && !string.IsNullOrWhiteSpace(info.Isrc))
        {
            return await TryMatchIsrcAsync(info, matchingConfig, resolvedConfig, preferences, cancellationToken);
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> TryMatchRecordingIdAsync(
        AutoTagAudioInfo info,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
    {
        foreach (var recordingId in GetRecordingIds(info))
        {
            try
            {
                var recording = await _client.GetRecordingAsync(recordingId, cancellationToken);
                if (recording == null || string.IsNullOrWhiteSpace(recording.Id))
                {
                    continue;
                }

                var track = ToTrack(recording, preferences);
                await ExtendTrackAsync(track, preferences, cancellationToken);
                return new AutoTagMatchResult
                {
                    Accuracy = 1.0,
                    Track = ToAutoTagTrack(track)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "MusicBrainz ID lookup failed for {RecordingId}", recordingId);
            }
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> TryMatchIsrcAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        MusicBrainzMatchConfig config,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
    {
        var query = $"isrc:{info.Isrc}";
        var results = await _client.SearchAsync(query, cancellationToken);
        if (results?.Recordings is null)
        {
            return null;
        }

        var tracks = results.Recordings
            .Take(config.SearchLimit)
            .Select(r => ToTrack(r, preferences))
            .ToList();
        var match = MatchTracks(info, tracks, matchingConfig);
        if (match != null)
        {
            await ExtendTrackAsync(match.Track, preferences, cancellationToken);
            return new AutoTagMatchResult
            {
                Accuracy = match.Accuracy,
                Track = ToAutoTagTrack(match.Track)
            };
        }
        return null;
    }

    private static List<string> BuildQueries(AutoTagAudioInfo info)
    {
        var title = OneTaggerMatching.CleanTitle(info.Title);
        var artist = OneTaggerMatching.CleanArtistSearching(info.Artist);
        var titleEscaped = EscapeQuery(title);
        var artistEscaped = EscapeQuery(artist);

        var queries = new List<string>
        {
            $"{artist} {title}~",
            $"recording:\"{titleEscaped}\" AND artist:\"{artistEscaped}\"",
            $"recording:\"{titleEscaped}\"",
            $"\"{titleEscaped}\" AND artist:\"{artistEscaped}\""
        };

        return queries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string EscapeQuery(string input) => input.Replace("\"", "\\\"");

    private static MatchCandidate? MatchTracks(AutoTagAudioInfo info, List<MusicBrainzTrack> tracks, AutoTagMatchingConfig config)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<MusicBrainzTrack>(
                track => track.Title,
                _ => null,
                track => track.Artists.Count > 0 ? track.Artists : track.AlbumArtists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);

        return match == null ? null : new MatchCandidate(match.Accuracy, match.Track);
    }

    private static MusicBrainzTrack ToTrack(Recording recording, MusicBrainzPreferences preferences)
    {
        var release = SelectBestReleaseSmall(recording.Releases ?? new List<ReleaseSmall>(), recording.FirstReleaseDate, preferences);
        var track = new MusicBrainzTrack
        {
            Title = recording.Title,
            Artists = recording.ArtistCredit?.Select(a => a.Name).ToList() ?? new List<string>(),
            AlbumArtists = release?.ArtistCredit?.Select(a => a.Name).ToList() ?? new List<string>(),
            Album = release?.Title,
            Url = $"https://musicbrainz.org/recording/{recording.Id}",
            TrackId = recording.Id,
            ReleaseId = release?.Id ?? string.Empty,
            Duration = recording.Length.HasValue ? TimeSpan.FromMilliseconds(recording.Length.Value) : TimeSpan.Zero,
            ReleaseYear = ParseYear(recording.FirstReleaseDate),
            ReleaseDate = ParseDate(recording.FirstReleaseDate),
            Isrc = recording.Isrcs?.FirstOrDefault()
        };

        return track;
    }

    private async Task ExtendTrackAsync(MusicBrainzTrack track, MusicBrainzPreferences preferences, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.TrackId))
        {
            return;
        }

        try
        {
            var releases = await _client.GetReleasesAsync(track.TrackId!, cancellationToken);
            if (releases == null)
            {
                return;
            }

            var release = SelectBestRelease(releases.Releases, track.ReleaseDate, preferences);
            if (release == null)
            {
                return;
            }

            track.Album = release.Title;
            track.ReleaseId = release.Id;
            ApplyCoverArt(track, release);
            ApplyLabelInfo(track, release);
            ApplyTrackPosition(track, release);
            ApplyReleaseMetadata(track, release);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to extend MusicBrainz track.");
        }
    }

    private static void ApplyCoverArt(MusicBrainzTrack track, Release release)
    {
        if (release.CoverArtArchive.Front || release.CoverArtArchive.Back)
        {
            var side = release.CoverArtArchive.Front ? "front" : "back";
            track.Art = $"https://coverartarchive.org/release/{release.Id}/{side}";
            return;
        }

        if (release.ReleaseGroup != null)
        {
            track.Art = $"https://coverartarchive.org/release-group/{release.ReleaseGroup.Id}/front";
        }
    }

    private static void ApplyLabelInfo(MusicBrainzTrack track, Release release)
    {
        var label = release.LabelInfo?.FirstOrDefault();
        if (label?.Label != null)
        {
            track.Label = label.Label.Name;
        }

        track.CatalogNumber = label?.CatalogNumber;
    }

    private static void ApplyTrackPosition(MusicBrainzTrack track, Release release)
    {
        var trackEntry = release.Media
            .SelectMany(media => media.Tracks.Select(trackInfo => new { Media = media, Track = trackInfo }))
            .FirstOrDefault(item => item.Track.Recording.Id == track.TrackId);
        if (trackEntry == null)
        {
            return;
        }

        track.TrackNumber = trackEntry.Track.Position;
        if (trackEntry.Media.Position.HasValue)
        {
            track.DiscNumber = trackEntry.Media.Position.Value;
        }

        var total = trackEntry.Media.TrackCount ?? trackEntry.Media.Tracks.Count;
        if (total > 0)
        {
            track.TrackTotal = total;
        }
    }

    private static void ApplyReleaseMetadata(MusicBrainzTrack track, Release release)
    {
        if (release.Media.Count > 0)
        {
            track.DiscNumber ??= 1;
        }

        track.Genres = release.Genres.Select(genre => genre.Name).ToList();
        if (release.ReleaseGroup != null)
        {
            track.Other.Add(("MUSICBRAINZ_RELEASEGROUPID", new List<string> { release.ReleaseGroup.Id }));
        }

        if (!string.IsNullOrWhiteSpace(release.Barcode))
        {
            track.Other.Add(("BARCODE", new List<string> { release.Barcode! }));
        }
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }
        return int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        if (DateTime.TryParseExact(date, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed;
        }
        return DateTime.TryParseExact(date, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            ? parsed
            : null;
    }

    private static bool IsCompilation(List<string>? types)
    {
        return types != null && types.Any(t => string.Equals(t, "compilation", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseSmall? SelectBestReleaseSmall(List<ReleaseSmall> releases, string? preferredDate, MusicBrainzPreferences preferences)
    {
        var preferredYear = ParseYear(preferredDate);
        return releases
            .OrderByDescending(r => ScoreReleaseSmall(r, preferredYear, preferences))
            .ThenBy(r => r.Date ?? "9999-99-99", StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int ScoreReleaseSmall(ReleaseSmall release, int? preferredYear, MusicBrainzPreferences preferences)
    {
        return ScoreReleaseCommon(
            release.ReleaseGroup?.SecondaryTypes,
            release.Status,
            release.ReleaseGroup?.PrimaryType,
            release.Country,
            release.Date,
            preferredYear,
            preferences);
    }

    private static Release? SelectBestRelease(List<Release> releases, DateTime? preferredDate, MusicBrainzPreferences preferences)
    {
        var preferredYear = preferredDate?.Year;
        return releases
            .OrderByDescending(r => ScoreRelease(r, preferredYear, preferences))
            .ThenBy(r => r.Date ?? "9999-99-99", StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int ScoreRelease(Release release, int? preferredYear, MusicBrainzPreferences preferences)
    {
        var score = ScoreReleaseCommon(
            release.ReleaseGroup?.SecondaryTypes,
            release.Status,
            release.ReleaseGroup?.PrimaryType,
            release.Country,
            release.Date,
            preferredYear,
            preferences);
        score += ScoreFormatRank(release.Media, preferences.PreferredFormats) * preferences.FormatWeight;

        var totalTracks = release.Media.Sum(m => m.TrackCount ?? m.Tracks.Count);
        if (totalTracks > 0)
        {
            score += 2;
        }
        return score;
    }

    private static int ScoreReleaseCommon(
        List<string>? secondaryTypes,
        string? status,
        string? primaryType,
        string? country,
        string? releaseDate,
        int? preferredYear,
        MusicBrainzPreferences preferences)
    {
        var score = 0;
        if (preferences.ExcludeCompilations)
        {
            score += IsCompilation(secondaryTypes)
                ? -preferences.CompilationPenaltyWeight
                : Math.Max(1, preferences.CompilationPenaltyWeight / 2);
        }

        if (preferences.PreferOfficial)
        {
            score += string.Equals(status, "Official", StringComparison.OrdinalIgnoreCase)
                ? preferences.OfficialWeight
                : PenaltyFromWeight(preferences.OfficialWeight);
        }

        if (preferences.PreferredPrimaryType != null)
        {
            var resolvedPrimaryType = primaryType ?? string.Empty;
            score += string.Equals(resolvedPrimaryType, preferences.PreferredPrimaryType, StringComparison.OrdinalIgnoreCase)
                ? preferences.PrimaryTypeWeight
                : PenaltyFromWeight(preferences.PrimaryTypeWeight);
        }

        score += ScoreCountryRank(country, preferences.PreferredCountries) * preferences.CountryWeight;

        var year = ParseYear(releaseDate);
        if (preferences.PreferReleaseYear && preferredYear.HasValue && year.HasValue)
        {
            score -= Math.Abs(preferredYear.Value - year.Value) * preferences.YearWeight;
        }

        return score;
    }

    private static int PenaltyFromWeight(int weight)
    {
        if (weight <= 0)
        {
            return 0;
        }

        return -Math.Max(1, weight / 3);
    }

    private static int ScoreCountryRank(string? releaseCountry, IReadOnlyList<string> preferredCountries)
    {
        if (preferredCountries.Count == 0 || string.IsNullOrWhiteSpace(releaseCountry))
        {
            return 0;
        }

        for (var index = 0; index < preferredCountries.Count; index++)
        {
            if (string.Equals(preferredCountries[index], releaseCountry, StringComparison.OrdinalIgnoreCase))
            {
                return (preferredCountries.Count - index) * 3;
            }
        }

        return -1;
    }

    private static int ScoreFormatRank(List<ReleaseMedia> media, IReadOnlyList<string> preferredFormats)
    {
        if (preferredFormats.Count == 0)
        {
            return 0;
        }

        var formats = media
            .Select(m => m.Format)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (formats.Count == 0)
        {
            return -1;
        }

        var best = int.MinValue;
        foreach (var format in formats)
        {
            var score = -1;
            for (var index = 0; index < preferredFormats.Count; index++)
            {
                if (string.Equals(preferredFormats[index], format, StringComparison.OrdinalIgnoreCase))
                {
                    score = (preferredFormats.Count - index) * 2;
                    break;
                }
            }

            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    private static List<string> GetRecordingIds(AutoTagAudioInfo info)
    {
        var result = new List<string>();
        var keys = new[]
        {
            "MUSICBRAINZ_RECORDING_ID",
            "MUSICBRAINZ_RECORDINGID",
            "MUSICBRAINZ_TRACK_ID",
            "MUSICBRAINZ_TRACKID"
        };

        foreach (var key in keys)
        {
            if (!info.Tags.TryGetValue(key, out var values) || values == null)
            {
                continue;
            }

            foreach (var normalized in values
                .Select(static value => value?.Trim())
                .Where(static normalized => !string.IsNullOrWhiteSpace(normalized))
                .Where(normalized => Guid.TryParse(normalized, out _) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase)))
            {
                result.Add(normalized!);
            }
        }

        return result;
    }

    private static MusicBrainzMatchConfig NormalizeConfig(MusicBrainzMatchConfig config)
    {
        var resolved = config ?? new MusicBrainzMatchConfig();
        if (resolved.SearchLimit < 5)
        {
            resolved.SearchLimit = 5;
        }
        else if (resolved.SearchLimit > 100)
        {
            resolved.SearchLimit = 100;
        }

        resolved.OfficialWeight = ClampWeight(resolved.OfficialWeight, 0, 30);
        resolved.CompilationPenaltyWeight = ClampWeight(resolved.CompilationPenaltyWeight, 0, 40);
        resolved.PrimaryTypeWeight = ClampWeight(resolved.PrimaryTypeWeight, 0, 30);
        resolved.CountryWeight = ClampWeight(resolved.CountryWeight, 0, 20);
        resolved.FormatWeight = ClampWeight(resolved.FormatWeight, 0, 20);
        resolved.YearWeight = ClampWeight(resolved.YearWeight, 0, 10);

        return resolved;
    }

    private static int ClampWeight(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }

    private static AutoTagTrack ToAutoTagTrack(MusicBrainzTrack track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Url = string.IsNullOrWhiteSpace(track.Url) ? null : track.Url,
            TrackId = track.TrackId,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal,
            DiscNumber = track.DiscNumber,
            Isrc = track.Isrc,
            Label = track.Label,
            CatalogNumber = track.CatalogNumber,
            Genres = track.Genres.ToList(),
            Art = track.Art,
            ReleaseDate = track.ReleaseDate,
            Other = track.Other.ToDictionary(k => k.Key, v => v.Values)
        };
    }

    private sealed record MatchCandidate(double Accuracy, MusicBrainzTrack Track);

    private sealed class MusicBrainzPreferences
    {
        private MusicBrainzPreferences()
        {
        }

        public bool PreferOfficial { get; init; }
        public bool ExcludeCompilations { get; init; }
        public bool PreferReleaseYear { get; init; }
        public string? PreferredPrimaryType { get; init; }
        public IReadOnlyList<string> PreferredCountries { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> PreferredFormats { get; init; } = Array.Empty<string>();
        public int OfficialWeight { get; init; }
        public int CompilationPenaltyWeight { get; init; }
        public int PrimaryTypeWeight { get; init; }
        public int CountryWeight { get; init; }
        public int FormatWeight { get; init; }
        public int YearWeight { get; init; }

        public static MusicBrainzPreferences FromConfig(MusicBrainzMatchConfig config)
        {
            var preferredType = string.IsNullOrWhiteSpace(config.PreferredPrimaryType)
                ? null
                : config.PreferredPrimaryType.Trim();
            if (string.Equals(preferredType, "Any", StringComparison.OrdinalIgnoreCase))
            {
                preferredType = null;
            }

            return new MusicBrainzPreferences
            {
                PreferOfficial = config.PreferOfficial,
                ExcludeCompilations = config.ExcludeCompilations,
                PreferReleaseYear = config.PreferReleaseYear,
                PreferredPrimaryType = preferredType,
                PreferredCountries = ParseCsv(config.PreferredReleaseCountries),
                PreferredFormats = ParseCsv(config.PreferredMediaFormats),
                OfficialWeight = config.OfficialWeight,
                CompilationPenaltyWeight = config.CompilationPenaltyWeight,
                PrimaryTypeWeight = config.PrimaryTypeWeight,
                CountryWeight = config.CountryWeight,
                FormatWeight = config.FormatWeight,
                YearWeight = config.YearWeight
            };
        }

        private static IReadOnlyList<string> ParseCsv(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
