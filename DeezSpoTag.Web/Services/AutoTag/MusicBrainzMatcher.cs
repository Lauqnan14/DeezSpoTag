using System.Globalization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusicBrainzMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex VariantSegmentRegex = CreateVariantRegex(@"(?:\(|\[|\{)(?<value>[^)\]\}]+)(?:\)|\]|\})");
    private static readonly Regex TrailingVariantRegex = CreateVariantRegex(@"\b(?<value>instrumental|radio edit|radio version|club version|club mix|extended(?: mix| version| edit)?|live|acoustic|karaoke|remix|remastered)\b$");
    private static readonly (string Key, Regex Pattern)[] VariantPatterns =
    [
        ("instrumental", CreateVariantRegex(@"\binstrumental\b")),
        ("radio", CreateVariantRegex(@"\bradio\s+(edit|version|mix)\b")),
        ("club", CreateVariantRegex(@"\bclub\s+(version|mix|edit)\b")),
        ("extended", CreateVariantRegex(@"\bextended(?:\s+(mix|version|edit))?\b")),
        ("live", CreateVariantRegex(@"\blive\b")),
        ("acoustic", CreateVariantRegex(@"\bacoustic\b")),
        ("karaoke", CreateVariantRegex(@"\bkaraoke\b")),
        ("remix", CreateVariantRegex(@"\bremix(ed)?\b")),
        ("remaster", CreateVariantRegex(@"\bremaster(ed)?\b"))
    ];

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
            var byIdResult = await TryMatchRecordingIdAsync(info, matchingConfig, preferences, cancellationToken);
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
            var results = await _client.SearchAsync(queries[queryIndex], resolvedConfig.SearchLimit, cancellationToken);
            if (results?.Recordings is null || results.Recordings.Count == 0)
            {
                continue;
            }

            var tracks = results.Recordings
                .Take(resolvedConfig.SearchLimit)
                .Select(r => ToTrack(r, preferences))
                .ToList();
            var result = await TryBuildMatchResultAsync(info, tracks, matchingConfig, preferences, cancellationToken);
            if (result != null)
            {
                return result;
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
        AutoTagMatchingConfig matchingConfig,
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
                await ExtendTrackAsync(info, track, preferences, cancellationToken);
                if (!IsCandidateCompatibleWithSource(info, track, matchingConfig))
                {
                    continue;
                }

                return new AutoTagMatchResult
                {
                    Accuracy = 1.0,
                    Track = ToAutoTagTrack(track),
                    MatchStrategy = "id"
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "MusicBrainz ID lookup failed for {RecordingId}", recordingId);
                }
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
        var results = await _client.SearchAsync(query, config.SearchLimit, cancellationToken);
        if (results?.Recordings is null)
        {
            return null;
        }

        var tracks = results.Recordings
            .Take(config.SearchLimit)
            .Select(r => ToTrack(r, preferences))
            .ToList();
        return await TryBuildMatchResultAsync(info, tracks, matchingConfig, preferences, cancellationToken);
    }

    private async Task<AutoTagMatchResult?> TryBuildMatchResultAsync(
        AutoTagAudioInfo info,
        List<MusicBrainzTrack> tracks,
        AutoTagMatchingConfig matchingConfig,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
    {
        var match = MatchTracks(info, tracks, matchingConfig);
        if (match == null)
        {
            return null;
        }

        await ExtendTrackAsync(info, match.Track, preferences, cancellationToken);
        if (!IsCandidateCompatibleWithSource(info, match.Track, matchingConfig))
        {
            return null;
        }

        return new AutoTagMatchResult
        {
            Accuracy = match.Accuracy,
            Track = ToAutoTagTrack(match.Track)
        };
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
            RecordingId = recording.Id,
            ArtistId = recording.ArtistCredit?.Select(credit => credit.Artist.Id).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)),
            AlbumArtistId = release?.ArtistCredit?.Select(credit => credit.Artist.Id).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)),
            AlbumId = release?.Id,
            Duration = recording.Length.HasValue ? TimeSpan.FromMilliseconds(recording.Length.Value) : TimeSpan.Zero,
            ReleaseYear = ParseYear(recording.FirstReleaseDate),
            ReleaseDate = ParseDate(recording.FirstReleaseDate),
            Isrc = recording.Isrcs?.FirstOrDefault()
        };

        AddOtherValue(track.Other, "MUSICBRAINZ_RECORDINGID", recording.Id);
        AddOtherValues(track.Other, "MUSICBRAINZ_ARTISTID", recording.ArtistCredit?.Select(credit => credit.Artist.Id));
        AddOtherValues(track.Other, "MUSICBRAINZ_ALBUMARTISTID", release?.ArtistCredit?.Select(credit => credit.Artist.Id));
        AddOtherValues(track.Other, "ISRCS", recording.Isrcs);
        AddOtherValue(track.Other, "ORIGINALDATE", recording.FirstReleaseDate);

        return track;
    }

    private async Task ExtendTrackAsync(
        AutoTagAudioInfo info,
        MusicBrainzTrack track,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
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

            var release = SelectBestRelease(releases.Releases, track.ReleaseDate, preferences, track.ReleaseId, info.Album);
            if (release == null)
            {
                return;
            }

            track.Album = release.Title;
            track.ReleaseId = release.Id;
            track.AlbumId = release.Id;
            track.ReleaseDate = ParseDate(release.Date) ?? track.ReleaseDate;
            track.AlbumArtists = release.ArtistCredit?.Select(a => a.Name).ToList() ?? track.AlbumArtists;
            track.AlbumArtistId = release.ArtistCredit?.Select(credit => credit.Artist.Id).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? track.AlbumArtistId;
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
            track.DiscTotal = release.Media.Count;
        }

        track.Genres = release.Genres.Select(genre => genre.Name).ToList();
        if (release.ReleaseGroup != null)
        {
            AddOtherValue(track.Other, "MUSICBRAINZ_RELEASEGROUPID", release.ReleaseGroup.Id);
            track.ReleaseGroupId = release.ReleaseGroup.Id;
            track.ReleaseType = AutoTagReleaseCategory.Resolve(
                release.ReleaseGroup.PrimaryType,
                release.ReleaseGroup.SecondaryTypes,
                track.TrackTotal);
            AddOtherValue(track.Other, "RELEASETYPE", track.ReleaseType);
        }

        if (!string.IsNullOrWhiteSpace(release.Barcode))
        {
            AddOtherValue(track.Other, "BARCODE", release.Barcode);
            track.Barcode = release.Barcode;
        }

        AddOtherValue(track.Other, "MUSICBRAINZ_ALBUMID", release.Id);
        AddOtherValue(track.Other, "RELEASESTATUS", release.Status);
        AddOtherValue(track.Other, "RELEASECOUNTRY", release.Country);
        AddOtherValue(track.Other, "RELEASEDATE", release.Date);
        AddOtherValues(track.Other, "MEDIA", release.Media.Select(media => media.Format));
        track.AlbumId = release.Id;
        track.ReleaseStatus = release.Status;
        track.ReleaseCountry = release.Country;
        track.Media = release.Media
            .Select(media => media.Format)
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Select(format => format!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static Release? SelectBestRelease(
        List<Release> releases,
        DateTime? preferredDate,
        MusicBrainzPreferences preferences,
        string? preferredReleaseId,
        string? preferredAlbum)
    {
        if (!string.IsNullOrWhiteSpace(preferredReleaseId))
        {
            var exact = releases.FirstOrDefault(release =>
                string.Equals(release.Id, preferredReleaseId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }
        }

        var preferredYear = preferredDate?.Year;
        return releases
            .OrderByDescending(r => ScoreRelease(r, preferredYear, preferences, preferredAlbum))
            .ThenBy(r => r.Date ?? "9999-99-99", StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int ScoreRelease(Release release, int? preferredYear, MusicBrainzPreferences preferences, string? preferredAlbum)
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

        if (!string.IsNullOrWhiteSpace(preferredAlbum)
            && !string.IsNullOrWhiteSpace(release.Title))
        {
            var albumScore = AutoTagSimilarity.ComputeScore(
                AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(preferredAlbum)),
                AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(release.Title)));
            if (albumScore >= 0.90d)
            {
                score += 8;
            }
            else if (albumScore < 0.55d)
            {
                score -= 6;
            }
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

    internal static bool IsVariantCompatible(string? sourceTitle, string? candidateTitle)
    {
        var sourceMarkers = ExtractVariantMarkers(sourceTitle);
        var candidateMarkers = ExtractVariantMarkers(candidateTitle);
        return sourceMarkers.SetEquals(candidateMarkers);
    }

    private static bool IsCandidateCompatibleWithSource(
        AutoTagAudioInfo info,
        MusicBrainzTrack track,
        AutoTagMatchingConfig config)
    {
        if (!IsVariantCompatible(info.Title, track.Title))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(info.Title) && !string.IsNullOrWhiteSpace(track.Title))
        {
            if (!HasCompatibleTitleIdentity(info.Title, track.Title, config))
            {
                return false;
            }
        }

        var sourceArtists = info.Artists.Count > 0
            ? info.Artists
            : string.IsNullOrWhiteSpace(info.Artist) ? [] : new List<string> { info.Artist };
        var candidateArtists = track.Artists.Count > 0 ? track.Artists : track.AlbumArtists;
        if (sourceArtists.Count > 0
            && candidateArtists.Count > 0
            && !OneTaggerMatching.MatchArtist(sourceArtists, candidateArtists, Math.Clamp(config.Strictness, 0.65d, 0.98d)))
        {
            return false;
        }

        if (info.DurationSeconds is > 0
            && track.Duration > TimeSpan.Zero
            && Math.Abs(info.DurationSeconds.Value - (int)Math.Round(track.Duration.TotalSeconds)) > Math.Max(config.MaxDurationDifferenceSeconds, 45))
        {
            return false;
        }

        return true;
    }

    private static bool HasCompatibleTitleIdentity(
        string sourceTitle,
        string candidateTitle,
        AutoTagMatchingConfig config)
    {
        _ = config;
        return TrackTitleMatcher.HasCompatibleTitleIdentity(sourceTitle, candidateTitle);
    }

    private static HashSet<string> ExtractVariantMarkers(string? title)
    {
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(title))
        {
            return markers;
        }

        foreach (Match match in VariantSegmentRegex.Matches(title))
        {
            AddVariantMarkers(markers, match.Groups["value"].Value);
        }

        var trailing = TrailingVariantRegex.Match(title);
        if (trailing.Success)
        {
            AddVariantMarkers(markers, trailing.Groups["value"].Value);
        }

        return markers;
    }

    private static void AddVariantMarkers(HashSet<string> markers, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var (key, pattern) in VariantPatterns)
        {
            if (pattern.IsMatch(value))
            {
                markers.Add(key);
            }
        }
    }

    private static void AddOtherValue(List<(string Key, List<string> Values)> other, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddOtherValues(other, key, [value]);
    }

    private static void AddOtherValues(List<(string Key, List<string> Values)> other, string key, IEnumerable<string?>? values)
    {
        if (values == null)
        {
            return;
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        var index = other.FindIndex(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            other.Add((key, normalized));
            return;
        }

        var existing = other[index].Values.ToList();
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        existing.AddRange(normalized.Where(seen.Add));
        other[index] = (other[index].Key, existing);
    }

    private static Dictionary<string, List<string>> BuildOtherDictionary(MusicBrainzTrack track)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in track.Other)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Values.Count == 0)
            {
                continue;
            }

            if (!result.TryGetValue(item.Key, out var values))
            {
                result[item.Key] = item.Values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                continue;
            }

            var seen = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            values.AddRange(item.Values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(seen.Add));
        }

        return result;
    }

    private static Regex CreateVariantRegex(string pattern)
        => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static List<string> GetRecordingIds(AutoTagAudioInfo info)
    {
        var result = new List<string>();
        var keys = new[]
        {
            "MUSICBRAINZ_RECORDING_ID",
            "MUSICBRAINZ_RECORDINGID",
            "MUSICBRAINZ_TRACK_ID",
            "MUSICBRAINZ_TRACKID",
            "RECORDINGID"
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
            RecordingId = track.RecordingId,
            ArtistId = track.ArtistId,
            AlbumArtistId = track.AlbumArtistId,
            ReleaseGroupId = track.ReleaseGroupId,
            AlbumId = track.AlbumId,
            ReleaseStatus = track.ReleaseStatus,
            ReleaseCountry = track.ReleaseCountry,
            Barcode = track.Barcode,
            Media = track.Media.ToList(),
            Duration = track.Duration,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal,
            ReleaseType = AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal),
            DiscNumber = track.DiscNumber,
            DiscTotal = track.DiscTotal,
            Isrc = track.Isrc,
            Label = track.Label,
            CatalogNumber = track.CatalogNumber,
            Genres = track.Genres.ToList(),
            Art = track.Art,
            ReleaseDate = track.ReleaseDate,
            Other = BuildOtherDictionary(track)
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
