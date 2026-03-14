using System.Globalization;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ShazamMatcher
{
    private readonly ShazamRecognitionService _recognitionService;

    public ShazamMatcher(ShazamRecognitionService recognitionService)
    {
        _recognitionService = recognitionService;
    }

    public Task<AutoTagMatchResult?> MatchAsync( // NOSONAR
        string filePath,
        AutoTagAudioInfo info,
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

        return Task.FromResult<AutoTagMatchResult?>(new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = track
        });
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
