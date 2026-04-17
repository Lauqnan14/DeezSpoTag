using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Tagging;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;

namespace DeezSpoTag.Workers;

/// <summary>
/// Durable file-tagging worker.
/// Jobs are persisted in queue.db and retried with backoff.
/// </summary>
public sealed class FileTaggingWorker : BackgroundService, ITaggingJobQueue
{
    private static readonly char[] MultiValueSeparators = [';', '|', ','];
    private static readonly HashSet<string> BlockedGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "other",
        "others"
    };

    private readonly ILogger<FileTaggingWorker> _logger;
    private readonly TaggingJobStore _jobStore;
    private readonly LibraryRepository _libraryRepository;
    private readonly AudioTagger _audioTagger;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly string _workerId = $"file-tagger:{Environment.MachineName}:{Environment.ProcessId}";
    private readonly string _libraryConnectionString;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleLockWindow;
    private readonly int _defaultMaxAttempts;
    private readonly int _baseRetryDelaySeconds;
    private readonly int _maxRetryDelaySeconds;
    private bool _genreTagNormalizationEnabled;

    public FileTaggingWorker(
        ILogger<FileTaggingWorker> logger,
        TaggingJobStore jobStore,
        LibraryRepository libraryRepository,
        AudioTagger audioTagger,
        DeezSpoTagSettingsService settingsService,
        IConfiguration configuration)
    {
        _logger = logger;
        _jobStore = jobStore;
        _libraryRepository = libraryRepository;
        _audioTagger = audioTagger;
        _settingsService = settingsService;

        var rawLibraryConnection =
            Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");
        _libraryConnectionString = SqliteConnectionStringResolver.Resolve(rawLibraryConnection, "deezspotag.db")
            ?? throw new InvalidOperationException("Library database connection string is not configured.");

        var pollSeconds = configuration.GetValue("Workers:FileTagging:PollIntervalSeconds", 2);
        var staleMinutes = configuration.GetValue("Workers:FileTagging:StaleLockMinutes", 30);
        _defaultMaxAttempts = Math.Clamp(configuration.GetValue("Workers:FileTagging:MaxAttempts", 5), 1, 20);
        _baseRetryDelaySeconds = Math.Clamp(configuration.GetValue("Workers:FileTagging:BaseRetryDelaySeconds", 10), 1, 600);
        _maxRetryDelaySeconds = Math.Clamp(configuration.GetValue("Workers:FileTagging:MaxRetryDelaySeconds", 300), 5, 7200);
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(pollSeconds, 1, 30));
        _staleLockWindow = TimeSpan.FromMinutes(Math.Clamp(staleMinutes, 1, 720));
    }

    public Task<long> EnqueueAsync(TaggingJobEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var maxAttempts = request.MaxAttempts ?? _defaultMaxAttempts;
        return _jobStore.EnqueueAsync(
            request.FilePath,
            request.TrackId,
            request.Operation,
            maxAttempts,
            cancellationToken);
    }

    // Legacy compatibility entrypoint.
    public void QueueTaggingTask(string filePath, string trackId, string operation = "retag")
    {
        _ = EnqueueAsync(
            new TaggingJobEnqueueRequest(filePath, trackId, operation, _defaultMaxAttempts),
            CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "File tagging worker started ({WorkerId}). Poll={Poll}s maxAttempts={MaxAttempts}.",
            _workerId,
            _pollInterval.TotalSeconds,
            _defaultMaxAttempts);

        var recovered = await _jobStore.RequeueStaleInProgressAsync(_staleLockWindow, stoppingToken);
        if (recovered > 0)
        {
            _logger.LogWarning(
                "Recovered {RecoveredCount} stale in-progress tagging jobs older than {Window}.",
                recovered,
                _staleLockWindow);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TaggingJobRecord? job;
                try
                {
                    job = await _jobStore.TryClaimNextAsync(_workerId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to claim next tagging job.");
                    await Task.Delay(_pollInterval, stoppingToken);
                    continue;
                }

                if (job is null)
                {
                    await Task.Delay(_pollInterval, stoppingToken);
                    continue;
                }

                await ProcessClaimedJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            _logger.LogInformation("File tagging worker stopped.");
        }
    }

    private async Task ProcessClaimedJobAsync(TaggingJobRecord job, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessOperationAsync(job, cancellationToken);
            await _jobStore.MarkCompletedAsync(job.Id, cancellationToken);
            _logger.LogInformation(
                "Tagging job {JobId} completed for {FilePath} (operation={Operation}, attempt={Attempt}/{Max}).",
                job.Id,
                job.FilePath,
                job.Operation,
                job.AttemptCount,
                job.MaxAttempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var retryDelay = ComputeRetryDelay(job.AttemptCount);
            await _jobStore.MarkFailedAsync(job, ex.Message, retryDelay, cancellationToken);
            if (job.AttemptCount >= job.MaxAttempts)
            {
                _logger.LogError(
                    ex,
                    "Tagging job {JobId} moved to dead-letter after {AttemptCount}/{MaxAttempts} attempts. file={FilePath}",
                    job.Id,
                    job.AttemptCount,
                    job.MaxAttempts,
                    job.FilePath);
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "Tagging job {JobId} failed attempt {AttemptCount}/{MaxAttempts}. Retrying in {RetryDelay}. file={FilePath}",
                    job.Id,
                    job.AttemptCount,
                    job.MaxAttempts,
                    retryDelay,
                    job.FilePath);
            }
        }
    }

    private async Task ProcessOperationAsync(TaggingJobRecord job, CancellationToken cancellationToken)
    {
        var operation = (job.Operation ?? string.Empty).Trim().ToLowerInvariant();
        switch (operation)
        {
            case "":
            case "retag":
            case "update":
            case "fix":
                await RetagFromLibraryAsync(job, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported tagging operation '{job.Operation}'.");
        }
    }

    private async Task RetagFromLibraryAsync(TaggingJobRecord job, CancellationToken cancellationToken)
    {
        var resolvedPath = await ResolveExistingPathAsync(job, cancellationToken);
        var trackId = await ResolveTrackIdAsync(job, resolvedPath, cancellationToken);
        if (!trackId.HasValue)
        {
            throw new InvalidOperationException($"No track mapping found for '{resolvedPath}'.");
        }

        var metadata = await LoadTrackMetadataAsync(trackId.Value, cancellationToken);
        if (metadata is null)
        {
            throw new InvalidOperationException($"Track metadata not found for track id {trackId.Value}.");
        }

        var settings = _settingsService.LoadSettings() ?? new Core.Models.Settings.DeezSpoTagSettings();
        settings.Tags ??= new Core.Models.Settings.TagSettings();

        var track = BuildTrack(metadata);
        var effectiveSettings = CreateEffectiveSettings(settings, track);
        await _audioTagger.TagTrackAsync(resolvedPath, track, effectiveSettings);
        await UpdateTaggedDateAsync(trackId.Value, cancellationToken);
    }

    private async Task<string> ResolveExistingPathAsync(TaggingJobRecord job, CancellationToken cancellationToken)
    {
        var requestedPath = NormalizePath(job.FilePath);
        if (File.Exists(requestedPath))
        {
            return requestedPath;
        }

        var parsedTrackId = TryParseTrackId(job.TrackId);
        if (parsedTrackId.HasValue)
        {
            var primaryPath = await _libraryRepository.GetTrackPrimaryFilePathAsync(parsedTrackId.Value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(primaryPath))
            {
                var normalized = NormalizePath(primaryPath);
                if (File.Exists(normalized))
                {
                    return normalized;
                }
            }
        }

        throw new FileNotFoundException("Audio file for tagging job was not found.", requestedPath);
    }

    private async Task<long?> ResolveTrackIdAsync(TaggingJobRecord job, string resolvedPath, CancellationToken cancellationToken)
    {
        var parsedTrackId = TryParseTrackId(job.TrackId);
        if (parsedTrackId.HasValue)
        {
            return parsedTrackId.Value;
        }

        return await _libraryRepository.GetTrackIdForFilePathAsync(resolvedPath, cancellationToken);
    }

    private async Task<TaggingTrackMetadata?> LoadTrackMetadataAsync(long trackId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_libraryConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = @"
SELECT t.id,
       t.title,
       t.duration_ms,
       t.disc,
       t.track_no,
       t.deezer_id,
       t.tag_title,
       t.tag_artist,
       t.tag_album,
       t.tag_album_artist,
       t.tag_label,
       t.tag_bpm,
       t.tag_key,
       t.tag_track_total,
       t.tag_year,
       t.tag_track_no,
       t.tag_disc,
       t.tag_genre,
       t.tag_isrc,
       t.tag_release_date,
       t.tag_publish_date,
       t.tag_url,
       t.tag_release_id,
       t.tag_track_id,
       t.lyrics_unsynced,
       t.lyrics_synced,
       al.id,
       al.title,
       al.preferred_cover_path,
       ar.id,
       ar.name,
       COALESCE((
           SELECT ts.source
           FROM track_source ts
           WHERE ts.track_id = t.id
           ORDER BY CASE
                        WHEN t.tag_url IS NOT NULL
                             AND TRIM(t.tag_url) <> ''
                             AND LOWER(t.tag_url) LIKE '%open.spotify.com/%'
                             AND LOWER(ts.source) = 'spotify' THEN 0
                        WHEN t.tag_url IS NOT NULL
                             AND TRIM(t.tag_url) <> ''
                             AND LOWER(t.tag_url) LIKE '%deezer.com/%'
                             AND LOWER(ts.source) = 'deezer' THEN 0
                        WHEN t.tag_url IS NOT NULL
                             AND TRIM(t.tag_url) <> ''
                             AND LOWER(t.tag_url) LIKE '%music.apple.com/%'
                             AND LOWER(ts.source) = 'apple' THEN 0
                        ELSE 1
                    END,
                    CASE LOWER(ts.source)
                        WHEN 'deezer' THEN 0
                        WHEN 'spotify' THEN 1
                        WHEN 'apple' THEN 2
                        ELSE 99
                    END,
                    CASE WHEN ts.source_id IS NULL OR TRIM(ts.source_id) = '' THEN 1 ELSE 0 END,
                    ts.rowid
           LIMIT 1
       ), 'deezer') AS source_name,
       COALESCE((
           SELECT ts.source_id
           FROM track_source ts
           WHERE ts.track_id = t.id
           ORDER BY CASE
                        WHEN t.tag_url IS NOT NULL
                             AND TRIM(t.tag_url) <> ''
                             AND LOWER(t.tag_url) LIKE '%open.spotify.com/%'
                             AND LOWER(ts.source) = 'spotify' THEN 0
                        WHEN t.tag_url IS NOT NULL
                             AND TRIM(t.tag_url) <> ''
                             AND LOWER(t.tag_url) LIKE '%deezer.com/%'
                             AND LOWER(ts.source) = 'deezer' THEN 0
                        WHEN t.tag_url IS NOT NULL
                             AND TRIM(t.tag_url) <> ''
                             AND LOWER(t.tag_url) LIKE '%music.apple.com/%'
                             AND LOWER(ts.source) = 'apple' THEN 0
                        ELSE 1
                    END,
                    CASE LOWER(ts.source)
                        WHEN 'deezer' THEN 0
                        WHEN 'spotify' THEN 1
                        WHEN 'apple' THEN 2
                        ELSE 99
                    END,
                    CASE WHEN ts.source_id IS NULL OR TRIM(ts.source_id) = '' THEN 1 ELSE 0 END,
                    ts.rowid
           LIMIT 1
       ), t.deezer_id, CAST(t.id AS TEXT)) AS source_id
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
WHERE t.id = @trackId
LIMIT 1;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var tagGenre = await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetString(17);
        var genres = await LoadTrackGenresAsync(connection, trackId, tagGenre, cancellationToken);

        return await MapTrackMetadataAsync(reader, genres, cancellationToken);
    }

    private static async Task<TaggingTrackMetadata> MapTrackMetadataAsync(
        SqliteDataReader reader,
        IReadOnlyList<string> genres,
        CancellationToken cancellationToken)
    {
        return new TaggingTrackMetadata(
            TrackId: reader.GetInt64(0),
            Title: reader.GetString(1),
            DurationMs: await ReadNullableInt32Async(reader, 2, cancellationToken),
            Disc: await ReadNullableInt32Async(reader, 3, cancellationToken),
            TrackNo: await ReadNullableInt32Async(reader, 4, cancellationToken),
            DeezerTrackId: await ReadNullableStringAsync(reader, 5, cancellationToken),
            TagTitle: await ReadNullableStringAsync(reader, 6, cancellationToken),
            TagArtist: await ReadNullableStringAsync(reader, 7, cancellationToken),
            TagAlbum: await ReadNullableStringAsync(reader, 8, cancellationToken),
            TagAlbumArtist: await ReadNullableStringAsync(reader, 9, cancellationToken),
            TagLabel: await ReadNullableStringAsync(reader, 10, cancellationToken),
            TagBpm: await ReadNullableInt32Async(reader, 11, cancellationToken),
            TagKey: await ReadNullableStringAsync(reader, 12, cancellationToken),
            TagTrackTotal: await ReadNullableInt32Async(reader, 13, cancellationToken),
            TagYear: await ReadNullableInt32Async(reader, 14, cancellationToken),
            TagTrackNo: await ReadNullableInt32Async(reader, 15, cancellationToken),
            TagDisc: await ReadNullableInt32Async(reader, 16, cancellationToken),
            TagIsrc: await ReadNullableStringAsync(reader, 18, cancellationToken),
            TagReleaseDate: await ReadNullableStringAsync(reader, 19, cancellationToken),
            TagPublishDate: await ReadNullableStringAsync(reader, 20, cancellationToken),
            TagUrl: await ReadNullableStringAsync(reader, 21, cancellationToken),
            TagReleaseId: await ReadNullableStringAsync(reader, 22, cancellationToken),
            TagTrackId: await ReadNullableStringAsync(reader, 23, cancellationToken),
            LyricsUnsynced: await ReadNullableStringAsync(reader, 24, cancellationToken),
            LyricsSynced: await ReadNullableStringAsync(reader, 25, cancellationToken),
            AlbumId: reader.GetInt64(26),
            AlbumTitle: reader.GetString(27),
            PreferredCoverPath: await ReadNullableStringAsync(reader, 28, cancellationToken),
            ArtistId: reader.GetInt64(29),
            ArtistName: reader.GetString(30),
            SourceName: await ReadNullableStringAsync(reader, 31, cancellationToken),
            SourceId: await ReadNullableStringAsync(reader, 32, cancellationToken),
            Genres: genres);
    }

    private static async Task<string?> ReadNullableStringAsync(
        SqliteDataReader reader,
        int ordinal,
        CancellationToken cancellationToken)
    {
        return await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);
    }

    private static async Task<int?> ReadNullableInt32Async(
        SqliteDataReader reader,
        int ordinal,
        CancellationToken cancellationToken)
    {
        return await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt32(ordinal);
    }

    private async Task<IReadOnlyList<string>> LoadTrackGenresAsync(
        SqliteConnection connection,
        long trackId,
        string? fallbackTagGenre,
        CancellationToken cancellationToken)
    {
        const string genreSql = @"
SELECT value
FROM track_genre
WHERE track_id = @trackId
ORDER BY value;";
        var values = new List<string>();
        await using (var command = new SqliteCommand(genreSql, connection))
        {
            command.Parameters.AddWithValue("trackId", trackId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (await reader.IsDBNullAsync(0, cancellationToken))
                {
                    continue;
                }

                var value = reader.GetString(0).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
        }

        if (values.Count == 0 && !string.IsNullOrWhiteSpace(fallbackTagGenre))
        {
            values.AddRange(fallbackTagGenre
                .Split(MultiValueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return NormalizeGenres(values);
    }

    private IReadOnlyList<string> NormalizeGenres(IEnumerable<string> values)
    {
        var aliasMap = GetGenreAliasMap();
        var splitComposite = _genreTagNormalizationEnabled;

        return GenreTagAliasNormalizer.NormalizeAndExpandValues(values, aliasMap, splitComposite)
            .Where(value => !BlockedGenres.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyDictionary<string, string> GetGenreAliasMap()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            _genreTagNormalizationEnabled = settings.NormalizeGenreTags;
            return settings.NormalizeGenreTags
                ? GenreTagAliasNormalizer.BuildAliasMap(settings.GenreTagAliasRules)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load settings for genre normalization in file tagging worker.");
            _genreTagNormalizationEnabled = false;
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task UpdateTaggedDateAsync(long trackId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_libraryConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
UPDATE track
SET tag_meta_tagged_date = @taggedDateUtc,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("taggedDateUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("id", trackId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Track BuildTrack(TaggingTrackMetadata metadata)
    {
        var artists = SplitArtists(metadata.TagArtist, metadata.ArtistName);
        var albumArtistCandidates = SplitArtists(metadata.TagAlbumArtist, metadata.ArtistName);
        var albumArtist = albumArtistCandidates.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(albumArtist))
        {
            albumArtist = metadata.ArtistName;
        }
        var title = FirstOrDefault(metadata.TagTitle, metadata.Title);
        var albumTitle = FirstOrDefault(metadata.TagAlbum, metadata.AlbumTitle);
        var sourceName = string.IsNullOrWhiteSpace(metadata.SourceName) ? "deezer" : metadata.SourceName!.Trim().ToLowerInvariant();
        var sourceId = FirstOrDefault(metadata.SourceId, metadata.DeezerTrackId, metadata.TrackId.ToString(CultureInfo.InvariantCulture));

        var album = new Album(metadata.AlbumId.ToString(CultureInfo.InvariantCulture), albumTitle)
        {
            MainArtist = new Artist(metadata.ArtistId.ToString(CultureInfo.InvariantCulture), albumArtist),
            Artist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Main"] = new List<string> { albumArtist }
            },
            Artists = new List<string> { albumArtist },
            TrackTotal = metadata.TagTrackTotal ?? 0,
            DiscTotal = metadata.TagDisc ?? metadata.Disc,
            Label = metadata.TagLabel,
            Genre = metadata.Genres.ToList(),
            EmbeddedCoverPath = ResolveCoverPath(metadata.PreferredCoverPath)
        };

        var track = new Track
        {
            Id = sourceId,
            Title = title,
            Duration = Math.Max(0, (metadata.DurationMs ?? 0) / 1000),
            MainArtist = new Artist(metadata.ArtistId.ToString(CultureInfo.InvariantCulture), artists[0]),
            Artist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Main"] = artists.ToList()
            },
            Artists = artists.ToList(),
            Album = album,
            TrackNumber = metadata.TagTrackNo ?? metadata.TrackNo ?? 0,
            DiscNumber = metadata.TagDisc ?? metadata.Disc ?? 0,
            Bpm = metadata.TagBpm ?? 0,
            Key = metadata.TagKey,
            ISRC = metadata.TagIsrc ?? string.Empty,
            Source = sourceName,
            SourceId = sourceId,
            DownloadURL = metadata.TagUrl ?? string.Empty,
            Lyrics = new Lyrics(metadata.TagTrackId ?? "0")
            {
                Unsync = metadata.LyricsUnsynced ?? string.Empty,
                Sync = metadata.LyricsSynced ?? string.Empty
            },
            Date = BuildDate(metadata.TagReleaseDate, metadata.TagPublishDate, metadata.TagYear)
        };

        track.ArtistString = track.MainArtist?.Name ?? string.Empty;
        track.ArtistsString = string.Join(", ", track.Artists);
        track.MainArtistsString = track.ArtistsString;
        track.DateString = track.Date.Format("ymd");
        return track;
    }

    private static CustomDate BuildDate(string? releaseDate, string? publishDate, int? year)
    {
        var candidate = FirstOrDefault(releaseDate, publishDate);
        if (TryParseDate(candidate, out var parsed))
        {
            return new CustomDate
            {
                Year = parsed.Year.ToString("D4"),
                Month = parsed.Month.ToString("D2"),
                Day = parsed.Day.ToString("D2")
            };
        }

        if (year.HasValue && year.Value > 0)
        {
            return new CustomDate
            {
                Year = year.Value.ToString("D4"),
                Month = "01",
                Day = "01"
            };
        }

        return new CustomDate();
    }

    private static bool TryParseDate(string? raw, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            return true;
        }

        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            return true;
        }

        if (DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            return true;
        }

        return false;
    }

    private static string ResolveCoverPath(string? preferredCoverPath)
    {
        if (string.IsNullOrWhiteSpace(preferredCoverPath))
        {
            return string.Empty;
        }

        var normalized = NormalizePath(preferredCoverPath);
        return File.Exists(normalized) ? normalized : string.Empty;
    }

    private static long? TryParseTrackId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(raw.Trim(), out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private TimeSpan ComputeRetryDelay(int attemptCount)
    {
        var retryOrdinal = Math.Max(1, attemptCount);
        var exponent = Math.Min(retryOrdinal - 1, 10);
        var candidate = _baseRetryDelaySeconds * Math.Pow(2, exponent);
        var clamped = Math.Min(candidate, _maxRetryDelaySeconds);
        return TimeSpan.FromSeconds(clamped);
    }

    private static string NormalizePath(string filePath)
    {
        return Path.GetFullPath(filePath.Trim());
    }

    private static string FirstOrDefault(params string?[] values)
        => values
            .Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;

    private static List<string> SplitArtists(string? raw, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var values = raw
                .Split(MultiValueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count > 0)
            {
                return values;
            }
        }

        return new List<string> { fallback };
    }

    private static Core.Models.Settings.DeezSpoTagSettings CreateEffectiveSettings(
        Core.Models.Settings.DeezSpoTagSettings settings,
        Track track)
    {
        Core.Models.Settings.DeezSpoTagSettings cloned;
        try
        {
            var json = JsonSerializer.Serialize(settings);
            cloned = JsonSerializer.Deserialize<Core.Models.Settings.DeezSpoTagSettings>(json)
                ?? new Core.Models.Settings.DeezSpoTagSettings();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cloned = new Core.Models.Settings.DeezSpoTagSettings();
        }

        cloned.Tags ??= new Core.Models.Settings.TagSettings();
        var hasValidYear = int.TryParse(track.Date?.Year, out var year) && year > 0;
        if (!hasValidYear)
        {
            cloned.Tags.Year = false;
        }

        if (track.Date == null || !track.Date.IsValid())
        {
            cloned.Tags.Date = false;
        }

        return cloned;
    }

    private sealed record TaggingTrackMetadata(
        long TrackId,
        string Title,
        int? DurationMs,
        int? Disc,
        int? TrackNo,
        string? DeezerTrackId,
        string? TagTitle,
        string? TagArtist,
        string? TagAlbum,
        string? TagAlbumArtist,
        string? TagLabel,
        int? TagBpm,
        string? TagKey,
        int? TagTrackTotal,
        int? TagYear,
        int? TagTrackNo,
        int? TagDisc,
        string? TagIsrc,
        string? TagReleaseDate,
        string? TagPublishDate,
        string? TagUrl,
        string? TagReleaseId,
        string? TagTrackId,
        string? LyricsUnsynced,
        string? LyricsSynced,
        long AlbumId,
        string AlbumTitle,
        string? PreferredCoverPath,
        long ArtistId,
        string ArtistName,
        string? SourceName,
        string? SourceId,
        IReadOnlyList<string> Genres);
}
