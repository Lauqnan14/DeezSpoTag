using System.Globalization;

#pragma warning disable CA1861
namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DeezerMatcher
{
    private const string CoverImageType = "cover";
    private static readonly string[] TrackIdTagKeys =
    {
        "DEEZER_TRACK_ID",
        "DEEZERID",
        "DEEZER_ID"
    };

    private readonly DeezerClient _client;
    private readonly ILogger<DeezerMatcher> _logger;

    public DeezerMatcher(DeezerClient client, ILogger<DeezerMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, DeezerConfig deezerConfig, CancellationToken cancellationToken) // NOSONAR
    {
        _client.SetArl(deezerConfig.Arl);
        var effectiveInfo = BuildEffectiveInfo(info);

        if (deezerConfig.MatchById)
        {
            var existingTrackId = AutoTagTagValueReader.ReadFirstTagValue(effectiveInfo, TrackIdTagKeys);
            if (long.TryParse(existingTrackId, out var trackIdFromTag) && trackIdFromTag > 0)
            {
                var byId = await TryMatchByTrackIdAsync(trackIdFromTag, deezerConfig, cancellationToken);
                if (byId != null)
                {
                    return byId;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(effectiveInfo.Isrc))
        {
            var byIsrc = await TryMatchByIsrcAsync(effectiveInfo.Isrc!, deezerConfig, cancellationToken);
            if (byIsrc != null)
            {
                return byIsrc;
            }
        }

        var byMetadata = await TryMatchByMetadataAsync(effectiveInfo, config, deezerConfig, cancellationToken);
        if (byMetadata != null)
        {
            return byMetadata;
        }

        return await TryMatchBySearchAsync(effectiveInfo, config, deezerConfig, cancellationToken);
    }

    private async Task<AutoTagMatchResult?> TryMatchBySearchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        DeezerConfig deezerConfig,
        CancellationToken cancellationToken)
    {
        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}".Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var results = await _client.SearchTracksAsync(query, cancellationToken);
        if (results == null || results.Data.Count == 0)
        {
            return null;
        }

        var tracks = results.Data.Select(t => t.ToTrackInfo()).ToList();
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<DeezerTrackInfo>(
                track => track.Title,
                track => track.Version,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);

        if (match == null)
        {
            return null;
        }

        return await BuildMatchResultAsync(match.Track, match.Accuracy, deezerConfig, cancellationToken);
    }

    private async Task<AutoTagMatchResult?> TryMatchByTrackIdAsync(long trackId, DeezerConfig deezerConfig, CancellationToken cancellationToken)
    {
        DeezerTrackInfo? track = null;
        try
        {
            var full = await _client.GetTrackAsync(trackId, cancellationToken);
            if (full != null)
            {
                track = ToTrackInfo(full);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer ID lookup failed for track ID {TrackId}.", trackId);
        }

        if (track == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(track.ArtHash))
        {
            track.ArtUrl = DeezerClient.BuildImageUrl(CoverImageType, track.ArtHash, deezerConfig.ArtResolution);
        }

        await ExtendTrackAsync(track, cancellationToken);
        return new AutoTagMatchResult { Accuracy = 1.0, Track = ToAutoTagTrack(track) };
    }

    private async Task<AutoTagMatchResult?> TryMatchByIsrcAsync(string isrc, DeezerConfig deezerConfig, CancellationToken cancellationToken)
    {
        DeezerTrackInfo? track = null;
        try
        {
            var results = await _client.SearchTracksByIsrcAsync(isrc, cancellationToken);
            track = results?.Data.FirstOrDefault()?.ToTrackInfo();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer ISRC lookup failed for {Isrc}.", isrc);
        }

        if (track == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(track.ArtHash))
        {
            track.ArtUrl = DeezerClient.BuildImageUrl(CoverImageType, track.ArtHash, deezerConfig.ArtResolution);
        }

        await ExtendTrackAsync(track, cancellationToken);
        return new AutoTagMatchResult { Accuracy = 1.0, Track = ToAutoTagTrack(track) };
    }

    private async Task<AutoTagMatchResult> BuildMatchResultAsync(
        DeezerTrackInfo track,
        double accuracy,
        DeezerConfig deezerConfig,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(track.ArtHash))
        {
            track.ArtUrl = DeezerClient.BuildImageUrl(CoverImageType, track.ArtHash, deezerConfig.ArtResolution);
        }

        await ExtendTrackAsync(track, cancellationToken);
        return new AutoTagMatchResult { Accuracy = accuracy, Track = ToAutoTagTrack(track) };
    }

    private async Task<AutoTagMatchResult?> TryMatchByMetadataAsync( // NOSONAR
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        DeezerConfig deezerConfig,
        CancellationToken cancellationToken)
    {
        var artist = FirstNonEmpty((info.Artist ?? string.Empty).Trim(), info.Artists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
        var title = OneTaggerMatching.CleanTitle(info.Title);
        var album = (info.Album ?? string.Empty).Trim();
        var releaseYearHint = ParseYear(AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_RELEASE_DATE", "SHAZAM_META_RELEASE_DATE", "SHAZAM_META_RELEASED", "SHAZAM_META_YEAR"));
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        foreach (var query in BuildMetadataQueries(artist, title, album))
        {
            try
            {
                var results = await _client.SearchTracksAsync(query, cancellationToken);
                if (results == null || results.Data.Count == 0)
                {
                    continue;
                }

                var tracks = FilterMetadataCandidates(
                    results.Data.Select(track => track.ToTrackInfo()).ToList(),
                    album,
                    releaseYearHint);
                var match = OneTaggerMatching.MatchTrack(
                    info,
                    tracks,
                    config,
                    new OneTaggerMatching.TrackSelectors<DeezerTrackInfo>(
                        track => track.Title,
                        track => track.Version,
                        track => track.Artists,
                        track => track.Duration,
                        track => track.ReleaseDate),
                    matchArtist: true);
                if (match == null)
                {
                    continue;
                }

                return await BuildMatchResultAsync(match.Track, match.Accuracy, deezerConfig, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Deezer metadata query failed for {Query}.", query);
            }
        }

        return null;
    }

    private static AutoTagAudioInfo BuildEffectiveInfo(AutoTagAudioInfo info)
    {
        var effective = new AutoTagAudioInfo
        {
            Title = info.Title,
            Artist = info.Artist,
            Artists = info.Artists.ToList(),
            Album = info.Album,
            DurationSeconds = info.DurationSeconds,
            Isrc = info.Isrc,
            TrackNumber = info.TrackNumber,
            Tags = info.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            HasEmbeddedTitle = info.HasEmbeddedTitle,
            HasEmbeddedArtist = info.HasEmbeddedArtist
        };

        effective.Title = FirstNonEmpty(effective.Title, AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_TITLE")) ?? effective.Title;
        var hintedArtist = FirstNonEmpty(effective.Artist, AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_ARTIST"));
        if (!string.IsNullOrWhiteSpace(hintedArtist))
        {
            effective.Artist = hintedArtist;
            if (effective.Artists.Count == 0)
            {
                effective.Artists = new List<string> { hintedArtist };
            }
        }

        effective.Album = FirstNonEmpty(effective.Album, AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_ALBUM"));
        effective.Isrc = FirstNonEmpty(effective.Isrc, AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_ISRC", "ISRC"));
        if (!effective.DurationSeconds.HasValue)
        {
            effective.DurationSeconds = ParseDurationSeconds(AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_DURATION_MS", "SHAZAM_META_DURATION", "SHAZAM_META_TIME", "SHAZAM_META_LENGTH"));
        }

        return effective;
    }

    private static int? ParseDurationSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Contains(':'))
        {
            var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var totalSeconds = 0;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var parsed) || parsed < 0)
                {
                    return null;
                }

                totalSeconds = checked((totalSeconds * 60) + parsed);
            }

            return totalSeconds > 0 ? totalSeconds : null;
        }

        if (!long.TryParse(trimmed, out var numeric) || numeric <= 0)
        {
            return null;
        }

        if (numeric <= 1000)
        {
            return (int)numeric;
        }

        return (int)Math.Round(numeric / 1000d);
    }

    private static int? ParseYear(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length >= 4 && int.TryParse(trimmed[..4], out var year) && year is >= 1000 and <= 9999)
        {
            return year;
        }

        return null;
    }

    private static bool AlbumLooksCompatible(string expectedAlbum, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(expectedAlbum) || string.IsNullOrWhiteSpace(candidateAlbum))
        {
            return false;
        }

        var expected = NormalizeAlbum(expectedAlbum);
        var candidate = NormalizeAlbum(candidateAlbum);
        if (expected.Length == 0 || candidate.Length == 0)
        {
            return false;
        }

        return expected == candidate
            || expected.Contains(candidate, StringComparison.Ordinal)
            || candidate.Contains(expected, StringComparison.Ordinal);
    }

    private static string NormalizeAlbum(string value)
    {
        var cleaned = new string(value
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            .ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task ExtendTrackAsync(DeezerTrackInfo track, CancellationToken cancellationToken) // NOSONAR
    {
        await ExtendTrackMetadataAsync(track, cancellationToken);
        await ExtendLyricsAsync(track, cancellationToken);
        await ExtendAlbumMetadataAsync(track, cancellationToken);
    }

    private static IEnumerable<string> BuildMetadataQueries(string artist, string title, string album)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(album))
        {
            queries.Add($"artist:\"{artist}\" track:\"{title}\" album:\"{album}\"");
        }

        queries.Add($"artist:\"{artist}\" track:\"{title}\"");
        if (!string.IsNullOrWhiteSpace(album))
        {
            queries.Add($"{artist} {title} {album}");
        }

        queries.Add($"{artist} {title}");
        return queries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static List<DeezerTrackInfo> FilterMetadataCandidates(
        List<DeezerTrackInfo> tracks,
        string album,
        int? releaseYearHint)
    {
        if (!string.IsNullOrWhiteSpace(album))
        {
            var albumCandidates = tracks
                .Where(track => AlbumLooksCompatible(album, track.Album))
                .ToList();
            if (albumCandidates.Count > 0)
            {
                tracks = albumCandidates;
            }
        }

        if (releaseYearHint.HasValue)
        {
            var yearCandidates = tracks
                .Where(track => track.ReleaseDate.HasValue && track.ReleaseDate.Value.Year == releaseYearHint.Value)
                .ToList();
            if (yearCandidates.Count > 0)
            {
                tracks = yearCandidates;
            }
        }

        return tracks;
    }

    private async Task ExtendTrackMetadataAsync(DeezerTrackInfo track, CancellationToken cancellationToken)
    {
        if (!long.TryParse(track.TrackId, out var trackId))
        {
            return;
        }

        try
        {
            var full = await _client.GetTrackAsync(trackId, cancellationToken);
            if (full == null)
            {
                return;
            }

            track.TrackNumber = full.TrackPosition;
            track.DiscNumber = full.DiskNumber;
            if (full.Bpm.HasValue && full.Bpm.Value > 1.0)
            {
                track.Bpm = (long)full.Bpm.Value;
            }
            track.Isrc = full.Isrc;
            track.ReleaseDate = TryParseDate(full.ReleaseDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed extending Deezer track ID {TrackId}.", trackId);
        }
    }

    private async Task ExtendLyricsAsync(DeezerTrackInfo track, CancellationToken cancellationToken)
    {
        if (!long.TryParse(track.TrackId, out var trackId))
        {
            return;
        }

        try
        {
            var lyrics = await _client.GetLyricsAsync(track.TrackId, cancellationToken);
            if (lyrics == null)
            {
                return;
            }

            track.UnsyncedLyrics = lyrics.UnsyncedLyrics;
            if (lyrics.SyncedLyrics.Count > 0)
            {
                track.SyncedLyrics = lyrics.SyncedLyrics;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed extending Deezer lyrics for track ID {TrackId}.", trackId);
        }
    }

    private async Task ExtendAlbumMetadataAsync(DeezerTrackInfo track, CancellationToken cancellationToken)
    {
        if (!long.TryParse(track.ReleaseId, out var releaseId))
        {
            return;
        }

        try
        {
            var album = await _client.GetAlbumAsync(releaseId, cancellationToken);
            if (album == null)
            {
                return;
            }

            track.Genres = album.Genres.Data.Select(genre => genre.Name).ToList();
            track.TrackTotal = album.NbTracks;
            track.Label = album.Label;
            track.AlbumArtists = album.Contributors.Select(artist => artist.Name).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed extending Deezer album ID {AlbumId}.", releaseId);
        }
    }

    private static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DeezerTrackInfo ToTrackInfo(DeezerTrackFull track)
    {
        var artists = track.Contributors
            .Select(artist => artist.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(track.Artist?.Name))
        {
            artists.Add(track.Artist.Name);
        }

        return new DeezerTrackInfo
        {
            Title = string.IsNullOrWhiteSpace(track.TitleShort) ? track.Title : track.TitleShort,
            Version = track.TitleVersion,
            Artists = artists,
            Album = track.Album?.Title,
            ArtHash = !string.IsNullOrWhiteSpace(track.Album?.Md5Image) ? track.Album.Md5Image : track.Md5Image,
            Url = track.Link,
            CatalogNumber = track.Id.ToString(),
            TrackId = track.Id.ToString(),
            ReleaseId = track.Album?.Id.ToString() ?? string.Empty,
            Duration = TimeSpan.FromSeconds(track.Duration),
            Isrc = track.Isrc,
            ReleaseDate = TryParseDate(track.ReleaseDate),
            TrackNumber = track.TrackPosition,
            DiscNumber = track.DiskNumber,
            Bpm = track.Bpm.HasValue && track.Bpm.Value > 1.0 ? (long)track.Bpm.Value : null
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static List<string> SplitLyricsLines(string value)
    {
        return value
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) // NOSONAR
            .ToList();
    }

    private static AutoTagTrack ToAutoTagTrack(DeezerTrackInfo track)
    {
        var other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (track.SyncedLyrics.Count > 0)
        {
            other["syncedLyrics"] = track.SyncedLyrics;
            other["lyrics"] = track.SyncedLyrics;
        }

        if (!string.IsNullOrWhiteSpace(track.UnsyncedLyrics))
        {
            var unsyncedLines = SplitLyricsLines(track.UnsyncedLyrics);
            if (unsyncedLines.Count == 0)
            {
                unsyncedLines = new List<string> { track.UnsyncedLyrics };
            }

            other["unsyncedLyrics"] = unsyncedLines;
            if (!other.ContainsKey("lyrics"))
            {
                other["lyrics"] = unsyncedLines;
            }
        }

        return new AutoTagTrack
        {
            Title = track.Title,
            Version = track.Version,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Art = track.ArtUrl,
            Url = track.Url,
            CatalogNumber = track.CatalogNumber,
            TrackId = track.TrackId,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            Genres = track.Genres.ToList(),
            Label = track.Label,
            Isrc = track.Isrc,
            ReleaseDate = track.ReleaseDate,
            TrackNumber = track.TrackNumber,
            DiscNumber = track.DiscNumber,
            TrackTotal = track.TrackTotal,
            Bpm = track.Bpm,
            Explicit = track.Explicit,
            Other = other
        };
    }

}
