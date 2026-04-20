using System.Globalization;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ShazamMatcher
{
    private readonly ShazamRecognitionService _recognitionService;
    private readonly ILogger<ShazamMatcher> _logger;

    public ShazamMatcher(ShazamRecognitionService recognitionService, ILogger<ShazamMatcher> logger)
    {
        _recognitionService = recognitionService;
        _logger = logger;
    }

    public Task<AutoTagMatchResult?> MatchAsync(
        string filePath,
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        ShazamMatchConfig config,
        IDictionary<string, ShazamRecognitionInfo?> cache,
        CancellationToken cancellationToken)
    {
        if (!_recognitionService.IsAvailable)
        {
            return Task.FromResult<AutoTagMatchResult?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!cache.TryGetValue(filePath, out var recognized))
        {
            recognized = _recognitionService.Recognize(filePath, cancellationToken);
            cache[filePath] = recognized;
        }

        if (recognized == null || !recognized.HasCoreMetadata)
        {
            return Task.FromResult<AutoTagMatchResult?>(null);
        }

        var resolvedConfig = config ?? new ShazamMatchConfig();
        var strictness = Math.Clamp(matchingConfig.Strictness, 0d, 1d);
        var minTitleSimilarity = Math.Max(NormalizeThreshold(resolvedConfig.MinTitleSimilarity), strictness * 0.9d);
        var minArtistSimilarity = Math.Max(NormalizeThreshold(resolvedConfig.MinArtistSimilarity), strictness * 0.65d);
        var titleSimilarity = ComputeTitleSimilarity(info.Title, recognized.Title);
        var artistSimilarity = ComputeArtistSimilarity(info, recognized);
        var durationDiffSeconds = ComputeDurationDiffSeconds(info.DurationSeconds, recognized.DurationMs);
        var durationSimilarity = ComputeDurationSimilarity(durationDiffSeconds, resolvedConfig.MaxDurationDeltaSeconds);
        if (!PassesQualityGuards(
                titleSimilarity,
                artistSimilarity,
                durationDiffSeconds,
                minTitleSimilarity,
                minArtistSimilarity,
                resolvedConfig.MaxDurationDeltaSeconds))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Rejected Shazam fingerprint match for {File}: titleSim={TitleSim:0.000} artistSim={ArtistSim:0.000} durationDiff={DurationDiff}s thresholds(title>={MinTitle:0.000}, artist>={MinArtist:0.000}, maxDurationDiff={MaxDiff}s)",
                    filePath,
                    titleSimilarity,
                    artistSimilarity,
                    durationDiffSeconds,
                    minTitleSimilarity,
                    minArtistSimilarity,
                    resolvedConfig.MaxDurationDeltaSeconds);
            }

            return Task.FromResult<AutoTagMatchResult?>(null);
        }

        var artists = ResolveArtists(recognized, info);
        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(info.Artist))
        {
            artists.Add(info.Artist);
        }

        var track = new AutoTagTrack
        {
            Title = recognized.Title ?? info.Title,
            Artists = artists,
            Album = ResolveAlbum(recognized, info, resolvedConfig),
            Duration = ResolveDuration(recognized, info),
            Isrc = FirstNonEmpty(recognized.Isrc, info.Isrc),
            Url = recognized.Url,
            TrackId = recognized.TrackId,
            ReleaseId = recognized.TrackId,
            TrackNumber = recognized.TrackNumber,
            DiscNumber = recognized.DiscNumber,
            Explicit = recognized.Explicit,
            Key = recognized.Key
        };

        ApplyConfiguredMetadata(track, recognized, resolvedConfig);
        AddShazamOtherFields(track, recognized);
        MergeAdditionalTags(track, recognized.Tags);
        AddFingerprintAuditFields(track, titleSimilarity, artistSimilarity, durationDiffSeconds, durationSimilarity);

        var compositeAccuracy = durationSimilarity.HasValue
            ? Math.Clamp((titleSimilarity * 0.45d) + (artistSimilarity * 0.20d) + (durationSimilarity.Value * 0.35d), 0d, 1d)
            : Math.Clamp((titleSimilarity * 0.68d) + (artistSimilarity * 0.32d), 0d, 1d);

        return Task.FromResult<AutoTagMatchResult?>(new AutoTagMatchResult
        {
            Accuracy = compositeAccuracy,
            Track = track
        });
    }

    private static void AddFingerprintAuditFields(
        AutoTagTrack track,
        double titleSimilarity,
        double artistSimilarity,
        int? durationDiffSeconds,
        double? durationSimilarity)
    {
        track.Other["SHAZAM_MATCH_STRATEGY"] = new List<string> { "FINGERPRINT" };
        track.Other["SHAZAM_TITLE_SIMILARITY"] = new List<string> { titleSimilarity.ToString("0.000", CultureInfo.InvariantCulture) };
        track.Other["SHAZAM_ARTIST_SIMILARITY"] = new List<string> { artistSimilarity.ToString("0.000", CultureInfo.InvariantCulture) };
        if (durationDiffSeconds.HasValue)
        {
            track.Other["SHAZAM_DURATION_DIFF_SECONDS"] = new List<string> { durationDiffSeconds.Value.ToString(CultureInfo.InvariantCulture) };
        }
        if (durationSimilarity.HasValue)
        {
            track.Other["SHAZAM_DURATION_SIMILARITY"] = new List<string> { durationSimilarity.Value.ToString("0.000", CultureInfo.InvariantCulture) };
        }
    }

    private static bool PassesQualityGuards(
        double titleSimilarity,
        double artistSimilarity,
        int? durationDiffSeconds,
        double minTitleSimilarity,
        double minArtistSimilarity,
        int maxDurationDeltaSeconds)
    {
        if (titleSimilarity < minTitleSimilarity)
        {
            return false;
        }

        if (artistSimilarity < minArtistSimilarity)
        {
            return false;
        }

        if (durationDiffSeconds.HasValue
            && durationDiffSeconds.Value > maxDurationDeltaSeconds)
        {
            return false;
        }

        return true;
    }

    private static double NormalizeThreshold(double raw)
    {
        var value = raw > 1d ? raw / 100d : raw;
        return Math.Clamp(value, 0d, 1d);
    }

    private static double ComputeTitleSimilarity(string? sourceTitle, string? recognizedTitle)
    {
        var left = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(sourceTitle ?? string.Empty));
        var right = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(recognizedTitle ?? string.Empty));
        return AutoTagSimilarity.ComputeScore(left, right);
    }

    private static double ComputeArtistSimilarity(AutoTagAudioInfo info, ShazamRecognitionInfo recognized)
    {
        var sourceArtists = OneTaggerMatching.CleanArtists(info.Artists);
        if (sourceArtists.Count == 0 && !string.IsNullOrWhiteSpace(info.Artist))
        {
            sourceArtists = OneTaggerMatching.CleanArtists(new[] { info.Artist });
        }

        var recognizedArtists = OneTaggerMatching.CleanArtists(recognized.Artists);
        if (recognizedArtists.Count == 0 && !string.IsNullOrWhiteSpace(recognized.Artist))
        {
            recognizedArtists = OneTaggerMatching.CleanArtists(new[] { recognized.Artist });
        }

        return AutoTagSimilarity.ComputeScore(
            AutoTagSimilarity.NormalizeText(string.Join(" ", sourceArtists)),
            AutoTagSimilarity.NormalizeText(string.Join(" ", recognizedArtists)));
    }

    private static int? ComputeDurationDiffSeconds(int? sourceDurationSeconds, long? recognizedDurationMs)
    {
        if (!sourceDurationSeconds.HasValue || !recognizedDurationMs.HasValue || sourceDurationSeconds.Value <= 0 || recognizedDurationMs.Value <= 0)
        {
            return null;
        }

        var recognizedSeconds = (int)Math.Round(recognizedDurationMs.Value / 1000d);
        if (recognizedSeconds <= 0)
        {
            return null;
        }

        return Math.Abs(sourceDurationSeconds.Value - recognizedSeconds);
    }

    private static double? ComputeDurationSimilarity(int? durationDiffSeconds, int maxDurationDeltaSeconds)
    {
        if (!durationDiffSeconds.HasValue)
        {
            return null;
        }

        var maxDiff = Math.Max(1, maxDurationDeltaSeconds);
        if (durationDiffSeconds.Value > maxDiff)
        {
            return 0d;
        }

        var normalized = durationDiffSeconds.Value / (double)maxDiff;
        return Math.Clamp(1d - (normalized * 0.9d), 0d, 1d);
    }

    private static bool TryParseDate(string? raw, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return true;
        }

        if (DateTime.TryParseExact(raw, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return true;
        }

        if (DateTime.TryParseExact(raw, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return true;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }

    private static void AddOtherIfNotEmpty(AutoTagTrack track, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        track.Other[key] = new List<string> { value.Trim() };
    }

    private static void AddOtherIfAny(AutoTagTrack track, string key, IEnumerable<string>? values)
    {
        if (values == null)
        {
            return;
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        track.Other[key] = normalized;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static List<string> ResolveArtists(ShazamRecognitionInfo recognized, AutoTagAudioInfo info)
    {
        if (recognized.Artists.Count > 0)
        {
            return recognized.Artists.ToList();
        }

        if (!string.IsNullOrWhiteSpace(recognized.Artist))
        {
            return new List<string> { recognized.Artist };
        }

        return info.Artists.ToList();
    }

    private static string? ResolveAlbum(ShazamRecognitionInfo recognized, AutoTagAudioInfo info, ShazamMatchConfig config)
    {
        if (!config.IncludeAlbum)
        {
            return null;
        }

        info.Tags.TryGetValue("ALBUM", out var albumTag);
        return FirstNonEmpty(recognized.Album, albumTag?.FirstOrDefault());
    }

    private static TimeSpan? ResolveDuration(ShazamRecognitionInfo recognized, AutoTagAudioInfo info)
    {
        if (recognized.DurationMs.HasValue)
        {
            return TimeSpan.FromMilliseconds(recognized.DurationMs.Value);
        }

        if (info.DurationSeconds.HasValue)
        {
            return TimeSpan.FromSeconds(info.DurationSeconds.Value);
        }

        return null;
    }

    private static string? ResolveArtwork(ShazamRecognitionInfo recognized, ShazamMatchConfig config)
    {
        return config.PreferHqArtwork
            ? FirstNonEmpty(recognized.ArtworkHqUrl, recognized.ArtworkUrl)
            : FirstNonEmpty(recognized.ArtworkUrl, recognized.ArtworkHqUrl);
    }

    private static void ApplyConfiguredMetadata(AutoTagTrack track, ShazamRecognitionInfo recognized, ShazamMatchConfig config)
    {
        if (config.IncludeGenre && !string.IsNullOrWhiteSpace(recognized.Genre))
        {
            track.Genres.Add(recognized.Genre.Trim());
        }

        if (config.IncludeLabel && !string.IsNullOrWhiteSpace(recognized.Label))
        {
            track.Label = recognized.Label.Trim();
        }

        if (config.IncludeReleaseDate && TryParseDate(recognized.ReleaseDate, out var releaseDate))
        {
            track.ReleaseDate = releaseDate;
        }

        track.Art = ResolveArtwork(recognized, config);
    }

    private static void AddShazamOtherFields(AutoTagTrack track, ShazamRecognitionInfo recognized)
    {
        AddOtherIfNotEmpty(track, "SHAZAM_URL", recognized.Url);
        AddOtherIfNotEmpty(track, "SHAZAM_TRACK_ID", recognized.TrackId);
        AddOtherIfNotEmpty(track, "SHAZAM_KEY", recognized.Key);
        AddOtherIfNotEmpty(track, "SHAZAM_ALBUM", recognized.Album);
        AddOtherIfNotEmpty(track, "SHAZAM_GENRE", recognized.Genre);
        AddOtherIfNotEmpty(track, "SHAZAM_LABEL", recognized.Label);
        AddOtherIfNotEmpty(track, "SHAZAM_RELEASE_DATE", recognized.ReleaseDate);
        AddOtherIfNotEmpty(track, "SHAZAM_ARTWORK", recognized.ArtworkUrl);
        AddOtherIfNotEmpty(track, "SHAZAM_ARTWORK_HQ", recognized.ArtworkHqUrl);
        AddOtherIfNotEmpty(track, "SHAZAM_ISRC", recognized.Isrc);
        AddOtherIfNotEmpty(track, "SHAZAM_LANGUAGE", recognized.Language);
        AddOtherIfNotEmpty(track, "SHAZAM_COMPOSER", recognized.Composer);
        AddOtherIfNotEmpty(track, "SHAZAM_LYRICIST", recognized.Lyricist);
        AddOtherIfNotEmpty(track, "SHAZAM_PUBLISHER", recognized.Publisher);
        AddOtherIfNotEmpty(track, "SHAZAM_ALBUM_ADAM_ID", recognized.AlbumAdamId);
        AddOtherIfNotEmpty(track, "SHAZAM_APPLE_MUSIC_URL", recognized.AppleMusicUrl);
        AddOtherIfNotEmpty(track, "SHAZAM_SPOTIFY_URL", recognized.SpotifyUrl);
        AddOtherIfNotEmpty(track, "SHAZAM_YOUTUBE_URL", recognized.YoutubeUrl);

        if (recognized.TrackNumber is > 0)
        {
            AddOtherIfNotEmpty(track, "SHAZAM_TRACK_NUMBER", recognized.TrackNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (recognized.DiscNumber is > 0)
        {
            AddOtherIfNotEmpty(track, "SHAZAM_DISC_NUMBER", recognized.DiscNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (recognized.DurationMs is > 0)
        {
            AddOtherIfNotEmpty(track, "SHAZAM_DURATION_MS", recognized.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (recognized.Explicit.HasValue)
        {
            AddOtherIfNotEmpty(track, "SHAZAM_EXPLICIT", recognized.Explicit.Value ? "true" : "false");
        }

        AddOtherIfAny(track, "SHAZAM_ARTIST_IDS", recognized.ArtistIds);
        AddOtherIfAny(track, "SHAZAM_ARTIST_ADAM_IDS", recognized.ArtistAdamIds);
    }

    private static void MergeAdditionalTags(AutoTagTrack track, IReadOnlyDictionary<string, List<string>> tags)
    {
        foreach (var (key, values) in tags)
        {
            if (string.IsNullOrWhiteSpace(key) || values == null || values.Count == 0)
            {
                continue;
            }

            var normalized = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalized.Count == 0)
            {
                continue;
            }

            track.Other[key.Trim()] = normalized;
        }
    }
}
