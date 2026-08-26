using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Library;

[SuppressMessage("Major Code Smell", "S1192", Justification = "SQL statements intentionally embed domain literals for stable query plans and readability.")]
public sealed class LibraryRepository
{
    private const string FolderContentVideo = "video";
    private const string FolderContentPodcast = "podcast";

    public sealed record BoomplayDeezerTrackMappingUpsertInput(
        string BoomplayTrackId,
        string? DeezerTrackId,
        string? Isrc,
        string? Title,
        string? Artist,
        string? Album,
        string? CoverUrl,
        int? DurationMs,
        string SourceFingerprint,
        string MatcherVersion,
        string Status,
        string? LastError,
        DateTimeOffset? NextRetryUtc);

    private sealed record ExistingTrackRecord(
        long Id,
        int? DurationMs,
        string? LyricsStatus,
        string? DeezerId,
        string? LyricsType,
        string? TagTitle,
        string? TagArtist,
        string? TagAlbum,
        string? TagAlbumArtist,
        string? TagVersion,
        string? TagLabel,
        string? TagCatalogNumber,
        int? TagBpm,
        string? TagKey,
        int? TagTrackTotal,
        int? TagDurationMs,
        int? TagYear,
        int? TagTrackNo,
        int? TagDisc,
        string? TagGenre,
        string? TagIsrc,
        string? TagReleaseDate,
        string? TagPublishDate,
        string? TagUrl,
        string? TagReleaseId,
        string? TagTrackId,
        string? TagMetaTaggedDate,
        string? LyricsUnsynced,
        string? LyricsSynced);

    public sealed record TrackShazamCacheUpsertInput(
        long TrackId,
        string Status,
        string? ShazamTrackId,
        string? Title,
        string? Artist,
        string? Isrc,
        IReadOnlyList<RecommendationTrackDto>? RelatedTracks,
        DateTimeOffset ScannedAtUtc,
        string? Error,
        string? FilePath = null,
        long? FileSize = null,
        DateTimeOffset? FileModifiedUtc = null,
        string? SpotifyId = null,
        string? AppleId = null,
        string? DeezerId = null,
        string? Album = null,
        string? ReleaseDate = null,
        bool? Explicit = null);

    public sealed record PlayHistoryWriteInput(
        long PlexUserId,
        long? LibraryId,
        long? TrackId,
        string? PlexTrackKey,
        string? PlexRatingKey,
        DateTimeOffset PlayedAtUtc,
        int? DurationMs,
        string? MetadataJson,
        string Source = "plex",
        long? FolderId = null,
        string? RemoteLibraryId = null);

    public sealed record MixCacheUpsertInput(
        string MixId,
        long PlexUserId,
        long LibraryId,
        string Name,
        string Description,
        IReadOnlyList<string> CoverUrls,
        int TrackCount,
        DateTimeOffset GeneratedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    public sealed record FolderUpsertInput(
        string RootPath,
        string DisplayName,
        bool Enabled,
        string? LibraryName,
        string DesiredQuality,
        bool ConvertEnabled,
        string? ConvertFormat,
        string? ConvertBitrate,
        string? AutoTagProfileId = null,
        bool ReplaceAutoTagProfile = false);

    public sealed record TrackAnalysisFilter(
        long LibraryId,
        double? MinEnergy,
        double? MaxEnergy,
        double? MinBpm,
        double? MaxBpm,
        double? MinSpectralCentroid,
        double? MaxSpectralCentroid,
        int Limit);

    public sealed record PlaylistWatchPreferenceUpsertInput(
        string Source,
        string SourceId,
        long? DestinationFolderId,
        string? Service,
        IReadOnlyList<string>? SyncTargets,
        string? PreferredEngine,
        DownloadEngineOrderSettings? DownloadEngineOrder,
        string? DownloadVariantMode,
        string? SyncMode,
        bool UpdateArtwork,
        bool ReuseSavedArtwork,
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules = null,
        IReadOnlyList<PlaylistTrackBlockRule>? IgnoreRules = null,
        long? AtmosDestinationFolderId = null);

    public sealed record PlaylistWatchStateUpsertInput(
        string Source,
        string SourceId,
        string? SnapshotId,
        int? TrackCount,
        int? BatchNextOffset,
        string? BatchProcessingSnapshotId,
        DateTimeOffset? LastCheckedUtc,
        string? LastRunStatus = null,
        string? LastRunMessage = null,
        DateTimeOffset? NextAttemptUtc = null,
        int? ConsecutiveFailures = null,
        string? CurrentPhase = null,
        int? CurrentTrackIndex = null,
        int? CurrentTrackTotal = null,
        DateTimeOffset? HeartbeatUtc = null,
        DateTimeOffset? DeadlineUtc = null);

    public sealed record WatchlistSchedulerStateUpsertInput(
        string WatchType,
        string? ActiveSource,
        string? ActiveSourceId,
        DateTimeOffset? ActiveStartedUtc,
        DateTimeOffset? LastProgressUtc);

    public sealed record WatchlistSourceCircuitStateUpsertInput(
        string WatchType,
        string Source,
        bool IsOpen,
        DateTimeOffset? OpenUntilUtc,
        string? Reason,
        string? Fingerprint,
        int FailureCount);

    public sealed record WatchlistTargetCircuitStateUpsertInput(
        string TargetService,
        bool IsOpen,
        DateTimeOffset? OpenUntilUtc,
        string? Reason,
        int FailureCount);

    public sealed record ArtistWatchPreferenceUpdateInput(
        long ArtistId,
        long? DestinationFolderId,
        IReadOnlyCollection<string>? AlbumGroups,
        bool? TopSongsEnabled,
        bool? LatestReleasesOnly,
        string? PreferredEngine,
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules,
        long? AtmosDestinationFolderId,
        string? DownloadVariantMode,
        string? TopSongsSyncMode,
        bool? DownloadDiscographyEnabled,
        IReadOnlyList<PlaylistTrackBlockRule>? IgnoreRules);

    private const DateTimeStyles ParseDateStyles = DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces;
    private const string ArtistType = "artist";
    private const string AlbumType = "album";
    private const string TrackType = "track";
    private const string GenreType = "genre";
    private const string TitleField = "title";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private const string AtmosVariant = "atmos";
    private const string RequireAtmosField = "requireAtmos";
    private const string TrackIdField = "trackId";
    private const string SourceField = "source";
    private const string SourceIdField = "sourceId";
    private const string WatchlistIdentityErrorFingerprintSql = @"
(
    last_error LIKE '%verification is incomplete%'
    OR last_error LIKE '%Source tracks:%'
    OR last_error LIKE '%No Jellyfin matches%'
    OR last_error LIKE '%No Plex matches%'
    OR last_error LIKE '%No Navidrome matches%'
    OR last_error LIKE '%sync is temporarily paused after repeated failures%'
    OR last_error LIKE '%Waiting for the durable playlist reconciliation request%'
)";
    private const string WatchlistIdentityCircuitReasonSql = @"
(
    reason LIKE '%verification is incomplete%'
    OR reason LIKE '%Source tracks:%'
    OR reason LIKE '%Target matches:%'
    OR reason LIKE '%No Jellyfin matches%'
    OR reason LIKE '%No Plex matches%'
    OR reason LIKE '%No Navidrome matches%'
    OR reason LIKE '%sync is temporarily paused after repeated failures%'
    OR reason LIKE '%Waiting for the durable playlist reconciliation request%'
)";
    private const string LibraryIdField = "libraryId";
    private const string DurationMsField = "durationMs";
    private const string TrackCountField = "trackCount";
    private const string FolderIdParameter = "folderId";
    private const string ArtistParameter = "artist";
    private const string ArtistSearchParameter = "artistSearch";
    private const string TrackIdsJsonParameter = "trackIdsJson";
    private const string EntityIdParameter = "entityId";
    private const string TrackGenreTable = "track_genre";
    private const string TrackStyleTable = "track_style";
    private const string TrackMoodTable = "track_mood";
    private const string TrackRemixerTable = "track_remixer";
    private const string TrackOtherTagTable = "track_other_tag";
    private static readonly HashSet<string> SupportedFolderConvertFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "aa",
        "aax",
        "mp3",
        "aac",
        "m4a",
        "m4b",
        "m4p",
        "alac",
        "aiff",
        "ape",
        "dsf",
        "ogg",
        "oga",
        "opus",
        "flac",
        "wav",
        "wma",
        "wv",
        "webm",
        "mpc",
        "mpp"
    };

    private static readonly HashSet<string> SupportedFolderConvertBitrates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUTO",
        "64",
        "96",
        "128",
        "160",
        "192",
        "256",
        "320"
    };

    private readonly string? _connectionString;

    public LibraryRepository(IConfiguration configuration, ILogger<LibraryRepository> logger)
    {
        _ = logger;
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");
        _connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public async Task<LibraryScanInfo> GetScanInfoAsync(CancellationToken cancellationToken = default)
    {
        await EnsureScanRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT last_run_utc, artist_count, album_count, track_count FROM library_scan_state WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var lastRun = await reader.IsDBNullAsync(0, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(0));
            return new LibraryScanInfo(lastRun, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }

        return new LibraryScanInfo(null, 0, 0, 0);
    }

    public async Task SaveScanInfoAsync(LibraryScanInfo info, CancellationToken cancellationToken = default)
    {
        await EnsureScanRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE library_scan_state
SET last_run_utc = @lastRun,
    artist_count = @artists,
    album_count = @albums,
    track_count = @tracks,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("lastRun", info.LastRunUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("artists", info.ArtistCount);
        command.Parameters.AddWithValue("albums", info.AlbumCount);
        command.Parameters.AddWithValue("tracks", info.TrackCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset ParseDateTimeOffsetInvariant(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, ParseDateStyles);

    private static DateTimeOffset ParseUtcDateTimeOffsetInvariant(string value)
    {
        var trimmed = value.Trim();
        if (HasExplicitUtcOffset(trimmed))
        {
            return DateTimeOffset.Parse(trimmed, CultureInfo.InvariantCulture, ParseDateStyles);
        }

        var parsed = DateTime.Parse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces);
        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
    }

    private static bool HasExplicitUtcOffset(string value)
    {
        if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var timeSeparatorIndex = Math.Max(value.IndexOf('T', StringComparison.Ordinal), value.IndexOf(' ', StringComparison.Ordinal));
        if (timeSeparatorIndex < 0)
        {
            return false;
        }

        var offsetIndex = Math.Max(value.LastIndexOf('+'), value.LastIndexOf('-'));
        return offsetIndex > timeSeparatorIndex;
    }

    public async Task<LibraryStatsDto> GetLibraryStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string totalsSql = @"
WITH folder_tracks AS (
    SELECT CASE
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'video'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%video%' THEN 'video'
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'podcast'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%podcast%' THEN 'podcast'
               ELSE 'music'
           END AS media_mode,
           ar.id AS artist_id,
           a.id AS album_id,
           t.id AS track_id
    FROM folder f
    LEFT JOIN audio_file af ON af.folder_id = f.id
    LEFT JOIN track_local tl ON tl.audio_file_id = af.id
    LEFT JOIN track t ON t.id = tl.track_id
    LEFT JOIN album a ON a.id = t.album_id
    LEFT JOIN artist ar ON ar.id = a.artist_id
    WHERE f.enabled = TRUE
)
SELECT COUNT(DISTINCT CASE WHEN media_mode = 'music' THEN artist_id END) AS artist_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'music' THEN album_id END) AS album_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'music' THEN track_id END) AS track_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'video' THEN track_id END) AS video_item_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'podcast' THEN track_id END) AS podcast_item_count
FROM folder_tracks;";

        var totals = await ReadLibraryTotalsAsync(connection, totalsSql, cancellationToken);

        const string librarySql = @"
WITH library_rows AS (
    SELECT l.id AS library_id,
           l.name AS library_name,
           f.id AS folder_id,
           CASE
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'video'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%video%' THEN 'video'
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'podcast'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%podcast%' THEN 'podcast'
               ELSE 'music'
           END AS media_mode,
           CASE
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'atmos'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%atmos%' THEN 5
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('max_hires_192', 'hires_96', 'hi_res_lossless', 'hi_res', '27', '7')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi_res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi-res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24 bit%' THEN 4
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('alac', 'cd_lossless', 'flac', 'lossless', '9', '6')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%lossless%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%flac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%alac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16 bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%cd%' THEN 3
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('aac_lc', 'aac', 'mp3_320', 'high', '5', '3')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%aac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%320%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%vorbis%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%opus%' THEN 2
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('mp3_128', 'mp3_96', 'low', '1')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%128%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%96%' THEN 1
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'video'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%video%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) = 'podcast'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%podcast%' THEN 0
               ELSE 3
           END AS desired_quality_rank,
           ar.id AS artist_id,
           a.id AS album_id,
           t.id AS track_id,
           t.lyrics_status AS lyrics_status,
           COALESCE(af.quality_rank, 0) AS local_quality_rank
    FROM library l
    LEFT JOIN folder f ON f.library_id = l.id AND f.enabled = TRUE
    LEFT JOIN audio_file af ON af.folder_id = f.id
    LEFT JOIN track_local tl ON tl.audio_file_id = af.id
    LEFT JOIN track t ON t.id = tl.track_id
    LEFT JOIN album a ON a.id = t.album_id
    LEFT JOIN artist ar ON ar.id = a.artist_id
),
library_quality_targets AS (
    SELECT library_id,
           track_id,
           MAX(desired_quality_rank) AS desired_quality_rank
    FROM library_rows
    WHERE media_mode = 'music'
      AND track_id IS NOT NULL
      AND desired_quality_rank > 0
    GROUP BY library_id, track_id
),
library_best_quality AS (
    SELECT library_id,
           track_id,
           MAX(local_quality_rank) AS best_quality_rank
    FROM library_rows
    WHERE media_mode = 'music'
      AND track_id IS NOT NULL
    GROUP BY library_id, track_id
),
library_no_lyrics AS (
    SELECT library_id,
           COUNT(DISTINCT track_id) AS no_lyrics_count
    FROM library_rows
    WHERE media_mode = 'music'
      AND track_id IS NOT NULL
      AND (lyrics_status IS NULL OR TRIM(lyrics_status) = '')
    GROUP BY library_id
),
library_unmet_quality AS (
    SELECT t.library_id,
           COUNT(*) AS unmet_quality_count
    FROM library_quality_targets t
    LEFT JOIN library_best_quality b
           ON b.library_id = t.library_id
          AND b.track_id = t.track_id
    WHERE COALESCE(b.best_quality_rank, 0) < t.desired_quality_rank
    GROUP BY t.library_id
)
SELECT lr.library_id,
       lr.library_name,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'music' THEN lr.artist_id END) AS artist_count,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'music' THEN lr.album_id END) AS album_count,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'music' THEN lr.track_id END) AS track_count,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'video' THEN lr.track_id END) AS video_item_count,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'podcast' THEN lr.track_id END) AS podcast_item_count,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'music' THEN lr.folder_id END) AS music_folder_count,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'video' THEN lr.folder_id END) AS video_folder_count,
       COUNT(DISTINCT CASE WHEN lr.media_mode = 'podcast' THEN lr.folder_id END) AS podcast_folder_count,
       COALESCE(MAX(luq.unmet_quality_count), 0) AS unmet_quality_count,
       COALESCE(MAX(lnl.no_lyrics_count), 0) AS no_lyrics_count
FROM library_rows lr
LEFT JOIN library_unmet_quality luq ON luq.library_id = lr.library_id
LEFT JOIN library_no_lyrics lnl ON lnl.library_id = lr.library_id
GROUP BY lr.library_id, lr.library_name
ORDER BY lr.library_name;";

        var libraries = await ReadLibraryStatsLibrariesAsync(connection, librarySql, cancellationToken);

        var extensionBreakdown = await ReadBreakdownAsync(connection, @"
WITH ranked_track_files AS (
    SELECT tl.track_id,
           LOWER(COALESCE(NULLIF(TRIM(af.extension), ''), 'unknown')) AS value,
           ROW_NUMBER() OVER (
               PARTITION BY tl.track_id
               ORDER BY COALESCE(af.quality_rank, -1) DESC,
                        COALESCE(af.bits_per_sample, -1) DESC,
                        COALESCE(af.sample_rate_hz, -1) DESC,
                        COALESCE(af.bitrate_kbps, -1) DESC,
                        af.id DESC
           ) AS rn
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.enabled = TRUE
)
SELECT value, COUNT(*)
FROM ranked_track_files
WHERE rn = 1
GROUP BY value
ORDER BY COUNT(*) DESC, value ASC;", cancellationToken);

        var bitDepthBreakdown = await ReadBreakdownAsync(connection, @"
WITH ranked_track_files AS (
    SELECT tl.track_id,
           COALESCE(CAST(af.bits_per_sample AS TEXT) || '-bit', 'unknown') AS value,
           ROW_NUMBER() OVER (
               PARTITION BY tl.track_id
               ORDER BY COALESCE(af.quality_rank, -1) DESC,
                        COALESCE(af.bits_per_sample, -1) DESC,
                        COALESCE(af.sample_rate_hz, -1) DESC,
                        COALESCE(af.bitrate_kbps, -1) DESC,
                        af.id DESC
           ) AS rn
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.enabled = TRUE
)
SELECT value, COUNT(*)
FROM ranked_track_files
WHERE rn = 1
GROUP BY value
ORDER BY COUNT(*) DESC, value ASC;", cancellationToken);

        var sampleRateBreakdown = await ReadBreakdownAsync(connection, @"
WITH ranked_track_files AS (
    SELECT tl.track_id,
           COALESCE(printf('%.1f kHz', af.sample_rate_hz / 1000.0), 'unknown') AS value,
           ROW_NUMBER() OVER (
               PARTITION BY tl.track_id
               ORDER BY COALESCE(af.quality_rank, -1) DESC,
                        COALESCE(af.bits_per_sample, -1) DESC,
                        COALESCE(af.sample_rate_hz, -1) DESC,
                        COALESCE(af.bitrate_kbps, -1) DESC,
                        af.id DESC
           ) AS rn
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.enabled = TRUE
)
SELECT value, COUNT(*)
FROM ranked_track_files
WHERE rn = 1
GROUP BY value
ORDER BY COUNT(*) DESC, value ASC;", cancellationToken);

        var technicalProfileBreakdown = await ReadBreakdownAsync(connection, @"
WITH ranked_track_files AS (
    SELECT tl.track_id,
           TRIM(
               COALESCE(UPPER(NULLIF(TRIM(af.extension), '')), 'UNKNOWN')
               || ' • '
               || COALESCE(CAST(af.bits_per_sample AS TEXT) || '-bit', 'unknown')
               || ' • '
               || COALESCE(printf('%.1f kHz', af.sample_rate_hz / 1000.0), 'unknown')
           ) AS value,
           ROW_NUMBER() OVER (
               PARTITION BY tl.track_id
               ORDER BY COALESCE(af.quality_rank, -1) DESC,
                        COALESCE(af.bits_per_sample, -1) DESC,
                        COALESCE(af.sample_rate_hz, -1) DESC,
                        COALESCE(af.bitrate_kbps, -1) DESC,
                        af.id DESC
           ) AS rn
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.enabled = TRUE
)
SELECT value, COUNT(*)
FROM ranked_track_files
WHERE rn = 1
GROUP BY value
ORDER BY COUNT(*) DESC, value ASC
LIMIT 20;", cancellationToken);

        var lyricsTypeBreakdown = await ReadBreakdownAsync(connection, @"
SELECT COALESCE(NULLIF(TRIM(lyrics_type), ''), 'none') AS value,
       COUNT(*) AS count
FROM track
WHERE EXISTS (
    SELECT 1
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE tl.track_id = track.id
      AND f.enabled = TRUE
)
GROUP BY value
ORDER BY count DESC, value ASC;", cancellationToken);

        const string detailSql = @"
WITH source_flags AS (
    SELECT t.id AS track_id,
           CASE
               WHEN MAX(CASE WHEN NULLIF(TRIM(COALESCE(t.deezer_id, '')), '') IS NOT NULL THEN 1 ELSE 0 END) > 0
                    OR MAX(CASE WHEN ts.source = 'deezer' THEN 1 ELSE 0 END) > 0
               THEN 1 ELSE 0
           END AS has_deezer_id,
           MAX(CASE WHEN ts.source = 'spotify' THEN 1 ELSE 0 END) AS has_spotify_id,
           MAX(CASE WHEN ts.source = 'apple' THEN 1 ELSE 0 END) AS has_apple_id,
           MAX(CASE WHEN ts.source = 'deezer' AND NULLIF(TRIM(COALESCE(ts.url, '')), '') IS NOT NULL THEN 1 ELSE 0 END) AS has_deezer_url,
           MAX(CASE WHEN ts.source = 'spotify' AND NULLIF(TRIM(COALESCE(ts.url, '')), '') IS NOT NULL THEN 1 ELSE 0 END) AS has_spotify_url,
           MAX(CASE WHEN ts.source = 'apple' AND NULLIF(TRIM(COALESCE(ts.url, '')), '') IS NOT NULL THEN 1 ELSE 0 END) AS has_apple_url
    FROM track t
    LEFT JOIN track_source ts ON ts.track_id = t.id
    GROUP BY t.id
)
SELECT
    COUNT(CASE WHEN t.lyrics_status IS NOT NULL AND TRIM(t.lyrics_status) <> '' THEN 1 END) AS tracks_with_lyrics,
    COUNT(CASE WHEN LOWER(COALESCE(t.lyrics_status, '')) = 'synced' THEN 1 END) AS tracks_with_synced_lyrics,
    COUNT(CASE WHEN LOWER(COALESCE(t.lyrics_status, '')) = 'unsynced' THEN 1 END) AS tracks_with_unsynced_lyrics,
    COUNT(CASE WHEN LOWER(COALESCE(t.lyrics_status, '')) = 'both' THEN 1 END) AS tracks_with_both_lyrics,
    (SELECT COUNT(*)
     FROM album
     WHERE has_animated_artwork = 1
       AND EXISTS (
           SELECT 1
           FROM album_local aloc
           JOIN folder f_album ON f_album.id = aloc.folder_id
           WHERE aloc.album_id = album.id
             AND f_album.enabled = TRUE
       )) AS albums_with_animated_artwork,
    COALESCE(SUM(sf.has_deezer_id), 0) AS deezer_track_ids,
    COALESCE(SUM(sf.has_spotify_id), 0) AS spotify_track_ids,
    COALESCE(SUM(sf.has_apple_id), 0) AS apple_track_ids,
    COALESCE(SUM(sf.has_deezer_url), 0) AS deezer_urls,
    COALESCE(SUM(sf.has_spotify_url), 0) AS spotify_urls,
    COALESCE(SUM(sf.has_apple_url), 0) AS apple_urls
FROM track t
LEFT JOIN source_flags sf ON sf.track_id = t.id
WHERE EXISTS (
    SELECT 1
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE tl.track_id = t.id
      AND f.enabled = TRUE
);";

        var detail = await ReadLibraryStatsDetailAsync(
            connection,
            detailSql,
            new LibraryStatsBreakdowns(
                extensionBreakdown,
                bitDepthBreakdown,
                sampleRateBreakdown,
                technicalProfileBreakdown,
                lyricsTypeBreakdown),
            cancellationToken);

        return new LibraryStatsDto(
            totals.TotalArtists,
            totals.TotalAlbums,
            totals.TotalTracks,
            libraries,
            totals.TotalVideoItems,
            totals.TotalPodcastItems,
            detail);
    }

    public async Task<(int Artists, int Albums, int Tracks, int VideoItems, int PodcastItems)> GetFolderStatsTotalsAsync(
        long folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        const string sql = @"
WITH folder_tracks AS (
    SELECT CASE
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'video'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%video%' THEN 'video'
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'podcast'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%podcast%' THEN 'podcast'
               ELSE 'music'
           END AS media_mode,
           ar.id AS artist_id,
           a.id AS album_id,
           t.id AS track_id
    FROM folder f
    LEFT JOIN audio_file af ON af.folder_id = f.id
    LEFT JOIN track_local tl ON tl.audio_file_id = af.id
    LEFT JOIN track t ON t.id = tl.track_id
    LEFT JOIN album a ON a.id = t.album_id
    LEFT JOIN artist ar ON ar.id = a.artist_id
    WHERE f.enabled = TRUE
      AND f.id = @folderId
)
SELECT COUNT(DISTINCT CASE WHEN media_mode = 'music' THEN artist_id END) AS artist_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'music' THEN album_id END) AS album_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'music' THEN track_id END) AS track_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'video' THEN track_id END) AS video_item_count,
       COUNT(DISTINCT CASE WHEN media_mode = 'podcast' THEN track_id END) AS podcast_item_count
FROM folder_tracks;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0, 0, 0, 0);
        }

        return (
            await ReadNullableIntAsync(reader, 0, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 1, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 2, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 3, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 4, cancellationToken) ?? 0);
    }

    private sealed record LibraryTotals(
        int TotalArtists,
        int TotalAlbums,
        int TotalTracks,
        int TotalVideoItems,
        int TotalPodcastItems);

    private sealed record LibraryStatsBreakdowns(
        IReadOnlyList<LibraryStatsBreakdownItemDto> Extension,
        IReadOnlyList<LibraryStatsBreakdownItemDto> BitDepth,
        IReadOnlyList<LibraryStatsBreakdownItemDto> SampleRate,
        IReadOnlyList<LibraryStatsBreakdownItemDto> TechnicalProfile,
        IReadOnlyList<LibraryStatsBreakdownItemDto> LyricsType);

    private static async Task<LibraryTotals> ReadLibraryTotalsAsync(
        SqliteConnection connection,
        string totalsSql,
        CancellationToken cancellationToken)
    {
        await using var totalsCommand = new SqliteCommand(totalsSql, connection);
        await using var totalsReader = await totalsCommand.ExecuteReaderAsync(cancellationToken);
        if (!await totalsReader.ReadAsync(cancellationToken))
        {
            return new LibraryTotals(0, 0, 0, 0, 0);
        }

        return new LibraryTotals(
            await ReadNullableIntAsync(totalsReader, 0, cancellationToken) ?? 0,
            await ReadNullableIntAsync(totalsReader, 1, cancellationToken) ?? 0,
            await ReadNullableIntAsync(totalsReader, 2, cancellationToken) ?? 0,
            await ReadNullableIntAsync(totalsReader, 3, cancellationToken) ?? 0,
            await ReadNullableIntAsync(totalsReader, 4, cancellationToken) ?? 0);
    }

    private static async Task<List<LibraryStatsLibraryDto>> ReadLibraryStatsLibrariesAsync(
        SqliteConnection connection,
        string librarySql,
        CancellationToken cancellationToken)
    {
        await using var libraryCommand = new SqliteCommand(librarySql, connection);
        await using var libraryReader = await libraryCommand.ExecuteReaderAsync(cancellationToken);
        var libraries = new List<LibraryStatsLibraryDto>();
        while (await libraryReader.ReadAsync(cancellationToken))
        {
            libraries.Add(await ReadLibraryStatsLibraryDtoAsync(libraryReader, cancellationToken));
        }

        return libraries;
    }

    private static async Task<LibraryStatsLibraryDto> ReadLibraryStatsLibraryDtoAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        return new LibraryStatsLibraryDto(
            reader.GetInt64(0),
            await ReadNullableStringAsync(reader, 1, cancellationToken) ?? "Library",
            await ReadNullableIntAsync(reader, 2, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 3, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 4, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 5, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 6, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 7, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 8, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 9, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 10, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 11, cancellationToken) ?? 0);
    }

    private static async Task<LibraryStatsDetailDto?> ReadLibraryStatsDetailAsync(
        SqliteConnection connection,
        string detailSql,
        LibraryStatsBreakdowns breakdowns,
        CancellationToken cancellationToken)
    {
        await using var detailCommand = new SqliteCommand(detailSql, connection);
        await using var detailReader = await detailCommand.ExecuteReaderAsync(cancellationToken);
        if (!await detailReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var sourceCoverage = new LibraryStatsSourceCoverageDto(
            await ReadNullableIntAsync(detailReader, 5, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 6, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 7, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 8, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 9, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 10, cancellationToken) ?? 0);

        return new LibraryStatsDetailDto(
            await ReadNullableIntAsync(detailReader, 0, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 1, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 2, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 3, cancellationToken) ?? 0,
            await ReadNullableIntAsync(detailReader, 4, cancellationToken) ?? 0,
            sourceCoverage,
            breakdowns.Extension,
            breakdowns.BitDepth,
            breakdowns.SampleRate,
            breakdowns.TechnicalProfile,
            breakdowns.LyricsType);
    }

    private static async Task<IReadOnlyList<LibraryStatsBreakdownItemDto>> ReadBreakdownAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<LibraryStatsBreakdownItemDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LibraryStatsBreakdownItemDto(
                await reader.IsDBNullAsync(0, cancellationToken) ? "unknown" : reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? 0 : reader.GetInt32(1)));
        }

        return items;
    }

    public async Task AddLogAsync(LibraryLogEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"INSERT INTO library_log (timestamp_utc, level, message)
VALUES (@timestampUtc, @level, @message);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("timestampUtc", entry.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("level", entry.Level);
        command.Parameters.AddWithValue("message", entry.Message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryLogEntry>> GetLogsAsync(int? limit = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = "SELECT timestamp_utc, level, message FROM library_log ORDER BY timestamp_utc DESC";
        if (limit.HasValue && limit.Value > 0)
        {
            sql += " LIMIT @limit";
        }

        await using var command = new SqliteCommand(sql, connection);
        if (limit.HasValue && limit.Value > 0)
        {
            command.Parameters.AddWithValue("limit", limit.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var logs = new List<LibraryLogEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var timestamp = ParseDateTimeOffsetInvariant(reader.GetString(0));
            logs.Add(new LibraryLogEntry(timestamp, reader.GetString(1), reader.GetString(2)));
        }

        return logs;
    }

    public async Task ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM library_log;";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LibraryClearResultDto> ClearLibraryDataAsync(CancellationToken cancellationToken = default)
    {
        await EnsureScanRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await SetForeignKeysAsync(connection, enabled: false, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var artistsRemoved = await CountRowsAsync(connection, transaction, ArtistType, cancellationToken);
            var albumsRemoved = await CountRowsAsync(connection, transaction, AlbumType, cancellationToken);
            var tracksRemoved = await CountRowsAsync(connection, transaction, TrackType, cancellationToken);

            const string sql = @"
DELETE FROM track_analysis;
DELETE FROM track_genre;
DELETE FROM track_local;
DELETE FROM track_mood;
DELETE FROM track_other_tag;
DELETE FROM track_plex_metadata;
DELETE FROM track_remixer;
DELETE FROM track_shazam_cache;
DELETE FROM media_server_track_metadata;
DELETE FROM media_server_track_variant_metadata;
DELETE FROM album_local;
DELETE FROM track_style;
DELETE FROM audio_file;
DELETE FROM track_source;
DELETE FROM album_source;
DELETE FROM artist_source;
DELETE FROM track;
DELETE FROM album;
DELETE FROM artist;
DELETE FROM match_candidate;
DELETE FROM scan_job;
UPDATE library_scan_state
SET last_run_utc = NULL,
    artist_count = 0,
    album_count = 0,
    track_count = 0,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1;";

            await using var command = new SqliteCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LibraryClearResultDto(artistsRemoved, albumsRemoved, tracksRemoved);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            await SetForeignKeysAsync(connection, enabled: true, cancellationToken);
        }
    }

    public async Task<LibraryClearResultDto> ClearFolderLocalContentAsync(long folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var counts = await CountFolderLocalContentAsync(connection, transaction, folderId, cancellationToken);

        const string sql = @"
DELETE FROM album_local
WHERE folder_id = @folderId;

DELETE FROM track_local
WHERE audio_file_id IN (
    SELECT id
    FROM audio_file
    WHERE folder_id = @folderId
);

DELETE FROM audio_file
WHERE folder_id = @folderId;";

        await using (var command = new SqliteCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue(FolderIdParameter, folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await CleanupOrphansAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return counts;
    }

    private static async Task<int> CountRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        var sql = tableName switch
        {
            ArtistType => "SELECT COUNT(*) FROM artist;",
            AlbumType => "SELECT COUNT(*) FROM album;",
            TrackType => "SELECT COUNT(*) FROM track;",
            _ => throw new InvalidOperationException($"Unsupported table count request for '{tableName}'.")
        };
        await using var command = new SqliteCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    public async Task<int> CleanupMissingFilesAsync(long? folderId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var missingIds = await FindMissingAudioFileIdsAsync(connection, transaction, folderId, cancellationToken);

        if (missingIds.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        await PopulateMissingAudioFileTempTableAsync(connection, transaction, missingIds, cancellationToken);
        await DeleteMissingAudioFileRowsAsync(connection, transaction, cancellationToken);
        await DeleteEmptyAlbumLocalRowsAsync(connection, transaction, cancellationToken);

        await CleanupOrphansAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return missingIds.Count;
    }

    public async Task<int> RemoveLocalAudioFilesByPathAsync(
        long folderId,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        if (folderId <= 0 || filePaths.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var folderRoot = await GetFolderRootPathAsync(connection, transaction, folderId, cancellationToken);
        if (string.IsNullOrWhiteSpace(folderRoot))
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var normalizedRoot = NormalizeRoot(folderRoot);
        await PopulateAudioFileDeleteTargetTempTableAsync(
            connection,
            transaction,
            normalizedRoot,
            filePaths,
            cancellationToken);

        var removed = await CountTargetedAudioFileDeletesAsync(connection, transaction, folderId, cancellationToken);
        if (removed == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        await DeleteTargetedAudioFileRowsAsync(connection, transaction, folderId, cancellationToken);
        await DeleteEmptyAlbumLocalRowsAsync(connection, transaction, cancellationToken);
        await CleanupOrphansAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static async Task<List<long>> FindMissingAudioFileIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? folderId,
        CancellationToken cancellationToken)
    {
        const string selectSql = @"
SELECT af.id,
       af.path,
       af.relative_path,
       f.root_path
FROM audio_file af
JOIN folder f ON f.id = af.folder_id
WHERE @folderId IS NULL OR af.folder_id = @folderId;";
        await using var selectCommand = new SqliteCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        var missingIds = new List<long>();

        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await AudioFileExistsAsync(reader, cancellationToken))
            {
                missingIds.Add(reader.GetInt64(0));
            }
        }

        return missingIds;
    }

    private static async Task<string?> GetFolderRootPathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT root_path FROM folder WHERE id = @folderId LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string rootPath && !string.IsNullOrWhiteSpace(rootPath) ? rootPath : null;
    }

    private static async Task PopulateAudioFileDeleteTargetTempTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedRoot,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken)
    {
        const string createTempSql = @"
CREATE TEMP TABLE IF NOT EXISTS audio_file_delete_target (
    relative_path TEXT NOT NULL,
    path TEXT NOT NULL,
    PRIMARY KEY (relative_path, path)
);";
        await ExecuteNonQueryAsync(connection, transaction, createTempSql, cancellationToken);

        const string clearTempSql = "DELETE FROM audio_file_delete_target;";
        await ExecuteNonQueryAsync(connection, transaction, clearTempSql, cancellationToken);

        const string insertTempSql = @"
INSERT OR IGNORE INTO audio_file_delete_target (relative_path, path)
VALUES (@relativePath, @path);";
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = NormalizeScanFilePath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            await using var insertCommand = new SqliteCommand(insertTempSql, connection, transaction);
            insertCommand.Parameters.AddWithValue("relativePath", ComputeRelativePath(normalizedRoot, normalizedPath));
            insertCommand.Parameters.AddWithValue("path", normalizedPath);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> CountTargetedAudioFileDeletesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT COUNT(*)
FROM audio_file af
JOIN audio_file_delete_target target
  ON af.relative_path = target.relative_path
  OR af.path = target.path
WHERE af.folder_id = @folderId;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    private static async Task DeleteTargetedAudioFileRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        CancellationToken cancellationToken)
    {
        const string deleteTrackLocalSql = @"
DELETE FROM track_local
WHERE audio_file_id IN (
    SELECT af.id
    FROM audio_file af
    JOIN audio_file_delete_target target
      ON af.relative_path = target.relative_path
      OR af.path = target.path
    WHERE af.folder_id = @folderId
);";
        await using (var deleteTrackLocalCommand = new SqliteCommand(deleteTrackLocalSql, connection, transaction))
        {
            deleteTrackLocalCommand.Parameters.AddWithValue(FolderIdParameter, folderId);
            await deleteTrackLocalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteAudioSql = @"
DELETE FROM audio_file
WHERE folder_id = @folderId
  AND EXISTS (
      SELECT 1
      FROM audio_file_delete_target target
      WHERE audio_file.relative_path = target.relative_path
         OR audio_file.path = target.path
  );";
        await using var deleteAudioCommand = new SqliteCommand(deleteAudioSql, connection, transaction);
        deleteAudioCommand.Parameters.AddWithValue(FolderIdParameter, folderId);
        await deleteAudioCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteEmptyAlbumLocalRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = @"
DELETE FROM album_local
WHERE NOT EXISTS (
    SELECT 1
    FROM track t
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    WHERE t.album_id = album_local.album_id
      AND af.folder_id = album_local.folder_id
);";
        await ExecuteNonQueryAsync(connection, transaction, sql, cancellationToken);
    }

    private static async Task<LibraryClearResultDto> CountFolderLocalContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT COUNT(DISTINCT al.artist_id) AS artists_removed,
       COUNT(DISTINCT t.album_id) AS albums_removed,
       COUNT(DISTINCT tl.track_id) AS tracks_removed
FROM audio_file af
LEFT JOIN track_local tl ON tl.audio_file_id = af.id
LEFT JOIN track t ON t.id = tl.track_id
LEFT JOIN album al ON al.id = t.album_id
WHERE af.folder_id = @folderId;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LibraryClearResultDto(0, 0, 0);
        }

        return new LibraryClearResultDto(
            await reader.IsDBNullAsync(0, cancellationToken) ? 0 : Convert.ToInt32(reader.GetInt64(0)),
            await reader.IsDBNullAsync(1, cancellationToken) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
            await reader.IsDBNullAsync(2, cancellationToken) ? 0 : Convert.ToInt32(reader.GetInt64(2)));
    }

    private static async Task<bool> AudioFileExistsAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var path = BuildAbsolutePath(
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1));
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task PopulateMissingAudioFileTempTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> missingIds,
        CancellationToken cancellationToken)
    {
        const string createTempSql = "CREATE TEMP TABLE IF NOT EXISTS missing_audio_file (id INTEGER PRIMARY KEY);";
        await ExecuteNonQueryAsync(connection, transaction, createTempSql, cancellationToken);

        const string clearTempSql = "DELETE FROM missing_audio_file;";
        await ExecuteNonQueryAsync(connection, transaction, clearTempSql, cancellationToken);

        const string insertTempSql = "INSERT OR IGNORE INTO missing_audio_file (id) VALUES (@id);";
        foreach (var id in missingIds)
        {
            await using var insertCommand = new SqliteCommand(insertTempSql, connection, transaction);
            insertCommand.Parameters.AddWithValue("id", id);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task DeleteMissingAudioFileRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string deleteTrackLocalSql = @"
DELETE FROM track_local
WHERE audio_file_id IN (SELECT id FROM missing_audio_file);";
        await ExecuteNonQueryAsync(connection, transaction, deleteTrackLocalSql, cancellationToken);

        const string deleteAudioSql = @"
DELETE FROM audio_file
WHERE id IN (SELECT id FROM missing_audio_file);";
        await ExecuteNonQueryAsync(connection, transaction, deleteAudioSql, cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetForeignKeysAsync(
        SqliteConnection connection,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var sql = enabled ? "PRAGMA foreign_keys=ON;" : "PRAGMA foreign_keys=OFF;";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LibrarySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT live_preview_ingest, enable_signal_analysis FROM library_settings WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var livePreviewIngest = !await reader.IsDBNullAsync(0, cancellationToken) && reader.GetBoolean(0);
            var enableSignalAnalysis = !await reader.IsDBNullAsync(1, cancellationToken) && reader.GetBoolean(1);
            return new LibrarySettingsDto(livePreviewIngest, enableSignalAnalysis);
        }

        return new LibrarySettingsDto(false, false);
    }

    public async Task<LibrarySettingsDto> UpdateSettingsAsync(LibrarySettingsDto settings, CancellationToken cancellationToken = default)
    {
        await EnsureSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE library_settings
SET live_preview_ingest = @livePreviewIngest,
    enable_signal_analysis = @enableSignalAnalysis,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("livePreviewIngest", settings.LivePreviewIngest);
        command.Parameters.AddWithValue("enableSignalAnalysis", settings.EnableSignalAnalysis);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return settings;
    }

    public async Task<QualityScannerAutomationSettingsDto> GetQualityScannerAutomationSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQualityScannerAutomationSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT enabled,
       interval_minutes,
       scope,
       folder_id,
       queue_atmos_alternatives,
       cooldown_minutes,
       last_started_utc,
       last_finished_utc
FROM quality_scan_automation_settings
WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new QualityScannerAutomationSettingsDto(
                false,
                1440,
                "watchlist",
                null,
                false,
                1440,
                null,
                null);
        }

        return new QualityScannerAutomationSettingsDto(
            !await reader.IsDBNullAsync(0, cancellationToken) && reader.GetBoolean(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? 1440 : Math.Clamp(reader.GetInt32(1), 15, 10080),
            await reader.IsDBNullAsync(2, cancellationToken) ? "watchlist" : NormalizeQualityScannerScope(reader.GetString(2)),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt64(3),
            !await reader.IsDBNullAsync(4, cancellationToken) && reader.GetBoolean(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? 1440 : Math.Clamp(reader.GetInt32(5), 0, 43200),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : ParseDateTimeOffsetOrNull(reader.GetString(6)),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : ParseDateTimeOffsetOrNull(reader.GetString(7)));
    }

    public async Task<QualityScannerAutomationSettingsDto> UpdateQualityScannerAutomationSettingsAsync(
        QualityScannerAutomationSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        await EnsureQualityScannerAutomationSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE quality_scan_automation_settings
SET enabled = @enabled,
    interval_minutes = @intervalMinutes,
    scope = @scope,
    folder_id = @folderId,
    queue_atmos_alternatives = @queueAtmos,
    cooldown_minutes = @cooldownMinutes,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1;";
        var normalizedScope = NormalizeQualityScannerScope(settings.Scope);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("enabled", settings.Enabled);
        command.Parameters.AddWithValue("intervalMinutes", Math.Clamp(settings.IntervalMinutes, 15, 10080));
        command.Parameters.AddWithValue("scope", normalizedScope);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)settings.FolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("queueAtmos", settings.QueueAtmosAlternatives);
        command.Parameters.AddWithValue("cooldownMinutes", Math.Clamp(settings.CooldownMinutes, 0, 43200));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetQualityScannerAutomationSettingsAsync(cancellationToken);
    }

    public async Task MarkQualityScannerAutomationStartedAsync(DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
    {
        await EnsureQualityScannerAutomationSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE quality_scan_automation_settings
SET last_started_utc = @startedAtUtc,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("startedAtUtc", startedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkQualityScannerAutomationFinishedAsync(DateTimeOffset finishedAtUtc, CancellationToken cancellationToken = default)
    {
        await EnsureQualityScannerAutomationSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE quality_scan_automation_settings
SET last_finished_utc = @finishedAtUtc,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("finishedAtUtc", finishedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> StartQualityScannerRunAsync(
        string trigger,
        string scope,
        long? folderId,
        bool queueAtmosAlternatives,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO quality_scan_run (
    trigger,
    status,
    scope,
    folder_id,
    queue_atmos_alternatives,
    started_at_utc,
    created_at,
    updated_at
) VALUES (
    @trigger,
    'running',
    @scope,
    @folderId,
    @queueAtmosAlternatives,
    @startedAtUtc,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
);
SELECT last_insert_rowid();";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trigger", string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("scope", NormalizeQualityScannerScope(scope));
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("queueAtmosAlternatives", queueAtmosAlternatives);
        command.Parameters.AddWithValue("startedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }

    public async Task UpdateQualityScannerRunProgressAsync(
        long runId,
        QualityScannerRunProgressDto progress,
        string? phase,
        CancellationToken cancellationToken = default)
    {
        if (runId <= 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE quality_scan_run
SET total_tracks = @totalTracks,
    processed_tracks = @processedTracks,
    quality_met = @qualityMet,
    low_quality = @lowQuality,
    upgrades_queued = @upgradesQueued,
    atmos_queued = @atmosQueued,
    duplicate_skipped = @duplicateSkipped,
    match_missed = @matchMissed,
    phase = @phase,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @runId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("runId", runId);
        command.Parameters.AddWithValue("totalTracks", Math.Max(0, progress.TotalTracks));
        command.Parameters.AddWithValue("processedTracks", Math.Max(0, progress.ProcessedTracks));
        command.Parameters.AddWithValue("qualityMet", Math.Max(0, progress.QualityMet));
        command.Parameters.AddWithValue("lowQuality", Math.Max(0, progress.LowQuality));
        command.Parameters.AddWithValue("upgradesQueued", Math.Max(0, progress.UpgradesQueued));
        command.Parameters.AddWithValue("atmosQueued", Math.Max(0, progress.AtmosQueued));
        command.Parameters.AddWithValue("duplicateSkipped", Math.Max(0, progress.DuplicateSkipped));
        command.Parameters.AddWithValue("matchMissed", Math.Max(0, progress.MatchMissed));
        command.Parameters.AddWithValue("phase", string.IsNullOrWhiteSpace(phase) ? (object)DBNull.Value : phase);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteQualityScannerRunAsync(
        long runId,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (runId <= 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE quality_scan_run
SET status = @status,
    error_message = @errorMessage,
    finished_at_utc = @finishedAtUtc,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @runId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("runId", runId);
        command.Parameters.AddWithValue("status", string.IsNullOrWhiteSpace(status) ? "finished" : status.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("errorMessage", string.IsNullOrWhiteSpace(errorMessage) ? (object)DBNull.Value : errorMessage);
        command.Parameters.AddWithValue("finishedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertQualityScannerTrackStateAsync(
        QualityScannerTrackStateUpdateDto update,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO quality_scan_track_state (
    track_id,
    last_run_id,
    last_scanned_utc,
    best_quality_rank,
    desired_quality_rank,
    last_action,
    last_upgrade_queued_utc,
    last_atmos_queued_utc,
    last_error,
    updated_at
) VALUES (
    @trackId,
    @runId,
    @lastScannedUtc,
    @bestQualityRank,
    @desiredQualityRank,
    @lastAction,
    @lastUpgradeQueuedUtc,
    @lastAtmosQueuedUtc,
    @lastError,
    CURRENT_TIMESTAMP
)
ON CONFLICT(track_id) DO UPDATE SET
    last_run_id = excluded.last_run_id,
    last_scanned_utc = excluded.last_scanned_utc,
    best_quality_rank = excluded.best_quality_rank,
    desired_quality_rank = excluded.desired_quality_rank,
    last_action = excluded.last_action,
    last_upgrade_queued_utc = COALESCE(excluded.last_upgrade_queued_utc, quality_scan_track_state.last_upgrade_queued_utc),
    last_atmos_queued_utc = COALESCE(excluded.last_atmos_queued_utc, quality_scan_track_state.last_atmos_queued_utc),
    last_error = COALESCE(excluded.last_error, quality_scan_track_state.last_error),
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, update.TrackId);
        command.Parameters.AddWithValue("runId", (object?)update.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("lastScannedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("bestQualityRank", update.BestQualityRank);
        command.Parameters.AddWithValue("desiredQualityRank", update.DesiredQualityRank);
        command.Parameters.AddWithValue("lastAction", update.LastAction);
        command.Parameters.AddWithValue("lastUpgradeQueuedUtc", update.LastUpgradeQueuedUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("lastAtmosQueuedUtc", update.LastAtmosQueuedUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("lastError", string.IsNullOrWhiteSpace(update.LastError) ? (object)DBNull.Value : update.LastError);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddQualityScannerActionLogAsync(
        QualityScannerActionLogDto action,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO quality_scan_action_log (
    run_id,
    track_id,
    action_type,
    source,
    quality,
    content_type,
    destination_folder_id,
    queue_uuid,
    message,
    created_at_utc
) VALUES (
    @runId,
    @trackId,
    @actionType,
    @source,
    @quality,
    @contentType,
    @destinationFolderId,
    @queueUuid,
    @message,
    @createdAtUtc
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("runId", (object?)action.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue(TrackIdField, action.TrackId);
        command.Parameters.AddWithValue("actionType", action.ActionType);
        command.Parameters.AddWithValue(SourceField, string.IsNullOrWhiteSpace(action.Source) ? (object)DBNull.Value : action.Source);
        command.Parameters.AddWithValue("quality", string.IsNullOrWhiteSpace(action.Quality) ? (object)DBNull.Value : action.Quality);
        command.Parameters.AddWithValue("contentType", string.IsNullOrWhiteSpace(action.ContentType) ? (object)DBNull.Value : action.ContentType);
        command.Parameters.AddWithValue("destinationFolderId", (object?)action.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", string.IsNullOrWhiteSpace(action.QueueUuid) ? (object)DBNull.Value : action.QueueUuid);
        command.Parameters.AddWithValue("message", string.IsNullOrWhiteSpace(action.Message) ? (object)DBNull.Value : action.Message);
        command.Parameters.AddWithValue("createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryDto>> GetLibrariesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT id, name FROM library ORDER BY name;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var libraries = new List<LibraryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            libraries.Add(new LibraryDto(reader.GetInt64(0), reader.GetString(1)));
        }

        return libraries;
    }

    public async Task<IReadOnlyList<FolderDto>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"SELECT folder.id,
                                    folder.root_path,
                                    folder.display_name,
                                    folder.enabled,
                                    folder.library_id,
                                    library.name,
                                    folder.desired_quality,
                                    folder.desired_quality_value,
                                    folder.auto_tag_profile_id,
                                    folder.auto_tag_enabled,
                                    folder.convert_enabled,
                                    folder.convert_format,
                                    folder.convert_bitrate
                               FROM folder
                          LEFT JOIN library ON library.id = folder.library_id
                           ORDER BY folder.display_name;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var folders = new List<FolderDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(await ReadFolderDtoAsync(reader, cancellationToken));
        }

        return folders;
    }

    public async Task<IReadOnlyList<FolderDto>> GetConfiguredEnabledMusicFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        var folders = await GetFoldersAsync(cancellationToken);
        return folders
            .Where(static folder => folder.Enabled
                && folder.LibraryId.HasValue
                && IsMusicFolderQuality(folder.DesiredQuality))
            .OrderBy(static folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static folder => folder.Id)
            .ToList();
    }

    public async Task<FolderLibraryScopeDto?> GetFolderScopeForTrackAsync(
        long trackId,
        long? preferredFolderId = null,
        long? preferredLibraryId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT f.id, f.library_id
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE tl.track_id = @trackId
  AND f.enabled = TRUE
  AND f.library_id IS NOT NULL
  AND (@folderId IS NULL OR f.id = @folderId)
  AND (@libraryId IS NULL OR f.library_id = @libraryId)
ORDER BY f.id
LIMIT 2;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)preferredFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue(LibraryIdField, (object?)preferredLibraryId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var scope = new FolderLibraryScopeDto(reader.GetInt64(0), reader.GetInt64(1));
        return await reader.ReadAsync(cancellationToken) ? null : scope;
    }

    public async Task<HistoryTrackScopeResolution> ResolveHistoryTrackScopeAsync(
        string? filePath,
        LibraryExistenceInput identity,
        CancellationToken cancellationToken = default)
    {
        var pathResolution = await ResolveHistoryTrackScopeByPathAsync(filePath, cancellationToken);
        if (pathResolution is not null)
        {
            return pathResolution;
        }

        var identityResolution = await ResolveLocalTrackIdentityAsync(identity, cancellationToken: cancellationToken);
        if (identityResolution.IsAmbiguous)
        {
            return new HistoryTrackScopeResolution(
                null,
                null,
                null,
                "ambiguous",
                identityResolution.Reason);
        }

        if (!identityResolution.LocalTrackId.HasValue)
        {
            return new HistoryTrackScopeResolution(
                null,
                null,
                null,
                "none",
                identityResolution.Reason);
        }

        var scope = await GetFolderScopeForTrackAsync(identityResolution.LocalTrackId.Value, cancellationToken: cancellationToken);
        return scope is null
            ? new HistoryTrackScopeResolution(
                null,
                null,
                null,
                "ambiguous",
                "The matched track exists in more than one enabled local folder scope.")
            : new HistoryTrackScopeResolution(
                identityResolution.LocalTrackId,
                scope.FolderId,
                scope.LibraryId,
                identityResolution.MatchType,
                identityResolution.Reason);
    }

    private async Task<HistoryTrackScopeResolution?> ResolveHistoryTrackScopeByPathAsync(
        string? filePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeComparableHistoryPath(filePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT tl.track_id,
       f.id,
       f.library_id,
       CASE WHEN LOWER(REPLACE(af.path, '\', '/')) = @path THEN 1 ELSE 0 END AS exact_path,
       LENGTH(COALESCE(af.relative_path, '')) AS relative_length
FROM audio_file af
JOIN track_local tl ON tl.audio_file_id = af.id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = TRUE
  AND f.library_id IS NOT NULL
  AND (
      LOWER(REPLACE(af.path, '\', '/')) = @path
      OR (
          af.relative_path IS NOT NULL
          AND TRIM(af.relative_path) <> ''
          AND (@path = LOWER(REPLACE(af.relative_path, '\', '/'))
               OR @path LIKE '%/' || LOWER(REPLACE(af.relative_path, '\', '/')))
      )
  )
ORDER BY exact_path DESC, relative_length DESC, f.id
LIMIT 20;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("path", normalizedPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<(long TrackId, long FolderId, long LibraryId, bool Exact, int RelativeLength)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt32(3) == 1,
                reader.GetInt32(4)));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var exactCandidates = candidates.Where(static candidate => candidate.Exact).ToList();
        var bestCandidates = exactCandidates.Count > 0
            ? exactCandidates
            : candidates.Where(candidate => candidate.RelativeLength == candidates.Max(static item => item.RelativeLength)).ToList();
        var scopes = bestCandidates
            .Select(static candidate => (candidate.TrackId, candidate.FolderId, candidate.LibraryId))
            .Distinct()
            .ToList();
        if (scopes.Count != 1)
        {
            return new HistoryTrackScopeResolution(
                null,
                null,
                null,
                "ambiguous",
                "The server path matches more than one enabled local library scope.");
        }

        var resolved = scopes[0];
        return new HistoryTrackScopeResolution(
            resolved.TrackId,
            resolved.FolderId,
            resolved.LibraryId,
            exactCandidates.Count > 0 ? "path" : "relative_path",
            exactCandidates.Count > 0
                ? "Matched the indexed audio-file path."
                : "Matched the indexed relative-path suffix.");
    }

    private static string? NormalizeComparableHistoryPath(string? value)
    {
        var normalized = value?.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Destination integrity repair intentionally evaluates playlist and artist preference paths in a single transactional workflow.")]
    public async Task<(int PlaylistPreferencesUpdated, int ArtistPreferencesUpdated)> RepairWatchlistDestinationEligibilityAsync(
        HashSet<long> validFolderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validFolderIds);
        var playlistUpdated = 0;
        var artistUpdated = 0;

        var playlistPreferences = await GetPlaylistWatchPreferencesAsync(cancellationToken);
        foreach (var preference in playlistPreferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationFolderId = preference.DestinationFolderId is long folderId && !validFolderIds.Contains(folderId)
                ? null
                : preference.DestinationFolderId;
            var atmosDestinationFolderId = preference.AtmosDestinationFolderId is long atmosFolderId && !validFolderIds.Contains(atmosFolderId)
                ? null
                : preference.AtmosDestinationFolderId;
            var routingRules = preference.RoutingRules?
                .Where(rule => validFolderIds.Contains(rule.DestinationFolderId))
                .ToList();
            if (routingRules is { Count: 0 })
            {
                routingRules = null;
            }

            if (destinationFolderId == preference.DestinationFolderId
                && atmosDestinationFolderId == preference.AtmosDestinationFolderId
                && !HaveRoutingRulesChanged(preference.RoutingRules, routingRules))
            {
                continue;
            }

            await UpsertPlaylistWatchPreferenceAsync(
                new PlaylistWatchPreferenceUpsertInput(
                    Source: preference.Source,
                    SourceId: preference.SourceId,
                    DestinationFolderId: destinationFolderId,
                    Service: preference.Service,
                    SyncTargets: preference.SyncTargets,
                    PreferredEngine: preference.PreferredEngine,
                    DownloadEngineOrder: preference.DownloadEngineOrder,
                    DownloadVariantMode: preference.DownloadVariantMode,
                    SyncMode: preference.SyncMode,
                    UpdateArtwork: preference.UpdateArtwork,
                    ReuseSavedArtwork: preference.ReuseSavedArtwork,
                    RoutingRules: routingRules,
                    IgnoreRules: preference.IgnoreRules,
                    AtmosDestinationFolderId: atmosDestinationFolderId),
                resetWatchState: false,
                cancellationToken);
            playlistUpdated++;
        }

        var watchlist = await GetWatchlistAsync(cancellationToken);
        foreach (var item in watchlist)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationFolderId = item.DestinationFolderId is long folderId && !validFolderIds.Contains(folderId)
                ? null
                : item.DestinationFolderId;
            var atmosDestinationFolderId = item.AtmosDestinationFolderId is long atmosFolderId && !validFolderIds.Contains(atmosFolderId)
                ? null
                : item.AtmosDestinationFolderId;
            var routingRules = item.RoutingRules?
                .Where(rule => validFolderIds.Contains(rule.DestinationFolderId))
                .ToList();
            if (routingRules is { Count: 0 })
            {
                routingRules = null;
            }

            if (destinationFolderId == item.DestinationFolderId
                && atmosDestinationFolderId == item.AtmosDestinationFolderId
                && !HaveRoutingRulesChanged(item.RoutingRules, routingRules))
            {
                continue;
            }

            await UpdateWatchlistPreferencesAsync(
                new ArtistWatchPreferenceUpdateInput(
                    item.ArtistId,
                    destinationFolderId,
                    item.WatchedAlbumGroups,
                    item.TopSongsEnabled,
                    item.LatestReleasesOnly,
                    item.PreferredEngine,
                    routingRules,
                    atmosDestinationFolderId,
                    item.DownloadVariantMode,
                    item.TopSongsSyncMode,
                    item.DownloadDiscographyEnabled,
                    item.IgnoreRules),
                cancellationToken);
            artistUpdated++;
        }

        return (playlistUpdated, artistUpdated);
    }

    private static async Task<FolderDto> ReadFolderDtoAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var desiredQuality = await ReadFolderDesiredQualityAsync(reader, cancellationToken);
        var autoTagProfileId = await ReadNullableStringAsync(reader, 8, cancellationToken);
        var autoTagEnabled = await reader.IsDBNullAsync(9, cancellationToken) || reader.GetBoolean(9);
        var convertEnabled = !await reader.IsDBNullAsync(10, cancellationToken) && reader.GetBoolean(10);
        var (convertFormat, convertBitrate) = await ReadFolderConvertSettingsAsync(reader, convertEnabled, cancellationToken);

        return new FolderDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            await ReadNullableInt64Async(reader, 4, cancellationToken),
            await ReadNullableStringAsync(reader, 5, cancellationToken),
            desiredQuality,
            autoTagProfileId,
            autoTagEnabled,
            convertEnabled,
            convertFormat,
            convertBitrate);
    }

    private static async Task<string> ReadFolderDesiredQualityAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var numericQuality = await reader.IsDBNullAsync(6, cancellationToken) ? 27 : reader.GetInt32(6);
        var qualityValue = await reader.IsDBNullAsync(7, cancellationToken) ? string.Empty : reader.GetString(7);
        return QualityCatalog.NormalizeLibraryFolderQualityValue(string.IsNullOrWhiteSpace(qualityValue)
            ? numericQuality.ToString(CultureInfo.InvariantCulture)
            : qualityValue);
    }

    private static async Task<(string? ConvertFormat, string? ConvertBitrate)> ReadFolderConvertSettingsAsync(
        SqliteDataReader reader,
        bool convertEnabled,
        CancellationToken cancellationToken)
    {
        if (!convertEnabled)
        {
            return (null, null);
        }

        var rawFormat = await ReadNullableStringAsync(reader, 11, cancellationToken);
        var rawBitrate = await ReadNullableStringAsync(reader, 12, cancellationToken);
        return (
            NormalizeFolderConvertFormat(rawFormat),
            NormalizeFolderConvertBitrate(rawBitrate));
    }

    public async Task<long> EnsurePlexUserAsync(string? username, string? plexUserId, string? serverUrl, string? machineId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(plexUserId))
        {
            throw new InvalidOperationException("Plex user identifier is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string lookupSql = @"
SELECT id
FROM plex_user
WHERE COALESCE(plex_user_id, '') = COALESCE(@plexUserId, '')
  AND COALESCE(username, '') = COALESCE(@username, '')
  AND COALESCE(plex_server_url, '') = COALESCE(@serverUrl, '');";
        await using (var lookup = new SqliteCommand(lookupSql, connection))
        {
            lookup.Parameters.AddWithValue("plexUserId", (object?)plexUserId ?? DBNull.Value);
            lookup.Parameters.AddWithValue("username", (object?)username ?? DBNull.Value);
            lookup.Parameters.AddWithValue("serverUrl", (object?)serverUrl ?? DBNull.Value);
            var existing = await lookup.ExecuteScalarAsync(cancellationToken);
            if (existing is long existingId)
            {
                return existingId;
            }
            if (existing is int existingInt)
            {
                return existingInt;
            }
        }

        const string insertSql = @"
INSERT INTO plex_user (username, plex_user_id, plex_server_url, plex_machine_identifier)
VALUES (@username, @plexUserId, @serverUrl, @machineId)
RETURNING id;";
        await using var insert = new SqliteCommand(insertSql, connection);
        insert.Parameters.AddWithValue("username", (object?)username ?? DBNull.Value);
        insert.Parameters.AddWithValue("plexUserId", (object?)plexUserId ?? DBNull.Value);
        insert.Parameters.AddWithValue("serverUrl", (object?)serverUrl ?? DBNull.Value);
        insert.Parameters.AddWithValue("machineId", (object?)machineId ?? DBNull.Value);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        return inserted is long insertedId ? insertedId : Convert.ToInt64(inserted);
    }

    public async Task<IReadOnlyList<long>> GetTrackIdsForLibraryAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        return await GetTrackIdsForLibraryScopeAsync(libraryId, null, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetTrackIdsForLibraryScopeAsync(
        long libraryId,
        long? folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT tl.track_id
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.library_id = @libraryId
  AND (@folderId IS NULL OR f.id = @folderId);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<LibraryRecommendationSeedTrackDto>> GetRecommendationSeedTracksForLibraryScopeAsync(
        long libraryId,
        long? folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       COALESCE(NULLIF(TRIM(t.title), ''), NULLIF(TRIM(t.tag_title), '')),
       COALESCE(NULLIF(TRIM(ar.name), ''), NULLIF(TRIM(t.tag_artist), '')),
       COALESCE(NULLIF(TRIM(al.title), ''), NULLIF(TRIM(t.tag_album), '')),
       COALESCE(af.duration_ms, t.duration_ms, t.tag_duration_ms),
       COALESCE(
           (SELECT source_id FROM track_source WHERE track_id = t.id AND LOWER(source) = 'isrc' LIMIT 1),
           NULLIF(TRIM(t.tag_isrc), '')),
       COALESCE(
           (SELECT source_id FROM track_source WHERE track_id = t.id AND LOWER(source) = 'deezer' LIMIT 1),
           NULLIF(TRIM(t.deezer_id), ''))
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
JOIN track t ON t.id = tl.track_id
LEFT JOIN album al ON al.id = t.album_id
LEFT JOIN artist ar ON ar.id = al.artist_id
WHERE f.library_id = @libraryId
  AND (@folderId IS NULL OR f.id = @folderId)
GROUP BY t.id
ORDER BY t.id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tracks = new List<LibraryRecommendationSeedTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tracks.Add(new LibraryRecommendationSeedTrackDto(
                reader.GetInt64(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? string.Empty : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? string.Empty : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? string.Empty : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt32(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6)));
        }

        return tracks;
    }

    public async Task<long?> GetTrackIdForFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT tl.track_id
FROM audio_file af
JOIN track_local tl ON tl.audio_file_id = af.id
WHERE af.path = @path
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("path", filePath);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            return await GetTrackIdForFilePathByFolderRelativeAsync(connection, filePath, cancellationToken);
        }
        return Convert.ToInt64(result);
    }

    public async Task<string?> GetTrackPrimaryFilePathAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return null;
        }

        var paths = await GetTrackPrimaryFilePathsAsync(new[] { trackId }, cancellationToken);
        return paths.TryGetValue(trackId, out var path) ? path : null;
    }

    /// <summary>
    /// Batch primary audio paths for DeezSpoTag local track ids (media-server hub resolution).
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string>> GetTrackPrimaryFilePathsAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        var distinct = trackIds.Where(static id => id > 0).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
WITH requested AS (
    SELECT CAST(value AS INTEGER) AS track_id
    FROM json_each(@trackIdsJson)
),
ranked AS (
    SELECT tl.track_id,
           af.path,
           af.relative_path,
           f.root_path,
           ROW_NUMBER() OVER (
               PARTITION BY tl.track_id
               ORDER BY af.quality_rank DESC NULLS LAST, af.size DESC, af.id DESC
           ) AS rn
    FROM track_local tl
    JOIN requested r ON r.track_id = tl.track_id
    JOIN audio_file af ON af.id = tl.audio_file_id
    LEFT JOIN folder f ON f.id = af.folder_id
)
SELECT track_id, path, relative_path, root_path
FROM ranked
WHERE rn = 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(distinct));
        var result = new Dictionary<long, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            var path = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
            var relativePath = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
            var rootPath = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3);
            var resolved = BuildAbsolutePath(rootPath, relativePath, path);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                result[trackId] = resolved;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<MediaServerIdentityRefreshFile>> GetMediaServerIdentityRefreshFilesAsync(
        IReadOnlyCollection<long> trackIds,
        string targetService,
        CancellationToken cancellationToken = default)
    {
        var distinct = trackIds.Where(static id => id > 0).Distinct().ToList();
        var service = NormalizeServiceKey(targetService);
        if (distinct.Count == 0 || string.IsNullOrWhiteSpace(service))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
WITH requested AS (
    SELECT CAST(value AS INTEGER) AS track_id FROM json_each(@trackIdsJson)
), ranked AS (
    SELECT tl.track_id,
           af.folder_id,
           af.path,
           af.relative_path,
           f.root_path,
           ROW_NUMBER() OVER (
               PARTITION BY tl.track_id
               ORDER BY af.quality_rank DESC NULLS LAST, af.size DESC, af.id DESC
           ) AS rn
    FROM track_local tl
    JOIN requested r ON r.track_id = tl.track_id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    LEFT JOIN media_server_track_metadata mtm
      ON mtm.track_id = tl.track_id
     AND mtm.service = @service
    WHERE f.enabled = TRUE
      AND NULLIF(TRIM(mtm.target_item_id), '') IS NULL
)
SELECT track_id, folder_id, path, relative_path, root_path
FROM ranked
WHERE rn = 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(distinct));
        command.Parameters.AddWithValue("service", service);
        var result = new List<MediaServerIdentityRefreshFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
            var relativePath = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3);
            var rootPath = await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4);
            var resolvedPath = BuildAbsolutePath(rootPath, relativePath, path);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                result.Add(new MediaServerIdentityRefreshFile(reader.GetInt64(0), reader.GetInt64(1), resolvedPath));
            }
        }

        return result;
    }

    public async Task<LocalTrackIdentityDto?> GetLocalTrackIdentityAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string identitySql = @"
SELECT COALESCE(NULLIF(TRIM(t.tag_title), ''), t.title),
       COALESCE(NULLIF(TRIM(t.tag_artist), ''), ar.name, ''),
       COALESCE(NULLIF(TRIM(t.tag_album), ''), al.title, ''),
       COALESCE(t.tag_duration_ms, t.duration_ms),
       COALESCE(
           (SELECT source_id FROM track_source WHERE track_id = t.id AND lower(source) = 'isrc' LIMIT 1),
           NULLIF(TRIM(t.tag_isrc), ''))
FROM track t
LEFT JOIN album al ON al.id = t.album_id
LEFT JOIN artist ar ON ar.id = al.artist_id
WHERE t.id = @trackId
LIMIT 1;";
        await using var identityCommand = new SqliteCommand(identitySql, connection);
        identityCommand.Parameters.AddWithValue(TrackIdField, trackId);
        await using var reader = await identityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var title = await reader.IsDBNullAsync(0, cancellationToken) ? string.Empty : reader.GetString(0);
        var artist = await reader.IsDBNullAsync(1, cancellationToken) ? string.Empty : reader.GetString(1);
        var album = await reader.IsDBNullAsync(2, cancellationToken) ? string.Empty : reader.GetString(2);
        int? durationMs = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt32(3);
        var isrc = await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4);
        await reader.DisposeAsync();

        const string sourcesSql = @"
SELECT lower(source), source_id
FROM track_source
WHERE track_id = @trackId
  AND source_id IS NOT NULL
  AND trim(source_id) <> '';";
        await using var sourcesCommand = new SqliteCommand(sourcesSql, connection);
        sourcesCommand.Parameters.AddWithValue(TrackIdField, trackId);
        await using var sourcesReader = await sourcesCommand.ExecuteReaderAsync(cancellationToken);
        var sourceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await sourcesReader.ReadAsync(cancellationToken))
        {
            sourceIds[sourcesReader.GetString(0)] = sourcesReader.GetString(1);
        }

        return new LocalTrackIdentityDto(trackId, title, artist, album, durationMs, isrc, sourceIds);
    }

    public async Task<IReadOnlyDictionary<string, LocalScanFileState>> GetLocalScanFileStatesAsync(
        long folderId,
        CancellationToken cancellationToken = default)
    {
        if (folderId <= 0)
        {
            return new Dictionary<string, LocalScanFileState>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT af.path,
       af.relative_path,
       f.root_path,
       COALESCE(af.size, 0),
       af.mtime,
       ar.name,
       al.title,
       t.title,
       t.tag_title,
       t.tag_artist,
       t.tag_album,
       t.tag_album_artist,
       t.tag_version,
       t.tag_label,
       t.tag_catalog_number,
       t.tag_bpm,
       t.tag_key,
       t.tag_track_total,
       t.tag_duration_ms,
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
       t.tag_meta_tagged_date,
       t.lyrics_unsynced,
       t.lyrics_synced,
       (SELECT group_concat(value, char(31)) FROM track_genre WHERE track_id = t.id),
       (SELECT group_concat(value, char(31)) FROM track_style WHERE track_id = t.id),
       (SELECT group_concat(value, char(31)) FROM track_mood WHERE track_id = t.id),
       (SELECT group_concat(value, char(31)) FROM track_remixer WHERE track_id = t.id),
       t.track_no,
       t.disc,
       COALESCE(af.duration_ms, t.duration_ms),
       t.lyrics_status,
       t.lyrics_type,
       af.codec,
       af.bitrate_kbps,
       af.sample_rate_hz,
       af.bits_per_sample,
       af.channels,
       af.quality_rank,
       af.audio_variant,
       COALESCE((SELECT source_id FROM track_source WHERE track_id = t.id AND LOWER(source) = 'deezer' LIMIT 1), t.deezer_id),
       COALESCE((SELECT source_id FROM track_source WHERE track_id = t.id AND LOWER(source) = 'isrc' LIMIT 1), t.tag_isrc),
       (SELECT source_id FROM album_source WHERE album_id = al.id AND LOWER(source) = 'deezer' LIMIT 1),
       (SELECT source_id FROM artist_source WHERE artist_id = ar.id AND LOWER(source) = 'deezer' LIMIT 1),
       (SELECT source_id FROM track_source WHERE track_id = t.id AND LOWER(source) = 'spotify' LIMIT 1),
       (SELECT source_id FROM album_source WHERE album_id = al.id AND LOWER(source) = 'spotify' LIMIT 1),
       (SELECT source_id FROM artist_source WHERE artist_id = ar.id AND LOWER(source) = 'spotify' LIMIT 1),
       (SELECT source_id FROM track_source WHERE track_id = t.id AND LOWER(source) = 'apple' LIMIT 1),
       (SELECT source_id FROM album_source WHERE album_id = al.id AND LOWER(source) = 'apple' LIMIT 1),
       (SELECT source_id FROM artist_source WHERE artist_id = ar.id AND LOWER(source) = 'apple' LIMIT 1),
       (SELECT source FROM track_source WHERE track_id = t.id AND LOWER(source) NOT IN ('deezer', 'spotify', 'apple', 'isrc') ORDER BY source LIMIT 1),
       (SELECT source_id FROM track_source WHERE track_id = t.id AND LOWER(source) NOT IN ('deezer', 'spotify', 'apple', 'isrc') ORDER BY source LIMIT 1)
FROM audio_file af
JOIN folder f ON f.id = af.folder_id
JOIN track_local tl ON tl.audio_file_id = af.id
JOIN track t ON t.id = tl.track_id
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
WHERE af.folder_id = @folderId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var states = new Dictionary<string, LocalScanFileState>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawPath = await ReadNullableStringAsync(reader, 0, cancellationToken);
            var relativePath = await ReadNullableStringAsync(reader, 1, cancellationToken);
            var rootPath = await ReadNullableStringAsync(reader, 2, cancellationToken);
            var filePath = BuildAbsolutePath(rootPath, relativePath, rawPath);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            var mtimeText = await ReadNullableStringAsync(reader, 4, cancellationToken);
            var lastWriteUtc = ParseDateTimeOffsetOrNull(mtimeText)?.UtcDateTime;
            if (!lastWriteUtc.HasValue)
            {
                continue;
            }

            var normalizedPath = NormalizeScanFilePath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var scan = new LocalTrackScanDto(
                await ReadNullableStringAsync(reader, 5, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 6, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 7, cancellationToken) ?? Path.GetFileNameWithoutExtension(filePath),
                filePath,
                await ReadNullableStringAsync(reader, 8, cancellationToken),
                await ReadNullableStringAsync(reader, 9, cancellationToken),
                await ReadNullableStringAsync(reader, 10, cancellationToken),
                await ReadNullableStringAsync(reader, 11, cancellationToken),
                await ReadNullableStringAsync(reader, 12, cancellationToken),
                await ReadNullableStringAsync(reader, 13, cancellationToken),
                await ReadNullableStringAsync(reader, 14, cancellationToken),
                await ReadNullableIntAsync(reader, 15, cancellationToken),
                await ReadNullableStringAsync(reader, 16, cancellationToken),
                await ReadNullableIntAsync(reader, 17, cancellationToken),
                await ReadNullableIntAsync(reader, 18, cancellationToken),
                await ReadNullableIntAsync(reader, 19, cancellationToken),
                await ReadNullableIntAsync(reader, 20, cancellationToken),
                await ReadNullableIntAsync(reader, 21, cancellationToken),
                await ReadNullableStringAsync(reader, 22, cancellationToken),
                await ReadNullableStringAsync(reader, 23, cancellationToken),
                await ReadNullableStringAsync(reader, 24, cancellationToken),
                await ReadNullableStringAsync(reader, 25, cancellationToken),
                await ReadNullableStringAsync(reader, 26, cancellationToken),
                await ReadNullableStringAsync(reader, 27, cancellationToken),
                await ReadNullableStringAsync(reader, 28, cancellationToken),
                await ReadNullableStringAsync(reader, 29, cancellationToken),
                await ReadNullableStringAsync(reader, 30, cancellationToken),
                await ReadNullableStringAsync(reader, 31, cancellationToken),
                ReadDelimitedValues(await ReadNullableStringAsync(reader, 32, cancellationToken)),
                ReadDelimitedValues(await ReadNullableStringAsync(reader, 33, cancellationToken)),
                ReadDelimitedValues(await ReadNullableStringAsync(reader, 34, cancellationToken)),
                ReadDelimitedValues(await ReadNullableStringAsync(reader, 35, cancellationToken)),
                Array.Empty<LocalTrackOtherTag>(),
                await ReadNullableIntAsync(reader, 36, cancellationToken),
                await ReadNullableIntAsync(reader, 37, cancellationToken),
                await ReadNullableIntAsync(reader, 38, cancellationToken),
                await ReadNullableStringAsync(reader, 39, cancellationToken),
                await ReadNullableStringAsync(reader, 40, cancellationToken),
                await ReadNullableStringAsync(reader, 41, cancellationToken),
                await ReadNullableIntAsync(reader, 42, cancellationToken),
                await ReadNullableIntAsync(reader, 43, cancellationToken),
                await ReadNullableIntAsync(reader, 44, cancellationToken),
                await ReadNullableIntAsync(reader, 45, cancellationToken),
                await ReadNullableIntAsync(reader, 46, cancellationToken),
                await ReadNullableStringAsync(reader, 47, cancellationToken),
                await ReadNullableStringAsync(reader, 48, cancellationToken),
                await ReadNullableStringAsync(reader, 49, cancellationToken),
                await ReadNullableStringAsync(reader, 50, cancellationToken),
                await ReadNullableStringAsync(reader, 51, cancellationToken),
                await ReadNullableStringAsync(reader, 52, cancellationToken),
                await ReadNullableStringAsync(reader, 53, cancellationToken),
                await ReadNullableStringAsync(reader, 54, cancellationToken),
                await ReadNullableStringAsync(reader, 55, cancellationToken),
                await ReadNullableStringAsync(reader, 56, cancellationToken),
                await ReadNullableStringAsync(reader, 57, cancellationToken),
                await ReadNullableStringAsync(reader, 58, cancellationToken),
                await ReadNullableStringAsync(reader, 59, cancellationToken));

            states[normalizedPath] = new LocalScanFileState(
                filePath,
                relativePath ?? string.Empty,
                reader.GetInt64(3),
                lastWriteUtc.Value,
                scan);
        }

        return states;
    }

    public async Task<IReadOnlyDictionary<long, ShazamTrackCacheDto>> GetShazamTrackCacheByTrackIdForLibraryAsync(
        long libraryId,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT lt.track_id,
       c.status,
       c.shazam_track_id,
       c.title,
       c.artist,
       c.isrc,
       c.related_tracks_json,
       c.scanned_at_utc,
       c.error,
       c.file_path,
       c.file_size,
       c.file_modified_utc,
       c.spotify_id,
       c.apple_id,
       c.deezer_id,
       c.album,
       c.release_date,
       c.explicit
FROM (
    SELECT DISTINCT tl.track_id
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.library_id = @libraryId
      AND (@folderId IS NULL OR f.id = @folderId)
) lt
LEFT JOIN track_shazam_cache c ON c.track_id = lt.track_id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var map = new Dictionary<long, ShazamTrackCacheDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            map[trackId] = await ReadShazamTrackCacheDtoAsync(trackId, reader, cancellationToken);
        }

        return map;
    }

    public async Task<IReadOnlyDictionary<long, ShazamTrackCacheDto>> GetShazamTrackCacheByTrackIdsAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        var ids = trackIds.Where(static id => id > 0).Distinct().ToArray();
        if (!IsConfigured || ids.Length == 0)
        {
            return new Dictionary<long, ShazamTrackCacheDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameterNames = new string[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            parameterNames[index] = $"@trackId{index}";
            command.Parameters.AddWithValue(parameterNames[index], ids[index]);
        }

        command.CommandText = $@"
SELECT requested.id,
       c.status,
       c.shazam_track_id,
       c.title,
       c.artist,
       c.isrc,
       c.related_tracks_json,
       c.scanned_at_utc,
       c.error,
       c.file_path,
       c.file_size,
       c.file_modified_utc,
       c.spotify_id,
       c.apple_id,
       c.deezer_id,
       c.album,
       c.release_date,
       c.explicit
FROM track requested
LEFT JOIN track_shazam_cache c ON c.track_id = requested.id
WHERE requested.id IN ({string.Join(", ", parameterNames)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var map = new Dictionary<long, ShazamTrackCacheDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            map[trackId] = await ReadShazamTrackCacheDtoAsync(trackId, reader, cancellationToken);
        }

        return map;
    }

    private static async Task<ShazamTrackCacheDto> ReadShazamTrackCacheDtoAsync(
        long trackId,
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var status = await reader.IsDBNullAsync(1, cancellationToken) ? "pending" : reader.GetString(1);
        var relatedTracks = DeserializeRecommendationTracks(await ReadNullableStringAsync(reader, 6, cancellationToken));
        var scannedAtUtc = ParseDateTimeOffsetOrNull(await ReadNullableStringAsync(reader, 7, cancellationToken));
        return new ShazamTrackCacheDto(
            trackId,
            status,
            await ReadNullableStringAsync(reader, 2, cancellationToken),
            await ReadNullableStringAsync(reader, 3, cancellationToken),
            await ReadNullableStringAsync(reader, 4, cancellationToken),
            await ReadNullableStringAsync(reader, 5, cancellationToken),
            relatedTracks,
            scannedAtUtc,
            await ReadNullableStringAsync(reader, 8, cancellationToken),
            await ReadNullableStringAsync(reader, 9, cancellationToken),
            await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetInt64(10),
            ParseDateTimeOffsetOrNull(await ReadNullableStringAsync(reader, 11, cancellationToken)),
            await ReadNullableStringAsync(reader, 12, cancellationToken),
            await ReadNullableStringAsync(reader, 13, cancellationToken),
            await ReadNullableStringAsync(reader, 14, cancellationToken),
            await ReadNullableStringAsync(reader, 15, cancellationToken),
            await ReadNullableStringAsync(reader, 16, cancellationToken),
            await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetInt64(17) != 0);
    }

    public async Task<IReadOnlyList<long>> GetTrackIdsNeedingShazamRefreshAsync(
        long libraryId,
        DateTimeOffset staleBeforeUtc,
        long? folderId = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = @"
SELECT lt.track_id
FROM (
    SELECT DISTINCT tl.track_id
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.library_id = @libraryId
      AND (@folderId IS NULL OR f.id = @folderId)
) lt
LEFT JOIN track_shazam_cache c ON c.track_id = lt.track_id
WHERE c.track_id IS NULL
   OR c.scanned_at_utc IS NULL
   OR julianday(c.scanned_at_utc) < julianday(@staleBeforeUtc)
ORDER BY COALESCE(c.scanned_at_utc, '0001-01-01T00:00:00.0000000+00:00') ASC,
         lt.track_id ASC";

        if (limit.HasValue && limit.Value > 0)
        {
            sql += "\nLIMIT @limit";
        }

        sql += ";";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("staleBeforeUtc", staleBeforeUtc.ToString("O"));
        if (limit.HasValue && limit.Value > 0)
        {
            command.Parameters.AddWithValue("limit", limit.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public async Task UpsertTrackShazamCacheAsync(
        TrackShazamCacheUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO track_shazam_cache (
    track_id,
    shazam_track_id,
    title,
    artist,
    isrc,
    status,
    related_tracks_json,
    scanned_at_utc,
    error,
    file_path,
    file_size,
    file_modified_utc,
    spotify_id,
    apple_id,
    deezer_id,
    album,
    release_date,
    explicit
)
VALUES (
    @trackId,
    @shazamTrackId,
    @title,
    @artist,
    @isrc,
    @status,
    @relatedTracksJson,
    @scannedAtUtc,
    @error,
    @filePath,
    @fileSize,
    @fileModifiedUtc,
    @spotifyId,
    @appleId,
    @deezerId,
    @album,
    @releaseDate,
    @explicit
)
ON CONFLICT(track_id) DO UPDATE SET
    shazam_track_id = excluded.shazam_track_id,
    title = excluded.title,
    artist = excluded.artist,
    isrc = excluded.isrc,
    status = excluded.status,
    related_tracks_json = excluded.related_tracks_json,
    scanned_at_utc = excluded.scanned_at_utc,
    error = excluded.error,
    file_path = excluded.file_path,
    file_size = excluded.file_size,
    file_modified_utc = excluded.file_modified_utc,
    spotify_id = excluded.spotify_id,
    apple_id = excluded.apple_id,
    deezer_id = excluded.deezer_id,
    album = excluded.album,
    release_date = excluded.release_date,
    explicit = excluded.explicit,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        var relatedTracksJson = input.RelatedTracks is { Count: > 0 } ? JsonSerializer.Serialize(input.RelatedTracks) : null;
        command.Parameters.AddWithValue(TrackIdField, input.TrackId);
        command.Parameters.AddWithValue("shazamTrackId", (object?)input.ShazamTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue(TitleField, (object?)input.Title ?? DBNull.Value);
        command.Parameters.AddWithValue(ArtistParameter, (object?)input.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("isrc", (object?)input.Isrc ?? DBNull.Value);
        command.Parameters.AddWithValue("status", string.IsNullOrWhiteSpace(input.Status) ? "pending" : input.Status.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("relatedTracksJson", (object?)relatedTracksJson ?? DBNull.Value);
        command.Parameters.AddWithValue("scannedAtUtc", input.ScannedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("error", (object?)input.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("filePath", (object?)input.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("fileSize", (object?)input.FileSize ?? DBNull.Value);
        command.Parameters.AddWithValue("fileModifiedUtc", input.FileModifiedUtc.HasValue ? input.FileModifiedUtc.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("spotifyId", (object?)input.SpotifyId ?? DBNull.Value);
        command.Parameters.AddWithValue("appleId", (object?)input.AppleId ?? DBNull.Value);
        command.Parameters.AddWithValue("deezerId", (object?)input.DeezerId ?? DBNull.Value);
        command.Parameters.AddWithValue("album", (object?)input.Album ?? DBNull.Value);
        command.Parameters.AddWithValue("releaseDate", (object?)input.ReleaseDate ?? DBNull.Value);
        command.Parameters.AddWithValue("explicit", input.Explicit.HasValue ? (input.Explicit.Value ? 1 : 0) : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddLocalDuplicateResolutionEventAsync(
        long winnerTrackId,
        long duplicateTrackId,
        string sourcePath,
        string? destinationPath,
        string status,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO local_duplicate_resolution_event (
    winner_track_id,
    duplicate_track_id,
    source_path,
    destination_path,
    status,
    error,
    created_at_utc,
    updated_at_utc
)
VALUES (
    @winnerTrackId,
    @duplicateTrackId,
    @sourcePath,
    @destinationPath,
    @status,
    @error,
    @now,
    @now
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("winnerTrackId", winnerTrackId);
        command.Parameters.AddWithValue("duplicateTrackId", duplicateTrackId);
        command.Parameters.AddWithValue("sourcePath", sourcePath);
        command.Parameters.AddWithValue("destinationPath", (object?)destinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AlbumTrackAudioInfoDto>> GetTrackAudioVariantsAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return Array.Empty<AlbumTrackAudioInfoDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT tl.track_id,
       af.id AS audio_file_id,
       af.audio_variant,
       af.codec,
       af.extension,
       af.bitrate_kbps,
       af.sample_rate_hz,
       af.bits_per_sample,
       af.channels,
       af.quality_rank,
       af.path,
       af.relative_path,
       f.root_path
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
LEFT JOIN folder f ON f.id = af.folder_id
WHERE tl.track_id = @trackId
ORDER BY f.enabled DESC,
         af.quality_rank DESC NULLS LAST,
         af.size DESC,
         af.id DESC;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<AlbumTrackAudioInfoDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(await ReadAlbumTrackAudioInfoAsync(reader, cancellationToken));
        }

        return results;
    }

    private static async Task<AlbumTrackAudioInfoDto> ReadAlbumTrackAudioInfoAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var audioFileId = await ReadNullableInt64Async(reader, 1, cancellationToken);
        var channels = await ReadNullableIntAsync(reader, 8, cancellationToken);
        var rawPath = await ReadNullableStringAsync(reader, 10, cancellationToken);
        var relativePath = await ReadNullableStringAsync(reader, 11, cancellationToken);
        var rootPath = await ReadNullableStringAsync(reader, 12, cancellationToken);
        var resolvedPath = BuildAbsolutePath(rootPath, relativePath, rawPath);
        var codec = await ReadNullableStringAsync(reader, 3, cancellationToken);
        var extension = await ReadNullableStringAsync(reader, 4, cancellationToken);
        var variant = ResolveAudioVariant(
            await ReadNullableStringAsync(reader, 2, cancellationToken),
            channels,
            resolvedPath,
            codec,
            extension);
        var isAtmos = string.Equals(variant, AtmosVariant, StringComparison.OrdinalIgnoreCase);

        return new AlbumTrackAudioInfoDto(
            reader.GetInt64(0),
            audioFileId,
            variant,
            codec,
            extension,
            await ReadNullableIntAsync(reader, 5, cancellationToken),
            await ReadNullableIntAsync(reader, 6, cancellationToken),
            await ReadNullableIntAsync(reader, 7, cancellationToken),
            channels,
            await ReadNullableIntAsync(reader, 9, cancellationToken),
            string.IsNullOrWhiteSpace(resolvedPath) ? rawPath : resolvedPath,
            !isAtmos,
            isAtmos);
    }

    private static string BuildAbsolutePath(string? rootPath, string? relativePath, string? fallbackPath)
    {
        if (!string.IsNullOrWhiteSpace(rootPath) && !string.IsNullOrWhiteSpace(relativePath))
        {
            var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Join(rootPath, normalizedRelative);
        }

        return fallbackPath ?? string.Empty;
    }

    private async Task<long?> GetTrackIdForFilePathByFolderRelativeAsync(
        SqliteConnection connection,
        string filePath,
        CancellationToken cancellationToken)
    {
        var folders = await GetFoldersAsync(cancellationToken);
        if (folders.Count == 0)
        {
            return null;
        }

        var folderRoots = folders
            .Select(folder => new FolderRoot(folder.Id, NormalizeRoot(folder.RootPath), folder.RootPath))
            .OrderByDescending(item => item.Root.Length)
            .ToList();
        var folderRoot = FindFolderForPath(folderRoots, filePath);
        if (folderRoot is null)
        {
            return null;
        }

        var relative = ComputeRelativePath(folderRoot.Root, filePath);
        const string sql = @"
SELECT tl.track_id
FROM audio_file af
JOIN track_local tl ON tl.audio_file_id = af.id
WHERE af.folder_id = @folderId
  AND af.relative_path = @relative
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(FolderIdParameter, folderRoot.Id);
        command.Parameters.AddWithValue("relative", relative);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            return null;
        }
        return Convert.ToInt64(result);
    }

    public async Task<bool> AddPlayHistoryAsync(
        PlayHistoryWriteInput input,
        CancellationToken cancellationToken = default)
    {
        var source = string.IsNullOrWhiteSpace(input.Source) ? "plex" : input.Source.Trim().ToLowerInvariant();
        var eventIdentity = FirstNonEmptyHistoryIdentity(input.PlexTrackKey, input.PlexRatingKey, input.TrackId);
        if (string.IsNullOrWhiteSpace(eventIdentity))
        {
            return false;
        }

        var remoteLibraryId = NormalizeRemoteLibraryId(input.RemoteLibraryId);
        var eventKey = $"{remoteLibraryId ?? "unscoped"}|{eventIdentity}|{input.PlayedAtUtc.ToUniversalTime():O}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = $@"
INSERT OR IGNORE INTO play_history
    (library_id, folder_id, plex_user_id, track_id, plex_track_key, plex_rating_key, event_key, played_at_utc, play_duration_ms, source, remote_library_id, metadata_json)
VALUES
    (@libraryId, @folderId, @plexUserId, @trackId, @plexTrackKey, @plexRatingKey, @eventKey, @playedAtUtc, @{DurationMsField}, @source, @remoteLibraryId, @metadataJson)
ON CONFLICT (plex_user_id, source, event_key) DO UPDATE SET
    library_id = CASE
        WHEN excluded.track_id IS NOT NULL THEN excluded.library_id
        ELSE COALESCE(play_history.library_id, excluded.library_id)
    END,
    folder_id = CASE
        WHEN excluded.track_id IS NOT NULL THEN excluded.folder_id
        ELSE COALESCE(play_history.folder_id, excluded.folder_id)
    END,
    track_id = COALESCE(excluded.track_id, play_history.track_id),
    plex_track_key = COALESCE(excluded.plex_track_key, play_history.plex_track_key),
    plex_rating_key = COALESCE(excluded.plex_rating_key, play_history.plex_rating_key),
    play_duration_ms = COALESCE(excluded.play_duration_ms, play_history.play_duration_ms),
    remote_library_id = COALESCE(excluded.remote_library_id, play_history.remote_library_id),
    metadata_json = COALESCE(excluded.metadata_json, play_history.metadata_json)
WHERE (play_history.track_id IS NULL AND excluded.track_id IS NOT NULL)
   OR (play_history.folder_id IS NULL AND excluded.folder_id IS NOT NULL)
   OR (play_history.library_id IS NULL AND excluded.library_id IS NOT NULL);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, (object?)input.LibraryId ?? DBNull.Value);
        command.Parameters.AddWithValue("folderId", (object?)input.FolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("plexUserId", input.PlexUserId);
        command.Parameters.AddWithValue(TrackIdField, (object?)input.TrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("plexTrackKey", (object?)input.PlexTrackKey ?? DBNull.Value);
        command.Parameters.AddWithValue("plexRatingKey", (object?)input.PlexRatingKey ?? DBNull.Value);
        command.Parameters.AddWithValue("eventKey", eventKey);
        command.Parameters.AddWithValue("playedAtUtc", input.PlayedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(DurationMsField, (object?)input.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("remoteLibraryId", (object?)remoteLibraryId ?? DBNull.Value);
        command.Parameters.AddWithValue("metadataJson", (object?)input.MetadataJson ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static string? FirstNonEmptyHistoryIdentity(string? trackKey, string? ratingKey, long? trackId)
    {
        if (!string.IsNullOrWhiteSpace(trackKey))
        {
            return $"key:{trackKey.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(ratingKey))
        {
            return $"rating:{ratingKey.Trim()}";
        }

        return trackId.HasValue ? $"track:{trackId.Value.ToString(CultureInfo.InvariantCulture)}" : null;
    }

    private static string? NormalizeRemoteLibraryId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public async Task<DateTimeOffset?> GetLatestPlayHistoryUtcAsync(
        long plexUserId,
        string source,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT MAX(played_at_utc)
FROM play_history
WHERE plex_user_id = @plexUserId
  AND source = @source;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue("source", string.IsNullOrWhiteSpace(source) ? "plex" : source.Trim().ToLowerInvariant());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? ParseDateTimeOffsetInvariant(text)
            : null;
    }

    public async Task<DateTimeOffset?> GetLatestPlayHistoryUtcForRemoteLibraryAsync(
        long userId,
        string source,
        string remoteLibraryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT MAX(played_at_utc)
FROM play_history
WHERE plex_user_id = @userId
  AND source = @source
  AND remote_library_id = @remoteLibraryId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("source", source.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("remoteLibraryId", remoteLibraryId.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull
            ? null
            : ParseDateTimeOffsetInvariant(Convert.ToString(result, CultureInfo.InvariantCulture)!);
    }

    public async Task<bool> TryClaimBackgroundJobAsync(
        string jobKey,
        TimeSpan interval,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobKey))
        {
            throw new ArgumentException("Background job key is required.", nameof(jobKey));
        }

        var normalizedKey = jobKey.Trim().ToLowerInvariant();
        var firstDueAt = nowUtc.Add(interval);
        var staleBefore = nowUtc.Subtract(TimeSpan.FromHours(2));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var ensureCommand = new SqliteCommand(@"
INSERT INTO background_job_state (job_key, status, next_due_at_utc, updated_at_utc)
VALUES (@jobKey, 'idle', @nextDueAtUtc, @nowUtc)
ON CONFLICT(job_key) DO NOTHING;", connection, transaction))
        {
            ensureCommand.Parameters.AddWithValue("jobKey", normalizedKey);
            ensureCommand.Parameters.AddWithValue("nextDueAtUtc", firstDueAt.ToString("O"));
            ensureCommand.Parameters.AddWithValue("nowUtc", nowUtc.ToString("O"));
            await ensureCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var claimCommand = new SqliteCommand(@"
UPDATE background_job_state
SET status = 'running',
    last_started_at_utc = @nowUtc,
    updated_at_utc = @nowUtc
WHERE job_key = @jobKey
  AND (status <> 'running' OR last_started_at_utc < @staleBeforeUtc)
  AND next_due_at_utc <= @nowUtc;", connection, transaction);
        claimCommand.Parameters.AddWithValue("jobKey", normalizedKey);
        claimCommand.Parameters.AddWithValue("nowUtc", nowUtc.ToString("O"));
        claimCommand.Parameters.AddWithValue("staleBeforeUtc", staleBefore.ToString("O"));
        var claimed = await claimCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task CompleteBackgroundJobAsync(
        string jobKey,
        TimeSpan interval,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await UpdateBackgroundJobAfterRunAsync(jobKey, "idle", completedAtUtc.Add(interval), completedAtUtc, cancellationToken);
    }

    public async Task FailBackgroundJobAsync(
        string jobKey,
        TimeSpan retryDelay,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await UpdateBackgroundJobAfterRunAsync(jobKey, "idle", failedAtUtc.Add(retryDelay), failedAtUtc, cancellationToken);
    }

    private async Task UpdateBackgroundJobAfterRunAsync(
        string jobKey,
        string status,
        DateTimeOffset nextDueAtUtc,
        DateTimeOffset finishedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE background_job_state
SET status = @status,
    last_finished_at_utc = @finishedAtUtc,
    next_due_at_utc = @nextDueAtUtc,
    updated_at_utc = @finishedAtUtc
WHERE job_key = @jobKey;", connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("finishedAtUtc", finishedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("nextDueAtUtc", nextDueAtUtc.ToString("O"));
        command.Parameters.AddWithValue("jobKey", jobKey.Trim().ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetTopTrackIdsAsync(long plexUserId, long libraryId, int limit, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT ph.track_id
FROM play_history ph
WHERE ph.plex_user_id = @plexUserId
  AND ph.library_id = @libraryId
  AND ph.track_id IS NOT NULL
GROUP BY ph.track_id
ORDER BY COUNT(ph.id) DESC
LIMIT @limit;";
        return await ExecuteTrackIdListQueryAsync(
            sql,
            command =>
            {
                command.Parameters.AddWithValue("plexUserId", plexUserId);
                command.Parameters.AddWithValue(LibraryIdField, libraryId);
                command.Parameters.AddWithValue("limit", limit);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetRediscoverTrackIdsAsync(long plexUserId, long libraryId, int limit, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT tl.track_id,
       COUNT(ph.id) AS plays
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
LEFT JOIN play_history ph ON ph.track_id = tl.track_id
    AND ph.plex_user_id = @plexUserId
    AND ph.library_id = @libraryId
WHERE f.library_id = @libraryId
GROUP BY tl.track_id
ORDER BY plays ASC
LIMIT @limit;";
        return await ExecuteTrackIdListQueryAsync(
            sql,
            command =>
            {
                command.Parameters.AddWithValue("plexUserId", plexUserId);
                command.Parameters.AddWithValue(LibraryIdField, libraryId);
                command.Parameters.AddWithValue("limit", limit);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetRandomTrackIdsAsync(long libraryId, int limit, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT DISTINCT tl.track_id
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.library_id = @libraryId
  AND f.enabled = 1
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%video%'
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%podcast%'
ORDER BY RANDOM()
LIMIT @limit;";
        return await ExecuteTrackIdListQueryAsync(
            sql,
            command =>
            {
                command.Parameters.AddWithValue(LibraryIdField, libraryId);
                command.Parameters.AddWithValue("limit", limit);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetUnplayedTrackIdsAsync(long plexUserId, long libraryId, int limit, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT DISTINCT tl.track_id
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.library_id = @libraryId
  AND NOT EXISTS (
      SELECT 1
      FROM play_history ph
      WHERE ph.track_id = tl.track_id
        AND ph.plex_user_id = @plexUserId
        AND ph.library_id = @libraryId
  )
ORDER BY RANDOM()
LIMIT @limit;";
        return await ExecuteTrackIdListQueryAsync(
            sql,
            command =>
            {
                command.Parameters.AddWithValue("plexUserId", plexUserId);
                command.Parameters.AddWithValue(LibraryIdField, libraryId);
                command.Parameters.AddWithValue("limit", limit);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetLeastPlayedTrackIdsAsync(long plexUserId, long libraryId, int limit, CancellationToken cancellationToken = default)
    {
        return await GetRediscoverTrackIdsAsync(plexUserId, libraryId, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetMostPlayedTrackIdsAsync(long plexUserId, long libraryId, int limit, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT tl.track_id,
       COUNT(ph.id) AS plays
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
LEFT JOIN play_history ph ON ph.track_id = tl.track_id
    AND ph.plex_user_id = @plexUserId
    AND ph.library_id = @libraryId
WHERE f.library_id = @libraryId
GROUP BY tl.track_id
HAVING plays > 0
ORDER BY plays DESC
LIMIT @limit;";
        return await ExecuteTrackIdListQueryAsync(
            sql,
            command =>
            {
                command.Parameters.AddWithValue("plexUserId", plexUserId);
                command.Parameters.AddWithValue(LibraryIdField, libraryId);
                command.Parameters.AddWithValue("limit", limit);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetTracksByDecadeAsync(long libraryId, int decadeStart, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT t.id
FROM track t
JOIN album a ON a.id = t.album_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.library_id = @libraryId
  AND a.release_date IS NOT NULL
  AND CAST(strftime('%Y', a.release_date) AS INTEGER) BETWEEN @startYear AND @endYear
ORDER BY RANDOM()
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue("startYear", decadeStart);
        command.Parameters.AddWithValue("endYear", decadeStart + 9);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<long>> GetTracksByAnalysisAsync(
        TrackAnalysisFilter filter,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT t.id
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.library_id = @libraryId
  AND ta.status IN ('complete', 'completed')
  AND (@minEnergy IS NULL OR ta.energy >= @minEnergy)
  AND (@maxEnergy IS NULL OR ta.energy <= @maxEnergy)
  AND (@minBpm IS NULL OR ta.bpm >= @minBpm)
  AND (@maxBpm IS NULL OR ta.bpm <= @maxBpm)
  AND (@minSpectralCentroid IS NULL OR ta.spectral_centroid >= @minSpectralCentroid)
  AND (@maxSpectralCentroid IS NULL OR ta.spectral_centroid <= @maxSpectralCentroid)
ORDER BY RANDOM()
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        BindTrackAnalysisParameters(
            command,
            filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static void BindTrackAnalysisParameters(SqliteCommand command, TrackAnalysisFilter filter)
    {
        command.Parameters.AddWithValue(LibraryIdField, filter.LibraryId);
        AddNullableParameter(command, "minEnergy", filter.MinEnergy);
        AddNullableParameter(command, "maxEnergy", filter.MaxEnergy);
        AddNullableParameter(command, "minBpm", filter.MinBpm);
        AddNullableParameter(command, "maxBpm", filter.MaxBpm);
        AddNullableParameter(command, "minSpectralCentroid", filter.MinSpectralCentroid);
        AddNullableParameter(command, "maxSpectralCentroid", filter.MaxSpectralCentroid);
        command.Parameters.AddWithValue("limit", filter.Limit);
    }

    private static void AddNullableParameter(SqliteCommand command, string name, double? value)
    {
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
    }

    public async Task<IReadOnlyList<PlayHistoryEntryDto>> GetPlayHistoryEntriesAsync(
        long plexUserId,
        long libraryId,
        DateTimeOffset lookbackStartUtc,
        IReadOnlyList<int> allowedHours,
        DateTimeOffset excludeAfterUtc,
        CancellationToken cancellationToken = default,
        long? folderId = null,
        TimeSpan? localUtcOffset = null)
    {
        if (allowedHours.Count == 0)
        {
            return Array.Empty<PlayHistoryEntryDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT ph.track_id,
       MAX(ph.played_at_utc) AS last_played_at_utc,
       COUNT(*) AS play_count
FROM play_history ph
WHERE ph.plex_user_id = @plexUserId
  AND ph.library_id = @libraryId
  AND (@folderId IS NULL OR ph.folder_id = @folderId)
  AND ph.track_id IS NOT NULL
  AND ph.played_at_utc >= @lookbackStart
  AND ph.played_at_utc < @excludeAfter
  AND CAST(strftime('%H', datetime(ph.played_at_utc, @utcOffsetModifier)) AS INTEGER) IN (
      SELECT CAST(value AS INTEGER)
      FROM json_each(@allowedHoursJson)
  )
GROUP BY ph.track_id
ORDER BY last_played_at_utc DESC
LIMIT @historyLimit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("lookbackStart", lookbackStartUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("excludeAfter", excludeAfterUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "utcOffsetModifier",
            $"{(localUtcOffset.GetValueOrDefault() < TimeSpan.Zero ? "-" : "+")}{Math.Abs((int)localUtcOffset.GetValueOrDefault().TotalMinutes)} minutes");
        command.Parameters.AddWithValue("allowedHoursJson", SerializeJsonArray(allowedHours));
        command.Parameters.AddWithValue("historyLimit", 50000);

        var results = new List<PlayHistoryEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var playedAt = ParseDateTimeOffsetInvariant(reader.GetString(1));
            results.Add(new PlayHistoryEntryDto(reader.GetInt64(0), playedAt, reader.GetInt32(2)));
        }

        return results;
    }

    public async Task<IReadOnlySet<long>> GetPlayedTrackIdsSinceAsync(
        long plexUserId,
        long libraryId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default,
        long? folderId = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT ph.track_id
FROM play_history ph
WHERE ph.plex_user_id = @plexUserId
  AND ph.library_id = @libraryId
  AND (@folderId IS NULL OR ph.folder_id = @folderId)
  AND ph.track_id IS NOT NULL
  AND ph.played_at_utc >= @sinceUtc;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("sinceUtc", sinceUtc.ToUniversalTime().ToString("O"));

        var ids = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> GetMoodTagsForTracksAsync(
        IReadOnlyList<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<string>>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT track_id, mood_tags
FROM track_analysis
WHERE track_id IN (
    SELECT CAST(value AS INTEGER)
    FROM json_each(@trackIdsJson)
)
  AND mood_tags IS NOT NULL;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));

        var results = new Dictionary<long, IReadOnlyList<string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            var tagsJson = reader.GetString(1);
            var tags = DeserializeMoodTags(tagsJson) ?? Array.Empty<string>();
            results[trackId] = tags;
        }

        return results;
    }

    public async Task<IReadOnlyList<DecadeBucketDto>> GetDecadesAsync(long libraryId, int minimumTracks, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT (CAST(strftime('%Y', a.release_date) AS INTEGER) / 10) * 10 AS decade,
       COUNT(t.id) AS track_count
FROM track t
JOIN album a ON a.id = t.album_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.library_id = @libraryId
  AND a.release_date IS NOT NULL
GROUP BY decade
HAVING track_count >= @minimumTracks
ORDER BY decade DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue("minimumTracks", minimumTracks);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<DecadeBucketDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DecadeBucketDto(reader.GetInt32(0), reader.GetInt32(1)));
        }
        return results;
    }

    public async Task<IReadOnlyList<MixTrackDto>> GetTrackSummariesAsync(IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0)
        {
            return Array.Empty<MixTrackDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
	SELECT t.id,
	       t.title,
	       ar.name,
	       a.title,
	       a.preferred_cover_path,
	       t.duration_ms,
	       selected_audio.audio_file_id,
	       selected_audio.file_path,
	       selected_audio.audio_variant
	FROM track t
	JOIN album a ON a.id = t.album_id
	JOIN artist ar ON ar.id = a.artist_id
	LEFT JOIN (
	    SELECT track_id,
	           audio_file_id,
	           audio_variant,
	           file_path
	    FROM (
	        SELECT tl.track_id,
	               af.id AS audio_file_id,
	               af.audio_variant,
	               COALESCE(
	                   CASE
	                       WHEN f.root_path IS NOT NULL AND af.relative_path IS NOT NULL AND TRIM(af.relative_path) <> ''
	                       THEN rtrim(f.root_path, '/\') || '/' || af.relative_path
	                   END,
	                   af.path) AS file_path,
	               ROW_NUMBER() OVER (
	                   PARTITION BY tl.track_id
	                   ORDER BY f.enabled DESC,
	                            af.quality_rank DESC NULLS LAST,
	                            af.size DESC,
	                            af.id DESC) AS rn
	        FROM track_local tl
	        JOIN audio_file af ON af.id = tl.audio_file_id
	        LEFT JOIN folder f ON f.id = af.folder_id
	    )
	    WHERE rn = 1
	) selected_audio ON selected_audio.track_id = t.id
	WHERE t.id IN (
	    SELECT CAST(value AS INTEGER)
	    FROM json_each(@trackIdsJson)
	);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<MixTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MixTrackDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
	                reader.GetString(3),
	                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
	                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5),
	                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt64(6),
	                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
	                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
	                BuildVariantKey(reader.GetInt64(0), await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt64(6), 0)));
        }

        var order = new Dictionary<long, int>();
        for (var i = 0; i < trackIds.Count; i++)
        {
            var trackId = trackIds[i];
            if (!order.ContainsKey(trackId))
            {
                order[trackId] = i;
            }
        }

        return results
            .OrderBy(track => order.TryGetValue(track.TrackId, out var index) ? index : int.MaxValue)
            .ToList();
    }

    public async Task<long?> GetLibraryIdForTrackAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT f.library_id
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE tl.track_id = @trackId
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", trackId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<int> BackfillPlayHistoryLibraryIdsAsync(CancellationToken cancellationToken = default)
    {
        await BackfillPlayHistoryFolderIdsAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE play_history AS ph
SET library_id = COALESCE(
    (
        SELECT f.library_id
        FROM folder f
        WHERE f.id = ph.folder_id
    ),
    (
        SELECT MIN(f.library_id)
        FROM track_local tl
        JOIN audio_file af ON af.id = tl.audio_file_id
        JOIN folder f ON f.id = af.folder_id
        WHERE tl.track_id = ph.track_id
          AND f.library_id IS NOT NULL
        GROUP BY tl.track_id
        HAVING COUNT(DISTINCT f.library_id) = 1
    )
)
WHERE ph.library_id IS NULL
  AND ph.track_id IS NOT NULL
  AND (
      ph.folder_id IS NOT NULL
      OR 1 = (
          SELECT COUNT(DISTINCT f.library_id)
          FROM track_local tl
          JOIN audio_file af ON af.id = tl.audio_file_id
          JOIN folder f ON f.id = af.folder_id
          WHERE tl.track_id = ph.track_id
            AND f.library_id IS NOT NULL
      )
  );";
        await using var command = new SqliteCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> BackfillPlayHistoryFolderIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string repairSql = @"
UPDATE play_history AS ph
SET folder_id = NULL,
    library_id = NULL
WHERE ph.track_id IS NOT NULL
  AND 1 <> (
      SELECT COUNT(DISTINCT af.folder_id)
      FROM track_local tl
      JOIN audio_file af ON af.id = tl.audio_file_id
      WHERE tl.track_id = ph.track_id
  );

UPDATE play_history AS ph
SET folder_id = (
    SELECT MIN(af.folder_id)
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    WHERE tl.track_id = ph.track_id
    GROUP BY tl.track_id
    HAVING COUNT(DISTINCT af.folder_id) = 1
),
    library_id = (
        SELECT MIN(f.library_id)
        FROM track_local tl
        JOIN audio_file af ON af.id = tl.audio_file_id
        JOIN folder f ON f.id = af.folder_id
        WHERE tl.track_id = ph.track_id
        GROUP BY tl.track_id
        HAVING COUNT(DISTINCT af.folder_id) = 1
    )
WHERE ph.track_id IS NOT NULL
  AND 1 = (
      SELECT COUNT(DISTINCT af.folder_id)
      FROM track_local tl
      JOIN audio_file af ON af.id = tl.audio_file_id
      WHERE tl.track_id = ph.track_id
  );";
        await using var repair = new SqliteCommand(repairSql, connection);
        return await repair.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteLegacyMelodayMixesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string legacyPredicate = @"
mix_id LIKE 'meloday-%'
AND mix_id NOT GLOB 'meloday-direct-[0-9]*'
AND mix_id NOT GLOB 'meloday-sonic-[0-9]*'";
        await using (var items = new SqliteCommand(
            $"DELETE FROM mix_item WHERE mix_cache_id IN (SELECT id FROM mix_cache WHERE {legacyPredicate});",
            connection,
            transaction))
        {
            await items.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var mixes = new SqliteCommand($"DELETE FROM mix_cache WHERE {legacyPredicate};", connection, transaction);
        var deleted = await mixes.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteInactiveMelodayMixesAsync(
        IReadOnlyCollection<long> activeLibraryIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var activeJson = SerializeJsonArray(activeLibraryIds.Distinct().ToList());
        const string predicate = @"
mix_id GLOB 'meloday-direct-[0-9]*' OR mix_id GLOB 'meloday-sonic-[0-9]*'";
        const string inactive = @"
library_id NOT IN (SELECT CAST(value AS INTEGER) FROM json_each(@activeLibraryIdsJson))";
        await using (var items = new SqliteCommand(
            $"DELETE FROM mix_item WHERE mix_cache_id IN (SELECT id FROM mix_cache WHERE ({predicate}) AND {inactive});",
            connection,
            transaction))
        {
            items.Parameters.AddWithValue("activeLibraryIdsJson", activeJson);
            await items.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var mixes = new SqliteCommand(
            $"DELETE FROM mix_cache WHERE ({predicate}) AND {inactive};", connection, transaction);
        mixes.Parameters.AddWithValue("activeLibraryIdsJson", activeJson);
        var deleted = await mixes.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<IReadOnlyList<string>> GetPlexRatingKeysAsync(IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT ph.plex_rating_key
FROM play_history ph
WHERE ph.track_id IN (
    SELECT CAST(value AS INTEGER)
    FROM json_each(@trackIdsJson)
)
  AND ph.plex_rating_key IS NOT NULL;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    public async Task<IReadOnlyDictionary<long, string>> GetPlexRatingKeysByTrackIdsAsync(
        IReadOnlyList<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
WITH requested AS (
    SELECT CAST(value AS INTEGER) AS track_id
    FROM json_each(@trackIdsJson)
),
metadata AS (
    SELECT tpm.track_id,
           tpm.plex_rating_key,
           0 AS sort_group,
           tpm.updated_at_utc AS sort_utc
    FROM track_plex_metadata tpm
    JOIN requested r ON r.track_id = tpm.track_id
    WHERE tpm.plex_rating_key IS NOT NULL
      AND TRIM(tpm.plex_rating_key) <> ''
),
history AS (
    SELECT ph.track_id,
           ph.plex_rating_key,
           1 AS sort_group,
           ph.played_at_utc AS sort_utc
    FROM play_history ph
    JOIN requested r ON r.track_id = ph.track_id
    WHERE ph.plex_rating_key IS NOT NULL
      AND TRIM(ph.plex_rating_key) <> ''
)
SELECT track_id,
       plex_rating_key
FROM (
    SELECT * FROM metadata
    UNION ALL
    SELECT * FROM history
)
ORDER BY track_id, sort_group, sort_utc DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));

        var mapping = new Dictionary<long, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            if (mapping.ContainsKey(trackId))
            {
                continue;
            }

            mapping[trackId] = reader.GetString(1);
        }

        return mapping;
    }

    public async Task UpsertPlexTrackMetadataAsync(
        IReadOnlyCollection<PlexTrackMetadataUpsertDto> metadata,
        CancellationToken cancellationToken = default)
    {
        if (metadata.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
INSERT INTO track_plex_metadata
    (track_id, plex_rating_key, updated_at_utc)
VALUES
    (@trackId, @ratingKey, @updatedAt)
ON CONFLICT(track_id) DO UPDATE SET
    plex_rating_key = excluded.plex_rating_key,
    updated_at_utc = excluded.updated_at_utc;";

        await using var command = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
        var trackIdParameter = command.Parameters.Add("@trackId", SqliteType.Integer);
        var ratingKeyParameter = command.Parameters.Add("@ratingKey", SqliteType.Text);
        var updatedAtParameter = command.Parameters.Add("@updatedAt", SqliteType.Text);

        foreach (var item in metadata)
        {
            if (item.TrackId <= 0 || string.IsNullOrWhiteSpace(item.PlexRatingKey))
            {
                continue;
            }

            trackIdParameter.Value = item.TrackId;
            ratingKeyParameter.Value = item.PlexRatingKey.Trim();
            updatedAtParameter.Value = item.UpdatedAtUtc.ToString("O");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteConfirmedMissingPlexTrackMetadataAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrackIds = trackIds.Where(static id => id > 0).Distinct().ToList();
        if (normalizedTrackIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
DELETE FROM track_plex_metadata
WHERE track_id IN (SELECT CAST(value AS INTEGER) FROM json_each(@trackIdsJson));
DELETE FROM media_server_track_metadata
WHERE service='plex'
  AND track_id IN (SELECT CAST(value AS INTEGER) FROM json_each(@trackIdsJson));";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(normalizedTrackIds));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, string>> GetMediaServerItemIdsByTrackIdsAsync(
        string service,
        IReadOnlyList<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedService = NormalizeServiceKey(service);
        if (string.IsNullOrWhiteSpace(normalizedService) || trackIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
WITH requested AS (
    SELECT CAST(value AS INTEGER) AS track_id
    FROM json_each(@trackIdsJson)
)
SELECT track_id,
       target_item_id
FROM (
    SELECT mst.track_id,
           mst.target_item_id,
           CASE WHEN mst.audio_variant = 'stereo' THEN 0 ELSE 1 END AS variant_order,
           mst.updated_at_utc
    FROM media_server_track_variant_metadata mst
    JOIN requested r ON r.track_id = mst.track_id
    WHERE mst.service = @service
      AND mst.target_item_id IS NOT NULL
      AND TRIM(mst.target_item_id) <> ''
    UNION ALL
    SELECT legacy.track_id,
           legacy.target_item_id,
           2 AS variant_order,
           legacy.updated_at_utc
    FROM media_server_track_metadata legacy
    JOIN requested r ON r.track_id = legacy.track_id
    WHERE legacy.service = @service
      AND legacy.target_item_id IS NOT NULL
      AND TRIM(legacy.target_item_id) <> ''
)
ORDER BY track_id, variant_order, updated_at_utc DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("service", normalizedService);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));

        var mapping = new Dictionary<long, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            if (!mapping.ContainsKey(trackId))
            {
                mapping[trackId] = reader.GetString(1);
            }
        }

        return mapping;
    }

    public async Task<IReadOnlyDictionary<long, TrackTargetServerIdsDto>> GetAlbumTrackTargetServerIdsAsync(
        long albumId,
        CancellationToken cancellationToken = default)
    {
        if (albumId <= 0)
        {
            return new Dictionary<long, TrackTargetServerIdsDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       MAX(CASE WHEN lower(m.service) = 'plex' THEN m.target_item_id END) AS plex_track_id,
       MAX(CASE WHEN lower(m.service) = 'jellyfin' THEN m.target_item_id END) AS jellyfin_track_id,
       MAX(CASE WHEN lower(m.service) = 'navidrome' THEN m.target_item_id END) AS navidrome_track_id
FROM track t
LEFT JOIN media_server_track_metadata m
  ON m.track_id = t.id
 AND m.target_item_id IS NOT NULL
 AND TRIM(m.target_item_id) <> ''
WHERE t.album_id = @albumId
GROUP BY t.id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("albumId", albumId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<long, TrackTargetServerIdsDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            result[trackId] = new TrackTargetServerIdsDto(
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3));
        }

        return result;
    }

    public async Task<IReadOnlyList<TargetServerIdentityCoverageDto>> GetTargetServerIdentityCoverageAsync(
        IReadOnlyCollection<string> services,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedServices = NormalizeTargetServices(services);
        if (normalizedServices.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
WITH local_tracks AS (
    SELECT DISTINCT tl.track_id
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.enabled = TRUE
      AND (@folderId IS NULL OR f.id = @folderId)
),
services AS (
    SELECT value AS service
    FROM json_each(@servicesJson)
)
SELECT services.service,
       (SELECT COUNT(*) FROM local_tracks) AS total_tracks,
       COUNT(DISTINCT m.track_id) AS mapped_tracks
FROM services
LEFT JOIN media_server_track_metadata m
  ON lower(m.service) = services.service
 AND m.track_id IN (SELECT track_id FROM local_tracks)
 AND m.target_item_id IS NOT NULL
 AND TRIM(m.target_item_id) <> ''
GROUP BY services.service
ORDER BY services.service;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("servicesJson", SerializeJsonArray(normalizedServices));
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TargetServerIdentityCoverageDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var service = reader.GetString(0);
            var total = reader.GetInt32(1);
            var mapped = reader.GetInt32(2);
            result.Add(new TargetServerIdentityCoverageDto(
                service,
                total,
                mapped,
                Math.Max(0, total - mapped)));
        }

        return result;
    }

    public async Task<IReadOnlyList<TargetServerIdentityLocalTrackDto>> GetTargetServerIdentityLocalTracksAsync(
        string service,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedService = NormalizeServiceKey(service);
        if (string.IsNullOrWhiteSpace(normalizedService))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT
       tl.track_id,
       af.path,
       af.relative_path,
       f.root_path,
       COALESCE(NULLIF(t.tag_title, ''), t.title, '') AS title,
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name, '') AS artist,
       COALESCE(NULLIF(t.tag_album, ''), al.title, '') AS album,
       COALESCE(t.tag_duration_ms, t.duration_ms, af.duration_ms) AS duration_ms,
       m.target_item_id
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
JOIN track t ON t.id = tl.track_id
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
LEFT JOIN media_server_track_metadata m
  ON m.track_id = tl.track_id
 AND m.service = @service
 AND m.target_item_id IS NOT NULL
 AND TRIM(m.target_item_id) <> ''
WHERE f.enabled = TRUE
  AND (@folderId IS NULL OR f.id = @folderId);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("service", normalizedService);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TargetServerIdentityLocalTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var rootPath = await ReadNullableStringAsync(reader, 3, cancellationToken);
            var relativePath = await ReadNullableStringAsync(reader, 2, cancellationToken);
            var rawPath = await ReadNullableStringAsync(reader, 1, cancellationToken);
            result.Add(new TargetServerIdentityLocalTrackDto(
                reader.GetInt64(0),
                BuildAbsolutePath(rootPath, relativePath, rawPath),
                relativePath,
                await ReadNullableStringAsync(reader, 4, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 5, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 6, cancellationToken) ?? string.Empty,
                await ReadNullableIntAsync(reader, 7, cancellationToken),
                await ReadNullableStringAsync(reader, 8, cancellationToken)));
        }

        return result;
    }

    public async Task<int> DeleteMediaServerTrackMetadataForScopeAsync(
        string service,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedService = NormalizeServiceKey(service);
        if (string.IsNullOrWhiteSpace(normalizedService))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string scopedTracks = @"
SELECT DISTINCT tl.track_id
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = TRUE
  AND (@folderId IS NULL OR f.id = @folderId)";
        var deleted = 0;
        await using (var command = new SqliteCommand($@"
DELETE FROM media_server_track_variant_metadata
WHERE service = @service
  AND track_id IN ({scopedTracks});", connection, transaction))
        {
            command.Parameters.AddWithValue("service", normalizedService);
            command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new SqliteCommand($@"
DELETE FROM media_server_track_metadata
WHERE service = @service
  AND track_id IN ({scopedTracks});", connection, transaction))
        {
            command.Parameters.AddWithValue("service", normalizedService);
            command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (normalizedService == "plex")
        {
            await using var command = new SqliteCommand($@"
DELETE FROM track_plex_metadata
WHERE track_id IN ({scopedTracks});", connection, transaction);
            command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private static List<string> NormalizeTargetServices(IReadOnlyCollection<string> services)
        => services
            .Select(NormalizeServiceKey)
            .Where(static service => service is "plex" or "jellyfin" or "navidrome")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<int> DeleteOrphanedMediaServerTrackMetadataAsync(
        string service,
        CancellationToken cancellationToken = default)
    {
        var normalizedService = NormalizeServiceKey(service);
        if (string.IsNullOrWhiteSpace(normalizedService))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string deleteVariantsSql = @"
DELETE FROM media_server_track_variant_metadata
WHERE service = @service
  AND NOT EXISTS (
      SELECT 1
      FROM track_local tl
      WHERE tl.track_id = media_server_track_variant_metadata.track_id
  );";
        await using (var deleteVariants = new SqliteCommand(deleteVariantsSql, connection, transaction))
        {
            deleteVariants.Parameters.AddWithValue("service", normalizedService);
            await deleteVariants.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteMetadataSql = @"
DELETE FROM media_server_track_metadata
WHERE service = @service
  AND NOT EXISTS (
      SELECT 1
      FROM track_local tl
      WHERE tl.track_id = media_server_track_metadata.track_id
  );";
        int deleted;
        await using (var deleteMetadata = new SqliteCommand(deleteMetadataSql, connection, transaction))
        {
            deleteMetadata.Parameters.AddWithValue("service", normalizedService);
            deleted = await deleteMetadata.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task UpsertMediaServerTrackMetadataAsync(
        IReadOnlyCollection<MediaServerTrackMetadataUpsertDto> metadata,
        CancellationToken cancellationToken = default)
    {
        await UpsertMediaServerTrackMetadataReturningNewAsync(metadata, cancellationToken);
    }

    public async Task<IReadOnlyList<(long TrackId, string Service)>> UpsertMediaServerTrackMetadataReturningNewAsync(
        IReadOnlyCollection<MediaServerTrackMetadataUpsertDto> metadata,
        CancellationToken cancellationToken = default)
    {
        if (metadata.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var newlyResolved = new List<(long TrackId, string Service)>();
        const string sql = @"
INSERT INTO media_server_track_metadata
    (track_id, service, target_item_id, file_path, updated_at_utc)
VALUES
    (@trackId, @service, @targetItemId, @filePath, @updatedAt)
ON CONFLICT(track_id, service) DO UPDATE SET
    target_item_id = excluded.target_item_id,
    file_path = excluded.file_path,
    updated_at_utc = excluded.updated_at_utc;";
        const string variantSql = @"
INSERT INTO media_server_track_variant_metadata
    (track_id, service, audio_variant, target_item_id, file_path, updated_at_utc)
VALUES
    (@trackId, @service, @audioVariant, @targetItemId, @filePath, @updatedAt)
ON CONFLICT(track_id, service, audio_variant) DO UPDATE SET
    target_item_id = excluded.target_item_id,
    file_path = excluded.file_path,
    updated_at_utc = excluded.updated_at_utc;";

        await using var command = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
        var trackIdParameter = command.Parameters.Add("@trackId", SqliteType.Integer);
        var serviceParameter = command.Parameters.Add("@service", SqliteType.Text);
        var targetItemIdParameter = command.Parameters.Add("@targetItemId", SqliteType.Text);
        var filePathParameter = command.Parameters.Add("@filePath", SqliteType.Text);
        var updatedAtParameter = command.Parameters.Add("@updatedAt", SqliteType.Text);
        await using var variantCommand = new SqliteCommand(variantSql, connection, (SqliteTransaction)transaction);
        var variantTrackIdParameter = variantCommand.Parameters.Add("@trackId", SqliteType.Integer);
        var variantServiceParameter = variantCommand.Parameters.Add("@service", SqliteType.Text);
        var audioVariantParameter = variantCommand.Parameters.Add("@audioVariant", SqliteType.Text);
        var variantTargetItemIdParameter = variantCommand.Parameters.Add("@targetItemId", SqliteType.Text);
        var variantFilePathParameter = variantCommand.Parameters.Add("@filePath", SqliteType.Text);
        var variantUpdatedAtParameter = variantCommand.Parameters.Add("@updatedAt", SqliteType.Text);

        await using var existsCommand = new SqliteCommand(
            "SELECT 1 FROM media_server_track_metadata WHERE track_id=@trackId AND service=@service LIMIT 1;",
            connection,
            (SqliteTransaction)transaction);
        var existsTrackIdParameter = existsCommand.Parameters.Add("@trackId", SqliteType.Integer);
        var existsServiceParameter = existsCommand.Parameters.Add("@service", SqliteType.Text);
        const string deleteReassignedSql = @"
DELETE FROM media_server_track_metadata
WHERE service = @service
  AND target_item_id = @targetItemId
  AND track_id <> @trackId;
DELETE FROM media_server_track_variant_metadata
WHERE service = @service
  AND target_item_id = @targetItemId
  AND track_id <> @trackId;";
        await using var deleteReassignedCommand = new SqliteCommand(
            deleteReassignedSql,
            connection,
            (SqliteTransaction)transaction);
        var reassignedTrackIdParameter = deleteReassignedCommand.Parameters.Add("@trackId", SqliteType.Integer);
        var reassignedServiceParameter = deleteReassignedCommand.Parameters.Add("@service", SqliteType.Text);
        var reassignedTargetItemIdParameter = deleteReassignedCommand.Parameters.Add("@targetItemId", SqliteType.Text);

        foreach (var item in metadata)
        {
            var normalizedService = NormalizeServiceKey(item.Service);
            if (item.TrackId <= 0
                || string.IsNullOrWhiteSpace(normalizedService)
                || string.IsNullOrWhiteSpace(item.TargetItemId))
            {
                continue;
            }

            existsTrackIdParameter.Value = item.TrackId;
            existsServiceParameter.Value = normalizedService;
            var existed = await existsCommand.ExecuteScalarAsync(cancellationToken) is not null;

            reassignedTrackIdParameter.Value = item.TrackId;
            reassignedServiceParameter.Value = normalizedService;
            reassignedTargetItemIdParameter.Value = item.TargetItemId.Trim();
            await deleteReassignedCommand.ExecuteNonQueryAsync(cancellationToken);

            trackIdParameter.Value = item.TrackId;
            serviceParameter.Value = normalizedService;
            targetItemIdParameter.Value = item.TargetItemId.Trim();
            filePathParameter.Value = string.IsNullOrWhiteSpace(item.FilePath) ? DBNull.Value : item.FilePath.Trim();
            updatedAtParameter.Value = item.UpdatedAtUtc.ToString("O");
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (!existed)
            {
                newlyResolved.Add((item.TrackId, normalizedService));
            }

            var audioVariant = await ResolveMediaServerAudioVariantAsync(
                connection,
                (SqliteTransaction)transaction,
                item,
                cancellationToken);
            variantTrackIdParameter.Value = item.TrackId;
            variantServiceParameter.Value = normalizedService;
            audioVariantParameter.Value = audioVariant;
            variantTargetItemIdParameter.Value = item.TargetItemId.Trim();
            variantFilePathParameter.Value = string.IsNullOrWhiteSpace(item.FilePath) ? DBNull.Value : item.FilePath.Trim();
            variantUpdatedAtParameter.Value = item.UpdatedAtUtc.ToString("O");
            await variantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var (trackId, service) in newlyResolved)
        {
            await EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
                trackId,
                service,
                cancellationToken);
        }

        return newlyResolved;
    }

    public async Task<IReadOnlyList<WatchlistSyncJobDto>> EnqueueMembershipJobsForResolvedUnsyncedIdentitiesAsync(
        string source,
        string playlistId,
        string targetService,
        string currentRevision,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, playlistId, out var normalizedSource, out var normalizedPlaylistId)
            || string.IsNullOrWhiteSpace(targetService))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT t.local_track_id
FROM playlist_watch_track t
JOIN media_server_track_metadata meta
  ON meta.track_id = t.local_track_id
 AND lower(meta.service) = lower(@targetService)
WHERE t.source=@source
  AND t.source_id=@playlistId
  AND t.local_track_id IS NOT NULL
  AND NOT EXISTS (
        SELECT 1 FROM playlist_watch_target_membership m
        WHERE m.source=t.source AND m.source_id=t.source_id
          AND m.track_source_id=t.track_source_id
          AND lower(m.target_service)=lower(@targetService)
          AND lower(m.sync_status)='playlist_synced');";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
        command.Parameters.AddWithValue("targetService", targetService.Trim().ToLowerInvariant());
        var trackIds = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!await reader.IsDBNullAsync(0, cancellationToken))
                {
                    trackIds.Add(reader.GetInt64(0));
                }
            }
        }

        var jobs = new List<WatchlistSyncJobDto>();
        foreach (var trackId in trackIds)
        {
            jobs.AddRange(await EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
                trackId,
                targetService,
                currentRevision,
                cancellationToken));
        }

        return jobs;
    }

    public async Task<int> EnqueueMembershipCatchUpForIncompletePlaylistsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT DISTINCT t.source, t.source_id, c.target,
       COALESCE(
           NULLIF(trim(state.applied_snapshot_id), ''),
           NULLIF(trim((
               SELECT job.snapshot_id
               FROM watchlist_sync_job job
               WHERE job.source=t.source
                 AND job.playlist_id=t.source_id
                 AND lower(job.target_service)=c.target
                 AND lower(job.track_id)='playlist'
                 AND trim(COALESCE(job.snapshot_id, '')) <> ''
               ORDER BY job.updated_at DESC, job.id DESC
               LIMIT 1)), ''),
           '')
FROM playlist_watch_track t
JOIN playlist_watch_configured_sync_targets c
  ON c.source = t.source AND c.source_id = t.source_id
LEFT JOIN playlist_watch_target_sync_state state
  ON state.source = t.source AND state.source_id = t.source_id
 AND lower(state.target_service) = c.target
WHERE t.local_track_id IS NOT NULL
  AND NOT EXISTS (
        SELECT 1 FROM playlist_watch_target_membership m
        WHERE m.source = t.source AND m.source_id = t.source_id
          AND m.track_source_id = t.track_source_id
          AND lower(m.target_service) = c.target
          AND lower(m.sync_status) = 'playlist_synced')
  AND NOT EXISTS (
        SELECT 1 FROM watchlist_sync_job j
        WHERE j.source = t.source AND j.playlist_id = t.source_id
          AND lower(j.target_service) = c.target
          AND lower(j.track_id) = 'playlist'
          AND lower(j.status) IN ('pending', 'retry', 'processing'));", connection);
        var work = new List<(string Source, string PlaylistId, string Target, string Revision)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var revision = await reader.IsDBNullAsync(3, cancellationToken) ? string.Empty : reader.GetString(3);
                if (string.IsNullOrWhiteSpace(revision))
                {
                    continue;
                }

                work.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), revision));
            }
        }

        var enqueued = 0;
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            enqueued += await EnqueueIncompletePlaylistTargetSyncJobAsync(
                item.Source,
                item.PlaylistId,
                item.Target,
                item.Revision,
                cancellationToken);
            enqueued += (await EnqueueMembershipJobsForResolvedUnsyncedIdentitiesAsync(
                item.Source,
                item.PlaylistId,
                item.Target,
                item.Revision,
                cancellationToken)).Count;
        }

        return enqueued;
    }

    private async Task<int> EnqueueIncompletePlaylistTargetSyncJobAsync(
        string source,
        string playlistId,
        string targetService,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizePlaylistWatchKey(source, playlistId, out var normalizedSource, out var normalizedPlaylistId)
            || string.IsNullOrWhiteSpace(targetService)
            || string.IsNullOrWhiteSpace(snapshotId))
        {
            return 0;
        }

        var normalizedTarget = targetService.Trim().ToLowerInvariant();
        var normalizedSnapshot = normalizedTarget == "plex"
            && !snapshotId.EndsWith(":plex-membership-v2", StringComparison.OrdinalIgnoreCase)
            ? $"{snapshotId}:plex-membership-v2"
            : snapshotId.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
INSERT INTO watchlist_sync_job (
    source, playlist_id, track_id, target_service, status, next_attempt_utc, snapshot_id)
SELECT @source, @playlistId, 'playlist', @targetService, 'pending', CURRENT_TIMESTAMP, @snapshotId
WHERE EXISTS (
        SELECT 1
          FROM playlist_watch_track t
          JOIN playlist_watch_configured_sync_targets c
            ON c.source=t.source AND c.source_id=t.source_id AND c.target=@targetService
         WHERE t.source=@source
           AND t.source_id=@playlistId
           AND t.local_track_id IS NOT NULL
           AND NOT EXISTS (
                SELECT 1
                  FROM playlist_watch_target_membership m
                 WHERE m.source=t.source
                   AND m.source_id=t.source_id
                   AND m.track_source_id=t.track_source_id
                   AND lower(m.target_service)=@targetService
                   AND lower(m.sync_status)='playlist_synced'))
ON CONFLICT(source, playlist_id, track_id, target_service) DO UPDATE SET
    status=CASE WHEN lower(watchlist_sync_job.status)='processing'
                THEN watchlist_sync_job.status ELSE 'pending' END,
    next_attempt_utc=CASE WHEN lower(watchlist_sync_job.status)='processing'
                          THEN watchlist_sync_job.next_attempt_utc ELSE CURRENT_TIMESTAMP END,
    snapshot_id=excluded.snapshot_id,
    updated_at=CURRENT_TIMESTAMP;", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
        command.Parameters.AddWithValue("targetService", normalizedTarget);
        command.Parameters.AddWithValue("snapshotId", normalizedSnapshot);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteMediaServerTrackMetadataAsync(
        string service,
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedIds = trackIds.Where(static id => id > 0).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(service) || normalizedIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = "DELETE FROM media_server_track_metadata WHERE service=@service AND track_id=@trackId;";
        const string variantSql = "DELETE FROM media_server_track_variant_metadata WHERE service=@service AND track_id=@trackId;";
        var normalizedService = service.Trim().ToLowerInvariant();
        foreach (var trackId in normalizedIds)
        {
            await using var command = new SqliteCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("service", normalizedService);
            command.Parameters.AddWithValue("trackId", trackId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var variantCommand = new SqliteCommand(variantSql, connection, transaction);
            variantCommand.Parameters.AddWithValue("service", normalizedService);
            variantCommand.Parameters.AddWithValue("trackId", trackId);
            await variantCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> ResolveMediaServerAudioVariantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MediaServerTrackMetadataUpsertDto item,
        CancellationToken cancellationToken)
    {
        var explicitVariant = item.AudioVariant?.Trim().ToLowerInvariant();
        if (explicitVariant is "atmos" or "stereo")
        {
            return explicitVariant;
        }

        await using var command = new SqliteCommand(@"
SELECT CASE
         WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos'
           OR COALESCE(af.channels, 0) > 2
           OR LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
           OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
           OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
           OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
           OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
           OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
         THEN 'atmos'
         ELSE 'stereo'
       END
FROM audio_file af
JOIN track_local tl ON tl.audio_file_id = af.id
WHERE tl.track_id = @trackId
ORDER BY CASE WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1 ELSE 0 END,
         COALESCE(af.quality_rank, 0) DESC
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("trackId", item.TrackId);
        var resolved = await command.ExecuteScalarAsync(cancellationToken);
        return resolved as string ?? "stereo";
    }

    public async Task UpsertPlexTrackMetadataAsync(
        PlexTrackMetadataDto metadata,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO track_plex_metadata
    (track_id, plex_rating_key, user_rating, genres_json, moods_json, updated_at_utc)
VALUES
    (@trackId, @ratingKey, @userRating, @genresJson, @moodsJson, @updatedAt)
ON CONFLICT(track_id) DO UPDATE SET
    plex_rating_key = excluded.plex_rating_key,
    user_rating = excluded.user_rating,
    genres_json = excluded.genres_json,
    moods_json = excluded.moods_json,
    updated_at_utc = excluded.updated_at_utc;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, metadata.TrackId);
        command.Parameters.AddWithValue("ratingKey", (object?)metadata.PlexRatingKey ?? DBNull.Value);
        command.Parameters.AddWithValue("userRating", (object?)metadata.UserRating ?? DBNull.Value);
        command.Parameters.AddWithValue("genresJson", metadata.Genres.Count == 0 ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.Genres));
        command.Parameters.AddWithValue("moodsJson", metadata.Moods.Count == 0 ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.Moods));
        command.Parameters.AddWithValue("updatedAt", metadata.UpdatedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetTrackIdsByFilePathsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var requestedPaths = filePaths
            .Select(static path => (path ?? string.Empty).Trim())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requestedPaths.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var result = await GetTrackIdsByExactFilePathsAsync(connection, requestedPaths, cancellationToken);
        if (result.Count == requestedPaths.Count)
        {
            return result;
        }

        var localRows = await GetLocalTrackFileRowsAsync(connection, cancellationToken);
        var byAbsolutePath = BuildUniquePathMap(localRows.Select(static row => (row.AbsolutePath, row.TrackId)));
        var byRelativePath = BuildUniquePathMap(localRows.Select(static row => (row.RelativePath, row.TrackId)));

        foreach (var path in requestedPaths)
        {
            if (result.ContainsKey(path))
            {
                continue;
            }

            var normalized = NormalizePathForLookup(path);
            if (byAbsolutePath.TryGetValue(normalized, out var trackId)
                || byRelativePath.TryGetValue(normalized, out trackId)
                || TryFindByUniqueRelativeSuffix(normalized, byRelativePath, out trackId)
                || TryFindByUniqueParentAndFileName(normalized, localRows, out trackId)
                || TryFindByUniqueAlbumAndTitle(normalized, localRows, out trackId))
            {
                result[path] = trackId;
            }
        }

        return result;
    }

    private static Dictionary<string, long> BuildUniquePathMap(IEnumerable<(string Path, long TrackId)> rows)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var normalized = NormalizePathForLookup(row.Path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (duplicates.Contains(normalized))
            {
                continue;
            }

            if (result.Remove(normalized))
            {
                duplicates.Add(normalized);
                continue;
            }

            result[normalized] = row.TrackId;
        }

        return result;
    }

    private static bool TryFindByUniqueRelativeSuffix(
        string normalizedPath,
        Dictionary<string, long> byRelativePath,
        out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var matches = byRelativePath
            .Where(pair => normalizedPath.EndsWith("/" + pair.Key, StringComparison.OrdinalIgnoreCase)
                           || pair.Key.EndsWith("/" + normalizedPath.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (matches.Count != 1)
        {
            return false;
        }

        trackId = matches[0].Value;
        return true;
    }

    private static bool TryFindByUniqueParentAndFileName(
        string normalizedPath,
        IReadOnlyList<LocalTrackFileRow> localRows,
        out long trackId)
    {
        trackId = 0;
        var fileName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var parent = Path.GetFileName(
            Path.GetDirectoryName(normalizedPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        long? matched = null;
        foreach (var row in localRows)
        {
            var localPath = NormalizePathForLookup(row.AbsolutePath);
            if (string.IsNullOrWhiteSpace(localPath))
            {
                localPath = NormalizePathForLookup(row.RelativePath);
            }

            var localFile = Path.GetFileName(localPath);
            var localParent = Path.GetFileName(
                Path.GetDirectoryName(localPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty);
            if (!string.Equals(fileName, localFile, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parent, localParent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (matched.HasValue && matched.Value != row.TrackId)
            {
                return false;
            }

            matched = row.TrackId;
        }

        if (!matched.HasValue)
        {
            return false;
        }

        trackId = matched.Value;
        return true;
    }

    private static bool TryFindByUniqueAlbumAndTitle(
        string normalizedPath,
        IReadOnlyList<LocalTrackFileRow> localRows,
        out long trackId)
    {
        trackId = 0;
        if (!TryGetAlbumArtistAndTitleKey(normalizedPath, out var album, out var artist, out var titleKey))
        {
            return false;
        }

        long? matched = null;
        foreach (var row in localRows)
        {
            var localPath = NormalizePathForLookup(row.AbsolutePath);
            if (string.IsNullOrWhiteSpace(localPath))
            {
                localPath = NormalizePathForLookup(row.RelativePath);
            }

            if (!TryGetAlbumArtistAndTitleKey(localPath, out var localAlbum, out var localArtist, out var localTitle)
                || !string.Equals(album, localAlbum, StringComparison.Ordinal)
                || !string.Equals(titleKey, localTitle, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(artist)
                && !string.IsNullOrWhiteSpace(localArtist)
                && !string.Equals(artist, localArtist, StringComparison.Ordinal))
            {
                continue;
            }

            if (matched.HasValue && matched.Value != row.TrackId)
            {
                return false;
            }

            matched = row.TrackId;
        }

        if (!matched.HasValue)
        {
            return false;
        }

        trackId = matched.Value;
        return true;
    }

    private static bool TryGetAlbumArtistAndTitleKey(
        string normalizedPath,
        out string album,
        out string artist,
        out string titleKey)
    {
        album = string.Empty;
        artist = string.Empty;
        titleKey = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var fileName = parts[^1];
        album = parts[^2];
        artist = parts.Length >= 3 ? parts[^3] : string.Empty;
        titleKey = NormalizeTitleKey(fileName, artist);
        return !string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(titleKey);
    }

    private static string NormalizeTitleKey(string fileName, string? artistFolder)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (string.IsNullOrWhiteSpace(stem))
        {
            return string.Empty;
        }

        var index = 0;
        while (index < stem.Length && index < 3 && char.IsDigit(stem[index]))
        {
            index++;
        }

        if (index > 0 && index < stem.Length)
        {
            var remainder = stem[index..].TrimStart();
            if (remainder.Length > 1 && remainder[0] is '-' or '.' or '_')
            {
                stem = remainder[1..].TrimStart();
            }
        }

        if (!string.IsNullOrWhiteSpace(artistFolder))
        {
            var prefix = artistFolder.Trim() + " - ";
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[prefix.Length..].TrimStart();
            }
        }

        return stem.ToLowerInvariant();
    }

    private static async Task<Dictionary<string, long>> GetTrackIdsByExactFilePathsAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT af.path,
       tl.track_id
FROM audio_file af
JOIN track_local tl ON tl.audio_file_id = af.id
WHERE af.path IN (
    SELECT value
    FROM json_each(@pathsJson)
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("pathsJson", SerializeJsonArray(filePaths));
        var mapping = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(0);
            if (!mapping.ContainsKey(path))
            {
                mapping[path] = reader.GetInt64(1);
            }
        }

        return mapping;
    }

    private static async Task<List<LocalTrackFileRow>> GetLocalTrackFileRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT tl.track_id,
       af.path,
       af.relative_path,
       f.root_path
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
LEFT JOIN folder f ON f.id = af.folder_id
WHERE af.path IS NOT NULL
   OR af.relative_path IS NOT NULL;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<LocalTrackFileRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            var rawPath = await ReadNullableStringAsync(reader, 1, cancellationToken) ?? string.Empty;
            var relativePath = await ReadNullableStringAsync(reader, 2, cancellationToken) ?? string.Empty;
            var rootPath = await ReadNullableStringAsync(reader, 3, cancellationToken);
            var absolutePath = BuildAbsolutePath(rootPath, relativePath, rawPath);
            rows.Add(new LocalTrackFileRow(trackId, absolutePath, relativePath));
        }

        return rows;
    }

    private static string NormalizePathForLookup(string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(normalized, UriKind.Absolute, out var fileUri)
            && fileUri.IsFile)
        {
            normalized = fileUri.LocalPath;
        }

        try
        {
            normalized = Uri.UnescapeDataString(normalized);
        }
        catch (UriFormatException)
        {
            // Keep the original path when it is not URI-encoded.
        }

        normalized = normalized.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimEnd('/').ToLowerInvariant();
    }

    private static string NormalizeServiceKey(string? service)
        => (service ?? string.Empty).Trim().ToLowerInvariant();

    private sealed record LocalTrackFileRow(long TrackId, string AbsolutePath, string RelativePath);

    public async Task<IReadOnlyList<PlexTrackMetadataDto>> GetPlexTrackMetadataAsync(
        IReadOnlyList<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0)
        {
            return Array.Empty<PlexTrackMetadataDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT track_id, plex_rating_key, user_rating, genres_json, moods_json, updated_at_utc
FROM track_plex_metadata
WHERE track_id IN (
    SELECT CAST(value AS INTEGER)
    FROM json_each(@trackIdsJson)
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));

        var results = new List<PlexTrackMetadataDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var genres = await reader.IsDBNullAsync(3, cancellationToken) ? new List<string>() : DeserializeMoodTags(reader.GetString(3))?.ToList() ?? new List<string>();
            var moods = await reader.IsDBNullAsync(4, cancellationToken) ? new List<string>() : DeserializeMoodTags(reader.GetString(4))?.ToList() ?? new List<string>();
            results.Add(new PlexTrackMetadataDto(
                reader.GetInt64(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetInt32(2),
                genres,
                moods,
                await reader.IsDBNullAsync(5, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(5))));
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<string, long>> GetTrackIdsBySourceIdsAsync(
        string source,
        IReadOnlyCollection<string> sourceIds,
        CancellationToken cancellationToken = default)
    {
        if (sourceIds.Count == 0 || string.IsNullOrWhiteSpace(source))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT source_id,
       track_id
FROM track_source
WHERE lower(source) = lower(@source)
  AND lower(source_id) IN (
      SELECT lower(value)
      FROM json_each(@sourceIdsJson)
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue("sourceIdsJson", SerializeJsonArray(sourceIds));

        var mapping = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sourceId = reader.GetString(0);
            if (!mapping.ContainsKey(sourceId))
            {
                mapping[sourceId] = reader.GetInt64(1);
            }
        }

        return mapping;
    }

    public async Task<long?> GetLocalAlbumIdByTrackSourceIdAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT t.album_id
FROM track_source ts
JOIN track t ON t.id = ts.track_id
WHERE ts.source = @source
  AND ts.source_id = @sourceId
  AND EXISTS (
      SELECT 1
      FROM track_local tl
      WHERE tl.track_id = t.id
  )
ORDER BY t.id DESC
LIMIT 1;";
        return await QueryNullableLongBySourceIdAsync(source, sourceId, sql, cancellationToken);
    }

    public async Task<long?> GetLocalAlbumIdByAlbumSourceIdAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT al.id
FROM album_source als
JOIN album al ON al.id = als.album_id
WHERE als.source = @source
  AND als.source_id = @sourceId
  AND EXISTS (
      SELECT 1
      FROM album_local aloc
      WHERE aloc.album_id = al.id
	  )
LIMIT 1;";
        return await QueryNullableLongBySourceIdAsync(source, sourceId, sql, cancellationToken);
    }

    public async Task<long?> GetLocalAlbumIdByTrackMetadataAsync(
        string artistName,
        string trackTitle,
        int? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        return await QueryLocalTrackMetadataIdAsync(
            LocalTrackMetadataIdKind.Album,
            artistName,
            trackTitle,
            durationMs,
            cancellationToken);
    }

    public async Task<long?> GetLocalTrackIdByTrackMetadataAsync(
        string artistName,
        string trackTitle,
        int? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        return await QueryLocalTrackMetadataIdAsync(
            LocalTrackMetadataIdKind.Track,
            artistName,
            trackTitle,
            durationMs,
            cancellationToken);
    }

    private enum LocalTrackMetadataIdKind
    {
        Album,
        Track
    }

    private async Task<long?> QueryLocalTrackMetadataIdAsync(
        LocalTrackMetadataIdKind idKind,
        string artistName,
        string trackTitle,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackTitle))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = $@"
SELECT al.id,
       t.id
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
LEFT JOIN audio_file af ON af.id = tl.audio_file_id
WHERE LOWER(ar.name) = LOWER(@artistName)
  AND LOWER(t.title) = LOWER(@trackTitle)
  AND (@{DurationMsField} IS NULL OR t.duration_ms IS NULL OR ABS(t.duration_ms - @{DurationMsField}) <= 2000)
ORDER BY af.quality_rank DESC NULLS LAST, t.id DESC
LIMIT 1;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        command.Parameters.AddWithValue(DurationMsField, (object?)durationMs ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var ordinal = idKind == LocalTrackMetadataIdKind.Album ? 0 : 1;
        return await reader.IsDBNullAsync(ordinal, cancellationToken)
            ? null
            : reader.GetInt64(ordinal);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetTrackIdsByPlexRatingKeysAsync(
        IReadOnlyList<string> ratingKeys,
        CancellationToken cancellationToken = default)
    {
        if (ratingKeys.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
WITH requested AS (
    SELECT value AS rating_key
    FROM json_each(@ratingKeysJson)
),
metadata AS (
    SELECT tpm.plex_rating_key,
           tpm.track_id,
           0 AS sort_group,
           tpm.updated_at_utc AS sort_utc
    FROM track_plex_metadata tpm
    JOIN requested r ON LOWER(TRIM(r.rating_key)) = LOWER(TRIM(tpm.plex_rating_key))
    WHERE tpm.track_id IS NOT NULL
      AND tpm.plex_rating_key IS NOT NULL
      AND TRIM(tpm.plex_rating_key) <> ''
),
history AS (
    SELECT ph.plex_rating_key,
           ph.track_id,
           1 AS sort_group,
           ph.played_at_utc AS sort_utc
    FROM play_history ph
    JOIN requested r ON LOWER(TRIM(r.rating_key)) = LOWER(TRIM(ph.plex_rating_key))
    WHERE ph.track_id IS NOT NULL
      AND ph.plex_rating_key IS NOT NULL
      AND TRIM(ph.plex_rating_key) <> ''
)
SELECT plex_rating_key,
       track_id
FROM (
    SELECT * FROM metadata
    UNION ALL
    SELECT * FROM history
)
ORDER BY sort_group, sort_utc DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("ratingKeysJson", SerializeJsonArray(ratingKeys));

        var mapping = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ratingKey = reader.GetString(0);
            if (!mapping.ContainsKey(ratingKey))
            {
                mapping[ratingKey] = reader.GetInt64(1);
            }
        }

        return mapping;
    }

    public async Task<IReadOnlyList<TrackAnalysisInputDto>> GetTracksForAnalysisAsync(
        int limit,
        bool includeCompletedStandard = false,
        DateTimeOffset? completedStandardRetryBeforeUtc = null,
        IReadOnlyList<long>? orderedLibraryIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var scopedLibraryIds = orderedLibraryIds?
            .Where(id => id > 0)
            .Distinct()
            .ToArray() ?? Array.Empty<long>();
        await CreateLibraryScopeTableAsync(connection, scopedLibraryIds, cancellationToken);
        const string sql = @"
WITH candidate_files AS (
    SELECT t.id,
           f.library_id,
           f.root_path,
           af.relative_path,
           af.path,
           t.duration_ms,
           COALESCE(
               (SELECT scope.sort_order FROM temp_analysis_library_scope scope WHERE scope.library_id = f.id),
               999999) AS library_sort_order,
           CASE
               WHEN lower(coalesce(af.codec, '')) LIKE '%eac3%'
                    OR lower(coalesce(af.codec, '')) LIKE '%dolby digital plus%'
                    OR lower(coalesce(af.audio_variant, '')) LIKE '%atmos%'
               THEN 2
               WHEN lower(coalesce(af.codec, '')) LIKE '%opus%'
                    OR lower(coalesce(af.extension, '')) = '.opus'
               THEN 1
               ELSE 0
           END AS variant_sort_order,
           af.quality_rank,
           af.size,
           af.id AS audio_file_id
    FROM track t
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    LEFT JOIN track_analysis ta ON ta.track_id = t.id
    WHERE f.enabled = 1
      AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%video%'
      AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%podcast%'
      AND coalesce(af.size, 0) > 0
      AND (coalesce(af.duration_ms, 0) > 0 OR coalesce(af.sample_rate_hz, 0) > 0 OR coalesce(af.channels, 0) > 0)
      AND (
          NOT EXISTS (SELECT 1 FROM temp_analysis_library_scope)
          OR EXISTS (
              SELECT 1
              FROM temp_analysis_library_scope scope
              WHERE scope.library_id = f.id
          )
      )
      AND (
          ta.status IS NULL
          OR ta.status IN ('pending', 'failed', 'error')
          OR (
              @includeCompletedStandard = 1
              AND ta.status IN ('complete', 'completed')
              AND lower(coalesce(ta.analysis_mode, '')) = 'standard'
              AND (
                  @completedStandardRetryBeforeUtc IS NULL
                  OR ta.analyzed_at_utc IS NULL
                  OR ta.analyzed_at_utc <= @completedStandardRetryBeforeUtc
              )
          )
      )
),
ranked_primary AS (
    SELECT id,
           library_sort_order,
           ROW_NUMBER() OVER (
               PARTITION BY id
               ORDER BY variant_sort_order,
                        quality_rank DESC NULLS LAST,
                        size DESC,
                        audio_file_id DESC
           ) AS variant_rank
    FROM candidate_files
),
selected_tracks AS (
    SELECT id, library_sort_order
    FROM ranked_primary
    WHERE variant_rank = 1
    ORDER BY library_sort_order, id
    LIMIT @limit
)
SELECT candidate_files.id,
       candidate_files.library_id,
       candidate_files.root_path,
       candidate_files.relative_path,
       candidate_files.path,
       candidate_files.duration_ms
FROM candidate_files
JOIN selected_tracks ON selected_tracks.id = candidate_files.id
ORDER BY selected_tracks.library_sort_order,
         candidate_files.id,
         candidate_files.variant_sort_order,
         candidate_files.quality_rank DESC NULLS LAST,
         candidate_files.size DESC,
         candidate_files.audio_file_id DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("includeCompletedStandard", includeCompletedStandard ? 1 : 0);
        command.Parameters.AddWithValue(
            "completedStandardRetryBeforeUtc",
            completedStandardRetryBeforeUtc?.ToString("O") ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadTrackAnalysisInputsAsync(reader, cancellationToken);
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static async Task CreateLibraryScopeTableAsync(
        SqliteConnection connection,
        long[] scopedLibraryIds,
        CancellationToken cancellationToken)
    {
        const string createSql = @"
CREATE TEMP TABLE IF NOT EXISTS temp_analysis_library_scope (
    library_id INTEGER PRIMARY KEY,
    sort_order INTEGER NOT NULL
);
DELETE FROM temp_analysis_library_scope;";
        await using (var createCommand = new SqliteCommand(createSql, connection))
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (scopedLibraryIds.Length == 0)
        {
            return;
        }

        const string insertSql = @"
INSERT OR REPLACE INTO temp_analysis_library_scope (library_id, sort_order)
VALUES (@libraryId, @sortOrder);";
        await using var insertCommand = new SqliteCommand(insertSql, connection);
        var libraryIdParameter = insertCommand.Parameters.Add("libraryId", SqliteType.Integer);
        var sortOrderParameter = insertCommand.Parameters.Add("sortOrder", SqliteType.Integer);
        for (var index = 0; index < scopedLibraryIds.Length; index++)
        {
            libraryIdParameter.Value = scopedLibraryIds[index];
            sortOrderParameter.Value = index;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task ResetProcessingTrackAnalysisAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE track_analysis
SET status = 'pending',
    error = NULL
WHERE status = 'processing';";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TrackAnalysisInputDto?> GetTrackForAnalysisAsync(long trackId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT t.id,
       f.library_id,
       f.root_path,
       af.relative_path,
       af.path,
       t.duration_ms
FROM track t
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE t.id = @trackId
  AND f.enabled = 1
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%video%'
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%podcast%'
  AND coalesce(af.size, 0) > 0
  AND (coalesce(af.duration_ms, 0) > 0 OR coalesce(af.sample_rate_hz, 0) > 0 OR coalesce(af.channels, 0) > 0)
ORDER BY f.enabled DESC,
         CASE
             WHEN lower(coalesce(af.codec, '')) LIKE '%eac3%'
                  OR lower(coalesce(af.codec, '')) LIKE '%dolby digital plus%'
                  OR lower(coalesce(af.audio_variant, '')) LIKE '%atmos%'
             THEN 2
             WHEN lower(coalesce(af.codec, '')) LIKE '%opus%'
                  OR lower(coalesce(af.extension, '')) = '.opus'
             THEN 1
             ELSE 0
         END,
         af.quality_rank DESC NULLS LAST,
         af.size DESC,
         af.id DESC;";
        var tracks = await QueryTrackAnalysisInputsAsync(sql, trackId, cancellationToken);
        return tracks.Count == 0 ? null : tracks[0];
    }

    public async Task MarkTrackAnalysisProcessingAsync(long trackId, long? libraryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO track_analysis
    (track_id, library_id, status)
VALUES
    (@trackId, @libraryId, 'processing')
ON CONFLICT(track_id) DO UPDATE SET
    library_id = excluded.library_id,
    status = excluded.status,
    error = NULL;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        command.Parameters.AddWithValue(LibraryIdField, (object?)libraryId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertTrackAnalysisAsync(TrackAnalysisResultDto result, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO track_analysis
    (track_id, library_id, status, energy, rms, zero_crossing, spectral_centroid, bpm, beats_count, key, key_scale, key_strength, loudness, dynamic_range, danceability, instrumentalness, acousticness, speechiness, danceability_ml, valence, arousal, analyzed_at_utc, error, analysis_mode, analysis_version, mood_tags, mood_happy, mood_sad, mood_relaxed, mood_aggressive, mood_party, mood_acoustic, mood_electronic, essentia_genres, lastfm_tags, approachability, engagement, voice_instrumental, tonal_atonal, valence_ml, arousal_ml, dynamic_complexity, loudness_ml)
VALUES
    (@trackId, @libraryId, @status, @energy, @rms, @zeroCrossing, @spectralCentroid, @bpm, @beatsCount, @key, @keyScale, @keyStrength, @loudness, @dynamicRange, @danceability, @instrumentalness, @acousticness, @speechiness, @danceabilityMl, @valence, @arousal, @analyzedAtUtc, @error, @analysisMode, @analysisVersion, @moodTags, @moodHappy, @moodSad, @moodRelaxed, @moodAggressive, @moodParty, @moodAcoustic, @moodElectronic, @essentiaGenres, @lastfmTags, @approachability, @engagement, @voiceInstrumental, @tonalAtonal, @valenceMl, @arousalMl, @dynamicComplexity, @loudnessMl)
ON CONFLICT(track_id) DO UPDATE SET
    library_id = excluded.library_id,
    status = excluded.status,
    energy = excluded.energy,
    rms = excluded.rms,
    zero_crossing = excluded.zero_crossing,
    spectral_centroid = excluded.spectral_centroid,
    bpm = excluded.bpm,
    beats_count = excluded.beats_count,
    key = excluded.key,
    key_scale = excluded.key_scale,
    key_strength = excluded.key_strength,
    loudness = excluded.loudness,
    dynamic_range = excluded.dynamic_range,
    danceability = excluded.danceability,
    instrumentalness = excluded.instrumentalness,
    acousticness = excluded.acousticness,
    speechiness = excluded.speechiness,
    danceability_ml = excluded.danceability_ml,
    valence = excluded.valence,
    arousal = excluded.arousal,
    analyzed_at_utc = excluded.analyzed_at_utc,
    error = excluded.error,
    analysis_mode = excluded.analysis_mode,
    analysis_version = excluded.analysis_version,
    mood_tags = excluded.mood_tags,
    mood_happy = excluded.mood_happy,
    mood_sad = excluded.mood_sad,
    mood_relaxed = excluded.mood_relaxed,
    mood_aggressive = excluded.mood_aggressive,
    mood_party = excluded.mood_party,
    mood_acoustic = excluded.mood_acoustic,
    mood_electronic = excluded.mood_electronic,
    essentia_genres = excluded.essentia_genres,
    lastfm_tags = excluded.lastfm_tags,
    approachability = excluded.approachability,
    engagement = excluded.engagement,
    voice_instrumental = excluded.voice_instrumental,
    tonal_atonal = excluded.tonal_atonal,
    valence_ml = excluded.valence_ml,
    arousal_ml = excluded.arousal_ml,
    dynamic_complexity = excluded.dynamic_complexity,
    loudness_ml = excluded.loudness_ml;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, result.TrackId);
        command.Parameters.AddWithValue(LibraryIdField, (object?)result.LibraryId ?? DBNull.Value);
        command.Parameters.AddWithValue("status", result.Status);
        command.Parameters.AddWithValue("energy", (object?)result.Energy ?? DBNull.Value);
        command.Parameters.AddWithValue("rms", (object?)result.Rms ?? DBNull.Value);
        command.Parameters.AddWithValue("zeroCrossing", (object?)result.ZeroCrossing ?? DBNull.Value);
        command.Parameters.AddWithValue("spectralCentroid", (object?)result.SpectralCentroid ?? DBNull.Value);
        command.Parameters.AddWithValue("bpm", (object?)result.Bpm ?? DBNull.Value);
        command.Parameters.AddWithValue("beatsCount", (object?)result.BeatsCount ?? DBNull.Value);
        command.Parameters.AddWithValue("key", (object?)result.Key ?? DBNull.Value);
        command.Parameters.AddWithValue("keyScale", (object?)result.KeyScale ?? DBNull.Value);
        command.Parameters.AddWithValue("keyStrength", (object?)result.KeyStrength ?? DBNull.Value);
        command.Parameters.AddWithValue("loudness", (object?)result.Loudness ?? DBNull.Value);
        command.Parameters.AddWithValue("dynamicRange", (object?)result.DynamicRange ?? DBNull.Value);
        command.Parameters.AddWithValue("danceability", (object?)result.Danceability ?? DBNull.Value);
        command.Parameters.AddWithValue("instrumentalness", (object?)result.Instrumentalness ?? DBNull.Value);
        command.Parameters.AddWithValue("acousticness", (object?)result.Acousticness ?? DBNull.Value);
        command.Parameters.AddWithValue("speechiness", (object?)result.Speechiness ?? DBNull.Value);
        command.Parameters.AddWithValue("danceabilityMl", (object?)result.DanceabilityMl ?? DBNull.Value);
        command.Parameters.AddWithValue("valence", (object?)result.Valence ?? DBNull.Value);
        command.Parameters.AddWithValue("arousal", (object?)result.Arousal ?? DBNull.Value);
        command.Parameters.AddWithValue("analyzedAtUtc", result.AnalyzedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)result.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("analysisMode", (object?)result.AnalysisMode ?? DBNull.Value);
        command.Parameters.AddWithValue("analysisVersion", (object?)result.AnalysisVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("moodTags", result.MoodTags is null ? (object)DBNull.Value : JsonSerializer.Serialize(result.MoodTags));
        command.Parameters.AddWithValue("moodHappy", (object?)result.MoodHappy ?? DBNull.Value);
        command.Parameters.AddWithValue("moodSad", (object?)result.MoodSad ?? DBNull.Value);
        command.Parameters.AddWithValue("moodRelaxed", (object?)result.MoodRelaxed ?? DBNull.Value);
        command.Parameters.AddWithValue("moodAggressive", (object?)result.MoodAggressive ?? DBNull.Value);
        command.Parameters.AddWithValue("moodParty", (object?)result.MoodParty ?? DBNull.Value);
        command.Parameters.AddWithValue("moodAcoustic", (object?)result.MoodAcoustic ?? DBNull.Value);
        command.Parameters.AddWithValue("moodElectronic", (object?)result.MoodElectronic ?? DBNull.Value);
        command.Parameters.AddWithValue("essentiaGenres", result.EssentiaGenres is null ? (object)DBNull.Value : JsonSerializer.Serialize(result.EssentiaGenres));
        command.Parameters.AddWithValue("lastfmTags", result.LastfmTags is null ? (object)DBNull.Value : JsonSerializer.Serialize(result.LastfmTags));
        // Vibe analysis - new fields
        command.Parameters.AddWithValue("approachability", (object?)result.Approachability ?? DBNull.Value);
        command.Parameters.AddWithValue("engagement", (object?)result.Engagement ?? DBNull.Value);
        command.Parameters.AddWithValue("voiceInstrumental", (object?)result.VoiceInstrumental ?? DBNull.Value);
        command.Parameters.AddWithValue("tonalAtonal", (object?)result.TonalAtonal ?? DBNull.Value);
        command.Parameters.AddWithValue("valenceMl", (object?)result.ValenceMl ?? DBNull.Value);
        command.Parameters.AddWithValue("arousalMl", (object?)result.ArousalMl ?? DBNull.Value);
        command.Parameters.AddWithValue("dynamicComplexity", (object?)result.DynamicComplexity ?? DBNull.Value);
        command.Parameters.AddWithValue("loudnessMl", (object?)result.LoudnessMl ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AnalysisStatusDto> GetAnalysisStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string totalSql = @"
SELECT COUNT(DISTINCT t.id)
FROM track t
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%video%'
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%podcast%'
  AND coalesce(af.size, 0) > 0
  AND (coalesce(af.duration_ms, 0) > 0 OR coalesce(af.sample_rate_hz, 0) > 0 OR coalesce(af.channels, 0) > 0);";
        const string analyzedSql = @"
SELECT COUNT(DISTINCT ta.track_id)
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%video%'
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%podcast%'
  AND coalesce(af.size, 0) > 0
  AND (coalesce(af.duration_ms, 0) > 0 OR coalesce(af.sample_rate_hz, 0) > 0 OR coalesce(af.channels, 0) > 0)
  AND ta.status IN ('complete', 'completed');";
        const string errorSql = @"
SELECT COUNT(DISTINCT ta.track_id)
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%video%'
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%podcast%'
  AND coalesce(af.size, 0) > 0
  AND (coalesce(af.duration_ms, 0) > 0 OR coalesce(af.sample_rate_hz, 0) > 0 OR coalesce(af.channels, 0) > 0)
  AND ta.status IN ('error', 'failed');";
        const string lastRunSql = @"
SELECT MAX(ta.analyzed_at_utc)
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%video%'
  AND lower(coalesce(f.desired_quality_value, '')) NOT LIKE '%podcast%'
  AND coalesce(af.size, 0) > 0
  AND (coalesce(af.duration_ms, 0) > 0 OR coalesce(af.sample_rate_hz, 0) > 0 OR coalesce(af.channels, 0) > 0)
  AND ta.analyzed_at_utc IS NOT NULL;";

        var total = await ExecuteCountScalarAsync(connection, totalSql, cancellationToken);
        var analyzed = await ExecuteCountScalarAsync(connection, analyzedSql, cancellationToken);
        var errors = await ExecuteCountScalarAsync(connection, errorSql, cancellationToken);
        var lastRunUtc = await ExecuteDateTimeOffsetScalarAsync(connection, lastRunSql, cancellationToken);

        var pending = Math.Max(0, total - analyzed - errors);
        return new AnalysisStatusDto(total, analyzed, pending, errors, lastRunUtc);
    }

    private static async Task<int> ExecuteCountScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<DateTimeOffset?> ExecuteDateTimeOffsetScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is string rawText
            && DateTimeOffset.TryParse(rawText, CultureInfo.InvariantCulture, ParseDateStyles, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private async Task<long?> QueryNullableLongBySourceIdAsync(
        string source,
        string sourceId,
        string sql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private static async Task<TrackAnalysisResultDto> ReadTrackAnalysisResultDtoAsync(
        SqliteDataReader reader,
        int offset,
        CancellationToken cancellationToken,
        bool includeVibeMetrics = false)
    {
        var moodTags = DeserializeStringListOrNull(await ReadNullableStringAsync(reader, offset + 25, cancellationToken));
        var essentiaGenres = DeserializeStringListOrNull(await ReadNullableStringAsync(reader, offset + 33, cancellationToken));
        var lastfmTags = DeserializeStringListOrNull(await ReadNullableStringAsync(reader, offset + 34, cancellationToken));
        var analyzedAtText = await ReadNullableStringAsync(reader, offset + 21, cancellationToken);
        DateTimeOffset? analyzedAt = string.IsNullOrWhiteSpace(analyzedAtText) ? null : ParseDateTimeOffsetInvariant(analyzedAtText);

        return new TrackAnalysisResultDto(
            reader.GetInt64(offset + 0),
            await ReadNullableInt64Async(reader, offset + 1, cancellationToken),
            reader.GetString(offset + 2),
            await ReadNullableDoubleAsync(reader, offset + 3, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 4, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 5, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 6, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 7, cancellationToken),
            analyzedAt,
            await ReadNullableStringAsync(reader, offset + 22, cancellationToken),
            await ReadNullableStringAsync(reader, offset + 23, cancellationToken),
            await ReadNullableStringAsync(reader, offset + 24, cancellationToken),
            moodTags,
            await ReadNullableDoubleAsync(reader, offset + 26, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 27, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 28, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 29, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 30, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 31, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 32, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 19, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 20, cancellationToken),
            await ReadNullableIntAsync(reader, offset + 8, cancellationToken),
            await ReadNullableStringAsync(reader, offset + 9, cancellationToken),
            await ReadNullableStringAsync(reader, offset + 10, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 11, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 12, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 13, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 14, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 15, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 16, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 17, cancellationToken),
            await ReadNullableDoubleAsync(reader, offset + 18, cancellationToken),
            essentiaGenres,
            lastfmTags,
            await ReadOptionalVibeMetricAsync(reader, offset + 35, includeVibeMetrics, cancellationToken),
            await ReadOptionalVibeMetricAsync(reader, offset + 36, includeVibeMetrics, cancellationToken),
            await ReadOptionalVibeMetricAsync(reader, offset + 37, includeVibeMetrics, cancellationToken),
            await ReadOptionalVibeMetricAsync(reader, offset + 38, includeVibeMetrics, cancellationToken),
            await ReadOptionalVibeMetricAsync(reader, offset + 39, includeVibeMetrics, cancellationToken),
            await ReadOptionalVibeMetricAsync(reader, offset + 40, includeVibeMetrics, cancellationToken),
            await ReadOptionalVibeMetricAsync(reader, offset + 41, includeVibeMetrics, cancellationToken),
            await ReadOptionalVibeMetricAsync(reader, offset + 42, includeVibeMetrics, cancellationToken));
    }

    private static async Task<double?> ReadOptionalVibeMetricAsync(
        SqliteDataReader reader,
        int ordinal,
        bool includeVibeMetrics,
        CancellationToken cancellationToken)
    {
        if (!includeVibeMetrics)
        {
            return null;
        }

        return await ReadNullableDoubleAsync(reader, ordinal, cancellationToken);
    }

    public async Task<TrackAnalysisResultDto?> GetTrackAnalysisAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT track_id, library_id, status, energy, rms, zero_crossing, spectral_centroid, bpm, beats_count, key, key_scale, key_strength, loudness, dynamic_range, danceability, instrumentalness, acousticness, speechiness, danceability_ml, valence, arousal, analyzed_at_utc, error, analysis_mode, analysis_version, mood_tags, mood_happy, mood_sad, mood_relaxed, mood_aggressive, mood_party, mood_acoustic, mood_electronic, essentia_genres, lastfm_tags, approachability, engagement, voice_instrumental, tonal_atonal, valence_ml, arousal_ml, dynamic_complexity, loudness_ml
FROM track_analysis
WHERE track_id = @trackId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ReadTrackAnalysisResultDtoAsync(reader, offset: 0, cancellationToken, includeVibeMetrics: true);
    }

    public async Task<IReadOnlyList<TrackAnalysisResultDto>> GetTrackAnalysisCandidatesAsync(
        long? libraryId,
        long sourceTrackId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<TrackAnalysisResultDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = @"
SELECT track_id, library_id, status, energy, rms, zero_crossing, spectral_centroid, bpm, beats_count, key, key_scale, key_strength, loudness, dynamic_range, danceability, instrumentalness, acousticness, speechiness, danceability_ml, valence, arousal, analyzed_at_utc, error, analysis_mode, analysis_version, mood_tags, mood_happy, mood_sad, mood_relaxed, mood_aggressive, mood_party, mood_acoustic, mood_electronic, essentia_genres, lastfm_tags, approachability, engagement, voice_instrumental, tonal_atonal, valence_ml, arousal_ml, dynamic_complexity, loudness_ml
FROM track_analysis
WHERE track_id <> @sourceTrackId
  AND status IN ('complete', 'completed')";
        if (libraryId.HasValue)
        {
            sql += "\n  AND library_id = @libraryId";
        }
        sql += "\nORDER BY analyzed_at_utc DESC\nLIMIT @limit;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("sourceTrackId", sourceTrackId);
        command.Parameters.AddWithValue("limit", limit);
        if (libraryId.HasValue)
        {
            command.Parameters.AddWithValue(LibraryIdField, libraryId.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<TrackAnalysisResultDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(await ReadTrackAnalysisResultDtoAsync(
                reader,
                offset: 0,
                cancellationToken,
                includeVibeMetrics: true));
        }

        return results;
    }

    public async Task<LatestTrackAnalysisDto?> GetLatestTrackAnalysisAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       t.title,
       ar.name,
       al.title,
       al.preferred_cover_path,
       t.duration_ms,
       ta.track_id,
       ta.library_id,
       ta.status,
       ta.energy,
       ta.rms,
       ta.zero_crossing,
       ta.spectral_centroid,
       ta.bpm,
       ta.beats_count,
       ta.key,
       ta.key_scale,
       ta.key_strength,
       ta.loudness,
       ta.dynamic_range,
       ta.danceability,
       ta.instrumentalness,
       ta.acousticness,
       ta.speechiness,
       ta.danceability_ml,
       ta.valence,
       ta.arousal,
       ta.analyzed_at_utc,
       ta.error,
       ta.analysis_mode,
       ta.analysis_version,
       ta.mood_tags,
       ta.mood_happy,
       ta.mood_sad,
       ta.mood_relaxed,
       ta.mood_aggressive,
       ta.mood_party,
       ta.mood_acoustic,
       ta.mood_electronic,
       ta.essentia_genres,
       ta.lastfm_tags
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1
  AND ta.status IN ('complete', 'completed')
ORDER BY ta.analyzed_at_utc DESC
LIMIT 1;";

        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var track = new MixTrackDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5));

        var analysis = await ReadTrackAnalysisResultDtoAsync(reader, offset: 6, cancellationToken);

        return new LatestTrackAnalysisDto(track, analysis);
    }

    public async Task<IReadOnlyList<long>> GetTrackIdsByMoodTagsAsync(
        long? libraryId,
        IReadOnlyList<string> tags,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (tags.Count == 0 || limit <= 0)
        {
            return Array.Empty<long>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var tagClauses = string.Join(" OR ", tags.Select((_, index) => $"mood_tags LIKE @tag{index}"));
        var sql = $@"
SELECT track_id
FROM track_analysis
WHERE status IN ('complete', 'completed')
  AND ({tagClauses})";
        if (libraryId.HasValue)
        {
            sql += "\n  AND library_id = @libraryId";
        }
        sql += "\nORDER BY analyzed_at_utc DESC\nLIMIT @limit;";

        await using var command = new SqliteCommand(sql, connection);
        for (var i = 0; i < tags.Count; i++)
        {
            var token = tags[i].Trim().ToLowerInvariant();
            command.Parameters.AddWithValue($"tag{i}", $"%\"{token}\"%");
        }
        if (libraryId.HasValue)
        {
            command.Parameters.AddWithValue(LibraryIdField, libraryId.Value);
        }
        command.Parameters.AddWithValue("limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    public async Task<IReadOnlyDictionary<long, TrackAnalysisResultDto>> GetTrackAnalysisByTrackIdsAsync(
        IReadOnlyList<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0)
        {
            return new Dictionary<long, TrackAnalysisResultDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT track_id, library_id, status, energy, rms, zero_crossing, spectral_centroid, bpm, beats_count, key, key_scale, key_strength, loudness, dynamic_range, danceability, instrumentalness, acousticness, speechiness, danceability_ml, valence, arousal, analyzed_at_utc, error, analysis_mode, analysis_version, mood_tags, mood_happy, mood_sad, mood_relaxed, mood_aggressive, mood_party, mood_acoustic, mood_electronic, essentia_genres, lastfm_tags, approachability, engagement, voice_instrumental, tonal_atonal, valence_ml, arousal_ml, dynamic_complexity, loudness_ml
FROM track_analysis
WHERE track_id IN (
    SELECT CAST(value AS INTEGER)
    FROM json_each(@trackIdsJson)
);";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new Dictionary<long, TrackAnalysisResultDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var dto = await ReadTrackAnalysisResultDtoAsync(
                reader,
                offset: 0,
                cancellationToken,
                includeVibeMetrics: true);

            results[dto.TrackId] = dto;
        }

        return results;
    }

    public async Task<long?> GetArtistIdForTrackAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT a.artist_id
FROM track t
JOIN album a ON a.id = t.album_id
WHERE t.id = @trackId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<long>> GetTrackIdsByArtistAsync(long artistId, long sourceTrackId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id
FROM track t
JOIN album a ON a.id = t.album_id
WHERE a.artist_id = @artistId
  AND t.id <> @sourceTrackId
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("sourceTrackId", sourceTrackId);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    public async Task<IReadOnlyList<string>> GetGenresForTrackAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT genres_json
FROM track_plex_metadata
WHERE track_id = @trackId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is string json)
        {
            return DeserializeStringList(json) ?? Array.Empty<string>();
        }
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<long>> GetTrackIdsByGenresAsync(
        IReadOnlyList<string> genres,
        long sourceTrackId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (genres.Count == 0 || limit <= 0)
        {
            return Array.Empty<long>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT track_id
FROM track_plex_metadata
WHERE track_id <> @sourceTrackId
  AND EXISTS (
      SELECT 1
      FROM json_each(@genresJson)
      WHERE track_plex_metadata.genres_json LIKE '%' || '""' || LOWER(TRIM(value)) || '""' || '%'
  )
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("genresJson", SerializeJsonArray(genres));
        command.Parameters.AddWithValue("sourceTrackId", sourceTrackId);
        command.Parameters.AddWithValue("limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    public async Task<IReadOnlyList<long>> GetRandomAnalyzedTrackIdsAsync(long sourceTrackId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT track_id
FROM track_analysis
WHERE track_id <> @sourceTrackId
  AND status IN ('complete', 'completed')
ORDER BY RANDOM()
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("sourceTrackId", sourceTrackId);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    private async Task<IReadOnlyList<long>> ExecuteTrackIdListQueryAsync(
        string sql,
        Action<SqliteCommand> configureParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        configureParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static async Task<string?> ReadAudioFilePathAsync(
        SqliteDataReader reader,
        int rootPathOrdinal,
        int relativePathOrdinal,
        int fallbackPathOrdinal,
        CancellationToken cancellationToken)
    {
        var rootPath = await reader.IsDBNullAsync(rootPathOrdinal, cancellationToken) ? null : reader.GetString(rootPathOrdinal);
        var relativePath = await reader.IsDBNullAsync(relativePathOrdinal, cancellationToken) ? null : reader.GetString(relativePathOrdinal);
        var fallbackPath = await reader.IsDBNullAsync(fallbackPathOrdinal, cancellationToken) ? null : reader.GetString(fallbackPathOrdinal);
        return BuildAbsolutePath(rootPath, relativePath, fallbackPath);
    }

    private async Task<T?> QuerySingleTrackAsync<T>(
        string sql,
        long trackId,
        Func<SqliteDataReader, CancellationToken, Task<T?>> mapAsync,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await mapAsync(reader, cancellationToken);
    }

    private async Task<IReadOnlyList<TrackAnalysisInputDto>> QueryTrackAnalysisInputsAsync(
        string sql,
        long trackId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadTrackAnalysisInputsAsync(reader, cancellationToken);
    }

    private static async Task<IReadOnlyList<TrackAnalysisInputDto>> ReadTrackAnalysisInputsAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var results = new List<TrackAnalysisInputDto>();
        var resultIndexesByTrackId = new Dictionary<long, int>();
        var alternatePathsByTrackId = new Dictionary<long, List<string>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            var filePath = await ReadAudioFilePathAsync(reader, 2, 3, 4, cancellationToken);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            if (resultIndexesByTrackId.TryGetValue(trackId, out var existingIndex))
            {
                AddAlternateAnalysisPath(results, alternatePathsByTrackId, trackId, existingIndex, filePath);
                continue;
            }

            var alternatePaths = new List<string>();
            alternatePathsByTrackId[trackId] = alternatePaths;
            resultIndexesByTrackId[trackId] = results.Count;
            results.Add(new TrackAnalysisInputDto(
                trackId,
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetInt64(1),
                filePath,
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5),
                alternatePaths));
        }

        return results;
    }

    private static void AddAlternateAnalysisPath(
        List<TrackAnalysisInputDto> results,
        Dictionary<long, List<string>> alternatePathsByTrackId,
        long trackId,
        int existingIndex,
        string filePath)
    {
        var alternates = alternatePathsByTrackId[trackId];
        if (PathEquals(results[existingIndex].FilePath, filePath)
            || alternates.Any(path => PathEquals(path, filePath)))
        {
            return;
        }

        alternates.Add(filePath);
        results[existingIndex] = results[existingIndex] with
        {
            AlternateFilePaths = alternates.ToArray()
        };
    }

    private static async Task<TrackAudioInfoDto?> ReadTrackAudioInfoAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var filePath = await ReadAudioFilePathAsync(reader, 7, 8, 9, cancellationToken);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return new TrackAudioInfoDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt32(4),
            filePath,
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            reader.GetInt64(6));
    }

    private static async Task<PlaylistWatchPreferenceDto> ReadPlaylistWatchPreferenceAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var updateArtwork = await reader.IsDBNullAsync(8, cancellationToken) || reader.GetInt32(8) != 0;
        var reuseSavedArtwork = !await reader.IsDBNullAsync(9, cancellationToken) && reader.GetInt32(9) != 0;
        var created = ParseDateTimeOffsetOrDefault(await ReadNullableStringAsync(reader, 10, cancellationToken), DateTimeOffset.MinValue);
        var updated = ParseDateTimeOffsetOrDefault(await ReadNullableStringAsync(reader, 11, cancellationToken), created);
        var rulesJson = await ReadNullableStringAsync(reader, 12, cancellationToken);
        var rules = rulesJson is null ? null : JsonSerializer.Deserialize<List<PlaylistTrackRoutingRule>>(rulesJson);
        var ignoreRulesJson = await ReadNullableStringAsync(reader, 13, cancellationToken);
        var ignoreRules = ignoreRulesJson is null ? null : JsonSerializer.Deserialize<List<PlaylistTrackBlockRule>>(ignoreRulesJson);
        var plexPlaylistId = await ReadNullableStringAsync(reader, 14, cancellationToken);
        var jellyfinPlaylistId = await ReadNullableStringAsync(reader, 15, cancellationToken);
        var navidromePlaylistId = await ReadNullableStringAsync(reader, 16, cancellationToken);
        var downloadEngineOrderJson = await ReadNullableStringAsync(reader, 17, cancellationToken);
        var downloadEngineOrder = downloadEngineOrderJson is null
            ? null
            : JsonSerializer.Deserialize<DownloadEngineOrderSettings>(downloadEngineOrderJson);
        var syncTargetsJson = await ReadNullableStringAsync(reader, 18, cancellationToken);
        var syncTargets = string.IsNullOrWhiteSpace(syncTargetsJson)
            ? null
            : JsonSerializer.Deserialize<List<string>>(syncTargetsJson);
        return new PlaylistWatchPreferenceDto(
            reader.GetString(0),
            reader.GetString(1),
            await ReadNullableInt64Async(reader, 2, cancellationToken),
            await ReadNullableStringAsync(reader, 4, cancellationToken),
            syncTargets,
            await ReadNullableStringAsync(reader, 5, cancellationToken),
            downloadEngineOrder,
            await ReadNullableStringAsync(reader, 6, cancellationToken),
            await ReadNullableStringAsync(reader, 7, cancellationToken),
            updateArtwork,
            reuseSavedArtwork,
            created,
            updated,
            rules,
            ignoreRules,
            await ReadNullableInt64Async(reader, 3, cancellationToken),
            plexPlaylistId,
            jellyfinPlaylistId,
            navidromePlaylistId);
    }

    private static DateTimeOffset ParseDateTimeOffsetOrDefault(string? value, DateTimeOffset defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : ParseDateTimeOffsetInvariant(value);

    private async Task<HashSet<string>> QueryPlaylistWatchTrackSourceIdsAsync(
        string sql,
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                var value = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    private async Task<HashSet<string>> QueryPlaylistWatchTrackSourceIdsBySourceAsync(
        string sql,
        string source,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizePlaylistWatchSource(source);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                var value = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    private async Task InsertPlaylistWatchRowsAsync<TTrack>(
        string sql,
        string source,
        string sourceId,
        IReadOnlyCollection<TTrack> tracks,
        Func<TTrack, string> trackSourceIdSelector,
        Func<TTrack, string?> isrcSelector,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection, transaction);
        var sourceParam = command.Parameters.Add("source", SqliteType.Text);
        var sourceIdParam = command.Parameters.Add("sourceId", SqliteType.Text);
        var trackParam = command.Parameters.Add("trackSourceId", SqliteType.Text);
        var isrcParam = command.Parameters.Add("isrc", SqliteType.Text);

        foreach (var track in tracks)
        {
            sourceParam.Value = normalizedSource;
            sourceIdParam.Value = normalizedSourceId;
            trackParam.Value = trackSourceIdSelector(track);
            isrcParam.Value = (object?)isrcSelector(track) ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string SerializeJsonArray<T>(IEnumerable<T> values)
        => JsonSerializer.Serialize(values);

    private static string NormalizePlaylistWatchSource(string source)
        => string.IsNullOrWhiteSpace(source) ? string.Empty : source.Trim().ToLowerInvariant();

    private static string NormalizePlaylistWatchSourceId(string sourceId)
        => string.IsNullOrWhiteSpace(sourceId) ? string.Empty : sourceId.Trim();

    private static bool TryNormalizePlaylistWatchKey(
        string source,
        string sourceId,
        out string normalizedSource,
        out string normalizedSourceId)
    {
        normalizedSource = NormalizePlaylistWatchSource(source);
        normalizedSourceId = NormalizePlaylistWatchSourceId(sourceId);
        return !string.IsNullOrWhiteSpace(normalizedSource) && !string.IsNullOrWhiteSpace(normalizedSourceId);
    }

    private static IReadOnlyList<string>? DeserializeStringList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string>? DeserializeStringListOrNull(string? json)
    {
        return string.IsNullOrWhiteSpace(json) ? null : DeserializeStringList(json);
    }

    private static IReadOnlyList<string>? DeserializeMoodTags(string json)
    {
        return DeserializeStringList(json);
    }

    private static IReadOnlyList<RecommendationTrackDto> DeserializeRecommendationTracks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<RecommendationTrackDto>();
        }

        try
        {
            var deserialized = JsonSerializer.Deserialize<List<RecommendationTrackDto>>(json);
            if (deserialized is null)
            {
                return Array.Empty<RecommendationTrackDto>();
            }

            return deserialized;
        }
        catch (JsonException)
        {
            return Array.Empty<RecommendationTrackDto>();
        }
    }

    public async Task<IReadOnlyList<string>> GetCoverPathsAsync(IReadOnlyList<long> trackIds, int limit, CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0 || limit <= 0)
        {
            return Array.Empty<string>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT a.preferred_cover_path
FROM track t
JOIN album a ON a.id = t.album_id
WHERE t.id IN (
    SELECT CAST(value AS INTEGER)
    FROM json_each(@trackIdsJson)
)
  AND a.preferred_cover_path IS NOT NULL
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdsJsonParameter, SerializeJsonArray(trackIds));
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var covers = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            covers.Add(reader.GetString(0));
        }
        return covers;
    }

    public async Task<MixSummaryDto?> GetMixCacheAsync(string mixId, long plexUserId, long libraryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, mix_id, name, description, track_count, cover_urls_json, generated_at_utc, expires_at_utc
FROM mix_cache
WHERE mix_id = @mixId
  AND plex_user_id = @plexUserId
  AND library_id = @libraryId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("mixId", mixId);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ReadMixSummaryAsync(reader, libraryId, cancellationToken);
    }

    public async Task<IReadOnlyList<MixSummaryDto>> GetGeneratedMixCachesAsync(long plexUserId, long libraryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, mix_id, name, description, track_count, cover_urls_json, generated_at_utc, expires_at_utc
FROM mix_cache
WHERE plex_user_id = @plexUserId
  AND library_id = @libraryId
  AND mix_id NOT IN ('top-tracks', 'rediscover', 'library-shuffle')
ORDER BY generated_at_utc DESC, id DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var mixes = new List<MixSummaryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            mixes.Add(await ReadMixSummaryAsync(reader, libraryId, cancellationToken));
        }

        return mixes;
    }

    public async Task<IReadOnlyList<MixSummaryDto>> GetGeneratedMixCachesAsync(long plexUserId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, mix_id, name, description, track_count, cover_urls_json, generated_at_utc, expires_at_utc, library_id
FROM mix_cache
WHERE plex_user_id = @plexUserId
  AND mix_id NOT IN ('top-tracks', 'rediscover', 'library-shuffle')
ORDER BY generated_at_utc DESC, id DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var mixes = new List<MixSummaryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var libraryId = await reader.IsDBNullAsync(8, cancellationToken) ? 0 : reader.GetInt64(8);
            mixes.Add(await ReadMixSummaryAsync(reader, libraryId, cancellationToken));
        }

        return mixes;
    }

    public async Task<MixSummaryDto?> GetGeneratedMixCacheAsync(string mixId, long plexUserId, long libraryId, CancellationToken cancellationToken = default)
    {
        if (IsRemovedHardcodedMixId(mixId))
        {
            return null;
        }

        return await GetMixCacheAsync(mixId, plexUserId, libraryId, cancellationToken);
    }

    public async Task<long?> GetMixCacheIdAsync(string mixId, long plexUserId, long libraryId, CancellationToken cancellationToken = default)
    {
        if (IsRemovedHardcodedMixId(mixId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id
FROM mix_cache
WHERE mix_id = @mixId
  AND plex_user_id = @plexUserId
  AND library_id = @libraryId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("mixId", mixId);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            return null;
        }
        return Convert.ToInt64(result);
    }

    public async Task<bool> DeleteGeneratedMixCacheAsync(string mixId, long plexUserId, long libraryId, CancellationToken cancellationToken = default)
    {
        if (IsRemovedHardcodedMixId(mixId))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        long? mixCacheId;
        const string selectSql = @"
SELECT id
FROM mix_cache
WHERE mix_id = @mixId
  AND plex_user_id = @plexUserId
  AND library_id = @libraryId;";
        await using (var select = new SqliteCommand(selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("mixId", mixId);
            select.Parameters.AddWithValue("plexUserId", plexUserId);
            select.Parameters.AddWithValue(LibraryIdField, libraryId);
            var result = await select.ExecuteScalarAsync(cancellationToken);
            mixCacheId = result is null || result == DBNull.Value ? null : Convert.ToInt64(result);
        }

        if (mixCacheId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var deleteItems = new SqliteCommand("DELETE FROM mix_item WHERE mix_cache_id = @mixCacheId;", connection, transaction))
        {
            deleteItems.Parameters.AddWithValue("mixCacheId", mixCacheId.Value);
            await deleteItems.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteMix = new SqliteCommand("DELETE FROM mix_cache WHERE id = @mixCacheId;", connection, transaction))
        {
            deleteMix.Parameters.AddWithValue("mixCacheId", mixCacheId.Value);
            await deleteMix.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<MixSummaryDto> ReadMixSummaryAsync(SqliteDataReader reader, long libraryId, CancellationToken cancellationToken)
    {
        var coverJson = await reader.IsDBNullAsync(5, cancellationToken) ? "[]" : reader.GetString(5);
        var covers = System.Text.Json.JsonSerializer.Deserialize<List<string>>(coverJson) ?? new List<string>();
        return new MixSummaryDto(
            reader.GetString(1),
            reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? string.Empty : reader.GetString(3),
            reader.GetInt32(4),
            covers,
            ParseDateTimeOffsetInvariant(reader.GetString(6)),
            ParseDateTimeOffsetInvariant(reader.GetString(7)),
            libraryId);
    }

    private static bool IsRemovedHardcodedMixId(string? mixId)
        => string.Equals(mixId, "top-tracks", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mixId, "rediscover", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mixId, "library-shuffle", StringComparison.OrdinalIgnoreCase);

    public async Task<long> UpsertMixCacheAsync(
        MixCacheUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO mix_cache (mix_id, plex_user_id, library_id, name, description, track_count, cover_urls_json, generated_at_utc, expires_at_utc)
VALUES (@mixId, @plexUserId, @libraryId, @name, @description, @trackCount, @coverUrls, @generatedAt, @expiresAt)
ON CONFLICT(mix_id, library_id, plex_user_id)
DO UPDATE SET
    name = excluded.name,
    description = excluded.description,
    track_count = excluded.track_count,
    cover_urls_json = excluded.cover_urls_json,
    generated_at_utc = excluded.generated_at_utc,
    expires_at_utc = excluded.expires_at_utc,
    updated_at = CURRENT_TIMESTAMP
RETURNING id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("mixId", input.MixId);
        command.Parameters.AddWithValue("plexUserId", input.PlexUserId);
        command.Parameters.AddWithValue(LibraryIdField, input.LibraryId);
        command.Parameters.AddWithValue("name", input.Name);
        command.Parameters.AddWithValue("description", input.Description);
        command.Parameters.AddWithValue(TrackCountField, input.TrackCount);
        command.Parameters.AddWithValue("coverUrls", System.Text.Json.JsonSerializer.Serialize(input.CoverUrls));
        command.Parameters.AddWithValue("generatedAt", input.GeneratedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("expiresAt", input.ExpiresAtUtc.ToString("O"));
        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        return inserted is long insertedId ? insertedId : Convert.ToInt64(inserted);
    }

    public async Task ReplaceMixItemsAsync(long mixCacheId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string deleteSql = "DELETE FROM mix_item WHERE mix_cache_id = @mixCacheId;";
        await using (var delete = new SqliteCommand(deleteSql, connection, transaction))
        {
            delete.Parameters.AddWithValue("mixCacheId", mixCacheId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = @"
INSERT INTO mix_item (mix_cache_id, position, track_id)
VALUES (@mixCacheId, @position, @trackId);";
        for (var i = 0; i < trackIds.Count; i++)
        {
            await using var insert = new SqliteCommand(insertSql, connection, transaction);
            insert.Parameters.AddWithValue("mixCacheId", mixCacheId);
            insert.Parameters.AddWithValue("position", i + 1);
            insert.Parameters.AddWithValue(TrackIdField, trackIds[i]);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MixTrackDto>> GetMixTracksAsync(long mixCacheId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT mi.position,
       t.id,
       t.title,
       ar.name,
	       al.title,
	       al.preferred_cover_path,
	       t.duration_ms,
	       selected_audio.audio_file_id,
	       selected_audio.file_path,
	       selected_audio.audio_variant
	FROM mix_item mi
	LEFT JOIN track t ON t.id = mi.track_id
	LEFT JOIN album al ON al.id = t.album_id
	LEFT JOIN artist ar ON ar.id = al.artist_id
	LEFT JOIN (
	    SELECT track_id,
	           audio_file_id,
	           audio_variant,
	           file_path
	    FROM (
	        SELECT tl.track_id,
	               af.id AS audio_file_id,
	               af.audio_variant,
	               COALESCE(
	                   CASE
	                       WHEN f.root_path IS NOT NULL AND af.relative_path IS NOT NULL AND TRIM(af.relative_path) <> ''
	                       THEN rtrim(f.root_path, '/\') || '/' || af.relative_path
	                   END,
	                   af.path) AS file_path,
	               ROW_NUMBER() OVER (
	                   PARTITION BY tl.track_id
	                   ORDER BY f.enabled DESC,
	                            af.quality_rank DESC NULLS LAST,
	                            af.size DESC,
	                            af.id DESC) AS rn
	        FROM track_local tl
	        JOIN audio_file af ON af.id = tl.audio_file_id
	        LEFT JOIN folder f ON f.id = af.folder_id
	    )
	    WHERE rn = 1
	) selected_audio ON selected_audio.track_id = t.id
	WHERE mi.mix_cache_id = @mixCacheId
	ORDER BY mi.position;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("mixCacheId", mixCacheId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tracks = new List<MixTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (await reader.IsDBNullAsync(1, cancellationToken))
            {
                continue;
            }
            tracks.Add(new MixTrackDto(
                reader.GetInt64(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? "Unknown" : reader.GetString(2),
	                await reader.IsDBNullAsync(3, cancellationToken) ? "Unknown" : reader.GetString(3),
	                await reader.IsDBNullAsync(4, cancellationToken) ? "Unknown" : reader.GetString(4),
	                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
	                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt32(6),
	                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetInt64(7),
	                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
	                await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
	                BuildVariantKey(reader.GetInt64(1), await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetInt64(7), 0)));
        }
        return tracks;
    }

    public async Task<FolderDto?> ResolveFolderForPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var folders = await GetFoldersAsync(cancellationToken);
        if (folders.Count == 0 || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var folderRoots = folders
            .Select(folder => new FolderRoot(folder.Id, NormalizeRoot(folder.RootPath), folder.RootPath))
            .OrderByDescending(item => item.Root.Length)
            .ToList();
        var folderRoot = FindFolderForPath(folderRoots, filePath);
        if (folderRoot is null)
        {
            return null;
        }

        return folders.FirstOrDefault(folder => folder.Id == folderRoot.Id);
    }

    public async Task<bool> HasLocalLibraryDataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT EXISTS(SELECT 1 FROM album_local LIMIT 1);";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<FolderDto> AddFolderAsync(
        FolderUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        if (RequiresAutoTagProfile(input.DesiredQuality)
            && string.IsNullOrWhiteSpace(input.AutoTagProfileId))
        {
            throw new ArgumentException("Music folders require an AutoTag profile.", nameof(input));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var libraryId = await EnsureLibraryAsync(
            connection,
            ResolveCanonicalLibraryName(input.LibraryName, input.DisplayName),
            cancellationToken);
        var desiredQualityNumeric = NormalizeDesiredQualityRank(input.DesiredQuality);
        var autoTagEnabled = !RequiresAutoTagProfile(input.DesiredQuality)
            || !string.IsNullOrWhiteSpace(input.AutoTagProfileId);
        var (normalizedConvertEnabled, normalizedConvertFormat, normalizedConvertBitrate) =
            NormalizeFolderConvertSettings(input.ConvertEnabled, input.ConvertFormat, input.ConvertBitrate);
        const string sql = @"
INSERT INTO folder (root_path, display_name, enabled, library_id, desired_quality, desired_quality_value, auto_tag_enabled, auto_tag_profile_id, convert_enabled, convert_format, convert_bitrate)
VALUES (@rootPath, @displayName, @enabled, @libraryId, @desiredQualityNumeric, @desiredQualityValue, @autoTagEnabled, @autoTagProfileId, @convertEnabled, @convertFormat, @convertBitrate)
RETURNING id;";
        await using var command = new SqliteCommand(sql, connection);
        AddFolderCommonParameters(
            command,
            new FolderCommonParameters(
                input.RootPath,
                input.DisplayName,
                input.Enabled,
                libraryId,
                desiredQualityNumeric,
                input.DesiredQuality,
                normalizedConvertEnabled,
                normalizedConvertFormat,
                normalizedConvertBitrate));
        command.Parameters.AddWithValue("autoTagEnabled", autoTagEnabled);
        command.Parameters.AddWithValue("autoTagProfileId", (object?)input.AutoTagProfileId ?? DBNull.Value);
        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        return (await GetFoldersAsync(cancellationToken)).First(folder => folder.Id == Convert.ToInt64(insertedId));
    }

    public async Task<FolderDto?> UpdateFolderAsync(
        long id,
        FolderUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var libraryId = await ResolveExistingFolderLibraryIdAsync(
            connection,
            id,
            input.LibraryName,
            input.DisplayName,
            cancellationToken);
        var desiredQualityNumeric = NormalizeDesiredQualityRank(input.DesiredQuality);
        var (normalizedConvertEnabled, normalizedConvertFormat, normalizedConvertBitrate) =
            NormalizeFolderConvertSettings(input.ConvertEnabled, input.ConvertFormat, input.ConvertBitrate);
        const string sql = @"
UPDATE folder
SET root_path = @rootPath,
    display_name = @displayName,
    enabled = @enabled,
    library_id = @libraryId,
    desired_quality = @desiredQualityNumeric,
    desired_quality_value = @desiredQualityValue,
    auto_tag_profile_id = CASE WHEN @replaceAutoTagProfile = 1 THEN @autoTagProfileId ELSE auto_tag_profile_id END,
    convert_enabled = @convertEnabled,
    convert_format = @convertFormat,
    convert_bitrate = @convertBitrate,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        AddFolderCommonParameters(
            command,
            new FolderCommonParameters(
                input.RootPath,
                input.DisplayName,
                input.Enabled,
                libraryId,
                desiredQualityNumeric,
                input.DesiredQuality,
                normalizedConvertEnabled,
                normalizedConvertFormat,
                normalizedConvertBitrate));
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("autoTagProfileId", (object?)input.AutoTagProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("replaceAutoTagProfile", input.ReplaceAutoTagProfile ? 1 : 0);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            return null;
        }

        return (await GetFoldersAsync(cancellationToken)).FirstOrDefault(folder => folder.Id == id);
    }

    public async Task<FolderDto?> UpdateFolderProfileAsync(long id, string? autoTagProfileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE folder
SET auto_tag_profile_id = @autoTagProfileId,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("autoTagProfileId", (object?)autoTagProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("id", id);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            return null;
        }

        return (await GetFoldersAsync(cancellationToken)).FirstOrDefault(folder => folder.Id == id);
    }

    public async Task<FolderDto?> UpdateFolderAutoTagEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE folder
SET auto_tag_enabled = @enabled,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("id", id);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            return null;
        }

        return (await GetFoldersAsync(cancellationToken)).FirstOrDefault(folder => folder.Id == id);
    }

    public async Task<bool> DeleteFolderAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM folder WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task DisableFolderAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await NullFolderReferencesAsync(connection, transaction, id, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FolderAliasDto>> GetFolderAliasesAsync(long folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT id, folder_id, alias_name FROM folder_alias WHERE folder_id = @folderId ORDER BY alias_name;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var aliases = new List<FolderAliasDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            aliases.Add(new FolderAliasDto(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return aliases;
    }

    public async Task<FolderAliasDto> AddFolderAliasAsync(long folderId, string aliasName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO folder_alias (folder_id, alias_name)
VALUES (@folderId, @aliasName)
RETURNING id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        command.Parameters.AddWithValue("aliasName", aliasName);
        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        return new FolderAliasDto(Convert.ToInt64(insertedId), folderId, aliasName);
    }

    public async Task<bool> DeleteFolderAliasAsync(long aliasId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM folder_alias WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", aliasId);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public Task<IReadOnlyList<ArtistDto>> GetArtistsAsync(string? availability, CancellationToken cancellationToken = default)
        => GetArtistsAsync(availability, null, cancellationToken);

    public async Task<IReadOnlyList<ArtistDto>> GetArtistsAsync(
        string? availability,
        long? folderId,
        CancellationToken cancellationToken = default)
    {
        const int chunkSize = 1000;
        var pageIndex = 1;
        var all = new List<ArtistDto>();
        while (true)
        {
            var page = await GetArtistsPageAsync(
                availability,
                folderId,
                page: pageIndex,
                pageSize: chunkSize,
                search: null,
                sort: null,
                cancellationToken);
            if (page.Items.Count == 0)
            {
                break;
            }

            all.AddRange(page.Items);
            if (all.Count >= page.TotalCount)
            {
                break;
            }

            pageIndex++;
        }

        return all;
    }

    public async Task<ArtistPageDto> GetArtistsPageAsync(
        string? availability,
        long? folderId,
        int page,
        int pageSize,
        string? search = null,
        string? sort = null,
        CancellationToken cancellationToken = default)
    {
        var filters = availability?.ToLowerInvariant() ?? "all";
        if (filters == "remote")
        {
            return new ArtistPageDto(Array.Empty<ArtistDto>(), 0, Math.Max(1, page), Math.Clamp(pageSize, 1, 1000));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);
        var offset = (safePage - 1) * safePageSize;
        var normalizedSearch = (search ?? string.Empty).Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(normalizedSearch);
        var searchPattern = hasSearch ? $"%{normalizedSearch}%" : null;
        var sortKey = (sort ?? "name-asc").Trim().ToLowerInvariant();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string countSql = @"
SELECT COUNT(*)
FROM (
    SELECT DISTINCT a.id
    FROM artist a
    JOIN album al ON al.artist_id = a.id
    JOIN track t ON t.album_id = al.id
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.enabled = TRUE
      AND (@folderId IS NULL OR af.folder_id = @folderId)
      AND (@searchPattern IS NULL OR a.name LIKE @searchPattern COLLATE NOCASE)
);";
        await using var countCommand = new SqliteCommand(countSql, connection);
        countCommand.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        countCommand.Parameters.AddWithValue("searchPattern", (object?)searchPattern ?? DBNull.Value);
        var totalCountObj = await countCommand.ExecuteScalarAsync(cancellationToken);
        var totalCount = totalCountObj is null || totalCountObj is DBNull
            ? 0
            : Convert.ToInt32(totalCountObj, CultureInfo.InvariantCulture);
        if (totalCount <= 0)
        {
            return new ArtistPageDto(Array.Empty<ArtistDto>(), 0, safePage, safePageSize);
        }

        const string pageSql = @"
SELECT DISTINCT
       a.id,
       a.name,
       a.preferred_image_path,
       a.preferred_background_path,
       a.apple_biography,
       a.apple_biography_checked_at,
       a.lastfm_images_checked_at
FROM artist a
JOIN album al ON al.artist_id = a.id
JOIN track t ON t.album_id = al.id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = TRUE
  AND (@folderId IS NULL OR af.folder_id = @folderId)
  AND (@searchPattern IS NULL OR a.name LIKE @searchPattern COLLATE NOCASE)
ORDER BY
    CASE WHEN @sortDesc = 0 THEN a.name END ASC,
    CASE WHEN @sortDesc = 1 THEN a.name END DESC
LIMIT @limit OFFSET @offset;";
        await using var command = new SqliteCommand(pageSql, connection);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("searchPattern", (object?)searchPattern ?? DBNull.Value);
        command.Parameters.AddWithValue("sortDesc", sortKey == "name-desc" ? 1 : 0);
        command.Parameters.AddWithValue("limit", safePageSize);
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artists = new List<ArtistDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artists.Add(new ArtistDto(
                reader.GetInt64(0),
                reader.GetString(1),
                true,
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await ReadDateTimeOffsetAsync(reader, 5, cancellationToken),
                await ReadDateTimeOffsetAsync(reader, 6, cancellationToken)));
        }

        return new ArtistPageDto(artists, totalCount, safePage, safePageSize);
    }

    public Task<IReadOnlyList<AlbumDto>> GetArtistAlbumsAsync(long artistId, CancellationToken cancellationToken = default)
        => GetArtistAlbumsAsync(artistId, null, cancellationToken);

    public async Task<IReadOnlyList<AlbumDto>> GetArtistAlbumsAsync(
        long artistId,
        long? folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
WITH album_audio_flags AS (
    SELECT
        t.album_id AS album_id,
        t.id AS track_id,
        CASE
            WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
            WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
            WHEN (
                LOWER(COALESCE(af.codec, '')) LIKE '%flac%'
                OR LOWER(COALESCE(af.codec, '')) LIKE '%alac%'
                OR LOWER(COALESCE(af.codec, '')) LIKE '%pcm%'
                OR LOWER(COALESCE(af.codec, '')) LIKE '%wave%'
                OR LOWER(COALESCE(af.codec, '')) LIKE '%wav%'
                OR LOWER(COALESCE(af.codec, '')) LIKE '%aiff%'
                OR LOWER(COALESCE(af.extension, '')) IN ('.flac', '.wav', '.aiff', '.aif', '.alac')
            ) THEN 0
            WHEN (
                LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                OR (
                    (
                        LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                        OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                    )
                    AND af.channels IS NOT NULL
                    AND af.channels > 2
                )
                OR (
                    (
                        LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/atmos/%'
                        OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/dolby atmos/%'
                        OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/spatial/%'
                        OR LOWER(COALESCE(af.path, '')) LIKE '%atmos%'
                    )
                    AND (
                        (af.channels IS NOT NULL AND af.channels > 2)
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                        OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                    )
                )
            ) THEN 1
            ELSE 0
        END AS is_atmos
    FROM track t
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE f.enabled = TRUE
      AND (@folderId IS NULL OR af.folder_id = @folderId)
),
album_variant_counts AS (
    SELECT
        album_id,
        COUNT(DISTINCT CASE WHEN is_atmos = 0 THEN track_id END) AS local_stereo_track_count,
        COUNT(DISTINCT CASE WHEN is_atmos = 1 THEN track_id END) AS local_atmos_track_count
    FROM album_audio_flags
    GROUP BY album_id
)
SELECT al.id,
       al.artist_id,
       al.title,
       al.preferred_cover_path,
       COALESCE(
           (
               SELECT GROUP_CONCAT(folder_name, '|')
               FROM (
                   SELECT DISTINCT f.display_name AS folder_name
                   FROM track t_local
                   JOIN track_local tl_local ON tl_local.track_id = t_local.id
                   JOIN audio_file af_local ON af_local.id = tl_local.audio_file_id
                   JOIN folder f ON f.id = af_local.folder_id
                   WHERE t_local.album_id = al.id
                     AND f.enabled = TRUE
                     AND (@folderId IS NULL OR af_local.folder_id = @folderId)
                   ORDER BY folder_name
               )
           ),
           ''
       ) AS local_folders,
       COALESCE(
           (
               SELECT COUNT(DISTINCT tl_count.track_id)
               FROM track_local tl_count
               JOIN track t_count ON t_count.id = tl_count.track_id
               JOIN audio_file af_count ON af_count.id = tl_count.audio_file_id
               JOIN folder f_count ON f_count.id = af_count.folder_id
               WHERE t_count.album_id = al.id
                 AND f_count.enabled = TRUE
                 AND (@folderId IS NULL OR af_count.folder_id = @folderId)
           ),
           0
       ) AS local_track_count,
       COALESCE(avc.local_stereo_track_count, 0) AS local_stereo_track_count,
       COALESCE(avc.local_atmos_track_count, 0) AS local_atmos_track_count,
       CASE
           WHEN COALESCE(avc.local_stereo_track_count, 0) > 0 THEN 1
           ELSE 0
       END AS has_stereo_variant,
       CASE
           WHEN COALESCE(avc.local_atmos_track_count, 0) > 0 THEN 1
           ELSE 0
       END AS has_atmos_variant
FROM album al
LEFT JOIN album_variant_counts avc ON avc.album_id = al.id
WHERE al.artist_id = @artistId
  AND EXISTS (
      SELECT 1
      FROM track t_visible
      JOIN track_local tl_visible ON tl_visible.track_id = t_visible.id
      JOIN audio_file af_visible ON af_visible.id = tl_visible.audio_file_id
      JOIN folder f_visible ON f_visible.id = af_visible.folder_id
      WHERE t_visible.album_id = al.id
        AND f_visible.enabled = TRUE
        AND (@folderId IS NULL OR af_visible.folder_id = @folderId)
  )
GROUP BY al.id, al.artist_id, al.title, al.preferred_cover_path
ORDER BY al.title;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var albums = new List<AlbumDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var foldersRaw = await reader.IsDBNullAsync(4, cancellationToken) ? string.Empty : reader.GetString(4);
            var folders = string.IsNullOrWhiteSpace(foldersRaw)
                ? Array.Empty<string>()
                : foldersRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            albums.Add(new AlbumDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                folders,
                !await reader.IsDBNullAsync(8, cancellationToken) && reader.GetInt64(8) != 0,
                !await reader.IsDBNullAsync(9, cancellationToken) && reader.GetInt64(9) != 0,
                await reader.IsDBNullAsync(5, cancellationToken) ? 0 : Convert.ToInt32(reader.GetInt64(5)),
                await reader.IsDBNullAsync(6, cancellationToken) ? 0 : Convert.ToInt32(reader.GetInt64(6)),
                await reader.IsDBNullAsync(7, cancellationToken) ? 0 : Convert.ToInt32(reader.GetInt64(7))));
        }

        return albums;
    }

    public async Task<ArtistDetailDto?> GetArtistAsync(long artistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT a.id,
       a.name,
       a.preferred_image_path,
       a.preferred_background_path,
       a.apple_biography,
       a.apple_biography_checked_at,
       a.lastfm_images_checked_at
FROM artist a
WHERE a.id = @artistId
  AND EXISTS (
      SELECT 1
      FROM album al
      JOIN track t ON t.album_id = al.id
      JOIN track_local tl ON tl.track_id = t.id
      JOIN audio_file af ON af.id = tl.audio_file_id
      JOIN folder f ON f.id = af.folder_id
      WHERE al.artist_id = a.id
        AND f.enabled = TRUE
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new ArtistDetailDto(
                reader.GetInt64(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await ReadDateTimeOffsetAsync(reader, 5, cancellationToken),
                await ReadDateTimeOffsetAsync(reader, 6, cancellationToken));
        }

        return null;
    }

    public async Task<IReadOnlyList<ArtistDetailDto>> GetArtistsMissingImageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT a.id,
       a.name,
       a.preferred_image_path,
       a.preferred_background_path,
       a.apple_biography,
       a.apple_biography_checked_at,
       a.lastfm_images_checked_at
FROM artist a
WHERE (a.preferred_image_path IS NULL OR TRIM(a.preferred_image_path) = '')
  AND EXISTS (
      SELECT 1
      FROM album al
      JOIN track t ON t.album_id = al.id
      JOIN track_local tl ON tl.track_id = t.id
      JOIN audio_file af ON af.id = tl.audio_file_id
      JOIN folder f ON f.id = af.folder_id
      WHERE al.artist_id = a.id
        AND f.enabled = TRUE
  )
ORDER BY name;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artists = new List<ArtistDetailDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artists.Add(new ArtistDetailDto(
                reader.GetInt64(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await ReadDateTimeOffsetAsync(reader, 5, cancellationToken),
                await ReadDateTimeOffsetAsync(reader, 6, cancellationToken)));
        }

        return artists;
    }

    public async Task UpdateArtistImagePathAsync(long artistId, string imagePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE artist
SET preferred_image_path = @path,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @artistId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("path", imagePath);
        command.Parameters.AddWithValue("artistId", artistId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateArtistBackgroundPathAsync(long artistId, string backgroundPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE artist
SET preferred_background_path = @path,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @artistId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("path", backgroundPath);
        command.Parameters.AddWithValue("artistId", artistId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateArtistAppleBiographyAsync(
        long artistId,
        string? biography,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE artist
SET apple_biography = @biography,
    apple_biography_checked_at = @checkedAtUtc,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @artistId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("biography", string.IsNullOrWhiteSpace(biography) ? DBNull.Value : (object)biography.Trim());
        command.Parameters.AddWithValue("checkedAtUtc", checkedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkArtistLastFmImagesCheckedAsync(
        long artistId,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE artist
SET lastfm_images_checked_at = @checkedAtUtc,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @artistId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("checkedAtUtc", checkedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArtistExternalMetadataBackfillDto>> GetArtistsForExternalMetadataBackfillAsync(
        DateTimeOffset staleBeforeUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = $@"
SELECT
       a.id,
       a.name,
       a.apple_biography,
       a.apple_biography_checked_at,
       a.lastfm_images_checked_at,
       (
           SELECT source_id
           FROM artist_source s
           WHERE s.artist_id = a.id
             AND s.source = 'apple'
           LIMIT 1
       ) AS apple_id
FROM artist a
WHERE EXISTS (
      SELECT 1
      FROM album al
      JOIN track t ON t.album_id = al.id
      JOIN track_local tl ON tl.track_id = t.id
      JOIN audio_file af ON af.id = tl.audio_file_id
      JOIN folder f ON f.id = af.folder_id
      WHERE al.artist_id = a.id
        AND f.enabled = TRUE
  )
  AND (
      a.apple_biography_checked_at IS NULL
      OR a.apple_biography_checked_at < @staleBeforeUtc
      OR a.lastfm_images_checked_at IS NULL
      OR a.lastfm_images_checked_at < @staleBeforeUtc
  )
ORDER BY
    COALESCE(a.apple_biography_checked_at, ''),
    COALESCE(a.lastfm_images_checked_at, ''),
    a.name COLLATE NOCASE
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("staleBeforeUtc", staleBeforeUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("limit", safeLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artists = new List<ArtistExternalMetadataBackfillDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artists.Add(new ArtistExternalMetadataBackfillDto(
                reader.GetInt64(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await ReadDateTimeOffsetAsync(reader, 3, cancellationToken),
                await ReadDateTimeOffsetAsync(reader, 4, cancellationToken),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5)));
        }

        return artists;
    }

    public async Task<IReadOnlyList<string>> GetArtistTrackTitlesAsync(
        long artistId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (artistId <= 0)
        {
            return Array.Empty<string>();
        }

        var safeLimit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT TRIM(COALESCE(NULLIF(t.tag_title, ''), t.title)) AS title
FROM album al
JOIN track t ON t.album_id = al.id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE al.artist_id = @artistId
  AND f.enabled = TRUE
  AND TRIM(COALESCE(NULLIF(t.tag_title, ''), t.title)) <> ''
ORDER BY title COLLATE NOCASE
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("limit", safeLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var titles = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            titles.Add(reader.GetString(0));
        }

        return titles;
    }

    public async Task<IReadOnlyList<WatchlistArtistDto>> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT w.artist_id,
       w.artist_name,
       w.spotify_id,
       w.deezer_id,
       (
           SELECT source_id
           FROM artist_source s
           WHERE s.artist_id = w.artist_id
             AND s.source = 'apple'
           LIMIT 1
       ) AS apple_id,
       a.preferred_image_path,
       w.created_at,
       ws.last_checked_utc,
       w.destination_folder_id,
       w.album_groups_json,
       w.top_songs_enabled,
       w.latest_releases_only,
       w.preferred_engine,
       w.routing_rules_json,
       w.atmos_destination_folder_id,
       w.download_variant_mode,
       w.top_songs_sync_mode,
       w.download_discography_enabled,
       w.ignore_rules_json,
       (
           SELECT source_id
           FROM artist_source s
           WHERE s.artist_id = w.artist_id
             AND s.source = 'qobuz'
           LIMIT 1
       ) AS qobuz_id,
       (
           SELECT source_id
           FROM artist_source s
           WHERE s.artist_id = w.artist_id
             AND s.source = 'tidal'
           LIMIT 1
       ) AS tidal_id
FROM artist_watchlist w
LEFT JOIN artist a ON a.id = w.artist_id
LEFT JOIN artist_watch_state ws ON ws.artist_id = w.artist_id
ORDER BY w.created_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<WatchlistArtistDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(await ReadWatchlistArtistAsync(reader, hasLastCheckedUtc: true, cancellationToken));
        }

        return items;
    }

    public async Task<bool> IsWatchlistedAsync(long artistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"SELECT EXISTS(SELECT 1 FROM artist_watchlist WHERE artist_id = @artistId);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<WatchlistArtistDto?> AddWatchlistAsync(
        long artistId,
        string artistName,
        string? spotifyId,
        string? deezerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_watchlist (artist_id, artist_name, spotify_id, deezer_id)
VALUES (@artistId, @artistName, @spotifyId, @deezerId)
ON CONFLICT(artist_id) DO UPDATE SET
    artist_name = excluded.artist_name,
    spotify_id = COALESCE(excluded.spotify_id, artist_watchlist.spotify_id),
    deezer_id = COALESCE(excluded.deezer_id, artist_watchlist.deezer_id);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("spotifyId", (object?)spotifyId ?? DBNull.Value);
        command.Parameters.AddWithValue("deezerId", (object?)deezerId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        const string selectSql = @"
SELECT w.artist_id,
       w.artist_name,
       w.spotify_id,
       w.deezer_id,
       (
           SELECT source_id
           FROM artist_source s
           WHERE s.artist_id = w.artist_id
             AND s.source = 'apple'
           LIMIT 1
       ) AS apple_id,
       a.preferred_image_path,
       w.created_at,
       w.destination_folder_id,
       w.album_groups_json,
       w.top_songs_enabled,
       w.latest_releases_only,
       w.preferred_engine,
       w.routing_rules_json,
       w.atmos_destination_folder_id,
       w.download_variant_mode,
       w.top_songs_sync_mode,
       w.download_discography_enabled,
       w.ignore_rules_json,
       (
           SELECT source_id
           FROM artist_source s
           WHERE s.artist_id = w.artist_id
             AND s.source = 'qobuz'
           LIMIT 1
       ) AS qobuz_id,
       (
           SELECT source_id
           FROM artist_source s
           WHERE s.artist_id = w.artist_id
             AND s.source = 'tidal'
           LIMIT 1
       ) AS tidal_id
FROM artist_watchlist w
LEFT JOIN artist a ON a.id = w.artist_id
WHERE w.artist_id = @artistId
LIMIT 1;";
        await using var selectCommand = new SqliteCommand(selectSql, connection);
        selectCommand.Parameters.AddWithValue("artistId", artistId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ReadWatchlistArtistAsync(reader, hasLastCheckedUtc: false, cancellationToken);
    }

    private static async Task<WatchlistArtistDto> ReadWatchlistArtistAsync(
        SqliteDataReader reader,
        bool hasLastCheckedUtc,
        CancellationToken cancellationToken)
    {
        var created = await ReadDateTimeOffsetAsync(reader, 6, cancellationToken) ?? DateTimeOffset.MinValue;
        var offset = hasLastCheckedUtc ? 1 : 0;
        var lastChecked = hasLastCheckedUtc
            ? await ReadDateTimeOffsetAsync(reader, 7, cancellationToken)
            : null;
        var routingRulesJson = await ReadStringAsync(reader, 12 + offset, cancellationToken);
        var ignoreRulesJson = await ReadStringAsync(reader, 17 + offset, cancellationToken);

        return new WatchlistArtistDto(
            reader.GetInt64(0),
            reader.GetString(1),
            await ReadStringAsync(reader, 2, cancellationToken),
            await ReadStringAsync(reader, 3, cancellationToken),
            await ReadStringAsync(reader, 4, cancellationToken),
            await ReadStringAsync(reader, 5, cancellationToken),
            created,
            lastChecked,
            await ReadInt64Async(reader, 7 + offset, cancellationToken),
            await ReadStringListAsync(reader, 8 + offset, cancellationToken),
            await ReadBooleanAsync(reader, 9 + offset, cancellationToken),
            await ReadBooleanAsync(reader, 10 + offset, cancellationToken),
            await ReadStringAsync(reader, 11 + offset, cancellationToken),
            routingRulesJson is null ? null : JsonSerializer.Deserialize<List<PlaylistTrackRoutingRule>>(routingRulesJson),
            await ReadInt64Async(reader, 13 + offset, cancellationToken),
            await ReadStringAsync(reader, 14 + offset, cancellationToken),
            await ReadStringAsync(reader, 15 + offset, cancellationToken),
            await ReadBooleanAsync(reader, 16 + offset, cancellationToken),
            ignoreRulesJson is null ? null : JsonSerializer.Deserialize<List<PlaylistTrackBlockRule>>(ignoreRulesJson),
            await ReadStringAsync(reader, 18 + offset, cancellationToken),
            await ReadStringAsync(reader, 19 + offset, cancellationToken));
    }

    private static async Task<string?> ReadStringAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);

    private static async Task<long?> ReadInt64Async(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt64(ordinal);

    private static async Task<bool?> ReadBooleanAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt32(ordinal) != 0;

    private static async Task<DateTimeOffset?> ReadDateTimeOffsetAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(ordinal));

    private static async Task<IReadOnlyList<string>?> ReadStringListAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : DeserializeStringList(reader.GetString(ordinal));

    public async Task<bool> UpdateWatchlistPreferencesAsync(
        ArtistWatchPreferenceUpdateInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE artist_watchlist
SET destination_folder_id = @destinationFolderId,
    album_groups_json = @albumGroupsJson,
    top_songs_enabled = @topSongsEnabled,
    latest_releases_only = @latestReleasesOnly,
    preferred_engine = @preferredEngine,
    routing_rules_json = @routingRulesJson,
    atmos_destination_folder_id = @atmosDestinationFolderId,
    download_variant_mode = @downloadVariantMode,
    top_songs_sync_mode = @topSongsSyncMode,
    download_discography_enabled = @downloadDiscographyEnabled,
    ignore_rules_json = @ignoreRulesJson
WHERE artist_id = @artistId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", input.ArtistId);
        command.Parameters.AddWithValue("destinationFolderId", (object?)input.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("albumGroupsJson", ToJsonDbValue(input.AlbumGroups));
        command.Parameters.AddWithValue("topSongsEnabled", ToDbBoolean(input.TopSongsEnabled));
        command.Parameters.AddWithValue("latestReleasesOnly", ToDbBoolean(input.LatestReleasesOnly));
        command.Parameters.AddWithValue("preferredEngine", ToLowerTextDbValue(input.PreferredEngine));
        command.Parameters.AddWithValue("routingRulesJson", ToJsonDbValue(input.RoutingRules));
        command.Parameters.AddWithValue("atmosDestinationFolderId", (object?)input.AtmosDestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("downloadVariantMode", ToLowerTextDbValue(input.DownloadVariantMode));
        command.Parameters.AddWithValue("topSongsSyncMode", ToLowerTextDbValue(input.TopSongsSyncMode));
        command.Parameters.AddWithValue("downloadDiscographyEnabled", ToDbBoolean(input.DownloadDiscographyEnabled));
        command.Parameters.AddWithValue("ignoreRulesJson", ToJsonDbValue(input.IgnoreRules));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static object ToDbBoolean(bool? value)
    {
        if (!value.HasValue)
        {
            return DBNull.Value;
        }

        return value.Value ? 1 : 0;
    }

    private static object ToLowerTextDbValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim().ToLowerInvariant();

    private static object ToJsonDbValue<T>(IReadOnlyCollection<T>? values)
        => values is { Count: > 0 } ? JsonSerializer.Serialize(values) : DBNull.Value;

    public async Task<bool> RemoveWatchlistAsync(long artistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
DELETE FROM artist_watch_album WHERE artist_id = @artistId;
DELETE FROM artist_watch_state WHERE artist_id = @artistId;
DELETE FROM artist_watchlist WHERE artist_id = @artistId;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("artistId", artistId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<bool> IsWatchlistedBySpotifyIdAsync(string spotifyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return false;
        }

        var normalizedSpotifyId = spotifyId.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"SELECT EXISTS(SELECT 1 FROM artist_watchlist WHERE LOWER(spotify_id) = LOWER(@spotifyId));";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("spotifyId", normalizedSpotifyId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<bool> RemoveWatchlistBySpotifyIdAsync(string spotifyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return false;
        }

        var normalizedSpotifyId = spotifyId.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
DELETE FROM artist_watch_album WHERE artist_id IN (SELECT artist_id FROM artist_watchlist WHERE LOWER(spotify_id)=LOWER(@spotifyId));
DELETE FROM artist_watch_state WHERE artist_id IN (SELECT artist_id FROM artist_watchlist WHERE LOWER(spotify_id)=LOWER(@spotifyId));
DELETE FROM artist_watchlist WHERE LOWER(spotify_id) = LOWER(@spotifyId);";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("spotifyId", normalizedSpotifyId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<ArtistWatchStateDto?> GetArtistWatchStateAsync(long artistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT artist_id,
       spotify_id,
       batch_next_offset,
       last_checked_utc,
       updated_at,
       last_run_status,
       last_run_message,
       next_attempt_utc,
       consecutive_failures,
       current_phase,
       heartbeat_utc,
       deadline_utc,
       apple_next_offset,
       deezer_next_offset,
       tidal_next_offset
FROM artist_watch_state
WHERE artist_id = @artistId
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var lastChecked = await reader.IsDBNullAsync(3, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(3));
        var updated = await reader.IsDBNullAsync(4, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(4));
        return new ArtistWatchStateDto(
            reader.GetInt64(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetInt32(2),
            lastChecked,
            updated,
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(7)),
            await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetInt32(8),
            await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
            await reader.IsDBNullAsync(10, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(10)),
            await reader.IsDBNullAsync(11, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(11)),
            await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetInt32(12),
            await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetInt32(13),
            await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetInt32(14));
    }

    public async Task UpsertArtistWatchSourceOffsetAsync(
        long artistId,
        string source,
        int? nextOffset,
        CancellationToken cancellationToken = default)
    {
        var column = source?.Trim().ToLowerInvariant() switch
        {
            "apple" => "apple_next_offset",
            "deezer" => "deezer_next_offset",
            "tidal" => "tidal_next_offset",
            _ => null
        };
        if (column == null)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = $@"
INSERT INTO artist_watch_state (artist_id, {column})
VALUES (@artistId, @nextOffset)
ON CONFLICT(artist_id) DO UPDATE SET
    {column} = excluded.{column},
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("nextOffset", (object?)nextOffset ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertArtistWatchStateAsync(
        long artistId,
        string? spotifyId,
        int? batchNextOffset,
        DateTimeOffset? lastCheckedUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_watch_state (artist_id, spotify_id, batch_next_offset, last_checked_utc)
VALUES (@artistId, @spotifyId, @batchNextOffset, @lastCheckedUtc)
ON CONFLICT(artist_id) DO UPDATE SET
    spotify_id = excluded.spotify_id,
    batch_next_offset = excluded.batch_next_offset,
    last_checked_utc = excluded.last_checked_utc,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("spotifyId", (object?)spotifyId ?? DBNull.Value);
        command.Parameters.AddWithValue("batchNextOffset", (object?)batchNextOffset ?? DBNull.Value);
        command.Parameters.AddWithValue("lastCheckedUtc", lastCheckedUtc?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateArtistWatchRunStateAsync(
        long artistId,
        string status,
        string? message,
        DateTimeOffset? nextAttemptUtc,
        int consecutiveFailures,
        string phase,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
INSERT INTO artist_watch_state (
    artist_id,last_run_status,last_run_message,next_attempt_utc,consecutive_failures,current_phase,heartbeat_utc,deadline_utc)
VALUES (
    @artistId,@status,@message,@nextAttemptUtc,@consecutiveFailures,@phase,@heartbeatUtc,@deadlineUtc)
ON CONFLICT(artist_id) DO UPDATE SET
    last_run_status=excluded.last_run_status,
    last_run_message=excluded.last_run_message,
    next_attempt_utc=excluded.next_attempt_utc,
    consecutive_failures=excluded.consecutive_failures,
    current_phase=excluded.current_phase,
    heartbeat_utc=excluded.heartbeat_utc,
    deadline_utc=excluded.deadline_utc,
    updated_at=CURRENT_TIMESTAMP;", connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("status", status.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("nextAttemptUtc", nextAttemptUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("consecutiveFailures", Math.Max(0, consecutiveFailures));
        command.Parameters.AddWithValue("phase", phase.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("heartbeatUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("deadlineUtc", deadlineUtc?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RecoverStaleWatchlistWorkAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var playlists = new SqliteCommand(@"
UPDATE playlist_watch_state
SET last_run_status='backoff',
    last_run_message='Recovered stale Watchlist work after its persisted deadline expired.',
    next_attempt_utc=CURRENT_TIMESTAMP,
    consecutive_failures=MIN(1, COALESCE(consecutive_failures,0)+1),
    current_phase='stale_recovered',
    heartbeat_utc=CURRENT_TIMESTAMP,
    deadline_utc=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE deadline_utc IS NOT NULL
  AND datetime(deadline_utc) <= datetime('now')
  AND datetime(COALESCE(heartbeat_utc, updated_at)) <= datetime('now', '-20 minutes')
  AND lower(COALESCE(current_phase,'')) NOT IN
      ('completed','source_failure','backoff','stale_recovered')
  AND NOT EXISTS (
        SELECT 1 FROM watchlist_sync_job j
         WHERE j.source = playlist_watch_state.source
           AND j.playlist_id = playlist_watch_state.source_id
           AND lower(j.status) = 'processing'
           AND datetime(j.lease_until_utc) > datetime('now'));", connection, transaction);
        var recovered = await playlists.ExecuteNonQueryAsync(cancellationToken);
        await using var artists = new SqliteCommand(@"
UPDATE artist_watch_state
SET last_run_status='backoff',
    last_run_message='Recovered stale Watchlist work after its persisted deadline expired.',
    next_attempt_utc=CURRENT_TIMESTAMP,
    consecutive_failures=MIN(1, COALESCE(consecutive_failures,0)+1),
    current_phase='stale_recovered',
    heartbeat_utc=CURRENT_TIMESTAMP,
    deadline_utc=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE deadline_utc IS NOT NULL
  AND datetime(deadline_utc) <= datetime('now')
  AND datetime(COALESCE(heartbeat_utc, updated_at)) <= datetime('now', '-20 minutes')
  AND lower(COALESCE(current_phase,'')) NOT IN
      ('completed','source_failure','backoff','stale_recovered');", connection, transaction);
        recovered += await artists.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return recovered;
    }

    public async Task<bool> ApplyWatchlistSmoothSyncRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var alreadyApplied = new SqliteCommand(@"
SELECT 1
FROM playlist_watch_state
WHERE COALESCE(recovery_generation, 0) >= 1
LIMIT 1;", connection);
        if (await alreadyApplied.ExecuteScalarAsync(cancellationToken) is not null)
        {
            await using var stampLeftovers = new SqliteCommand(@"
UPDATE playlist_watch_state
SET recovery_generation=1
WHERE COALESCE(recovery_generation, 0) < 1;", connection);
            await stampLeftovers.ExecuteNonQueryAsync(cancellationToken);
            return false;
        }

        await using var needsRecovery = new SqliteCommand(@"
SELECT 1
FROM playlist_watch_state
WHERE COALESCE(recovery_generation, 0) < 1
LIMIT 1;", connection);
        if (await needsRecovery.ExecuteScalarAsync(cancellationToken) is null)
        {
            return false;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var resetBackoff = new SqliteCommand(@"
UPDATE playlist_watch_state
SET last_run_status='pending',
    last_run_message=NULL,
    next_attempt_utc=CURRENT_TIMESTAMP,
    consecutive_failures=0,
    current_phase='pending',
    heartbeat_utc=CURRENT_TIMESTAMP,
    deadline_utc=NULL,
    recovery_generation=1,
    updated_at=CURRENT_TIMESTAMP
WHERE lower(COALESCE(last_run_status,'')) IN ('backoff','stale_recovered')
   OR lower(COALESCE(current_phase,'')) IN ('backoff','stale_recovered');", connection, transaction))
        {
            await resetBackoff.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var resetBlockedIdentityJobs = new SqliteCommand($@"
UPDATE watchlist_sync_job
SET status='pending',
    attempt_count=0,
    lease_owner=NULL,
    lease_until_utc=NULL,
    next_attempt_utc=CURRENT_TIMESTAMP,
    updated_at=CURRENT_TIMESTAMP
WHERE lower(status)='blocked'
  AND {WatchlistIdentityErrorFingerprintSql};", connection, transaction))
        {
            await resetBlockedIdentityJobs.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var resetDueIdentityJobs = new SqliteCommand($@"
UPDATE watchlist_sync_job
SET next_attempt_utc=CURRENT_TIMESTAMP,
    updated_at=CURRENT_TIMESTAMP
WHERE lower(status) IN ('pending','retry')
  AND {WatchlistIdentityErrorFingerprintSql};", connection, transaction))
        {
            await resetDueIdentityJobs.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var resetIdentityCircuits = new SqliteCommand($@"
UPDATE watchlist_target_circuit_state
SET is_open=0,
    open_until_utc=NULL,
    reason=NULL,
    failure_count=0,
    updated_at=CURRENT_TIMESTAMP
WHERE {WatchlistIdentityCircuitReasonSql};", connection, transaction))
        {
            await resetIdentityCircuits.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var markRecovered = new SqliteCommand(@"
UPDATE playlist_watch_state
SET recovery_generation=1
WHERE COALESCE(recovery_generation, 0) < 1;", connection, transaction))
        {
            await markRecovered.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<HashSet<string>> GetArtistWatchAlbumIdsAsync(
        long artistId,
        string source,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT album_source_id
FROM artist_watch_album
WHERE artist_id = @artistId AND source = @source;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue(SourceField, source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                var value = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    public async Task AddArtistWatchAlbumsAsync(
        long artistId,
        IReadOnlyCollection<ArtistWatchAlbumInsert> albums,
        CancellationToken cancellationToken = default)
    {
        if (albums.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
INSERT OR IGNORE INTO artist_watch_album (artist_id, source, album_source_id)
VALUES (@artistId, @source, @albumSourceId);";
        await using var command = new SqliteCommand(sql, connection, transaction);
        var artistParam = command.Parameters.Add("artistId", SqliteType.Integer);
        var sourceParam = command.Parameters.Add("source", SqliteType.Text);
        var albumParam = command.Parameters.Add("albumSourceId", SqliteType.Text);

        foreach (var album in albums)
        {
            if (string.IsNullOrWhiteSpace(album.AlbumSourceId))
            {
                continue;
            }

            artistParam.Value = artistId;
            sourceParam.Value = album.Source;
            albumParam.Value = album.AlbumSourceId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveArtistWatchAlbumsExceptAsync(
        long artistId,
        string source,
        string idPrefix,
        IReadOnlyCollection<string> retainedIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var normalizedPrefix = idPrefix ?? string.Empty;
        if (retainedIds.Count == 0)
        {
            const string deleteAllSql = @"
DELETE FROM artist_watch_album
WHERE artist_id = @artistId
  AND source = @source
  AND album_source_id LIKE @idPrefix;";
            await using var deleteAllCommand = new SqliteCommand(deleteAllSql, connection);
            deleteAllCommand.Parameters.AddWithValue("artistId", artistId);
            deleteAllCommand.Parameters.AddWithValue(SourceField, source);
            deleteAllCommand.Parameters.AddWithValue("idPrefix", $"{normalizedPrefix}%");
            await deleteAllCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string selectSql = @"
SELECT album_source_id
FROM artist_watch_album
WHERE artist_id = @artistId
  AND source = @source
  AND album_source_id LIKE @idPrefix;";
        await using var selectCommand = new SqliteCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("artistId", artistId);
        selectCommand.Parameters.AddWithValue(SourceField, source);
        selectCommand.Parameters.AddWithValue("idPrefix", $"{normalizedPrefix}%");
        var idsToRemove = new List<string>();
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var albumSourceId = reader.GetString(0);
                if (!retainedIds.Contains(albumSourceId))
                {
                    idsToRemove.Add(albumSourceId);
                }
            }
        }

        const string deleteSql = @"
DELETE FROM artist_watch_album
WHERE artist_id = @artistId
  AND source = @source
  AND album_source_id = @albumSourceId;";
        await using var deleteCommand = new SqliteCommand(deleteSql, connection, transaction);
        var artistParam = deleteCommand.Parameters.Add("artistId", SqliteType.Integer);
        var sourceParam = deleteCommand.Parameters.Add(SourceField, SqliteType.Text);
        var albumParam = deleteCommand.Parameters.Add("albumSourceId", SqliteType.Text);
        foreach (var id in idsToRemove)
        {
            artistParam.Value = artistId;
            sourceParam.Value = source;
            albumParam.Value = id;
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Playlist watchlist hydration keeps explicit null/date handling for schema compatibility.")]
    public async Task<IReadOnlyList<PlaylistWatchlistDto>> GetPlaylistWatchlistAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id,
       pw.source,
       pw.source_id,
       name,
       image_url,
       description,
       pw.track_count,
       created_at,
       pws.last_checked_utc,
       pws.snapshot_id,
       pws.last_run_status,
       pws.last_run_message,
       pws.next_attempt_utc,
       pws.consecutive_failures,
       pw.sync_priority,
       COALESCE(track_summary.verified_sync_count, 0),
       pws.ignored_blocked_track_count,
       pws.rerouted_track_count,
       pw.owner_name,
       COALESCE(claim_summary.pending_count, 0),
       COALESCE(state_summary.unavailable_count, 0),
       COALESCE(state_summary.review_count, 0),
       pw.source_url,
       pw.source_storefront,
       COALESCE(state_breakdown.waiting_for_target_count, 0),
       COALESCE(state_breakdown.waiting_for_identity_count, 0),
       COALESCE(missing_summary.missing_count, 0),
       COALESCE(state_breakdown.mapping_retry_count, 0),
       COALESCE(state_breakdown.blocked_count, 0),
       COALESCE(state_breakdown.failed_count, 0)
FROM playlist_watchlist pw
LEFT JOIN playlist_watch_state pws
    ON pws.source = pw.source
   AND pws.source_id = pw.source_id
LEFT JOIN (
    -- A track counts as verified-synced only once it has been confirmed on every currently
    -- configured target, not just one of them -- see playlist_watch_track_sync_progress
    -- (shared with GetPlaylistWatchTrackStatusesAsync so this logic is defined exactly once).
    SELECT progress.source,
           progress.source_id,
           COUNT(DISTINCT progress.track_source_id) AS verified_sync_count
    FROM playlist_watch_track_sync_progress progress
    JOIN playlist_watch_track track
      ON track.source = progress.source
     AND track.source_id = progress.source_id
     AND track.track_source_id = progress.track_source_id
    WHERE lower(COALESCE(track.identity_status, '')) <> 'review'
      AND progress.configured_target_count > 0
      AND progress.verified_target_count >= progress.configured_target_count
    GROUP BY progress.source, progress.source_id
) track_summary
    ON track_summary.source = pw.source
   AND track_summary.source_id = pw.source_id
LEFT JOIN (
    SELECT source,
           source_id,
           COUNT(DISTINCT CASE WHEN lower(status) = 'pending' THEN track_source_id END) AS pending_count
    FROM playlist_watch_download_claim
    GROUP BY source, source_id
) claim_summary
    ON claim_summary.source = pw.source
   AND claim_summary.source_id = pw.source_id
LEFT JOIN (
    SELECT source,
           source_id,
           COUNT(DISTINCT CASE WHEN lower(status) = 'unavailable' THEN track_source_id END) AS unavailable_count,
           COUNT(DISTINCT CASE WHEN lower(COALESCE(identity_status, '')) = 'review' THEN track_source_id END) AS review_count
    FROM playlist_watch_track
    GROUP BY source, source_id
) state_summary
    ON state_summary.source = pw.source
   AND state_summary.source_id = pw.source_id
LEFT JOIN (
    -- Presentation buckets share the track CASE in playlist_watch_track_presentation_status
    -- (ordinals 24-29 are appended after source_storefront so 0-23 stay valid).
    SELECT source,
           source_id,
           COUNT(DISTINCT CASE WHEN presentation_status = 'waiting_for_target' THEN track_source_id END) AS waiting_for_target_count,
           COUNT(DISTINCT CASE WHEN presentation_status = 'waiting_for_identity' THEN track_source_id END) AS waiting_for_identity_count,
           COUNT(DISTINCT CASE WHEN presentation_status = 'missing' THEN track_source_id END) AS missing_count,
           COUNT(DISTINCT CASE WHEN presentation_status = 'mapping_retry' THEN track_source_id END) AS mapping_retry_count,
           COUNT(DISTINCT CASE WHEN presentation_status = 'blocked' THEN track_source_id END) AS blocked_count,
           COUNT(DISTINCT CASE WHEN presentation_status = 'failed' THEN track_source_id END) AS failed_count
    FROM playlist_watch_track_presentation_status
    GROUP BY source, source_id
) state_breakdown
    ON state_breakdown.source = pw.source
   AND state_breakdown.source_id = pw.source_id
LEFT JOIN (
    SELECT source,
           source_id,
           COUNT(DISTINCT track_source_id) AS missing_count
    FROM playlist_watch_missing_track
    WHERE lower(status) IN ('missing', 'failed')
      AND (retry_after_utc IS NULL OR datetime(retry_after_utc) <= datetime('now'))
    GROUP BY source, source_id
) missing_summary
    ON missing_summary.source = pw.source
   AND missing_summary.source_id = pw.source_id
ORDER BY CASE WHEN pw.sync_priority IS NULL OR pw.sync_priority <= 0 THEN 1 ELSE 0 END,
         pw.sync_priority ASC,
         pw.created_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PlaylistWatchlistDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var created = await reader.IsDBNullAsync(7, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(7));
            var lastChecked = await reader.IsDBNullAsync(8, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(8));
            items.Add(new PlaylistWatchlistDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt32(6),
                created,
                lastChecked,
                await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
                await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10),
                await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11),
                await reader.IsDBNullAsync(12, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(12)),
                await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetInt32(13),
                await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetInt32(14),
                SyncedTrackCount: await reader.IsDBNullAsync(15, cancellationToken) ? null : reader.GetInt32(15),
                IncompleteTrackCount: await reader.IsDBNullAsync(15, cancellationToken) || await reader.IsDBNullAsync(6, cancellationToken)
                    ? null
                    : Math.Max(0,
                        reader.GetInt32(6)
                        - (await reader.IsDBNullAsync(16, cancellationToken) ? 0 : reader.GetInt32(16))
                        - reader.GetInt32(15)),
                IgnoredBlockedTrackCount: await reader.IsDBNullAsync(16, cancellationToken) ? null : reader.GetInt32(16),
                ReroutedTrackCount: await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetInt32(17),
                OwnerName: await reader.IsDBNullAsync(18, cancellationToken) ? null : reader.GetString(18),
                EligibleTrackCount: await reader.IsDBNullAsync(6, cancellationToken)
                    ? null
                    : Math.Max(0, reader.GetInt32(6) - (await reader.IsDBNullAsync(16, cancellationToken) ? 0 : reader.GetInt32(16))),
                QueuedTrackCount: await reader.IsDBNullAsync(19, cancellationToken) ? 0 : reader.GetInt32(19),
                DownloadingTrackCount: 0,
                UnavailableTrackCount: await reader.IsDBNullAsync(20, cancellationToken) ? 0 : reader.GetInt32(20),
                ReviewTrackCount: await reader.IsDBNullAsync(21, cancellationToken) ? 0 : reader.GetInt32(21),
                SourceUrl: await reader.IsDBNullAsync(22, cancellationToken) ? null : reader.GetString(22),
                SourceStorefront: await reader.IsDBNullAsync(23, cancellationToken) ? null : reader.GetString(23),
                WaitingForTargetCount: await reader.IsDBNullAsync(24, cancellationToken) ? 0 : reader.GetInt32(24),
                WaitingForIdentityCount: await reader.IsDBNullAsync(25, cancellationToken) ? 0 : reader.GetInt32(25),
                MissingTrackCount: await reader.IsDBNullAsync(26, cancellationToken) ? 0 : reader.GetInt32(26),
                MappingRetryCount: await reader.IsDBNullAsync(27, cancellationToken) ? 0 : reader.GetInt32(27),
                BlockedTrackCount: await reader.IsDBNullAsync(28, cancellationToken) ? 0 : reader.GetInt32(28),
                FailedTrackCount: await reader.IsDBNullAsync(29, cancellationToken) ? 0 : reader.GetInt32(29)));
        }

        return items;
    }

    public async Task<bool> IsPlaylistWatchlistedAsync(string source, string sourceId, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"SELECT EXISTS(SELECT 1 FROM playlist_watchlist WHERE source = @source AND source_id = @sourceId);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<PlaylistWatchlistDto?> AddPlaylistWatchlistAsync(
        string source,
        string sourceId,
        PlaylistWatchlistMetadataInput metadata,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO playlist_watchlist (source, source_id, name, image_url, description, track_count, owner_name, source_url, source_storefront, sync_priority)
VALUES (
    @source,
    @sourceId,
    @name,
    @imageUrl,
    @description,
    @trackCount,
    @ownerName,
    @sourceUrl,
    @sourceStorefront,
    1
)
ON CONFLICT(source, source_id) DO UPDATE SET
    name = CASE WHEN excluded.name IS NULL OR TRIM(excluded.name) = '' THEN playlist_watchlist.name ELSE excluded.name END,
    image_url = COALESCE(excluded.image_url, playlist_watchlist.image_url),
    description = COALESCE(excluded.description, playlist_watchlist.description),
    track_count = COALESCE(excluded.track_count, playlist_watchlist.track_count),
    owner_name = COALESCE(excluded.owner_name, playlist_watchlist.owner_name),
    source_url = COALESCE(excluded.source_url, playlist_watchlist.source_url),
    source_storefront = COALESCE(excluded.source_storefront, playlist_watchlist.source_storefront);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("name", metadata.Name ?? string.Empty);
        command.Parameters.AddWithValue("imageUrl", (object?)metadata.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)metadata.Description ?? DBNull.Value);
        command.Parameters.AddWithValue(TrackCountField, (object?)metadata.TrackCount ?? DBNull.Value);
        command.Parameters.AddWithValue("ownerName", (object?)metadata.OwnerName ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceUrl", (object?)metadata.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceStorefront", (object?)metadata.SourceStorefront ?? DBNull.Value);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        command.Transaction = transaction;
        var isNewEntry = !await PlaylistWatchlistEntryExistsAsync(connection, transaction, normalizedSource, normalizedSourceId, cancellationToken);
        if (isNewEntry)
        {
            await ShiftPlaylistWatchlistPrioritiesForNewEntryAsync(connection, transaction, normalizedSource, normalizedSourceId, cancellationToken);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);

        const string selectSql = @"
SELECT id,
       source,
       source_id,
       name,
       image_url,
       description,
       track_count,
       created_at,
       sync_priority,
       owner_name,
       source_url,
       source_storefront
FROM playlist_watchlist
WHERE source = @source AND source_id = @sourceId
LIMIT 1;";
        var item = await ReadPlaylistWatchlistEntryAsync(
            connection,
            transaction,
            selectSql,
            normalizedSource,
            normalizedSourceId,
            cancellationToken);
        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return item;
    }

    private static async Task<PlaylistWatchlistDto?> ReadPlaylistWatchlistEntryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string selectSql,
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var selectCommand = new SqliteCommand(selectSql, connection);
        if (transaction != null)
        {
            selectCommand.Transaction = transaction;
        }
        selectCommand.Parameters.AddWithValue(SourceField, source);
        selectCommand.Parameters.AddWithValue(SourceIdField, sourceId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var created = await reader.IsDBNullAsync(7, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(7));
        return new PlaylistWatchlistDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt32(6),
            created,
            SyncPriority: await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetInt32(8),
            OwnerName: await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
            SourceUrl: await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10),
            SourceStorefront: await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11));
    }

    public async Task<PlaylistWatchlistDto?> GetPlaylistWatchlistEntryAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id,
       source,
       source_id,
       name,
       image_url,
       description,
       track_count,
       created_at,
       sync_priority,
       owner_name,
       source_url,
       source_storefront
FROM playlist_watchlist
WHERE source = @source AND source_id = @sourceId
LIMIT 1;";
        return await ReadPlaylistWatchlistEntryAsync(
            connection,
            transaction: null,
            sql,
            normalizedSource,
            normalizedSourceId,
            cancellationToken);
    }

    private static async Task<bool> PlaylistWatchlistEntryExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const string sql = @"SELECT EXISTS(SELECT 1 FROM playlist_watchlist WHERE source = @source AND source_id = @sourceId);";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    private static async Task ShiftPlaylistWatchlistPrioritiesForNewEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
WITH ordered AS (
    SELECT id,
           ROW_NUMBER() OVER (
               ORDER BY CASE WHEN sync_priority IS NULL OR sync_priority <= 0 THEN 1 ELSE 0 END,
                        sync_priority ASC,
                        created_at DESC,
                        id DESC
           ) + 1 AS priority
    FROM playlist_watchlist
    WHERE NOT (source = @source AND source_id = @sourceId)
)
UPDATE playlist_watchlist
SET sync_priority = (
    SELECT priority
    FROM ordered
    WHERE ordered.id = playlist_watchlist.id
)
WHERE id IN (SELECT id FROM ordered);";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdatePlaylistWatchlistPrioritiesAsync(
        IReadOnlyList<(string Source, string SourceId, int SyncPriority)> priorities,
        CancellationToken cancellationToken = default)
    {
        if (priorities.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watchlist
SET sync_priority = @syncPriority
WHERE source = @source AND source_id = @sourceId;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        var sourceParameter = command.Parameters.Add(SourceField, SqliteType.Text);
        var sourceIdParameter = command.Parameters.Add(SourceIdField, SqliteType.Text);
        var priorityParameter = command.Parameters.Add("syncPriority", SqliteType.Integer);
        foreach (var priority in priorities)
        {
            if (!TryNormalizePlaylistWatchKey(priority.Source, priority.SourceId, out var normalizedSource, out var normalizedSourceId))
            {
                continue;
            }

            sourceParameter.Value = normalizedSource;
            sourceIdParameter.Value = normalizedSourceId;
            priorityParameter.Value = Math.Max(1, priority.SyncPriority);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdatePlaylistWatchlistMetadataAsync(
        string source,
        string sourceId,
        PlaylistWatchlistMetadataInput metadata,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watchlist
SET name = COALESCE(@name, name),
    image_url = CASE WHEN @clearImageUrl = 1 THEN @imageUrl ELSE COALESCE(@imageUrl, image_url) END,
    description = COALESCE(@description, description),
    track_count = COALESCE(@trackCount, track_count),
    owner_name = COALESCE(@ownerName, owner_name),
    source_url = COALESCE(@sourceUrl, source_url),
    source_storefront = COALESCE(@sourceStorefront, source_storefront)
WHERE source = @source AND source_id = @sourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("name", (object?)metadata.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("imageUrl", (object?)metadata.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)metadata.Description ?? DBNull.Value);
        command.Parameters.AddWithValue(TrackCountField, (object?)metadata.TrackCount ?? DBNull.Value);
        command.Parameters.AddWithValue("clearImageUrl", metadata.ClearImageUrl ? 1 : 0);
        command.Parameters.AddWithValue("ownerName", (object?)metadata.OwnerName ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceUrl", (object?)metadata.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceStorefront", (object?)metadata.SourceStorefront ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> BackfillLegacyApplePlaylistStorefrontAsync(
        string storefront,
        CancellationToken cancellationToken = default)
    {
        var normalizedStorefront = (storefront ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedStorefront.Length is < 2 or > 5
            || normalizedStorefront.Any(character => !char.IsAsciiLetter(character) && character != '-'))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE playlist_watchlist
SET source_storefront=@storefront
WHERE lower(source)='apple'
  AND (source_storefront IS NULL OR trim(source_storefront)='')
RETURNING source_id;", connection);
        command.Parameters.AddWithValue("storefront", normalizedStorefront);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var repairedSourceIds = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                repairedSourceIds.Add(reader.GetString(0));
            }
        }

        return repairedSourceIds;
    }

    public async Task<PlaylistWatchArtworkStateDto?> GetPlaylistWatchArtworkStateAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT source,source_id,remote_identity,still_content_hash,still_local_path,
       animated_content_hash,animated_local_path,status,last_error,last_checked_utc,revision
FROM playlist_watch_artwork_state
WHERE source=@source AND source_id=@sourceId;", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PlaylistWatchArtworkStateDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : ParseDateTimeOffsetInvariant(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    public async Task UpsertPlaylistWatchArtworkStateAsync(
        PlaylistWatchArtworkStateDto state,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(state.Source, state.SourceId, out var source, out var sourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
INSERT INTO playlist_watch_artwork_state (
 source,source_id,remote_identity,still_content_hash,still_local_path,
 animated_content_hash,animated_local_path,status,last_error,last_checked_utc,revision)
VALUES (
 @source,@sourceId,@remoteIdentity,@stillHash,@stillPath,
 @animatedHash,@animatedPath,@status,@lastError,@lastChecked,@revision)
ON CONFLICT(source,source_id) DO UPDATE SET
 remote_identity=excluded.remote_identity,
 still_content_hash=COALESCE(excluded.still_content_hash,playlist_watch_artwork_state.still_content_hash),
 still_local_path=COALESCE(excluded.still_local_path,playlist_watch_artwork_state.still_local_path),
 animated_content_hash=COALESCE(excluded.animated_content_hash,playlist_watch_artwork_state.animated_content_hash),
 animated_local_path=COALESCE(excluded.animated_local_path,playlist_watch_artwork_state.animated_local_path),
 status=excluded.status,last_error=excluded.last_error,last_checked_utc=excluded.last_checked_utc,
 revision=COALESCE(excluded.revision,playlist_watch_artwork_state.revision),updated_at=CURRENT_TIMESTAMP;", connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        command.Parameters.AddWithValue("remoteIdentity", (object?)state.RemoteIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("stillHash", (object?)state.StillContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("stillPath", (object?)state.StillLocalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("animatedHash", (object?)state.AnimatedContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("animatedPath", (object?)state.AnimatedLocalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("status", state.Status);
        command.Parameters.AddWithValue("lastError", (object?)state.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("lastChecked", (object?)state.LastCheckedUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("revision", (object?)state.Revision ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WatchlistSyncJobDto?> EnqueueWatchlistPlaylistArtworkSyncJobAsync(
        string source,
        string playlistId,
        string targetService,
        string revision,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, playlistId, out var normalizedSource, out var normalizedPlaylistId)
            || string.IsNullOrWhiteSpace(targetService)
            || string.IsNullOrWhiteSpace(revision))
        {
            return null;
        }

        var normalizedTarget = targetService.Trim().ToLowerInvariant();
        if (normalizedTarget is not ("plex" or "jellyfin" or "navidrome"))
        {
            return null;
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var removeObsolete = new SqliteCommand(@"
DELETE FROM watchlist_sync_job
WHERE source=@source AND playlist_id=@playlistId
  AND target_service=@target
  AND track_id LIKE 'artwork:%' AND track_id <> 'artwork:' || @revision
  AND lower(status) IN ('pending','retry','completed');", connection))
        {
            removeObsolete.Parameters.AddWithValue(SourceField, normalizedSource);
            removeObsolete.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
            removeObsolete.Parameters.AddWithValue("target", normalizedTarget);
            removeObsolete.Parameters.AddWithValue("revision", revision.Trim());
            await removeObsolete.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new SqliteCommand(@"
INSERT INTO watchlist_sync_job (source,playlist_id,track_id,target_service,status,next_attempt_utc)
SELECT @source,@playlistId,'artwork:' || @revision,@target,'pending',CURRENT_TIMESTAMP
FROM playlist_watch_preferences preference
WHERE preference.source=@source AND preference.source_id=@playlistId
  AND preference.update_artwork=1
  AND EXISTS (
      SELECT 1
      FROM json_each(CASE
        WHEN json_valid(preference.sync_targets_json) AND json_array_length(preference.sync_targets_json)>0
        THEN preference.sync_targets_json ELSE json_array(preference.service) END) configured
      WHERE lower(trim(configured.value))=@target)
  AND NOT EXISTS (
      SELECT 1 FROM playlist_watch_artwork_target_state target
      WHERE target.source=@source AND target.source_id=@playlistId
        AND target.target_service=@target
        AND target.status='applied' AND target.applied_revision=@revision)
ON CONFLICT(source,playlist_id,track_id,target_service) DO UPDATE SET
 status=CASE
   WHEN lower(watchlist_sync_job.status) IN ('processing','retry','blocked')
   THEN watchlist_sync_job.status ELSE 'pending' END,
 next_attempt_utc=CASE
   WHEN lower(watchlist_sync_job.status) IN ('processing','retry','blocked')
   THEN watchlist_sync_job.next_attempt_utc ELSE CURRENT_TIMESTAMP END,
 lease_owner=CASE WHEN lower(watchlist_sync_job.status)='processing' THEN watchlist_sync_job.lease_owner ELSE NULL END,
 lease_until_utc=CASE WHEN lower(watchlist_sync_job.status)='processing' THEN watchlist_sync_job.lease_until_utc ELSE NULL END,
 last_error=CASE
   WHEN lower(watchlist_sync_job.status) IN ('processing','retry','blocked')
   THEN watchlist_sync_job.last_error ELSE NULL END,
 updated_at=CURRENT_TIMESTAMP
RETURNING id,source,playlist_id,track_id,target_service,destination_folder_id,final_file_paths_json,
 attempt_count,next_attempt_utc,queue_uuid,lease_owner,status,last_error,snapshot_id;", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
        command.Parameters.AddWithValue("target", normalizedTarget);
        command.Parameters.AddWithValue("revision", revision.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadWatchlistSyncJobAsync(reader, cancellationToken)
            : null;
    }

    public async Task SetPlaylistWatchArtworkTargetStateAsync(
        string source,
        string sourceId,
        string targetService,
        string? revision,
        bool success,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
INSERT INTO playlist_watch_artwork_target_state (
 source,source_id,target_service,applied_revision,status,last_error,last_attempt_utc)
VALUES (@source,@sourceId,@target,@revision,@status,@error,@attempt)
ON CONFLICT(source,source_id,target_service) DO UPDATE SET
 applied_revision=CASE WHEN excluded.status='applied' THEN excluded.applied_revision ELSE playlist_watch_artwork_target_state.applied_revision END,
 status=excluded.status,last_error=excluded.last_error,last_attempt_utc=excluded.last_attempt_utc,updated_at=CURRENT_TIMESTAMP;", connection);
        command.Parameters.AddWithValue(SourceField, NormalizePlaylistWatchSource(source));
        command.Parameters.AddWithValue(SourceIdField, sourceId.Trim());
        command.Parameters.AddWithValue("target", targetService.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("revision", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("status", success ? "applied" : "failed");
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("attempt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsPlaylistWatchArtworkRevisionAppliedAsync(
        string source,
        string sourceId,
        string targetService,
        string revision,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId)
            || string.IsNullOrWhiteSpace(targetService)
            || string.IsNullOrWhiteSpace(revision))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT EXISTS (
    SELECT 1
    FROM playlist_watch_artwork_target_state
    WHERE source=@source
      AND source_id=@sourceId
      AND target_service=@target
      AND status='applied'
      AND applied_revision=@revision
);", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("target", targetService.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("revision", revision.Trim());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<bool> RemovePlaylistWatchlistAsync(string source, string sourceId, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
	DELETE FROM playlist_watch_track WHERE source = @source AND source_id = @sourceId;
	DELETE FROM playlist_watch_download_claim WHERE source = @source AND source_id = @sourceId;
	DELETE FROM playlist_watch_target_membership WHERE source = @source AND source_id = @sourceId;
	DELETE FROM playlist_watch_target_sync_state WHERE source = @source AND source_id = @sourceId;
	DELETE FROM playlist_watch_ignore WHERE source = @source AND source_id = @sourceId;
	DELETE FROM watchlist_sync_job WHERE source = @source AND playlist_id = @sourceId;
	DELETE FROM playlist_watch_state WHERE source = @source AND source_id = @sourceId;
DELETE FROM playlist_track_candidate_cache WHERE source = @source AND source_id = @sourceId;
DELETE FROM playlist_watch_preferences WHERE source = @source AND source_id = @sourceId;
DELETE FROM playlist_watchlist WHERE source = @source AND source_id = @sourceId;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<IReadOnlyList<PlaylistWatchPreferenceDto>> GetPlaylistWatchPreferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
	SELECT source,
	       source_id,
	       destination_folder_id,
           atmos_destination_folder_id,
	       service,
	       preferred_engine,
	       download_variant_mode,
	       sync_mode,
	       update_artwork,
	       reuse_saved_artwork,
	       created_at,
	       updated_at,
       routing_rules_json,
       ignore_rules_json,
       plex_playlist_id,
       jellyfin_playlist_id,
       navidrome_playlist_id,
       download_engine_order_json,
       sync_targets_json
FROM playlist_watch_preferences
ORDER BY updated_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PlaylistWatchPreferenceDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(await ReadPlaylistWatchPreferenceAsync(reader, cancellationToken));
        }

        return items;
    }

    public async Task<PlaylistWatchPreferenceDto?> GetPlaylistWatchPreferenceAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
	SELECT source,
	       source_id,
	       destination_folder_id,
           atmos_destination_folder_id,
	       service,
	       preferred_engine,
	       download_variant_mode,
	       sync_mode,
	       update_artwork,
	       reuse_saved_artwork,
	       created_at,
	       updated_at,
       routing_rules_json,
       ignore_rules_json,
       plex_playlist_id,
       jellyfin_playlist_id,
       navidrome_playlist_id,
       download_engine_order_json,
       sync_targets_json
FROM playlist_watch_preferences
WHERE source = @source AND source_id = @sourceId
LIMIT 1;";
        return await QuerySingleByPlaylistWatchKeyAsync(
            source,
            sourceId,
            sql,
            ReadPlaylistWatchPreferenceAsync,
            cancellationToken);
    }

    public async Task<PlaylistWatchPreferenceDto?> UpsertPlaylistWatchPreferenceAsync(
        PlaylistWatchPreferenceUpsertInput input,
        CancellationToken cancellationToken = default)
        => await UpsertPlaylistWatchPreferenceAsync(input, resetWatchState: true, cancellationToken);

    private async Task<PlaylistWatchPreferenceDto?> UpsertPlaylistWatchPreferenceAsync(
        PlaylistWatchPreferenceUpsertInput input,
        bool resetWatchState,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizePlaylistWatchKey(input.Source, input.SourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var previousTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var previousTargetsCommand = new SqliteCommand(@"
SELECT lower(trim(configured.value))
FROM playlist_watch_preferences preference,
     json_each(CASE
         WHEN json_valid(preference.sync_targets_json) AND json_array_length(preference.sync_targets_json) > 0
             THEN preference.sync_targets_json
         ELSE json_array(preference.service)
     END) configured
WHERE preference.source=@source AND preference.source_id=@sourceId
  AND lower(trim(configured.value)) IN ('plex','jellyfin','navidrome');", connection, transaction))
        {
            previousTargetsCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            previousTargetsCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await using var reader = await previousTargetsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                previousTargets.Add(reader.GetString(0));
            }
        }
        const string sql = @"
	INSERT INTO playlist_watch_preferences (source, source_id, destination_folder_id, atmos_destination_folder_id, service, sync_targets_json, preferred_engine, download_engine_order_json, download_variant_mode, sync_mode, update_artwork, reuse_saved_artwork, routing_rules_json, ignore_rules_json)
	        VALUES (@source, @sourceId, @destinationFolderId, @atmosDestinationFolderId, @service, @syncTargetsJson, @preferredEngine, @downloadEngineOrderJson, @downloadVariantMode, @syncMode, @updateArtwork, @reuseSavedArtwork, @routingRulesJson, @ignoreRulesJson)
	ON CONFLICT(source, source_id) DO UPDATE SET
	    destination_folder_id = excluded.destination_folder_id,
        atmos_destination_folder_id = excluded.atmos_destination_folder_id,
	    service = excluded.service,
	    sync_targets_json = excluded.sync_targets_json,
	    preferred_engine = excluded.preferred_engine,
	    download_engine_order_json = excluded.download_engine_order_json,
	    download_variant_mode = excluded.download_variant_mode,
	    sync_mode = excluded.sync_mode,
	    update_artwork = excluded.update_artwork,
	    reuse_saved_artwork = excluded.reuse_saved_artwork,
	    routing_rules_json = excluded.routing_rules_json,
	    ignore_rules_json = excluded.ignore_rules_json,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("destinationFolderId", (object?)input.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("atmosDestinationFolderId", (object?)input.AtmosDestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("service", (object?)input.Service ?? DBNull.Value);
        var syncTargetsJson = input.SyncTargets is { Count: > 0 } ? JsonSerializer.Serialize(input.SyncTargets) : null;
        command.Parameters.AddWithValue("syncTargetsJson", (object?)syncTargetsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("preferredEngine", (object?)input.PreferredEngine ?? DBNull.Value);
        var downloadEngineOrderJson = input.DownloadEngineOrder is null ? null : JsonSerializer.Serialize(input.DownloadEngineOrder);
        command.Parameters.AddWithValue("downloadEngineOrderJson", (object?)downloadEngineOrderJson ?? DBNull.Value);
        command.Parameters.AddWithValue("downloadVariantMode", (object?)input.DownloadVariantMode ?? DBNull.Value);
        command.Parameters.AddWithValue("syncMode", (object?)input.SyncMode ?? DBNull.Value);
        command.Parameters.AddWithValue("updateArtwork", input.UpdateArtwork ? 1 : 0);
        command.Parameters.AddWithValue("reuseSavedArtwork", input.ReuseSavedArtwork ? 1 : 0);
        var rulesJson = input.RoutingRules is { Count: > 0 } ? JsonSerializer.Serialize(input.RoutingRules) : null;
        command.Parameters.AddWithValue("routingRulesJson", (object?)rulesJson ?? DBNull.Value);
        var ignoreRulesJson = input.IgnoreRules is { Count: > 0 } ? JsonSerializer.Serialize(input.IgnoreRules) : null;
        command.Parameters.AddWithValue("ignoreRulesJson", (object?)ignoreRulesJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (resetWatchState)
        {
            const string resetStateSql = @"
DELETE FROM playlist_watch_state
WHERE source = @source AND source_id = @sourceId;
DELETE FROM playlist_watch_target_sync_state
WHERE source = @source AND source_id = @sourceId;";
            await using var resetStateCommand = new SqliteCommand(resetStateSql, connection, transaction);
            resetStateCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            resetStateCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await resetStateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var currentTargets = (input.SyncTargets is { Count: > 0 } ? input.SyncTargets : [input.Service ?? string.Empty])
            .Select(static target => target?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(static target => target is "plex" or "jellyfin" or "navidrome")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidatedTargets = previousTargets
            .Except(currentTargets, StringComparer.OrdinalIgnoreCase)
            .Concat(currentTargets.Except(previousTargets, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var target in invalidatedTargets)
        {
            await using var deleteMembership = new SqliteCommand(@"
DELETE FROM playlist_watch_target_membership
WHERE source=@source AND source_id=@sourceId AND lower(target_service)=@target;", connection, transaction);
            deleteMembership.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteMembership.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            deleteMembership.Parameters.AddWithValue("target", target);
            await deleteMembership.ExecuteNonQueryAsync(cancellationToken);
            await using var deleteTargetState = new SqliteCommand(@"
DELETE FROM playlist_watch_target_sync_state
WHERE source=@source AND source_id=@sourceId AND lower(target_service)=@target;", connection, transaction);
            deleteTargetState.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteTargetState.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            deleteTargetState.Parameters.AddWithValue("target", target);
            await deleteTargetState.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteObsoleteJobs = new SqliteCommand(@"
DELETE FROM watchlist_sync_job
WHERE source=@source AND playlist_id=@sourceId
  AND target_service NOT IN (SELECT value FROM json_each(@targetsJson));", connection, transaction))
        {
            deleteObsoleteJobs.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteObsoleteJobs.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            deleteObsoleteJobs.Parameters.AddWithValue("targetsJson", JsonSerializer.Serialize(currentTargets));
            await deleteObsoleteJobs.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var resumeConfiguredJobs = new SqliteCommand(@"
UPDATE watchlist_sync_job
SET status='pending', attempt_count=0, lease_owner=NULL, lease_until_utc=NULL,
    next_attempt_utc=CURRENT_TIMESTAMP, last_error=NULL, updated_at=CURRENT_TIMESTAMP
WHERE source=@source AND playlist_id=@sourceId
  AND lower(status)='blocked'
  AND target_service IN (SELECT value FROM json_each(@targetsJson));", connection, transaction))
        {
            resumeConfiguredJobs.Parameters.AddWithValue(SourceField, normalizedSource);
            resumeConfiguredJobs.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            resumeConfiguredJobs.Parameters.AddWithValue("targetsJson", JsonSerializer.Serialize(currentTargets));
            await resumeConfiguredJobs.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetPlaylistWatchPreferenceAsync(normalizedSource, normalizedSourceId, cancellationToken);
    }

    public async Task UpdatePlaylistWatchTargetPlaylistIdAsync(
        string source,
        string sourceId,
        string service,
        string? playlistId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId)
            || string.IsNullOrWhiteSpace(service))
        {
            return;
        }

        var sql = service.Trim().ToLowerInvariant() switch
        {
            "plex" => @"
UPDATE playlist_watch_preferences
SET plex_playlist_id = @playlistId,
    updated_at = CURRENT_TIMESTAMP
WHERE source = @source AND source_id = @sourceId;",
            "jellyfin" => @"
UPDATE playlist_watch_preferences
SET jellyfin_playlist_id = @playlistId,
    updated_at = CURRENT_TIMESTAMP
WHERE source = @source AND source_id = @sourceId;",
            "navidrome" => @"
UPDATE playlist_watch_preferences
SET navidrome_playlist_id = @playlistId,
    updated_at = CURRENT_TIMESTAMP
WHERE source = @source AND source_id = @sourceId;",
            _ => null
        };
        if (sql is null)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue(
            "playlistId",
            string.IsNullOrWhiteSpace(playlistId) ? DBNull.Value : playlistId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlaylistWatchStateDto?> GetPlaylistWatchStateAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT source,
       source_id,
       snapshot_id,
       track_count,
       batch_next_offset,
       batch_processing_snapshot_id,
       last_checked_utc,
       updated_at,
       last_run_status,
       last_run_message,
       next_attempt_utc,
       consecutive_failures,
       current_phase,
       current_track_index,
       current_track_total,
       heartbeat_utc,
       deadline_utc
FROM playlist_watch_state
WHERE source = @source AND source_id = @sourceId
LIMIT 1;";
        return await QuerySingleByPlaylistWatchKeyAsync(
            source,
            sourceId,
            sql,
            ReadPlaylistWatchStateAsync,
            cancellationToken);
    }

    public async Task UpsertPlaylistWatchStateAsync(
        PlaylistWatchStateUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(input.Source, input.SourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO playlist_watch_state (source, source_id, snapshot_id, track_count, batch_next_offset, batch_processing_snapshot_id, last_checked_utc, last_run_status, last_run_message, next_attempt_utc, consecutive_failures, current_phase, current_track_index, current_track_total, heartbeat_utc, deadline_utc)
VALUES (@source, @sourceId, @snapshotId, @trackCount, @batchNextOffset, @batchProcessingSnapshotId, @lastCheckedUtc, @lastRunStatus, @lastRunMessage, @nextAttemptUtc, @consecutiveFailures, @currentPhase, @currentTrackIndex, @currentTrackTotal, @heartbeatUtc, @deadlineUtc)
ON CONFLICT(source, source_id) DO UPDATE SET
    snapshot_id = excluded.snapshot_id,
    track_count = excluded.track_count,
    batch_next_offset = excluded.batch_next_offset,
    batch_processing_snapshot_id = excluded.batch_processing_snapshot_id,
    last_checked_utc = excluded.last_checked_utc,
    last_run_status = excluded.last_run_status,
    last_run_message = excluded.last_run_message,
    next_attempt_utc = excluded.next_attempt_utc,
    consecutive_failures = excluded.consecutive_failures,
    current_phase = excluded.current_phase,
    current_track_index = excluded.current_track_index,
    current_track_total = excluded.current_track_total,
    heartbeat_utc = excluded.heartbeat_utc,
    deadline_utc = excluded.deadline_utc,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("snapshotId", (object?)input.SnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue(TrackCountField, (object?)input.TrackCount ?? DBNull.Value);
        command.Parameters.AddWithValue("batchNextOffset", (object?)input.BatchNextOffset ?? DBNull.Value);
        command.Parameters.AddWithValue("batchProcessingSnapshotId", (object?)input.BatchProcessingSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("lastCheckedUtc", input.LastCheckedUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("lastRunStatus", (object?)input.LastRunStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("lastRunMessage", (object?)input.LastRunMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("nextAttemptUtc", input.NextAttemptUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("consecutiveFailures", (object?)input.ConsecutiveFailures ?? DBNull.Value);
        command.Parameters.AddWithValue("currentPhase", (object?)input.CurrentPhase ?? DBNull.Value);
        command.Parameters.AddWithValue("currentTrackIndex", (object?)input.CurrentTrackIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("currentTrackTotal", (object?)input.CurrentTrackTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("heartbeatUtc", input.HeartbeatUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("deadlineUtc", input.DeadlineUtc?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdatePlaylistWatchProgressAsync(
        string source,
        string sourceId,
        string phase,
        int currentTrackIndex,
        int currentTrackTotal,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE playlist_watch_state
SET current_track_index=@currentTrackIndex,
    current_track_total=@currentTrackTotal,
    updated_at=CURRENT_TIMESTAMP
WHERE source=@source AND source_id=@sourceId;", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("currentTrackIndex", Math.Max(0, currentTrackIndex));
        command.Parameters.AddWithValue("currentTrackTotal", Math.Max(0, currentTrackTotal));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TouchPlaylistWatchHeartbeatAsync(
        string source,
        string sourceId,
        TimeSpan deadlineExtension,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE playlist_watch_state
SET heartbeat_utc=@now,
    deadline_utc=CASE
        WHEN lower(COALESCE(current_phase,'')) IN ('completed','failed','backoff','source_failure','stale_recovered')
            THEN NULL
        ELSE @deadlineUtc
    END,
    updated_at=CURRENT_TIMESTAMP
WHERE source=@source AND source_id=@sourceId;", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("now", now.ToString("O"));
        command.Parameters.AddWithValue("deadlineUtc", now.Add(deadlineExtension).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdatePlaylistWatchPresentationSummaryAsync(
        string source,
        string sourceId,
        int ignoredBlockedTrackCount,
        int reroutedTrackCount,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO playlist_watch_state (
    source,
    source_id,
    ignored_blocked_track_count,
    rerouted_track_count,
    presentation_updated_at)
VALUES (
    @source,
    @sourceId,
    @ignoredBlockedTrackCount,
    @reroutedTrackCount,
    CURRENT_TIMESTAMP)
ON CONFLICT(source, source_id) DO UPDATE SET
    ignored_blocked_track_count = excluded.ignored_blocked_track_count,
    rerouted_track_count = excluded.rerouted_track_count,
    presentation_updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("ignoredBlockedTrackCount", Math.Max(0, ignoredBlockedTrackCount));
        command.Parameters.AddWithValue("reroutedTrackCount", Math.Max(0, reroutedTrackCount));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlaylistWatchTrackStatusSummaryDto> GetPlaylistWatchTrackStatusSummaryAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return new PlaylistWatchTrackStatusSummaryDto(0, 0, 0, 0, 0);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT
    SUM(CASE WHEN lower(status) = 'queued' THEN 1 ELSE 0 END) AS queued_count,
    SUM(CASE WHEN lower(status) = 'completed' THEN 1 ELSE 0 END) AS completed_count,
    SUM(CASE WHEN lower(status) IN ('failed', 'canceled', 'cancelled') THEN 1 ELSE 0 END) AS failed_count,
    SUM(CASE WHEN lower(status) IN ('inqueue', 'running', 'downloading', 'paused', 'retrying') THEN 1 ELSE 0 END) AS active_count,
    SUM(CASE WHEN lower(status) NOT IN ('completed', 'failed', 'canceled', 'cancelled') THEN 1 ELSE 0 END) AS unresolved_count
FROM playlist_watch_track
WHERE source = @source
  AND source_id = @sourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PlaylistWatchTrackStatusSummaryDto(0, 0, 0, 0, 0);
        }

        return new PlaylistWatchTrackStatusSummaryDto(
            await reader.IsDBNullAsync(0, cancellationToken) ? 0 : reader.GetInt32(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? 0 : reader.GetInt32(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? 0 : reader.GetInt32(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? 0 : reader.GetInt32(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? 0 : reader.GetInt32(4));
    }

    public async Task<WatchlistSchedulerStateDto?> GetWatchlistSchedulerStateAsync(
        string watchType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(watchType))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT watch_type,
       active_source,
       active_source_id,
       active_started_utc,
       last_progress_utc,
       cycle_status,
       cycle_started_utc,
       cycle_completed_utc,
       next_cycle_utc,
       updated_at
FROM watchlist_scheduler_state
WHERE watch_type = @watchType
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("watchType", watchType.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WatchlistSchedulerStateDto(
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(3)),
            await reader.IsDBNullAsync(4, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(4)),
            await reader.IsDBNullAsync(5, cancellationToken) ? "idle" : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(6)),
            await reader.IsDBNullAsync(7, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(7)),
            await reader.IsDBNullAsync(8, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(8)),
            await reader.IsDBNullAsync(9, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(9)));
    }

    public async Task UpsertWatchlistSchedulerStateAsync(
        WatchlistSchedulerStateUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.WatchType))
        {
            return;
        }

        var normalizedWatchType = input.WatchType.Trim().ToLowerInvariant();
        var activeSource = string.IsNullOrWhiteSpace(input.ActiveSource)
            ? null
            : input.ActiveSource.Trim().ToLowerInvariant();
        var activeSourceId = string.IsNullOrWhiteSpace(input.ActiveSourceId)
            ? null
            : input.ActiveSourceId.Trim();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO watchlist_scheduler_state (
    watch_type,
    active_source,
    active_source_id,
    active_started_utc,
    last_progress_utc
)
VALUES (
    @watchType,
    @activeSource,
    @activeSourceId,
    @activeStartedUtc,
    @lastProgressUtc
)
ON CONFLICT(watch_type) DO UPDATE SET
    active_source = excluded.active_source,
    active_source_id = excluded.active_source_id,
    active_started_utc = excluded.active_started_utc,
    last_progress_utc = excluded.last_progress_utc,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("watchType", normalizedWatchType);
        command.Parameters.AddWithValue("activeSource", (object?)activeSource ?? DBNull.Value);
        command.Parameters.AddWithValue("activeSourceId", (object?)activeSourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("activeStartedUtc", input.ActiveStartedUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("lastProgressUtc", input.LastProgressUtc?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateWatchlistCycleStateAsync(
        string watchType,
        string cycleStatus,
        DateTimeOffset? cycleStartedUtc,
        DateTimeOffset? cycleCompletedUtc,
        DateTimeOffset? nextCycleUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(watchType) || string.IsNullOrWhiteSpace(cycleStatus))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO watchlist_scheduler_state (
    watch_type, cycle_status, cycle_started_utc, cycle_completed_utc, next_cycle_utc)
VALUES (@watchType, @cycleStatus, @cycleStartedUtc, @cycleCompletedUtc, @nextCycleUtc)
ON CONFLICT(watch_type) DO UPDATE SET
    cycle_status=excluded.cycle_status,
    cycle_started_utc=excluded.cycle_started_utc,
    cycle_completed_utc=excluded.cycle_completed_utc,
    next_cycle_utc=excluded.next_cycle_utc,
    updated_at=CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("watchType", watchType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("cycleStatus", cycleStatus.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("cycleStartedUtc", cycleStartedUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("cycleCompletedUtc", cycleCompletedUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("nextCycleUtc", nextCycleUtc?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WatchlistSourceCircuitStateDto?> GetWatchlistSourceCircuitStateAsync(
        string watchType,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(watchType) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT watch_type,
       source,
       is_open,
       open_until_utc,
       reason,
       fingerprint,
       failure_count,
       updated_at
FROM watchlist_source_circuit_state
WHERE watch_type = @watchType
  AND source = @source
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("watchType", watchType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue(SourceField, source.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WatchlistSourceCircuitStateDto(
            reader.GetString(0),
            reader.GetString(1),
            !await reader.IsDBNullAsync(2, cancellationToken) && reader.GetInt32(2) == 1,
            await reader.IsDBNullAsync(3, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(3)),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? 0 : reader.GetInt32(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(7)));
    }

    public async Task UpsertWatchlistSourceCircuitStateAsync(
        WatchlistSourceCircuitStateUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.WatchType) || string.IsNullOrWhiteSpace(input.Source))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO watchlist_source_circuit_state (
    watch_type,
    source,
    is_open,
    open_until_utc,
    reason,
    fingerprint,
    failure_count
)
VALUES (
    @watchType,
    @source,
    @isOpen,
    @openUntilUtc,
    @reason,
    @fingerprint,
    @failureCount
)
ON CONFLICT(watch_type, source) DO UPDATE SET
    is_open = excluded.is_open,
    open_until_utc = excluded.open_until_utc,
    reason = excluded.reason,
    fingerprint = excluded.fingerprint,
    failure_count = excluded.failure_count,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("watchType", input.WatchType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue(SourceField, input.Source.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("isOpen", input.IsOpen ? 1 : 0);
        command.Parameters.AddWithValue("openUntilUtc", input.OpenUntilUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("reason", (object?)input.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("fingerprint", (object?)input.Fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("failureCount", Math.Max(0, input.FailureCount));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WatchlistTargetCircuitStateDto?> GetWatchlistTargetCircuitStateAsync(
        string targetService,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetService))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT target_service,
       is_open,
       open_until_utc,
       reason,
       failure_count,
       updated_at
FROM watchlist_target_circuit_state
WHERE target_service = @targetService
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("targetService", targetService.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WatchlistTargetCircuitStateDto(
            reader.GetString(0),
            !await reader.IsDBNullAsync(1, cancellationToken) && reader.GetInt32(1) == 1,
            await reader.IsDBNullAsync(2, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(2)),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? 0 : reader.GetInt32(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(5)));
    }

    public async Task<int> CloseExpiredWatchlistTargetCircuitsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_target_circuit_state
SET is_open=0,
    open_until_utc=NULL,
    reason=NULL,
    failure_count=0,
    updated_at=CURRENT_TIMESTAMP
WHERE is_open=1
  AND open_until_utc IS NOT NULL
  AND datetime(open_until_utc) <= datetime('now');", connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertWatchlistTargetCircuitStateAsync(
        WatchlistTargetCircuitStateUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.TargetService))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO watchlist_target_circuit_state (
    target_service,
    is_open,
    open_until_utc,
    reason,
    failure_count
)
VALUES (
    @targetService,
    @isOpen,
    @openUntilUtc,
    @reason,
    @failureCount
)
ON CONFLICT(target_service) DO UPDATE SET
    is_open = excluded.is_open,
    open_until_utc = excluded.open_until_utc,
    reason = excluded.reason,
    failure_count = excluded.failure_count,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("targetService", input.TargetService.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("isOpen", input.IsOpen ? 1 : 0);
        command.Parameters.AddWithValue("openUntilUtc", input.OpenUntilUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("reason", (object?)input.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("failureCount", Math.Max(0, input.FailureCount));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool?> GetWatchlistTargetCapabilitySupportedAsync(
        string targetService,
        string capability,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetService) || string.IsNullOrWhiteSpace(capability))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT supported
FROM watchlist_target_capability
WHERE target_service = @targetService AND capability = @capability
LIMIT 1;", connection);
        command.Parameters.AddWithValue("targetService", targetService.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("capability", capability.Trim().ToLowerInvariant());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return null;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    public async Task SetWatchlistTargetCapabilityAsync(
        string targetService,
        string capability,
        bool supported,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetService) || string.IsNullOrWhiteSpace(capability))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
INSERT INTO watchlist_target_capability (
    target_service,
    capability,
    supported,
    last_checked_utc,
    last_error)
VALUES (
    @targetService,
    @capability,
    @supported,
    @lastCheckedUtc,
    @lastError)
ON CONFLICT(target_service, capability) DO UPDATE SET
    supported = excluded.supported,
    last_checked_utc = excluded.last_checked_utc,
    last_error = excluded.last_error;", connection);
        command.Parameters.AddWithValue("targetService", targetService.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("capability", capability.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("supported", supported ? 1 : 0);
        command.Parameters.AddWithValue("lastCheckedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("lastError", (object?)lastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlaylistTrackCandidateCacheDto?> GetPlaylistTrackCandidateCacheAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT source,
       source_id,
       snapshot_id,
       candidates_json,
       updated_at,
       schema_version,
       identity_revision,
       provider_readiness_revision,
       is_complete
FROM playlist_track_candidate_cache
WHERE source = @source AND source_id = @sourceId
LIMIT 1;";
        return await QuerySingleByPlaylistWatchKeyAsync(
            source,
            sourceId,
            sql,
            ReadPlaylistTrackCandidateCacheAsync,
            cancellationToken);
    }

    private async Task<TDto?> QuerySingleByPlaylistWatchKeyAsync<TDto>(
        string source,
        string sourceId,
        string sql,
        Func<SqliteDataReader, CancellationToken, Task<TDto>> projector,
        CancellationToken cancellationToken)
        where TDto : class
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await projector(reader, cancellationToken);
    }

    private static async Task<PlaylistWatchStateDto> ReadPlaylistWatchStateAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var batchOffset = await reader.IsDBNullAsync(4, cancellationToken) ? (int?)null : reader.GetInt32(4);
        var batchSnapshot = await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5);
        var lastChecked = await reader.IsDBNullAsync(6, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(6));
        var updated = await reader.IsDBNullAsync(7, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(7));
        return new PlaylistWatchStateDto(
            reader.GetString(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt32(3),
            batchOffset,
            batchSnapshot,
            lastChecked,
            updated,
            await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
            await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
            await reader.IsDBNullAsync(10, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(10)),
            await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetInt32(11),
            await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12),
            await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetInt32(13),
            await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetInt32(14),
            await reader.IsDBNullAsync(15, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(15)),
            await reader.IsDBNullAsync(16, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(16)));
    }

    private static async Task<PlaylistTrackCandidateCacheDto> ReadPlaylistTrackCandidateCacheAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var updatedAt = await reader.IsDBNullAsync(4, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(4));
        return new PlaylistTrackCandidateCacheDto(
            reader.GetString(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            reader.GetString(3),
            updatedAt,
            await reader.IsDBNullAsync(5, cancellationToken) ? 0 : reader.GetInt32(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
            !await reader.IsDBNullAsync(8, cancellationToken) && reader.GetInt32(8) != 0);
    }

    public async Task UpsertPlaylistTrackCandidateCacheAsync(
        string source,
        string sourceId,
        string? snapshotId,
        string candidatesJson,
        int schemaVersion,
        string? identityRevision,
        string? providerReadinessRevision,
        bool isComplete,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO playlist_track_candidate_cache (
    source, source_id, snapshot_id, candidates_json, schema_version,
    identity_revision, provider_readiness_revision, is_complete)
VALUES (
    @source, @sourceId, @snapshotId, @candidatesJson, @schemaVersion,
    @identityRevision, @providerReadinessRevision, @isComplete)
ON CONFLICT(source, source_id) DO UPDATE SET
    snapshot_id = excluded.snapshot_id,
    candidates_json = excluded.candidates_json,
    schema_version = excluded.schema_version,
    identity_revision = excluded.identity_revision,
    provider_readiness_revision = excluded.provider_readiness_revision,
    is_complete = excluded.is_complete,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("snapshotId", (object?)snapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("candidatesJson", candidatesJson);
        command.Parameters.AddWithValue("schemaVersion", Math.Max(0, schemaVersion));
        command.Parameters.AddWithValue("identityRevision", (object?)identityRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("providerReadinessRevision", (object?)providerReadinessRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("isComplete", isComplete ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeletePlaylistTrackCandidateCacheAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
DELETE FROM playlist_track_candidate_cache
WHERE source = @source AND source_id = @sourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<BoomplayDeezerTrackMappingDto?> GetBoomplayDeezerTrackMappingAsync(
        string boomplayTrackId,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrackId = boomplayTrackId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT boomplay_track_id,
       deezer_track_id,
       isrc,
       title,
       artist,
       album,
       cover_url,
       duration_ms,
       source_fingerprint,
       matcher_version,
       status,
       last_error,
       next_retry_utc,
       updated_at
FROM boomplay_deezer_track_mapping
WHERE boomplay_track_id=@boomplayTrackId
LIMIT 1;", connection);
        command.Parameters.AddWithValue("boomplayTrackId", normalizedTrackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BoomplayDeezerTrackMappingDto(
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetInt32(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11),
            await reader.IsDBNullAsync(12, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(12)),
            ParseDateTimeOffsetInvariant(reader.GetString(13)));
    }

    public async Task<IReadOnlyDictionary<string, BoomplayDeezerTrackMappingDto>> GetBoomplayDeezerTrackMappingsAsync(
        IEnumerable<string> boomplayTrackIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrackIds = boomplayTrackIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTrackIds.Length == 0)
        {
            return new Dictionary<string, BoomplayDeezerTrackMappingDto>(StringComparer.Ordinal);
        }
        if (normalizedTrackIds.Length > 500)
        {
            var combined = new Dictionary<string, BoomplayDeezerTrackMappingDto>(StringComparer.Ordinal);
            foreach (var batch in normalizedTrackIds.Chunk(500))
            {
                var batchMappings = await GetBoomplayDeezerTrackMappingsAsync(batch, cancellationToken);
                foreach (var mapping in batchMappings)
                {
                    combined[mapping.Key] = mapping.Value;
                }
            }

            return combined;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameterNames = new string[normalizedTrackIds.Length];
        for (var index = 0; index < normalizedTrackIds.Length; index++)
        {
            parameterNames[index] = $"@trackId{index}";
            command.Parameters.AddWithValue(parameterNames[index], normalizedTrackIds[index]);
        }

        command.CommandText = $@"
SELECT boomplay_track_id,
       deezer_track_id,
       isrc,
       title,
       artist,
       album,
       cover_url,
       duration_ms,
       source_fingerprint,
       matcher_version,
       status,
       last_error,
       next_retry_utc,
       updated_at
FROM boomplay_deezer_track_mapping
WHERE boomplay_track_id IN ({string.Join(", ", parameterNames)});";

        var mappings = new Dictionary<string, BoomplayDeezerTrackMappingDto>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mapping = new BoomplayDeezerTrackMappingDto(
                reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11),
                await reader.IsDBNullAsync(12, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(12)),
                ParseDateTimeOffsetInvariant(reader.GetString(13)));
            mappings[mapping.BoomplayTrackId] = mapping;
        }

        return mappings;
    }

    public async Task UpsertBoomplayDeezerTrackMappingAsync(
        BoomplayDeezerTrackMappingUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalizedBoomplayTrackId = input.BoomplayTrackId?.Trim();
        var normalizedFingerprint = input.SourceFingerprint?.Trim();
        var normalizedMatcherVersion = input.MatcherVersion?.Trim();
        var normalizedStatus = input.Status?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedBoomplayTrackId)
            || string.IsNullOrWhiteSpace(normalizedFingerprint)
            || string.IsNullOrWhiteSpace(normalizedMatcherVersion)
            || normalizedStatus is not "matched" and not "mapping_retry")
        {
            throw new ArgumentException("A valid Boomplay mapping identity, fingerprint, matcher version, and status are required.", nameof(input));
        }

        var normalizedDeezerTrackId = string.IsNullOrWhiteSpace(input.DeezerTrackId) ? null : input.DeezerTrackId.Trim();
        if (normalizedStatus == "matched" && string.IsNullOrWhiteSpace(normalizedDeezerTrackId))
        {
            throw new ArgumentException("A matched Boomplay mapping requires a Deezer track id.", nameof(input));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
INSERT INTO boomplay_deezer_track_mapping (
    boomplay_track_id, deezer_track_id, isrc, title, artist, album, cover_url,
    duration_ms, source_fingerprint, matcher_version, status, last_error, next_retry_utc)
VALUES (
    @boomplayTrackId, @deezerTrackId, @isrc, @title, @artist, @album, @coverUrl,
    @durationMs, @sourceFingerprint, @matcherVersion, @status, @lastError, @nextRetryUtc)
ON CONFLICT(boomplay_track_id) DO UPDATE SET
    deezer_track_id=excluded.deezer_track_id,
    isrc=excluded.isrc,
    title=excluded.title,
    artist=excluded.artist,
    album=excluded.album,
    cover_url=excluded.cover_url,
    duration_ms=excluded.duration_ms,
    source_fingerprint=excluded.source_fingerprint,
    matcher_version=excluded.matcher_version,
    status=excluded.status,
    last_error=excluded.last_error,
    next_retry_utc=excluded.next_retry_utc,
    updated_at=CURRENT_TIMESTAMP;", connection);
        command.Parameters.AddWithValue("boomplayTrackId", normalizedBoomplayTrackId);
        command.Parameters.AddWithValue("deezerTrackId", (object?)normalizedDeezerTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("isrc", string.IsNullOrWhiteSpace(input.Isrc) ? DBNull.Value : input.Isrc.Trim());
        command.Parameters.AddWithValue("title", input.Title?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("artist", input.Artist?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("album", input.Album?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("coverUrl", string.IsNullOrWhiteSpace(input.CoverUrl) ? DBNull.Value : input.CoverUrl.Trim());
        command.Parameters.AddWithValue("durationMs", input.DurationMs is > 0 ? input.DurationMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("sourceFingerprint", normalizedFingerprint);
        command.Parameters.AddWithValue("matcherVersion", normalizedMatcherVersion);
        command.Parameters.AddWithValue("status", normalizedStatus);
        command.Parameters.AddWithValue("lastError", string.IsNullOrWhiteSpace(input.LastError) ? DBNull.Value : input.LastError.Trim());
        command.Parameters.AddWithValue("nextRetryUtc", input.NextRetryUtc?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddRecommendationRejectionAsync(
        RecommendationRejectionUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrackSourceId = NormalizeRecommendationTrackSourceId(input.TrackSourceId);
        if (input.LibraryId <= 0
            || string.IsNullOrWhiteSpace(input.StationId)
            || string.IsNullOrWhiteSpace(normalizedTrackSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO recommendation_rejection (
    library_id,
    folder_id,
    station_id,
    track_source_id,
    isrc,
    title,
    artist,
    rejected_at_utc)
VALUES (
    @libraryId,
    @folderId,
    @stationId,
    @trackSourceId,
    @isrc,
    @title,
    @artist,
    @rejectedAtUtc)
ON CONFLICT(station_id, track_source_id) DO UPDATE SET
    library_id = excluded.library_id,
    folder_id = excluded.folder_id,
    isrc = COALESCE(excluded.isrc, recommendation_rejection.isrc),
    title = COALESCE(excluded.title, recommendation_rejection.title),
    artist = COALESCE(excluded.artist, recommendation_rejection.artist),
    rejected_at_utc = excluded.rejected_at_utc;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, input.LibraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)input.FolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("stationId", input.StationId.Trim());
        command.Parameters.AddWithValue("trackSourceId", normalizedTrackSourceId);
        command.Parameters.AddWithValue("isrc", string.IsNullOrWhiteSpace(input.Isrc) ? DBNull.Value : input.Isrc.Trim());
        command.Parameters.AddWithValue("title", string.IsNullOrWhiteSpace(input.Title) ? DBNull.Value : input.Title.Trim());
        command.Parameters.AddWithValue(ArtistParameter, string.IsNullOrWhiteSpace(input.Artist) ? DBNull.Value : input.Artist.Trim());
        command.Parameters.AddWithValue("rejectedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HashSet<string>> GetRecommendationRejectedTrackIdsAsync(
        long libraryId,
        long? folderId,
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || string.IsNullOrWhiteSpace(stationId))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT track_source_id
FROM recommendation_rejection
WHERE library_id = @libraryId
  AND station_id = @stationId
  AND (@folderId IS NULL OR folder_id = @folderId OR folder_id IS NULL);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("stationId", stationId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (await reader.IsDBNullAsync(0, cancellationToken))
            {
                continue;
            }

            var value = NormalizeRecommendationTrackSourceId(reader.GetString(0));
            if (!string.IsNullOrWhiteSpace(value))
            {
                ids.Add(value);
            }
        }

        return ids;
    }

    private static string NormalizeRecommendationTrackSourceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return long.TryParse(trimmed, out _) ? trimmed : string.Empty;
    }

    public async Task<RecommendationGenerationStateDto?> GetRecommendationGenerationStateAsync(
        long libraryId,
        long folderId,
        DateOnly targetDay,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || folderId <= 0)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT library_id,
       folder_id,
       station_id,
       target_day,
       status,
       reason_code,
       started_at_utc,
       completed_at_utc,
       last_error,
       attempt_count,
       updated_at_utc
FROM recommendation_generation_state
WHERE library_id = @libraryId
  AND folder_id = @folderId
  AND target_day = @targetDay
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        command.Parameters.AddWithValue("targetDay", FormatRecommendationTargetDay(targetDay));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadRecommendationGenerationStateAsync(reader, cancellationToken)
            : null;
    }

    public async Task RequestRecommendationGenerationAsync(
        RecommendationGenerationStateKey key,
        string reasonCode,
        bool forceReset = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRecommendationGenerationKey(key))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = forceReset
            ? @"
INSERT INTO recommendation_generation_state (
    library_id,
    folder_id,
    station_id,
    target_day,
    status,
    reason_code,
    started_at_utc,
    completed_at_utc,
    last_error,
    attempt_count,
    updated_at_utc)
VALUES (
    @libraryId,
    @folderId,
    @stationId,
    @targetDay,
    'pending',
    @reasonCode,
    NULL,
    NULL,
    NULL,
    0,
    @updatedAtUtc)
ON CONFLICT(library_id, folder_id, target_day) DO UPDATE SET
    station_id = excluded.station_id,
    status = 'pending',
    reason_code = excluded.reason_code,
    started_at_utc = NULL,
    completed_at_utc = NULL,
    last_error = NULL,
    attempt_count = 0,
    updated_at_utc = excluded.updated_at_utc;"
            : @"
INSERT INTO recommendation_generation_state (
    library_id,
    folder_id,
    station_id,
    target_day,
    status,
    reason_code,
    updated_at_utc)
VALUES (
    @libraryId,
    @folderId,
    @stationId,
    @targetDay,
    'pending',
    @reasonCode,
    @updatedAtUtc)
ON CONFLICT(library_id, folder_id, target_day) DO UPDATE SET
    station_id = excluded.station_id,
    status = CASE
        WHEN recommendation_generation_state.status = 'completed' THEN recommendation_generation_state.status
        WHEN recommendation_generation_state.status = 'running' THEN recommendation_generation_state.status
        ELSE 'pending'
    END,
    reason_code = CASE
        WHEN recommendation_generation_state.status = 'completed' THEN recommendation_generation_state.reason_code
        ELSE excluded.reason_code
    END,
    updated_at_utc = excluded.updated_at_utc;";
        await using var command = new SqliteCommand(sql, connection);
        AddRecommendationGenerationKeyParameters(command, key);
        command.Parameters.AddWithValue("reasonCode", NormalizeRecommendationGenerationText(reasonCode) ?? "requested");
        command.Parameters.AddWithValue("updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryStartRecommendationGenerationAsync(
        RecommendationGenerationStateKey key,
        string reasonCode,
        DateTimeOffset? runningExpiresBeforeUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRecommendationGenerationKey(key))
        {
            return false;
        }

        await RequestRecommendationGenerationAsync(key, reasonCode, forceReset: false, cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE recommendation_generation_state
SET status = 'running',
    reason_code = @reasonCode,
    started_at_utc = @startedAtUtc,
    completed_at_utc = NULL,
    last_error = NULL,
    attempt_count = attempt_count + 1,
    updated_at_utc = @startedAtUtc
WHERE library_id = @libraryId
  AND folder_id = @folderId
  AND target_day = @targetDay
  AND (
      status IN ('pending', 'failed')
      OR (
          status = 'running'
          AND @runningExpiresBeforeUtc IS NOT NULL
          AND COALESCE(started_at_utc, updated_at_utc, '') < @runningExpiresBeforeUtc
      )
  );";
        await using var command = new SqliteCommand(sql, connection);
        AddRecommendationGenerationKeyParameters(command, key);
        command.Parameters.AddWithValue("reasonCode", NormalizeRecommendationGenerationText(reasonCode) ?? "running");
        command.Parameters.AddWithValue("startedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "runningExpiresBeforeUtc",
            runningExpiresBeforeUtc.HasValue
                ? runningExpiresBeforeUtc.Value.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task CompleteRecommendationGenerationAsync(
        RecommendationGenerationStateKey key,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRecommendationGenerationKey(key))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE recommendation_generation_state
SET status = 'completed',
    reason_code = NULL,
    completed_at_utc = @completedAtUtc,
    last_error = NULL,
    updated_at_utc = @completedAtUtc
WHERE library_id = @libraryId
  AND folder_id = @folderId
  AND target_day = @targetDay;";
        await using var command = new SqliteCommand(sql, connection);
        AddRecommendationGenerationKeyParameters(command, key);
        command.Parameters.AddWithValue("completedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailRecommendationGenerationAsync(
        RecommendationGenerationStateKey key,
        string reasonCode,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRecommendationGenerationKey(key))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE recommendation_generation_state
SET status = 'failed',
    reason_code = @reasonCode,
    completed_at_utc = @completedAtUtc,
    last_error = @lastError,
    updated_at_utc = @completedAtUtc
WHERE library_id = @libraryId
  AND folder_id = @folderId
  AND target_day = @targetDay;";
        await using var command = new SqliteCommand(sql, connection);
        AddRecommendationGenerationKeyParameters(command, key);
        command.Parameters.AddWithValue("reasonCode", NormalizeRecommendationGenerationText(reasonCode) ?? "failed");
        command.Parameters.AddWithValue("completedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("lastError", (object?)NormalizeRecommendationGenerationText(error) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RecommendationGenerationStateDto> ReadRecommendationGenerationStateAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        return new RecommendationGenerationStateDto(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(6)),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(7)),
            await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
            reader.GetInt32(9),
            await reader.IsDBNullAsync(10, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(10)));
    }

    private static bool IsValidRecommendationGenerationKey(RecommendationGenerationStateKey key)
        => key.LibraryId > 0
           && key.FolderId > 0
           && !string.IsNullOrWhiteSpace(key.StationId);

    private static void AddRecommendationGenerationKeyParameters(
        SqliteCommand command,
        RecommendationGenerationStateKey key)
    {
        command.Parameters.AddWithValue(LibraryIdField, key.LibraryId);
        command.Parameters.AddWithValue(FolderIdParameter, key.FolderId);
        command.Parameters.AddWithValue("stationId", key.StationId.Trim());
        command.Parameters.AddWithValue("targetDay", FormatRecommendationTargetDay(key.TargetDay));
    }

    private static string FormatRecommendationTargetDay(DateOnly targetDay)
        => targetDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? NormalizeRecommendationGenerationText(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is { Length: > 512 } ? normalized[..512] : normalized;
    }

    public async Task<HashSet<string>> GetPlaylistWatchIgnoredTrackIdsAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT track_source_id
FROM playlist_watch_ignore
WHERE source = @source AND source_id = @sourceId;";
        return await QueryPlaylistWatchTrackSourceIdsAsync(sql, source, sourceId, cancellationToken);
    }

    public async Task<IReadOnlyList<PlaylistWatchTrackStatusDto>> GetPlaylistWatchTrackStatusesAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return Array.Empty<PlaylistWatchTrackStatusDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT playlist_watch_track.track_source_id,
       isrc,
       status,
       COALESCE(playlist_watch_track.updated_at, playlist_watch_track.created_at, '') AS updated_at,
       unavailable_reason,
       unavailable_since_utc,
       unavailable_last_checked_utc,
       unavailable_next_retry_utc,
       unavailable_settings_fingerprint,
       local_track_id,
       identity_status,
       identity_reason,
       COALESCE(
           (SELECT group_concat(m.target_service, ', ')
              FROM playlist_watch_target_membership m
             WHERE m.source = playlist_watch_track.source
               AND m.source_id = playlist_watch_track.source_id
               AND m.track_source_id = playlist_watch_track.track_source_id
               AND lower(m.sync_status) = 'playlist_synced'),
           (SELECT group_concat(cst.target, ', ')
              FROM playlist_watch_configured_sync_targets cst
             WHERE cst.source = playlist_watch_track.source
               AND cst.source_id = playlist_watch_track.source_id)) AS target_service,
       (SELECT group_concat(m.target_playlist_id, ', ')
          FROM playlist_watch_target_membership m
         WHERE m.source = playlist_watch_track.source
           AND m.source_id = playlist_watch_track.source_id
           AND m.track_source_id = playlist_watch_track.track_source_id
           AND lower(m.sync_status) = 'playlist_synced') AS target_playlist_id,
       (SELECT group_concat(m.target_item_id, ', ')
          FROM playlist_watch_target_membership m
         WHERE m.source = playlist_watch_track.source
           AND m.source_id = playlist_watch_track.source_id
           AND m.track_source_id = playlist_watch_track.track_source_id
           AND lower(m.sync_status) = 'playlist_synced') AS target_item_id,
       -- Presentation CASE lives in playlist_watch_track_presentation_status (shared with
       -- GetPlaylistWatchlistAsync list buckets) so card counts and tracklist pills cannot drift.
       COALESCE(presentation.presentation_status, playlist_watch_track.status) AS sync_status,
       redirect_track_source_id,
       redirect_reason,
       verified_at_utc,
       source_position,
       (SELECT group_concat(m.target_service, ', ')
          FROM playlist_watch_target_membership m
         WHERE m.source = playlist_watch_track.source
           AND m.source_id = playlist_watch_track.source_id
           AND m.track_source_id = playlist_watch_track.track_source_id
           AND lower(m.sync_status) = 'playlist_synced') AS synced_target_service,
       (SELECT group_concat(cst.target, ', ')
          FROM playlist_watch_configured_sync_targets cst
         WHERE cst.source = playlist_watch_track.source
           AND cst.source_id = playlist_watch_track.source_id
           AND NOT EXISTS (
               SELECT 1
                 FROM playlist_watch_target_membership m
                WHERE m.source = playlist_watch_track.source
                  AND m.source_id = playlist_watch_track.source_id
                  AND m.track_source_id = playlist_watch_track.track_source_id
                  AND lower(m.target_service) = cst.target
                  AND lower(m.sync_status) = 'playlist_synced')) AS missing_target_service
FROM playlist_watch_track
LEFT JOIN playlist_watch_track_presentation_status presentation
  ON presentation.source = playlist_watch_track.source
 AND presentation.source_id = playlist_watch_track.source_id
 AND presentation.track_source_id = playlist_watch_track.track_source_id
WHERE playlist_watch_track.source = @source AND playlist_watch_track.source_id = @sourceId
ORDER BY CASE WHEN source_position IS NULL THEN 1 ELSE 0 END,
         source_position,
         playlist_watch_track.track_source_id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var statuses = new List<PlaylistWatchTrackStatusDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackSourceId = await reader.IsDBNullAsync(0, cancellationToken) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(trackSourceId))
            {
                continue;
            }

            var isrc = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
            var status = await reader.IsDBNullAsync(2, cancellationToken) ? string.Empty : reader.GetString(2);
            var updatedAtText = await reader.IsDBNullAsync(3, cancellationToken) ? string.Empty : reader.GetString(3);
            var updatedAt = string.IsNullOrWhiteSpace(updatedAtText)
                ? DateTimeOffset.MinValue
                : ParseDateTimeOffsetInvariant(updatedAtText);
            var unavailableReason = await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4);
            var unavailableSince = ReadNullableDateTimeOffset(reader, 5, cancellationToken);
            var unavailableLastChecked = ReadNullableDateTimeOffset(reader, 6, cancellationToken);
            var unavailableNextRetry = ReadNullableDateTimeOffset(reader, 7, cancellationToken);
            var unavailableSettingsFingerprint = await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8);
            long? localTrackId = await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetInt64(9);
            var identityStatus = await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10);
            var identityReason = await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11);
            var targetService = await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12);
            var targetPlaylistId = await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetString(13);
            var targetItemId = await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetString(14);
            var syncStatus = await reader.IsDBNullAsync(15, cancellationToken) ? null : reader.GetString(15);
            var redirectTrackSourceId = await reader.IsDBNullAsync(16, cancellationToken) ? null : reader.GetString(16);
            var redirectReason = await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetString(17);
            var verifiedAt = ReadNullableDateTimeOffset(reader, 18, cancellationToken);
            int? sourcePosition = await reader.IsDBNullAsync(19, cancellationToken) ? null : reader.GetInt32(19);
            var syncedTargetServices = await reader.IsDBNullAsync(20, cancellationToken) ? null : reader.GetString(20);
            var missingTargetServices = await reader.IsDBNullAsync(21, cancellationToken) ? null : reader.GetString(21);
            statuses.Add(new PlaylistWatchTrackStatusDto(
                trackSourceId,
                isrc,
                status,
                updatedAt,
                unavailableReason,
                unavailableSince,
                unavailableLastChecked,
                unavailableNextRetry,
                unavailableSettingsFingerprint,
                localTrackId,
                identityStatus,
                identityReason,
                targetService,
                targetPlaylistId,
                targetItemId,
                syncStatus,
                redirectTrackSourceId,
                redirectReason,
                verifiedAt,
                sourcePosition,
                syncedTargetServices,
                missingTargetServices));
        }

        return statuses;
    }

    public async Task UpdatePlaylistWatchTrackVerificationAsync(
        string source,
        string sourceId,
        PlaylistWatchTrackVerification verification,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId)
            || string.IsNullOrWhiteSpace(verification.TrackSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string insertSql = @"
INSERT OR IGNORE INTO playlist_watch_track (
    source,
    source_id,
    track_source_id,
    status,
    created_at,
    updated_at)
VALUES (
    @source,
    @sourceId,
    @trackSourceId,
    @initialStatus,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP);";
        await using (var insert = new SqliteCommand(insertSql, connection))
        {
            insert.Parameters.AddWithValue(SourceField, normalizedSource);
            insert.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            insert.Parameters.AddWithValue("trackSourceId", verification.TrackSourceId.Trim());
            insert.Parameters.AddWithValue(
                "initialStatus",
                verification.LocalTrackId.HasValue && !string.Equals(verification.IdentityStatus, "review", StringComparison.OrdinalIgnoreCase)
                    ? "completed"
                    : string.Equals(verification.IdentityStatus, "review", StringComparison.OrdinalIgnoreCase)
                        ? "review"
                        : "missing");
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = @"
UPDATE playlist_watch_track
SET status = CASE
        WHEN @localTrackId IS NOT NULL AND @identityStatus <> 'review' THEN 'completed'
        WHEN @identityStatus = 'missing' AND lower(status) IN ('completed', 'complete', 'downloaded') THEN 'missing'
        ELSE status
    END,
    local_track_id = @localTrackId,
    identity_status = @identityStatus,
    identity_reason = @identityReason,
    redirect_track_source_id = @redirectTrackSourceId,
    redirect_reason = @redirectReason,
    verified_at_utc = CURRENT_TIMESTAMP,
    updated_at = CURRENT_TIMESTAMP
WHERE source = @source
  AND source_id = @sourceId
  AND track_source_id = @trackSourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", verification.TrackSourceId.Trim());
        command.Parameters.AddWithValue("localTrackId", (object?)verification.LocalTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("identityStatus", verification.IdentityStatus);
        command.Parameters.AddWithValue("identityReason", (object?)verification.IdentityReason ?? DBNull.Value);
        command.Parameters.AddWithValue("redirectTrackSourceId", (object?)verification.RedirectTrackSourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("redirectReason", (object?)verification.RedirectReason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (verification.LocalTrackId.HasValue
            && !string.Equals(verification.IdentityStatus, "review", StringComparison.OrdinalIgnoreCase))
        {
            await ResolvePlaylistWatchMissingTrackAsync(
                normalizedSource,
                normalizedSourceId,
                verification.TrackSourceId,
                cancellationToken);
        }
    }

    public Task ReplacePlaylistWatchTargetMembershipAsync(
        string source,
        string sourceId,
        string targetService,
        string targetPlaylistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> memberships,
        CancellationToken cancellationToken = default)
        => ReplacePlaylistWatchTargetMembershipAsync(
            source,
            sourceId,
            targetService,
            targetPlaylistId,
            memberships
                .Select(static membership => new PlaylistWatchTargetMembershipWrite(
                    membership.TrackSourceId,
                    membership.LocalTrackId,
                    membership.TargetItemId,
                    "playlist_synced"))
                .ToList(),
            cancellationToken);

    public async Task ReplacePlaylistWatchTargetMembershipAsync(
        string source,
        string sourceId,
        string targetService,
        string? targetPlaylistId,
        IReadOnlyCollection<PlaylistWatchTargetMembershipWrite> memberships,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId)
            || string.IsNullOrWhiteSpace(targetService))
        {
            return;
        }

        var normalizedTarget = targetService.Trim().ToLowerInvariant();
        var playlistId = string.IsNullOrWhiteSpace(targetPlaylistId) ? string.Empty : targetPlaylistId.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string insertMembershipSql = @"
INSERT INTO playlist_watch_target_membership (
    source,
    source_id,
    track_source_id,
    target_service,
    target_playlist_id,
    target_item_id,
    local_track_id,
    sync_status,
    verified_at_utc,
    updated_at
)
VALUES (
    @source,
    @sourceId,
    @trackSourceId,
    @targetService,
    @targetPlaylistId,
    @targetItemId,
    @localTrackId,
    @syncStatus,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
)
ON CONFLICT(source, source_id, track_source_id, target_service) DO UPDATE SET
    target_playlist_id = excluded.target_playlist_id,
    target_item_id = excluded.target_item_id,
    local_track_id = excluded.local_track_id,
    sync_status = excluded.sync_status,
    verified_at_utc = CASE
        WHEN excluded.sync_status = 'playlist_synced' THEN excluded.verified_at_utc
        ELSE playlist_watch_target_membership.verified_at_utc
    END,
    updated_at = CURRENT_TIMESTAMP;";
        foreach (var membership in memberships)
        {
            if (string.IsNullOrWhiteSpace(membership.TrackSourceId)
                || string.IsNullOrWhiteSpace(membership.SyncStatus))
            {
                continue;
            }

            await using var insertMembership = new SqliteCommand(insertMembershipSql, connection, transaction);
            insertMembership.Parameters.AddWithValue(SourceField, normalizedSource);
            insertMembership.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            insertMembership.Parameters.AddWithValue("trackSourceId", membership.TrackSourceId.Trim());
            insertMembership.Parameters.AddWithValue("targetService", normalizedTarget);
            insertMembership.Parameters.AddWithValue("targetPlaylistId", playlistId);
            insertMembership.Parameters.AddWithValue(
                "targetItemId",
                string.IsNullOrWhiteSpace(membership.TargetItemId) ? DBNull.Value : membership.TargetItemId.Trim());
            insertMembership.Parameters.AddWithValue(
                "localTrackId",
                membership.LocalTrackId.HasValue && membership.LocalTrackId.Value > 0
                    ? membership.LocalTrackId.Value
                    : DBNull.Value);
            insertMembership.Parameters.AddWithValue("syncStatus", membership.SyncStatus.Trim());
            await insertMembership.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteRemovedSql = @"
DELETE FROM playlist_watch_target_membership
WHERE source = @source
  AND source_id = @sourceId
  AND target_service = @targetService
  AND track_source_id NOT IN (
      SELECT track.track_source_id
      FROM playlist_watch_track track
      WHERE track.source = @source
        AND track.source_id = @sourceId);";
        await using (var deleteRemoved = new SqliteCommand(deleteRemovedSql, connection, transaction))
        {
            deleteRemoved.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteRemoved.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            deleteRemoved.Parameters.AddWithValue("targetService", normalizedTarget);
            await deleteRemoved.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsPlaylistWatchTrackSyncedToTargetAsync(
        string source,
        string sourceId,
        string trackSourceId,
        string targetService,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId)
            || string.IsNullOrWhiteSpace(trackSourceId)
            || string.IsNullOrWhiteSpace(targetService))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT 1
FROM playlist_watch_target_membership
WHERE source = @source
  AND source_id = @sourceId
  AND track_source_id = @trackSourceId
  AND lower(target_service) = @targetService
  AND lower(sync_status) = 'playlist_synced'
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", trackSourceId.Trim());
        command.Parameters.AddWithValue("targetService", targetService.Trim().ToLowerInvariant());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task<IReadOnlyList<PlaylistWatchTrackStatusDto>> GetGlobalPlaylistWatchTrackUnavailableStatusesAsync(
        string source,
        IReadOnlyCollection<PlaylistWatchTrackInsert> tracks,
        string settingsFingerprint,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = NormalizePlaylistWatchSource(source);
        var trackSourceIds = tracks
            .Select(static track => track.TrackSourceId?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var isrcs = tracks
            .Select(static track => track.Isrc?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(normalizedSource)
            || (trackSourceIds.Count == 0 && isrcs.Count == 0)
            || string.IsNullOrWhiteSpace(settingsFingerprint))
        {
            return Array.Empty<PlaylistWatchTrackStatusDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var trackSourceParameters = AddInParameters("trackSourceId", trackSourceIds);
        var isrcParameters = AddInParameters("isrc", isrcs);
        var filters = new List<string>();
        if (trackSourceParameters.Count > 0)
        {
            filters.Add($"track_source_id IN ({string.Join(", ", trackSourceParameters)})");
        }
        if (isrcParameters.Count > 0)
        {
            filters.Add($"isrc IN ({string.Join(", ", isrcParameters)})");
        }

        var sql = $@"
SELECT track_source_id,
       isrc,
       status,
       COALESCE(updated_at, created_at, '') AS updated_at,
       unavailable_reason,
       unavailable_since_utc,
       unavailable_last_checked_utc,
       unavailable_next_retry_utc,
       unavailable_settings_fingerprint
FROM playlist_watch_track
WHERE source = @source
  AND status = 'unavailable'
  AND unavailable_settings_fingerprint = @settingsFingerprint
  AND ({string.Join(" OR ", filters)})
ORDER BY unavailable_next_retry_utc DESC, updated_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("settingsFingerprint", settingsFingerprint);
        AddParameterValues(command, trackSourceParameters, trackSourceIds);
        AddParameterValues(command, isrcParameters, isrcs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var statuses = new List<PlaylistWatchTrackStatusDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var matchedTrackSourceId = await reader.IsDBNullAsync(0, cancellationToken) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(matchedTrackSourceId))
            {
                continue;
            }

            var matchedIsrc = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
            var status = await reader.IsDBNullAsync(2, cancellationToken) ? string.Empty : reader.GetString(2);
            var updatedAtText = await reader.IsDBNullAsync(3, cancellationToken) ? string.Empty : reader.GetString(3);
            var updatedAt = string.IsNullOrWhiteSpace(updatedAtText)
                ? DateTimeOffset.MinValue
                : ParseDateTimeOffsetInvariant(updatedAtText);
            var unavailableReason = await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4);
            var unavailableSince = ReadNullableDateTimeOffset(reader, 5, cancellationToken);
            var unavailableLastChecked = ReadNullableDateTimeOffset(reader, 6, cancellationToken);
            var unavailableNextRetry = ReadNullableDateTimeOffset(reader, 7, cancellationToken);
            var unavailableSettingsFingerprint = await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8);
            statuses.Add(new PlaylistWatchTrackStatusDto(
                matchedTrackSourceId,
                matchedIsrc,
                status,
                updatedAt,
                unavailableReason,
                unavailableSince,
                unavailableLastChecked,
                unavailableNextRetry,
                unavailableSettingsFingerprint));
        }

        return statuses;
    }

    private static List<string> AddInParameters(string prefix, IReadOnlyList<string> values)
        => values.Select((_, index) => $"@{prefix}{index}").ToList();

    private static void AddParameterValues(SqliteCommand command, IReadOnlyList<string> parameterNames, IReadOnlyList<string> values)
    {
        for (var i = 0; i < parameterNames.Count; i++)
        {
            command.Parameters.AddWithValue(parameterNames[i].TrimStart('@'), values[i]);
        }
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetString(ordinal);
        return string.IsNullOrWhiteSpace(value) ? null : ParseDateTimeOffsetInvariant(value);
    }

    public async Task<HashSet<string>> GetPlaylistWatchIgnoredTrackIdsBySourceAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT track_source_id
FROM playlist_watch_ignore
WHERE source = @source;";
        return await QueryPlaylistWatchTrackSourceIdsBySourceAsync(sql, source, cancellationToken);
    }

    public async Task AddPlaylistWatchIgnoredTracksAsync(
        string source,
        string sourceId,
        IReadOnlyCollection<PlaylistWatchIgnoreInsert> tracks,
        CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        const string sql = @"
INSERT OR IGNORE INTO playlist_watch_ignore (source, source_id, track_source_id, isrc)
VALUES (@source, @sourceId, @trackSourceId, @isrc);";
        await InsertPlaylistWatchRowsAsync(
            sql,
            source,
            sourceId,
            tracks,
            track => track.TrackSourceId,
            track => track.Isrc,
            cancellationToken);
    }

    public async Task<bool> RemovePlaylistWatchIgnoredTrackAsync(
        string source,
        string sourceId,
        string trackSourceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
DELETE FROM playlist_watch_ignore
WHERE source = @source AND source_id = @sourceId AND track_source_id = @trackSourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", trackSourceId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<IReadOnlyList<DownloadBlocklistEntryDto>> GetDownloadBlocklistEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id,
       field,
       value,
       is_enabled,
       created_at
FROM download_blocklist
ORDER BY field, value, id;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<DownloadBlocklistEntryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new DownloadBlocklistEntryDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                !await reader.IsDBNullAsync(3, cancellationToken) && reader.GetInt64(3) != 0,
                await reader.IsDBNullAsync(4, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(4))));
        }

        return entries;
    }

    public async Task<DownloadBlocklistEntryDto?> UpsertDownloadBlocklistEntryAsync(
        string field,
        string value,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        var normalizedField = NormalizeBlocklistField(field);
        var normalizedValue = NormalizeBlocklistValue(value);
        if (string.IsNullOrWhiteSpace(normalizedField) || string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        var storedValue = value.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string upsertSql = @"
INSERT INTO download_blocklist (field, value, normalized_value, is_enabled)
VALUES (@field, @value, @normalizedValue, @enabled)
ON CONFLICT(field, normalized_value) DO UPDATE SET
    value = excluded.value,
    is_enabled = excluded.is_enabled;";
        await using (var upsert = new SqliteCommand(upsertSql, connection))
        {
            upsert.Parameters.AddWithValue("field", normalizedField);
            upsert.Parameters.AddWithValue("value", storedValue);
            upsert.Parameters.AddWithValue("normalizedValue", normalizedValue);
            upsert.Parameters.AddWithValue("enabled", enabled ? 1 : 0);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        const string readSql = @"
SELECT id, field, value, is_enabled, created_at
FROM download_blocklist
WHERE field = @field AND normalized_value = @normalizedValue
LIMIT 1;";
        await using var command = new SqliteCommand(readSql, connection);
        command.Parameters.AddWithValue("field", normalizedField);
        command.Parameters.AddWithValue("normalizedValue", normalizedValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DownloadBlocklistEntryDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            !await reader.IsDBNullAsync(3, cancellationToken) && reader.GetInt64(3) != 0,
            await reader.IsDBNullAsync(4, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(4)));
    }

    public async Task<bool> RemoveDownloadBlocklistEntryAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM download_blocklist WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<DownloadBlocklistMatchDto?> FindMatchingDownloadBlocklistAsync(
        string? trackTitle,
        string? artistName,
        string? albumTitle,
        IReadOnlyCollection<string>? genres,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrack = NormalizeBlocklistValue(trackTitle);
        var normalizedArtist = NormalizeBlocklistValue(artistName);
        var normalizedAlbum = NormalizeBlocklistValue(albumTitle);
        var normalizedGenres = (genres ?? Array.Empty<string>())
            .Select(NormalizeBlocklistValue)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (string.IsNullOrWhiteSpace(normalizedTrack)
            && string.IsNullOrWhiteSpace(normalizedArtist)
            && string.IsNullOrWhiteSpace(normalizedAlbum)
            && normalizedGenres.Count == 0)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CreateBlocklistGenreScopeTableAsync(connection, normalizedGenres, cancellationToken);
        const string sql = @"
	SELECT field, value
	FROM download_blocklist
	WHERE is_enabled = 1
	  AND (
	      (field = 'track' AND normalized_value = @track)
	      OR (field = 'artist' AND normalized_value = @artist)
	      OR (field = 'album' AND normalized_value = @album)
	      OR (
	          field = 'genre'
	          AND EXISTS (
	              SELECT 1
	              FROM temp_blocklist_genre_scope genre
	              WHERE genre.normalized_value = download_blocklist.normalized_value
	          )
	      )
	  )
	ORDER BY id
	LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("track", (object?)normalizedTrack ?? DBNull.Value);
        command.Parameters.AddWithValue(ArtistParameter, (object?)normalizedArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("album", (object?)normalizedAlbum ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DownloadBlocklistMatchDto(reader.GetString(0), reader.GetString(1));
    }

    public Task<DownloadBlocklistMatchDto?> FindMatchingDownloadBlocklistAsync(
        string? trackTitle,
        string? artistName,
        string? albumTitle,
        CancellationToken cancellationToken = default)
        => FindMatchingDownloadBlocklistAsync(trackTitle, artistName, albumTitle, null, cancellationToken);

    private static async Task CreateBlocklistGenreScopeTableAsync(
        SqliteConnection connection,
        List<string> normalizedGenres,
        CancellationToken cancellationToken)
    {
        const string createSql = @"
CREATE TEMP TABLE IF NOT EXISTS temp_blocklist_genre_scope (
    normalized_value TEXT PRIMARY KEY
);
DELETE FROM temp_blocklist_genre_scope;";
        await using (var createCommand = new SqliteCommand(createSql, connection))
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (normalizedGenres.Count == 0)
        {
            return;
        }

        const string insertSql = @"
INSERT OR IGNORE INTO temp_blocklist_genre_scope (normalized_value)
VALUES (@normalizedValue);";
        await using var insertCommand = new SqliteCommand(insertSql, connection);
        var genreParameter = insertCommand.Parameters.Add("normalizedValue", SqliteType.Text);
        foreach (var normalizedGenre in normalizedGenres)
        {
            genreParameter.Value = normalizedGenre;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task AddPlaylistWatchTracksAsync(
        string source,
        string sourceId,
        IReadOnlyCollection<PlaylistWatchTrackInsert> tracks,
        CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
INSERT INTO playlist_watch_track (
    source, source_id, track_source_id, isrc, status, updated_at,
    source_position, title, artist, album, duration_ms, cover_url,
    candidate_revision, last_snapshot_id, mapping_status)
VALUES (
    @source, @sourceId, @trackSourceId, @isrc, 'missing', CURRENT_TIMESTAMP,
    @sourcePosition, @title, @artist, @album, @durationMs, @coverUrl,
    @candidateRevision, @snapshotId, @mappingStatus)
ON CONFLICT(source, source_id, track_source_id) DO UPDATE SET
    isrc=COALESCE(excluded.isrc, playlist_watch_track.isrc),
    source_position=COALESCE(excluded.source_position, playlist_watch_track.source_position),
    title=COALESCE(excluded.title, playlist_watch_track.title),
    artist=COALESCE(excluded.artist, playlist_watch_track.artist),
    album=COALESCE(excluded.album, playlist_watch_track.album),
    duration_ms=COALESCE(excluded.duration_ms, playlist_watch_track.duration_ms),
    cover_url=COALESCE(excluded.cover_url, playlist_watch_track.cover_url),
    candidate_revision=COALESCE(excluded.candidate_revision, playlist_watch_track.candidate_revision),
    last_snapshot_id=COALESCE(excluded.last_snapshot_id, playlist_watch_track.last_snapshot_id),
    mapping_status=COALESCE(excluded.mapping_status, playlist_watch_track.mapping_status),
    updated_at=CURRENT_TIMESTAMP;";
        foreach (var track in tracks)
        {
            await using var command = new SqliteCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(SourceField, normalizedSource);
            command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            command.Parameters.AddWithValue("trackSourceId", track.TrackSourceId.Trim());
            command.Parameters.AddWithValue("isrc", (object?)track.Isrc ?? DBNull.Value);
            command.Parameters.AddWithValue("sourcePosition", (object?)track.SourcePosition ?? DBNull.Value);
            command.Parameters.AddWithValue("title", (object?)track.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("artist", (object?)track.Artist ?? DBNull.Value);
            command.Parameters.AddWithValue("album", (object?)track.Album ?? DBNull.Value);
            command.Parameters.AddWithValue("durationMs", (object?)track.DurationMs ?? DBNull.Value);
            command.Parameters.AddWithValue("coverUrl", (object?)track.CoverUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("candidateRevision", (object?)track.CandidateRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("snapshotId", (object?)track.SnapshotId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "mappingStatus",
                string.IsNullOrWhiteSpace(track.MappingStatus) ? DBNull.Value : track.MappingStatus.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertPlaylistWatchMissingTracksAsync(
        string source,
        string sourceId,
        IReadOnlyCollection<PlaylistWatchMissingTrackUpsert> tracks,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string upsertSql = @"
INSERT INTO playlist_watch_missing_track (
    source, source_id, track_source_id, isrc, source_position, title, artist, album,
    duration_ms, cover_url, deezer_id, mapping_status, status, snapshot_id,
    candidate_revision, provider_readiness_revision, queue_uuid, last_error,
    retry_after_utc, updated_at)
VALUES (
    @source, @sourceId, @trackSourceId, @isrc, @sourcePosition, @title, @artist, @album,
    @durationMs, @coverUrl, @deezerId, @mappingStatus, 'missing', @snapshotId,
    @candidateRevision, @providerReadinessRevision, NULL, NULL, NULL, CURRENT_TIMESTAMP)
ON CONFLICT(source, source_id, track_source_id) DO UPDATE SET
    isrc=COALESCE(excluded.isrc, playlist_watch_missing_track.isrc),
    source_position=COALESCE(excluded.source_position, playlist_watch_missing_track.source_position),
    title=COALESCE(excluded.title, playlist_watch_missing_track.title),
    artist=COALESCE(excluded.artist, playlist_watch_missing_track.artist),
    album=COALESCE(excluded.album, playlist_watch_missing_track.album),
    duration_ms=COALESCE(excluded.duration_ms, playlist_watch_missing_track.duration_ms),
    cover_url=COALESCE(excluded.cover_url, playlist_watch_missing_track.cover_url),
    deezer_id=COALESCE(excluded.deezer_id, playlist_watch_missing_track.deezer_id),
    mapping_status=COALESCE(excluded.mapping_status, playlist_watch_missing_track.mapping_status),
    snapshot_id=COALESCE(excluded.snapshot_id, playlist_watch_missing_track.snapshot_id),
    candidate_revision=COALESCE(excluded.candidate_revision, playlist_watch_missing_track.candidate_revision),
    provider_readiness_revision=COALESCE(excluded.provider_readiness_revision, playlist_watch_missing_track.provider_readiness_revision),
    status=CASE
        WHEN lower(playlist_watch_missing_track.status) IN ('queued', 'downloading', 'unavailable', 'blocked') THEN playlist_watch_missing_track.status
        ELSE 'missing'
    END,
    queue_uuid=CASE
        WHEN lower(playlist_watch_missing_track.status) IN ('queued', 'downloading') THEN playlist_watch_missing_track.queue_uuid
        ELSE NULL
    END,
    last_error=NULL,
    retry_after_utc=CASE
        WHEN lower(playlist_watch_missing_track.status) = 'unavailable' THEN playlist_watch_missing_track.retry_after_utc
        ELSE NULL
    END,
    updated_at=CURRENT_TIMESTAMP;";
        foreach (var track in tracks.Where(static item => !string.IsNullOrWhiteSpace(item.TrackSourceId)))
        {
            await using var command = new SqliteCommand(upsertSql, connection, transaction);
            command.Parameters.AddWithValue(SourceField, normalizedSource);
            command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            command.Parameters.AddWithValue("trackSourceId", track.TrackSourceId.Trim());
            command.Parameters.AddWithValue("isrc", (object?)track.Isrc ?? DBNull.Value);
            command.Parameters.AddWithValue("sourcePosition", (object?)track.SourcePosition ?? DBNull.Value);
            command.Parameters.AddWithValue("title", (object?)track.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("artist", (object?)track.Artist ?? DBNull.Value);
            command.Parameters.AddWithValue("album", (object?)track.Album ?? DBNull.Value);
            command.Parameters.AddWithValue("durationMs", (object?)track.DurationMs ?? DBNull.Value);
            command.Parameters.AddWithValue("coverUrl", (object?)track.CoverUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("deezerId", (object?)track.DeezerId ?? DBNull.Value);
            command.Parameters.AddWithValue("mappingStatus", (object?)track.MappingStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("snapshotId", (object?)track.SnapshotId ?? DBNull.Value);
            command.Parameters.AddWithValue("candidateRevision", (object?)track.CandidateRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("providerReadinessRevision", (object?)track.ProviderReadinessRevision ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string resolveLocalSql = @"
UPDATE playlist_watch_missing_track
SET status='resolved',
    queue_uuid=NULL,
    last_error=NULL,
    retry_after_utc=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE source=@source
  AND source_id=@sourceId
  AND EXISTS (
      SELECT 1
      FROM playlist_watch_track track
      WHERE track.source=playlist_watch_missing_track.source
        AND track.source_id=playlist_watch_missing_track.source_id
        AND track.track_source_id=playlist_watch_missing_track.track_source_id
        AND track.local_track_id IS NOT NULL
        AND lower(COALESCE(track.identity_status, '')) <> 'review'
  );";
        await using (var resolveLocalCommand = new SqliteCommand(resolveLocalSql, connection, transaction))
        {
            resolveLocalCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            resolveLocalCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await resolveLocalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlaylistWatchMissingTrackDto>> GetDuePlaylistWatchMissingTracksInPriorityOrderAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT missing.id, missing.source, missing.source_id, missing.track_source_id, missing.isrc,
       missing.source_position, missing.title, missing.artist, missing.album, missing.duration_ms,
       missing.cover_url, missing.deezer_id, missing.mapping_status, missing.status,
       missing.snapshot_id, missing.candidate_revision, missing.provider_readiness_revision,
       missing.queue_uuid, missing.updated_at
FROM playlist_watch_missing_track missing
JOIN playlist_watchlist playlist
  ON playlist.source=missing.source AND playlist.source_id=missing.source_id
WHERE lower(missing.status) IN ('missing', 'failed')
  AND (missing.retry_after_utc IS NULL OR datetime(missing.retry_after_utc) <= datetime('now'))
ORDER BY CASE WHEN playlist.sync_priority IS NULL OR playlist.sync_priority <= 0 THEN 1 ELSE 0 END,
         playlist.sync_priority,
         playlist.created_at DESC,
         CASE WHEN missing.source_position IS NULL THEN 1 ELSE 0 END,
         missing.source_position,
         missing.id;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PlaylistWatchMissingTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(await ReadPlaylistWatchMissingTrackAsync(reader, cancellationToken));
        }

        return rows;
    }

    public async Task<int> MarkPlaylistWatchMissingTrackQueuedAsync(
        string source,
        string sourceId,
        string trackSourceId,
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackSourceId)
            || string.IsNullOrWhiteSpace(queueUuid)
            || !TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watch_missing_track
SET status='queued',
    queue_uuid=@queueUuid,
    last_error=NULL,
    retry_after_utc=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE source=@source AND source_id=@sourceId AND track_source_id=@trackSourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", trackSourceId.Trim());
        command.Parameters.AddWithValue("queueUuid", queueUuid.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ResolvePlaylistWatchMissingTrackAsync(
        string source,
        string sourceId,
        string trackSourceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackSourceId)
            || !TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watch_missing_track
SET status='resolved',
    queue_uuid=NULL,
    last_error=NULL,
    retry_after_utc=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE source=@source AND source_id=@sourceId AND track_source_id=@trackSourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", trackSourceId.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> MarkPlaylistWatchMissingTrackStatusAsync(
        string source,
        string sourceId,
        string trackSourceId,
        string status,
        string? lastError,
        DateTimeOffset? retryAfterUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackSourceId)
            || string.IsNullOrWhiteSpace(status)
            || !TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watch_missing_track
SET status=@status,
    last_error=@lastError,
    retry_after_utc=@retryAfterUtc,
    updated_at=CURRENT_TIMESTAMP
WHERE source=@source AND source_id=@sourceId AND track_source_id=@trackSourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", trackSourceId.Trim());
        command.Parameters.AddWithValue("status", status.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("lastError", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("retryAfterUtc", retryAfterUtc.HasValue ? retryAfterUtc.Value.ToString("O") : DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ResolvePlaylistWatchMissingTracksByQueueAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watch_missing_track
SET status='resolved',
    queue_uuid=NULL,
    last_error=NULL,
    retry_after_utc=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE queue_uuid=@queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> MarkPlaylistWatchMissingTracksByQueueStatusAsync(
        string queueUuid,
        string status,
        string? lastError,
        DateTimeOffset? retryAfterUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid) || string.IsNullOrWhiteSpace(status))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watch_missing_track
SET status=@status,
    last_error=@lastError,
    retry_after_utc=@retryAfterUtc,
    updated_at=CURRENT_TIMESTAMP
WHERE queue_uuid=@queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid.Trim());
        command.Parameters.AddWithValue("status", status.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("lastError", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("retryAfterUtc", retryAfterUtc.HasValue ? retryAfterUtc.Value.ToString("O") : DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PlaylistWatchMissingTrackDto> ReadPlaylistWatchMissingTrackAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var updatedAtText = await reader.IsDBNullAsync(18, cancellationToken) ? string.Empty : reader.GetString(18);
        return new PlaylistWatchMissingTrackDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
            await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
            await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetInt32(9),
            await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10),
            await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11),
            await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12),
            reader.GetString(13),
            await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetString(14),
            await reader.IsDBNullAsync(15, cancellationToken) ? null : reader.GetString(15),
            await reader.IsDBNullAsync(16, cancellationToken) ? null : reader.GetString(16),
            await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetString(17),
            string.IsNullOrWhiteSpace(updatedAtText) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(updatedAtText));
    }

    public async Task<int> RemovePlaylistWatchTracksNotInAsync(
        string source,
        string sourceId,
        IReadOnlyCollection<string> currentTrackSourceIds,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (currentTrackSourceIds.Count == 0)
        {
            const string deleteAllSql = @"
DELETE FROM playlist_watch_track
WHERE source = @source AND source_id = @sourceId;";
            await using var deleteAllCommand = new SqliteCommand(deleteAllSql, connection, transaction);
            deleteAllCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteAllCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            var deletedAll = await deleteAllCommand.ExecuteNonQueryAsync(cancellationToken);
            const string deleteAllClaimsSql = @"
DELETE FROM playlist_watch_download_claim
WHERE source = @source AND source_id = @sourceId;";
            await using var deleteAllClaimsCommand = new SqliteCommand(deleteAllClaimsSql, connection, transaction);
            deleteAllClaimsCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteAllClaimsCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await deleteAllClaimsCommand.ExecuteNonQueryAsync(cancellationToken);
            const string deleteAllMembershipSql = @"
DELETE FROM playlist_watch_target_membership
WHERE source = @source AND source_id = @sourceId;";
            await using var deleteAllMembershipCommand = new SqliteCommand(deleteAllMembershipSql, connection, transaction);
            deleteAllMembershipCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteAllMembershipCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await deleteAllMembershipCommand.ExecuteNonQueryAsync(cancellationToken);
            const string deleteAllMissingSql = @"
DELETE FROM playlist_watch_missing_track
WHERE source = @source AND source_id = @sourceId;";
            await using var deleteAllMissingCommand = new SqliteCommand(deleteAllMissingSql, connection, transaction);
            deleteAllMissingCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteAllMissingCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await deleteAllMissingCommand.ExecuteNonQueryAsync(cancellationToken);
            const string deleteAllJobsSql = @"
DELETE FROM watchlist_sync_job
WHERE source = @source AND playlist_id = @sourceId;";
            await using var deleteAllJobsCommand = new SqliteCommand(deleteAllJobsSql, connection, transaction);
            deleteAllJobsCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteAllJobsCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await deleteAllJobsCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deletedAll;
        }

        const string createTempSql = @"
CREATE TEMP TABLE IF NOT EXISTS temp_current_playlist_watch_track (
    track_source_id TEXT NOT NULL PRIMARY KEY
);";
        await using (var createTempCommand = new SqliteCommand(createTempSql, connection, transaction))
        {
            await createTempCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearTempSql = "DELETE FROM temp_current_playlist_watch_track;";
        await using (var clearTempCommand = new SqliteCommand(clearTempSql, connection, transaction))
        {
            await clearTempCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertTempSql = @"
INSERT OR IGNORE INTO temp_current_playlist_watch_track (track_source_id)
VALUES (@trackSourceId);";
        await using (var insertTempCommand = new SqliteCommand(insertTempSql, connection, transaction))
        {
            var trackParam = insertTempCommand.Parameters.Add("trackSourceId", SqliteType.Text);
            foreach (var trackSourceId in currentTrackSourceIds)
            {
                if (string.IsNullOrWhiteSpace(trackSourceId))
                {
                    continue;
                }

                trackParam.Value = trackSourceId.Trim();
                await insertTempCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        const string deleteStaleSql = @"
DELETE FROM playlist_watch_track
WHERE source = @source
  AND source_id = @sourceId
  AND NOT EXISTS (
      SELECT 1
      FROM temp_current_playlist_watch_track current_track
      WHERE current_track.track_source_id = playlist_watch_track.track_source_id
  );";
        await using var deleteStaleCommand = new SqliteCommand(deleteStaleSql, connection, transaction);
        deleteStaleCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        deleteStaleCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        var deleted = await deleteStaleCommand.ExecuteNonQueryAsync(cancellationToken);

        const string deleteStaleClaimsSql = @"
DELETE FROM playlist_watch_download_claim
WHERE source = @source
  AND source_id = @sourceId
  AND NOT EXISTS (
      SELECT 1
      FROM temp_current_playlist_watch_track current_track
      WHERE current_track.track_source_id = playlist_watch_download_claim.track_source_id
  );";
        await using var deleteStaleClaimsCommand = new SqliteCommand(deleteStaleClaimsSql, connection, transaction);
        deleteStaleClaimsCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        deleteStaleClaimsCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await deleteStaleClaimsCommand.ExecuteNonQueryAsync(cancellationToken);

        const string deleteStaleMembershipSql = @"
DELETE FROM playlist_watch_target_membership
WHERE source = @source
  AND source_id = @sourceId
  AND NOT EXISTS (
      SELECT 1 FROM temp_current_playlist_watch_track current_track
      WHERE current_track.track_source_id = playlist_watch_target_membership.track_source_id
  );";
        await using var deleteStaleMembershipCommand = new SqliteCommand(deleteStaleMembershipSql, connection, transaction);
        deleteStaleMembershipCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        deleteStaleMembershipCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await deleteStaleMembershipCommand.ExecuteNonQueryAsync(cancellationToken);

        const string deleteStaleMissingSql = @"
DELETE FROM playlist_watch_missing_track
WHERE source = @source
  AND source_id = @sourceId
  AND NOT EXISTS (
      SELECT 1 FROM temp_current_playlist_watch_track current_track
      WHERE current_track.track_source_id = playlist_watch_missing_track.track_source_id
  );";
        await using var deleteStaleMissingCommand = new SqliteCommand(deleteStaleMissingSql, connection, transaction);
        deleteStaleMissingCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        deleteStaleMissingCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await deleteStaleMissingCommand.ExecuteNonQueryAsync(cancellationToken);

        const string deleteStaleJobsSql = @"
DELETE FROM watchlist_sync_job
WHERE source = @source
  AND playlist_id = @sourceId
  AND track_id <> 'playlist'
  AND NOT EXISTS (
      SELECT 1 FROM temp_current_playlist_watch_track current_track
      WHERE current_track.track_source_id = watchlist_sync_job.track_id
  );";
        await using var deleteStaleJobsCommand = new SqliteCommand(deleteStaleJobsSql, connection, transaction);
        deleteStaleJobsCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        deleteStaleJobsCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        await deleteStaleJobsCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<bool> UpdatePlaylistWatchTrackStatusAsync(
        string source,
        string sourceId,
        string trackSourceId,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string insertSql = @"
INSERT OR IGNORE INTO playlist_watch_track (source, source_id, track_source_id, status, updated_at)
VALUES (@source, @sourceId, @trackSourceId, @status, CURRENT_TIMESTAMP);";
        await using (var insertCommand = new SqliteCommand(insertSql, connection))
        {
            insertCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            insertCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            insertCommand.Parameters.AddWithValue("trackSourceId", trackSourceId);
            insertCommand.Parameters.AddWithValue("status", status);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateSql = @"
UPDATE playlist_watch_track
SET status = CASE
        WHEN local_track_id IS NOT NULL
             AND lower(COALESCE(identity_status, '')) <> 'review'
             AND lower(@status) <> 'review'
          THEN 'completed'
        ELSE @status
    END,
    updated_at = CURRENT_TIMESTAMP,
    unavailable_reason = CASE WHEN @status = 'unavailable' THEN unavailable_reason ELSE NULL END,
    unavailable_since_utc = CASE WHEN @status = 'unavailable' THEN unavailable_since_utc ELSE NULL END,
    unavailable_last_checked_utc = CASE WHEN @status = 'unavailable' THEN unavailable_last_checked_utc ELSE NULL END,
    unavailable_next_retry_utc = CASE WHEN @status = 'unavailable' THEN unavailable_next_retry_utc ELSE NULL END,
    unavailable_settings_fingerprint = CASE WHEN @status = 'unavailable' THEN unavailable_settings_fingerprint ELSE NULL END
WHERE source = @source AND source_id = @sourceId AND track_source_id = @trackSourceId;";
        await using var updateCommand = new SqliteCommand(updateSql, connection);
        updateCommand.Parameters.AddWithValue("status", status);
        updateCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        updateCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        updateCommand.Parameters.AddWithValue("trackSourceId", trackSourceId);
        var updated = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<bool> MarkPlaylistWatchTrackUnavailableAsync(
        string source,
        string sourceId,
        string trackSourceId,
        string? isrc,
        string reason,
        string settingsFingerprint,
        DateTimeOffset nextRecheckUtc,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var normalizedIsrc = string.IsNullOrWhiteSpace(isrc) ? string.Empty : isrc.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string insertSql = @"
INSERT OR IGNORE INTO playlist_watch_track (
    source,
    source_id,
    track_source_id,
    isrc,
    status,
    updated_at,
    unavailable_reason,
    unavailable_since_utc,
    unavailable_last_checked_utc,
    unavailable_next_retry_utc,
    unavailable_settings_fingerprint)
VALUES (
    @source,
    @sourceId,
    @trackSourceId,
    @isrc,
    'unavailable',
    @now,
    @reason,
    @now,
    @now,
    @nextRecheckUtc,
    @settingsFingerprint);";
        await using (var insertCommand = new SqliteCommand(insertSql, connection))
        {
            insertCommand.Parameters.AddWithValue(SourceField, normalizedSource);
            insertCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            insertCommand.Parameters.AddWithValue("trackSourceId", trackSourceId);
            insertCommand.Parameters.AddWithValue("isrc", (object?)normalizedIsrc ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("reason", reason);
            insertCommand.Parameters.AddWithValue("settingsFingerprint", settingsFingerprint);
            insertCommand.Parameters.AddWithValue("now", now.ToString("O", CultureInfo.InvariantCulture));
            insertCommand.Parameters.AddWithValue("nextRecheckUtc", nextRecheckUtc.ToString("O", CultureInfo.InvariantCulture));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateSql = @"
UPDATE playlist_watch_track
SET status = 'unavailable',
    updated_at = @now,
    unavailable_reason = @reason,
    unavailable_since_utc = COALESCE(unavailable_since_utc, @now),
    unavailable_last_checked_utc = @now,
    unavailable_next_retry_utc = @nextRecheckUtc,
    unavailable_settings_fingerprint = @settingsFingerprint
WHERE source = @source AND source_id = @sourceId AND track_source_id = @trackSourceId;";
        await using var updateCommand = new SqliteCommand(updateSql, connection);
        updateCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        updateCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        updateCommand.Parameters.AddWithValue("trackSourceId", trackSourceId);
        updateCommand.Parameters.AddWithValue("reason", reason);
        updateCommand.Parameters.AddWithValue("settingsFingerprint", settingsFingerprint);
        updateCommand.Parameters.AddWithValue("now", now.ToString("O", CultureInfo.InvariantCulture));
        updateCommand.Parameters.AddWithValue("nextRecheckUtc", nextRecheckUtc.ToString("O", CultureInfo.InvariantCulture));
        var updated = await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        const string updateGlobalSql = @"
UPDATE playlist_watch_track
SET status = 'unavailable',
    updated_at = @now,
    unavailable_reason = @reason,
    unavailable_since_utc = COALESCE(unavailable_since_utc, @now),
    unavailable_last_checked_utc = @now,
    unavailable_next_retry_utc = @nextRecheckUtc,
    unavailable_settings_fingerprint = @settingsFingerprint
WHERE source = @source
  AND (
      track_source_id = @trackSourceId
      OR (@isrc <> '' AND isrc = @isrc)
  );";
        await using var updateGlobalCommand = new SqliteCommand(updateGlobalSql, connection);
        updateGlobalCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        updateGlobalCommand.Parameters.AddWithValue("trackSourceId", trackSourceId);
        updateGlobalCommand.Parameters.AddWithValue("isrc", normalizedIsrc);
        updateGlobalCommand.Parameters.AddWithValue("reason", reason);
        updateGlobalCommand.Parameters.AddWithValue("settingsFingerprint", settingsFingerprint);
        updateGlobalCommand.Parameters.AddWithValue("now", now.ToString("O", CultureInfo.InvariantCulture));
        updateGlobalCommand.Parameters.AddWithValue("nextRecheckUtc", nextRecheckUtc.ToString("O", CultureInfo.InvariantCulture));
        var updatedGlobal = await updateGlobalCommand.ExecuteNonQueryAsync(cancellationToken);
        return updated > 0 || updatedGlobal > 0;
    }

    public async Task<int> ClearPlaylistWatchUnavailableStatusesWithDifferentFingerprintAsync(
        string source,
        string sourceId,
        string currentFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId)
            || string.IsNullOrWhiteSpace(currentFingerprint))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE playlist_watch_track
SET status='pending',
    unavailable_reason=NULL,
    unavailable_since_utc=NULL,
    unavailable_last_checked_utc=NULL,
    unavailable_next_retry_utc=NULL,
    unavailable_settings_fingerprint=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE source=@source
  AND source_id=@sourceId
  AND lower(status)='unavailable'
  AND COALESCE(unavailable_settings_fingerprint, '') <> @currentFingerprint;", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("currentFingerprint", currentFingerprint);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertPlaylistWatchDownloadClaimsAsync(
        string source,
        string sourceId,
        string trackSourceId,
        IReadOnlyCollection<string> queueUuids,
        long? destinationFolderId,
        CancellationToken cancellationToken = default)
    {
        if (queueUuids.Count == 0
            || string.IsNullOrWhiteSpace(trackSourceId)
            || !TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO playlist_watch_download_claim (source, source_id, track_source_id, queue_uuid, destination_folder_id, status)
SELECT @source, @sourceId, @trackSourceId, @queueUuid, @destinationFolderId, 'pending'
WHERE NOT EXISTS (
    SELECT 1
    FROM playlist_watch_download_claim
    WHERE source = @source
      AND source_id = @sourceId
      AND track_source_id = @trackSourceId
      AND queue_uuid <> @queueUuid
      AND lower(status) IN ('pending', 'completed', 'complete')
)
ON CONFLICT(source, source_id, track_source_id, queue_uuid) DO UPDATE SET
    destination_folder_id = COALESCE(excluded.destination_folder_id, playlist_watch_download_claim.destination_folder_id),
    status = CASE
        WHEN lower(playlist_watch_download_claim.status) IN ('completed', 'complete') THEN playlist_watch_download_claim.status
        ELSE 'pending'
    END,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", trackSourceId.Trim());
        var queueUuidParam = command.Parameters.Add("queueUuid", SqliteType.Text);
        command.Parameters.AddWithValue("destinationFolderId", (object?)destinationFolderId ?? DBNull.Value);

        foreach (var queueUuid in queueUuids.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            queueUuidParam.Value = queueUuid.Trim();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PlaylistWatchDownloadClaimDto>> GetPlaylistWatchDownloadClaimsAsync(
        string queueUuid,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return Array.Empty<PlaylistWatchDownloadClaimDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT source, source_id, track_source_id, queue_uuid, destination_folder_id, status, updated_at
FROM playlist_watch_download_claim
WHERE queue_uuid = @queueUuid
  AND (@status IS NULL OR lower(status) = lower(@status))
ORDER BY source, source_id, track_source_id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid.Trim());
        command.Parameters.AddWithValue("status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var claims = new List<PlaylistWatchDownloadClaimDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(await ReadPlaylistWatchDownloadClaimAsync(reader, cancellationToken));
        }

        return claims;
    }

    public async Task<IReadOnlyList<PlaylistWatchDownloadClaimDto>> GetPlaylistWatchDownloadClaimsForPlaylistAsync(
        string source,
        string sourceId,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return Array.Empty<PlaylistWatchDownloadClaimDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT source, source_id, track_source_id, queue_uuid, destination_folder_id, status, updated_at
FROM playlist_watch_download_claim
WHERE source = @source
  AND source_id = @sourceId
  AND (@status IS NULL OR lower(status) = lower(@status))
ORDER BY track_source_id, queue_uuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var claims = new List<PlaylistWatchDownloadClaimDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(await ReadPlaylistWatchDownloadClaimAsync(reader, cancellationToken));
        }

        return claims;
    }

    public async Task<IReadOnlyList<PlaylistWatchDownloadClaimDto>> GetAllPlaylistWatchDownloadClaimsAsync(
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT source, source_id, track_source_id, queue_uuid, destination_folder_id, status, updated_at
FROM playlist_watch_download_claim
WHERE (@status IS NULL OR lower(status) = lower(@status))
ORDER BY source, source_id, track_source_id, queue_uuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var claims = new List<PlaylistWatchDownloadClaimDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(await ReadPlaylistWatchDownloadClaimAsync(reader, cancellationToken));
        }

        return claims;
    }

    private static async Task<PlaylistWatchDownloadClaimDto> ReadPlaylistWatchDownloadClaimAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        return new PlaylistWatchDownloadClaimDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt64(4),
            reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken)
                ? DateTimeOffset.MinValue
                : ParseDateTimeOffsetInvariant(reader.GetString(6)));
    }

    public async Task<int> UpdatePlaylistWatchDownloadClaimStatusAsync(
        string queueUuid,
        string source,
        string sourceId,
        string trackSourceId,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid)
            || string.IsNullOrWhiteSpace(status)
            || !TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId)
            || string.IsNullOrWhiteSpace(trackSourceId))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE playlist_watch_download_claim
SET status = @status,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND source = @source
  AND source_id = @sourceId
  AND track_source_id = @trackSourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid.Trim());
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("trackSourceId", trackSourceId.Trim());
        command.Parameters.AddWithValue("status", status.Trim().ToLowerInvariant());
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        var normalizedStatus = status.Trim().ToLowerInvariant();
        if (normalizedStatus is "completed" or "complete")
        {
            await ResolvePlaylistWatchMissingTrackAsync(normalizedSource, normalizedSourceId, trackSourceId, cancellationToken);
        }
        else if (normalizedStatus is "failed" or "cancelled" or "canceled")
        {
            await MarkPlaylistWatchMissingTracksByQueueStatusAsync(
                queueUuid,
                "failed",
                normalizedStatus,
                retryAfterUtc: null,
                cancellationToken);
        }

        return updated;
    }

    public async Task<int> DeleteTerminalPlaylistWatchDownloadClaimsOlderThanAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
DELETE FROM playlist_watch_download_claim
WHERE lower(status) IN ('completed', 'complete', 'failed', 'cancelled', 'canceled')
  AND datetime(updated_at) < datetime(@cutoffUtc);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("cutoffUtc", cutoffUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> EnqueueWatchlistReconciliationRequestAsync(
        string kind,
        string? source,
        string? identifier,
        CancellationToken cancellationToken = default)
    {
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? string.Empty : kind.Trim().ToLowerInvariant();
        if (normalizedKind is not "all" and not "playlist" and not "artist")
        {
            return false;
        }

        var normalizedSource = normalizedKind == "playlist"
            ? NormalizePlaylistWatchSource(source ?? string.Empty)
            : normalizedKind == "artist" ? "artist" : string.Empty;
        var normalizedIdentifier = normalizedKind == "all"
            ? string.Empty
            : identifier?.Trim() ?? string.Empty;
        if (normalizedKind != "all" && (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(normalizedIdentifier)))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        const string existsSql = @"
SELECT 1 FROM watchlist_reconciliation_request
WHERE kind=@kind AND source=@source AND identifier=@identifier LIMIT 1;";
        await using var existsCommand = new SqliteCommand(existsSql, connection, transaction);
        existsCommand.Parameters.AddWithValue("kind", normalizedKind);
        existsCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        existsCommand.Parameters.AddWithValue("identifier", normalizedIdentifier);
        var alreadyExists = await existsCommand.ExecuteScalarAsync(cancellationToken) is not null;

        const string sql = @"
INSERT INTO watchlist_reconciliation_request(kind,source,identifier,created_at,updated_at)
VALUES(@kind,@source,@identifier,@nowUtc,@nowUtc)
ON CONFLICT(kind,source,identifier) DO UPDATE SET
    updated_at=excluded.updated_at,
    next_attempt_utc=excluded.updated_at,
    status=CASE WHEN lower(watchlist_reconciliation_request.status)='processing' THEN 'processing' ELSE 'pending' END,
    last_error=NULL;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("kind", normalizedKind);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("identifier", normalizedIdentifier);
        command.Parameters.AddWithValue("nowUtc", nowUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return !alreadyExists;
    }

    public async Task<IReadOnlyList<WatchlistReconciliationRequestDto>> GetWatchlistReconciliationRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT kind,source,identifier,created_at,updated_at,status,attempt_count,next_attempt_utc,lease_owner,lease_until_utc,last_error
FROM watchlist_reconciliation_request
ORDER BY CASE kind WHEN 'all' THEN 0 WHEN 'playlist' THEN 1 ELSE 2 END, updated_at, source, identifier;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<WatchlistReconciliationRequestDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new WatchlistReconciliationRequestDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseDateTimeOffsetInvariant(reader.GetString(3)),
                ParseDateTimeOffsetInvariant(reader.GetString(4)),
                await reader.IsDBNullAsync(5, cancellationToken) ? "pending" : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? 0 : reader.GetInt32(6),
                await reader.IsDBNullAsync(7, cancellationToken) || string.IsNullOrWhiteSpace(reader.GetString(7))
                    ? null
                    : ParseDateTimeOffsetInvariant(reader.GetString(7)),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
                await reader.IsDBNullAsync(9, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(9)),
                await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10)));
        }
        return requests;
    }

    public async Task<IReadOnlyList<WatchlistReconciliationRequestDto>> ClaimDueWatchlistReconciliationRequestsAsync(
        int limit,
        TimeSpan lease,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_reconciliation_request
SET status='processing',
    lease_owner=@leaseOwner,
    lease_until_utc=@leaseUntilUtc
WHERE rowid IN (
    SELECT rowid
    FROM watchlist_reconciliation_request
    WHERE (lower(status) IN ('pending','retry')
           AND (next_attempt_utc='' OR datetime(next_attempt_utc) <= datetime('now')))
       OR (lower(status)='processing' AND datetime(lease_until_utc) <= datetime('now'))
    ORDER BY CASE kind WHEN 'all' THEN 0 WHEN 'playlist' THEN 1 ELSE 2 END, updated_at
    LIMIT @limit
)
RETURNING kind,source,identifier,created_at,updated_at,status,attempt_count,next_attempt_utc,lease_owner,lease_until_utc,last_error;", connection, transaction);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
        command.Parameters.AddWithValue("leaseUntilUtc", (DateTimeOffset.UtcNow + lease).ToString("O"));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<WatchlistReconciliationRequestDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new WatchlistReconciliationRequestDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseDateTimeOffsetInvariant(reader.GetString(3)),
                ParseDateTimeOffsetInvariant(reader.GetString(4)),
                reader.GetString(5),
                reader.GetInt32(6),
                await reader.IsDBNullAsync(7, cancellationToken) || string.IsNullOrWhiteSpace(reader.GetString(7)) ? null : ParseDateTimeOffsetInvariant(reader.GetString(7)),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
                await reader.IsDBNullAsync(9, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(9)),
                await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10)));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return requests;
    }

    public async Task<int> CompleteClaimedWatchlistReconciliationRequestsAsync(
        IReadOnlyCollection<WatchlistReconciliationRequestDto> requests,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0 || string.IsNullOrWhiteSpace(leaseOwner))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var completed = 0;
        foreach (var request in requests)
        {
            await using var delete = new SqliteCommand(@"
DELETE FROM watchlist_reconciliation_request
WHERE kind=@kind AND source=@source AND identifier=@identifier
  AND updated_at=@updatedAt AND lease_owner=@leaseOwner;", connection, transaction);
            delete.Parameters.AddWithValue("kind", request.Kind);
            delete.Parameters.AddWithValue(SourceField, request.Source);
            delete.Parameters.AddWithValue("identifier", request.Identifier);
            delete.Parameters.AddWithValue("updatedAt", request.UpdatedAt.ToString("O"));
            delete.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
            completed += await delete.ExecuteNonQueryAsync(cancellationToken);

            await using var releaseRefreshed = new SqliteCommand(@"
UPDATE watchlist_reconciliation_request
SET status='pending', lease_owner=NULL, lease_until_utc=NULL, next_attempt_utc=CURRENT_TIMESTAMP
WHERE kind=@kind AND source=@source AND identifier=@identifier AND lease_owner=@leaseOwner;", connection, transaction);
            releaseRefreshed.Parameters.AddWithValue("kind", request.Kind);
            releaseRefreshed.Parameters.AddWithValue(SourceField, request.Source);
            releaseRefreshed.Parameters.AddWithValue("identifier", request.Identifier);
            releaseRefreshed.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
            await releaseRefreshed.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return completed;
    }

    public async Task<int> RenewClaimedWatchlistReconciliationRequestsAsync(
        string leaseOwner,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
        {
            return 0;
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_reconciliation_request
SET lease_until_utc=@leaseUntilUtc
WHERE lower(status)='processing' AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("leaseUntilUtc", (DateTimeOffset.UtcNow + lease).ToString("O"));
        command.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RetryClaimedWatchlistReconciliationRequestsAsync(
        IReadOnlyCollection<WatchlistReconciliationRequestDto> requests,
        string leaseOwner,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0 || string.IsNullOrWhiteSpace(leaseOwner))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var updated = 0;
        foreach (var request in requests)
        {
            var attempt = request.AttemptCount + 1;
            var nextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(600, 15 * Math.Pow(2, Math.Min(attempt - 1, 6))));
            await using var command = new SqliteCommand(@"
UPDATE watchlist_reconciliation_request
SET status='retry', attempt_count=@attempt, next_attempt_utc=@nextAttemptUtc,
    lease_owner=NULL, lease_until_utc=NULL, last_error=@error
WHERE kind=@kind AND source=@source AND identifier=@identifier AND lease_owner=@leaseOwner;", connection, transaction);
            command.Parameters.AddWithValue("attempt", attempt);
            command.Parameters.AddWithValue("nextAttemptUtc", nextAttemptUtc.ToString("O"));
            command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("kind", request.Kind);
            command.Parameters.AddWithValue(SourceField, request.Source);
            command.Parameters.AddWithValue("identifier", request.Identifier);
            command.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<int> GetWatchlistReconciliationRequestCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand("SELECT COUNT(*) FROM watchlist_reconciliation_request;", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public Task<bool> HasWatchlistReconciliationRequestAsync(
        string kind,
        string source,
        string identifier,
        CancellationToken cancellationToken = default)
        => HasWatchlistReconciliationRequestAsync(kind, source, identifier, ignoreLeaseOwner: null, cancellationToken);

    public async Task<bool> HasWatchlistReconciliationRequestAsync(
        string kind,
        string source,
        string identifier,
        string? ignoreLeaseOwner,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT 1
FROM watchlist_reconciliation_request
WHERE kind=@kind AND source=@source AND identifier=@identifier
  AND lower(status) IN ('pending', 'retry', 'processing')
  AND NOT (
        @ignoreLeaseOwner IS NOT NULL
        AND lease_owner=@ignoreLeaseOwner
        AND lower(status)='processing'
        AND datetime(lease_until_utc) > datetime('now')
      )
LIMIT 1;", connection);
        command.Parameters.AddWithValue("kind", kind.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue(SourceField, NormalizePlaylistWatchSource(source));
        command.Parameters.AddWithValue("identifier", identifier.Trim());
        command.Parameters.AddWithValue(
            "ignoreLeaseOwner",
            string.IsNullOrWhiteSpace(ignoreLeaseOwner) ? DBNull.Value : ignoreLeaseOwner.Trim());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task UpsertWatchlistFinalizationOutboxAsync(
        string queueUuid,
        string? payloadJson,
        IReadOnlyCollection<string> finalFilePaths,
        CancellationToken cancellationToken = default)
    {
        var normalizedQueueUuid = queueUuid?.Trim();
        var paths = finalFilePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(normalizedQueueUuid) || paths.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
INSERT INTO watchlist_finalization_outbox (queue_uuid,payload_json,final_file_paths_json,status,next_attempt_utc)
VALUES (@queueUuid,@payloadJson,@paths,'pending',CURRENT_TIMESTAMP)
ON CONFLICT(queue_uuid) DO UPDATE SET
    payload_json=COALESCE(excluded.payload_json,watchlist_finalization_outbox.payload_json),
    final_file_paths_json=excluded.final_file_paths_json,
    status=CASE WHEN lower(watchlist_finalization_outbox.status)='completed' THEN 'completed' ELSE 'pending' END,
    next_attempt_utc=CURRENT_TIMESTAMP,
    lease_owner=NULL,
    lease_until_utc=NULL,
    last_error=NULL,
    updated_at=CURRENT_TIMESTAMP;", connection);
        command.Parameters.AddWithValue("queueUuid", normalizedQueueUuid);
        command.Parameters.AddWithValue("payloadJson", string.IsNullOrWhiteSpace(payloadJson) ? DBNull.Value : payloadJson);
        command.Parameters.AddWithValue("paths", JsonSerializer.Serialize(paths));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WatchlistFinalizationOutboxDto>> ClaimDueWatchlistFinalizationOutboxAsync(
        int limit,
        TimeSpan lease,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_finalization_outbox
SET status='processing', lease_owner=@leaseOwner, lease_until_utc=@leaseUntilUtc, updated_at=CURRENT_TIMESTAMP
WHERE id IN (
    SELECT id FROM watchlist_finalization_outbox
    WHERE (lower(status) IN ('pending','retry') AND datetime(next_attempt_utc) <= datetime('now'))
       OR (lower(status)='processing' AND datetime(lease_until_utc) <= datetime('now'))
    ORDER BY next_attempt_utc,id LIMIT @limit
)
RETURNING id,queue_uuid,payload_json,final_file_paths_json,status,attempt_count,next_attempt_utc,lease_owner,lease_until_utc,last_error,updated_at;", connection, transaction);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
        command.Parameters.AddWithValue("leaseUntilUtc", (DateTimeOffset.UtcNow + lease).ToString("O"));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<WatchlistFinalizationOutboxDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWatchlistFinalizationOutbox(reader));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    private static WatchlistFinalizationOutboxDto ReadWatchlistFinalizationOutbox(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
            reader.GetString(4),
            reader.GetInt32(5),
            ParseDateTimeOffsetInvariant(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : ParseDateTimeOffsetInvariant(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            ParseDateTimeOffsetInvariant(reader.GetString(10)));

    public async Task<bool> CompleteWatchlistFinalizationOutboxAsync(
        long id,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_finalization_outbox
SET status='completed',lease_owner=NULL,lease_until_utc=NULL,last_error=NULL,updated_at=CURRENT_TIMESTAMP
WHERE id=@id AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RetryWatchlistFinalizationOutboxAsync(
        long id,
        string leaseOwner,
        int attemptCount,
        DateTimeOffset nextAttemptUtc,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_finalization_outbox
SET status='retry',attempt_count=@attemptCount,next_attempt_utc=@nextAttemptUtc,
    lease_owner=NULL,lease_until_utc=NULL,last_error=@error,updated_at=CURRENT_TIMESTAMP
WHERE id=@id AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("attemptCount", Math.Max(0, attemptCount));
        command.Parameters.AddWithValue("nextAttemptUtc", nextAttemptUtc.ToString("O"));
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> DeleteCompletedWatchlistFinalizationOutboxOlderThanAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
DELETE FROM watchlist_finalization_outbox
WHERE lower(status)='completed' AND datetime(updated_at) < datetime(@cutoffUtc);", connection);
        command.Parameters.AddWithValue("cutoffUtc", cutoffUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueMediaServerRefreshAsync(
        long destinationFolderId,
        string targetService,
        IReadOnlyCollection<string> changedFilePaths,
        IReadOnlyCollection<long> requestedTrackIds,
        TimeSpan? coalescingDelay = null,
        CancellationToken cancellationToken = default)
    {
        var service = targetService?.Trim().ToLowerInvariant();
        var paths = changedFilePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var trackIds = requestedTrackIds.Where(static id => id > 0).Distinct().ToList();
        if (destinationFolderId <= 0
            || service is not ("plex" or "jellyfin" or "navidrome")
            || paths.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var read = new SqliteCommand(@"
SELECT changed_file_paths_json,requested_track_ids_json
FROM media_server_refresh_outbox
WHERE destination_folder_id=@destinationFolderId AND target_service=@targetService;", connection, transaction))
        {
            read.Parameters.AddWithValue("destinationFolderId", destinationFolderId);
            read.Parameters.AddWithValue("targetService", service);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    paths.AddRange(JsonSerializer.Deserialize<List<string>>(reader.GetString(0)) ?? []);
                    paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    trackIds.AddRange(JsonSerializer.Deserialize<List<long>>(reader.GetString(1)) ?? []);
                    trackIds = trackIds.Where(static id => id > 0).Distinct().ToList();
                }
                catch (JsonException)
                {
                    // Replace malformed persisted path data with the current verified paths.
                }
            }
        }

        await using var command = new SqliteCommand(@"
INSERT INTO media_server_refresh_outbox
    (destination_folder_id,target_service,changed_file_paths_json,requested_track_ids_json,status,attempt_count,next_attempt_utc)
VALUES (@destinationFolderId,@targetService,@paths,@trackIds,'pending',0,@nextAttemptUtc)
ON CONFLICT(destination_folder_id,target_service) DO UPDATE SET
    changed_file_paths_json=excluded.changed_file_paths_json,
    requested_track_ids_json=excluded.requested_track_ids_json,
    status='pending',
    attempt_count=0,
    next_attempt_utc=excluded.next_attempt_utc,
    lease_owner=NULL,
    lease_until_utc=NULL,
    last_error=NULL,
    updated_at=CURRENT_TIMESTAMP;", connection, transaction);
        command.Parameters.AddWithValue("destinationFolderId", destinationFolderId);
        command.Parameters.AddWithValue("targetService", service);
        command.Parameters.AddWithValue("paths", JsonSerializer.Serialize(paths));
        command.Parameters.AddWithValue("trackIds", JsonSerializer.Serialize(trackIds));
        command.Parameters.AddWithValue(
            "nextAttemptUtc",
            DateTimeOffset.UtcNow.Add(coalescingDelay ?? TimeSpan.FromSeconds(5)).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaServerRefreshOutboxDto>> ClaimDueMediaServerRefreshesAsync(
        int limit,
        TimeSpan lease,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE media_server_refresh_outbox
SET status='processing',lease_owner=@leaseOwner,lease_until_utc=@leaseUntilUtc,updated_at=CURRENT_TIMESTAMP
WHERE id IN (
    SELECT id FROM media_server_refresh_outbox
    WHERE (lower(status) IN ('pending','retry') AND datetime(next_attempt_utc) <= datetime('now'))
       OR (lower(status)='processing' AND datetime(lease_until_utc) <= datetime('now'))
    ORDER BY next_attempt_utc,id LIMIT @limit
)
RETURNING id,destination_folder_id,target_service,changed_file_paths_json,requested_track_ids_json,status,attempt_count,
          next_attempt_utc,lease_owner,lease_until_utc,last_error,updated_at;", connection, transaction);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
        command.Parameters.AddWithValue("leaseUntilUtc", (DateTimeOffset.UtcNow + lease).ToString("O"));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 25));
        var rows = new List<MediaServerRefreshOutboxDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MediaServerRefreshOutboxDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
                JsonSerializer.Deserialize<List<long>>(reader.GetString(4)) ?? [],
                reader.GetString(5),
                reader.GetInt32(6),
                ParseDateTimeOffsetInvariant(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : ParseDateTimeOffsetInvariant(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                ParseDateTimeOffsetInvariant(reader.GetString(11))));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<bool> CompleteMediaServerRefreshAsync(
        long id,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE media_server_refresh_outbox
SET status='completed',changed_file_paths_json='[]',requested_track_ids_json='[]',lease_owner=NULL,lease_until_utc=NULL,
    last_error=NULL,updated_at=CURRENT_TIMESTAMP
WHERE id=@id AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RetryMediaServerRefreshAsync(
        long id,
        string leaseOwner,
        int attemptCount,
        DateTimeOffset nextAttemptUtc,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE media_server_refresh_outbox
SET status='retry',attempt_count=@attemptCount,next_attempt_utc=@nextAttemptUtc,
    lease_owner=NULL,lease_until_utc=NULL,last_error=@error,updated_at=CURRENT_TIMESTAMP
WHERE id=@id AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("attemptCount", Math.Max(1, attemptCount));
        command.Parameters.AddWithValue("nextAttemptUtc", nextAttemptUtc.ToString("O"));
        command.Parameters.AddWithValue("error", error);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<(int Pending, int Processing, int Retry)> GetMediaServerRefreshOutboxCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT
    SUM(CASE WHEN lower(status)='pending' THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status)='processing' THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status)='retry' THEN 1 ELSE 0 END)
FROM media_server_refresh_outbox;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0, 0);
        }
        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
    }

    public async Task<bool> HasPendingMediaServerRefreshAsync(
        string targetService,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT 1
FROM media_server_refresh_outbox
WHERE target_service=@targetService AND lower(status) IN ('pending','processing','retry')
LIMIT 1;", connection);
        command.Parameters.AddWithValue("targetService", targetService.Trim().ToLowerInvariant());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task ClearPlaylistWatchTargetSyncStateAsync(
        string source,
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, playlistId, out var normalizedSource, out var normalizedPlaylistId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var jobs = new SqliteCommand(
                         "DELETE FROM watchlist_sync_job WHERE source=@source AND playlist_id=@playlistId;",
                         connection,
                         transaction))
        {
            jobs.Parameters.AddWithValue(SourceField, normalizedSource);
            jobs.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
            await jobs.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var state = new SqliteCommand(
                         "DELETE FROM playlist_watch_target_sync_state WHERE source=@source AND source_id=@playlistId;",
                         connection,
                         transaction))
        {
            state.Parameters.AddWithValue(SourceField, normalizedSource);
            state.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WatchlistSyncJobDto>> EnqueueWatchlistPlaylistSyncJobsAsync(
        string source,
        string playlistId,
        string snapshotId,
        CancellationToken cancellationToken = default)
        => await EnqueueWatchlistPlaylistSyncJobsAsync(source, playlistId, snapshotId, force: false, cancellationToken);

    public async Task<IReadOnlyList<WatchlistSyncJobDto>> EnqueueWatchlistPlaylistSyncJobsAsync(
        string source,
        string playlistId,
        string snapshotId,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, playlistId, out var normalizedSource, out var normalizedPlaylistId))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var skipApplied = force
            ? string.Empty
            : @"
  AND NOT EXISTS (
      SELECT 1 FROM playlist_watch_target_sync_state target
      WHERE target.source=@source AND target.source_id=@playlistId
        AND target.target_service=lower(trim(configured.value))
        AND target.status='applied'
        AND target.applied_snapshot_id=CASE
            WHEN lower(trim(configured.value))='plex' THEN @plexSnapshotId ELSE @snapshotId END)";
        var sql = $@"
INSERT INTO watchlist_sync_job (source, playlist_id, track_id, target_service, status, next_attempt_utc, snapshot_id)
SELECT @source, @playlistId, 'playlist', lower(trim(configured.value)), 'pending', CURRENT_TIMESTAMP,
       CASE WHEN lower(trim(configured.value))='plex' THEN @plexSnapshotId ELSE @snapshotId END
FROM playlist_watch_preferences preference,
     json_each(CASE
         WHEN json_valid(preference.sync_targets_json) AND json_array_length(preference.sync_targets_json) > 0
             THEN preference.sync_targets_json
         ELSE json_array(preference.service)
     END) configured
WHERE preference.source=@source AND preference.source_id=@playlistId
  AND lower(trim(configured.value)) IN ('plex','jellyfin','navidrome')
{skipApplied}
ON CONFLICT(source, playlist_id, track_id, target_service) DO UPDATE SET
 attempt_count=CASE WHEN watchlist_sync_job.snapshot_id=excluded.snapshot_id
                    THEN watchlist_sync_job.attempt_count ELSE 0 END,
 status=CASE WHEN watchlist_sync_job.snapshot_id=excluded.snapshot_id
             THEN watchlist_sync_job.status ELSE 'pending' END,
 lease_owner=CASE WHEN watchlist_sync_job.snapshot_id=excluded.snapshot_id
                  THEN watchlist_sync_job.lease_owner ELSE NULL END,
 lease_until_utc=CASE WHEN watchlist_sync_job.snapshot_id=excluded.snapshot_id
                      THEN watchlist_sync_job.lease_until_utc ELSE NULL END,
 next_attempt_utc=CASE WHEN watchlist_sync_job.snapshot_id=excluded.snapshot_id
                       THEN watchlist_sync_job.next_attempt_utc ELSE CURRENT_TIMESTAMP END,
 last_error=CASE WHEN watchlist_sync_job.snapshot_id=excluded.snapshot_id
                 THEN watchlist_sync_job.last_error ELSE NULL END,
 snapshot_id=excluded.snapshot_id,
 updated_at=CURRENT_TIMESTAMP
RETURNING id,source,playlist_id,track_id,target_service,destination_folder_id,final_file_paths_json,
          attempt_count,next_attempt_utc,queue_uuid,lease_owner,status,last_error,snapshot_id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
        command.Parameters.AddWithValue("snapshotId", snapshotId);
        command.Parameters.AddWithValue("plexSnapshotId", $"{snapshotId}:plex-membership-v2");
        var jobs = new List<WatchlistSyncJobDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(await ReadWatchlistSyncJobAsync(reader, cancellationToken));
        }
        return jobs;
    }

    public async Task<IReadOnlyList<WatchlistSyncJobDto>> EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
        long localTrackId,
        string targetService,
        CancellationToken cancellationToken = default)
        => await EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
            localTrackId,
            targetService,
            currentRevision: null,
            cancellationToken);

    private async Task<IReadOnlyList<WatchlistSyncJobDto>> EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
        long localTrackId,
        string targetService,
        string? currentRevision,
        CancellationToken cancellationToken = default)
    {
        if (localTrackId <= 0 || string.IsNullOrWhiteSpace(targetService))
        {
            return [];
        }

        var normalizedTarget = targetService.Trim().ToLowerInvariant();
        var snapshotId = (currentRevision ?? string.Empty).Trim();
        var plexSnapshotId = string.IsNullOrWhiteSpace(snapshotId)
            ? string.Empty
            : $"{snapshotId}:plex-membership-v2";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO watchlist_sync_job (
    source, playlist_id, track_id, target_service, status, next_attempt_utc, snapshot_id)
SELECT DISTINCT t.source, t.source_id, 'playlist', lower(@targetService),
       'pending', CURRENT_TIMESTAMP, catchup.catchup_snapshot_id
FROM playlist_watch_track t
JOIN playlist_watch_configured_sync_targets c
  ON c.source=t.source AND c.source_id=t.source_id AND c.target=lower(@targetService)
JOIN (
    SELECT t2.source, t2.source_id,
           COALESCE(
               NULLIF(trim(s.applied_snapshot_id), ''),
               NULLIF(trim((
                   SELECT job.snapshot_id
                   FROM watchlist_sync_job job
                   WHERE job.source=t2.source
                     AND job.playlist_id=t2.source_id
                     AND lower(job.target_service)=lower(@targetService)
                     AND lower(job.track_id)='playlist'
                     AND trim(COALESCE(job.snapshot_id, '')) <> ''
                   ORDER BY job.updated_at DESC, job.id DESC
                   LIMIT 1)), ''),
               CASE
                   WHEN lower(@targetService)='plex' AND trim(@plexSnapshotId) <> '' THEN @plexSnapshotId
                   WHEN trim(@snapshotId) <> '' THEN @snapshotId
                   ELSE NULL
               END
           ) AS catchup_snapshot_id
    FROM playlist_watch_track t2
    LEFT JOIN playlist_watch_target_sync_state s
      ON s.source=t2.source AND s.source_id=t2.source_id
     AND lower(s.target_service)=lower(@targetService)
    WHERE t2.local_track_id=@localTrackId
) catchup
  ON catchup.source=t.source AND catchup.source_id=t.source_id
LEFT JOIN playlist_watch_target_sync_state state
  ON state.source=t.source AND state.source_id=t.source_id
 AND lower(state.target_service)=lower(@targetService)
WHERE t.local_track_id=@localTrackId
  AND catchup.catchup_snapshot_id IS NOT NULL
  AND trim(catchup.catchup_snapshot_id) <> ''
  AND NOT EXISTS (
        SELECT 1 FROM playlist_watch_target_membership m
        WHERE m.source=t.source AND m.source_id=t.source_id
          AND m.track_source_id=t.track_source_id
          AND lower(m.target_service)=lower(@targetService)
          AND lower(m.sync_status)='playlist_synced')
ON CONFLICT(source, playlist_id, track_id, target_service) DO UPDATE SET
    status=CASE WHEN lower(watchlist_sync_job.status)='processing'
                THEN watchlist_sync_job.status ELSE 'pending' END,
    next_attempt_utc=CASE WHEN lower(watchlist_sync_job.status)='processing'
                          THEN watchlist_sync_job.next_attempt_utc
                          ELSE CURRENT_TIMESTAMP END,
    snapshot_id=COALESCE(excluded.snapshot_id, watchlist_sync_job.snapshot_id),
    updated_at=CURRENT_TIMESTAMP
RETURNING id,source,playlist_id,track_id,target_service,destination_folder_id,final_file_paths_json,
          attempt_count,next_attempt_utc,queue_uuid,lease_owner,status,last_error,snapshot_id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("localTrackId", localTrackId);
        command.Parameters.AddWithValue("targetService", normalizedTarget);
        command.Parameters.AddWithValue("snapshotId", snapshotId);
        command.Parameters.AddWithValue("plexSnapshotId", plexSnapshotId);
        var jobs = new List<WatchlistSyncJobDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(await ReadWatchlistSyncJobAsync(reader, cancellationToken));
        }

        return jobs;
    }


    public sealed record WatchlistRuntimeCleanupResult(
        int ReconciliationRequestsDeleted,
        int SyncJobsDeleted,
        int FinalizationOutboxDeleted,
        int ClaimsDeleted,
        int SchedulerRowsDeleted,
        int SourceCircuitsDeleted,
        int PlaylistStatesDeleted,
        int ArtistStatesDeleted,
        int TargetCircuitsDeleted = 0);

    public async Task<WatchlistRuntimeCleanupResult> ClearWatchlistRuntimeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var reconciliationRequestsDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM watchlist_reconciliation_request;",
            cancellationToken);
        var syncJobsDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM watchlist_sync_job;",
            cancellationToken);
        await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM playlist_watch_target_sync_state;",
            cancellationToken);
        var finalizationOutboxDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM watchlist_finalization_outbox;",
            cancellationToken);
        var claimsDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM playlist_watch_download_claim;",
            cancellationToken);
        var schedulerRowsDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM watchlist_scheduler_state;",
            cancellationToken);
        var sourceCircuitsDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM watchlist_source_circuit_state;",
            cancellationToken);
        var targetCircuitsDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM watchlist_target_circuit_state;",
            cancellationToken);
        var playlistStatesDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM playlist_watch_state;",
            cancellationToken);
        var artistStatesDeleted = await ExecuteRuntimeCleanupAsync(
            connection,
            transaction,
            "DELETE FROM artist_watch_state;",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new WatchlistRuntimeCleanupResult(
            reconciliationRequestsDeleted,
            syncJobsDeleted,
            finalizationOutboxDeleted,
            claimsDeleted,
            schedulerRowsDeleted,
            sourceCircuitsDeleted,
            playlistStatesDeleted,
            artistStatesDeleted,
            targetCircuitsDeleted);
    }

    private static async Task<int> ExecuteRuntimeCleanupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<WatchlistSyncJobDto>> ClaimDueWatchlistSyncJobsAsync(
        int limit,
        TimeSpan lease,
        string leaseOwner,
        IReadOnlyCollection<long>? excludedJobIds = null,
        CancellationToken cancellationToken = default)
        => ClaimDueWatchlistSyncJobsAsync(
            limit,
            lease,
            leaseOwner,
            source: null,
            playlistId: null,
            WatchlistSyncJobKind.All,
            excludedJobIds,
            cancellationToken);

    public async Task<IReadOnlyList<WatchlistSyncJobDto>> ClaimDueWatchlistSyncJobsAsync(
        int limit,
        TimeSpan lease,
        string leaseOwner,
        string? source,
        string? playlistId,
        WatchlistSyncJobKind kind,
        IReadOnlyCollection<long>? excludedJobIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : NormalizePlaylistWatchSource(source);
        var normalizedPlaylistId = string.IsNullOrWhiteSpace(playlistId) ? null : NormalizePlaylistWatchSourceId(playlistId);
        var kindFilter = kind switch
        {
            WatchlistSyncJobKind.Membership => "membership",
            WatchlistSyncJobKind.Artwork => "artwork",
            _ => "all"
        };
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
UPDATE watchlist_sync_job
SET status='processing',
    lease_owner=@leaseOwner,
    lease_until_utc=@leaseUntilUtc,
    last_error=NULL,
    updated_at=CURRENT_TIMESTAMP
WHERE id IN (
    SELECT ranked.id
    FROM (
        SELECT job.id,
               CASE lower(job.target_service)
                   WHEN 'plex' THEN 0
                   WHEN 'jellyfin' THEN 1
                   WHEN 'navidrome' THEN 2
                   ELSE 3
               END AS target_order,
               CASE WHEN playlist.sync_priority IS NULL OR playlist.sync_priority <= 0 THEN 1 ELSE 0 END AS missing_priority,
               playlist.sync_priority AS playlist_priority,
               job.attempt_count,
               job.next_attempt_utc
        FROM watchlist_sync_job job
        LEFT JOIN playlist_watchlist playlist
          ON playlist.source = job.source
         AND playlist.source_id = job.playlist_id
        WHERE datetime(job.next_attempt_utc) <= datetime('now')
          AND (lower(job.status) IN ('pending', 'retry')
               OR (lower(job.status) = 'processing' AND datetime(job.lease_until_utc) <= datetime('now')))
          AND job.id NOT IN (SELECT CAST(value AS INTEGER) FROM json_each(@excludedJobIdsJson))
          AND (@source IS NULL OR job.source=@source)
          AND (@playlistId IS NULL OR job.playlist_id=@playlistId)
          AND (
                @kind='all'
                OR (@kind='membership' AND lower(job.track_id)='playlist')
                OR (@kind='artwork' AND lower(job.track_id) LIKE 'artwork:%')
              )
    ) ranked
    ORDER BY ranked.missing_priority,
             ranked.playlist_priority ASC,
             ranked.next_attempt_utc,
             ranked.attempt_count ASC,
             ranked.target_order,
             ranked.id
    LIMIT @limit
)
RETURNING id,source,playlist_id,track_id,target_service,destination_folder_id,final_file_paths_json,
          attempt_count,next_attempt_utc,queue_uuid,lease_owner,status,last_error,snapshot_id;";
        await using var command = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
        command.Parameters.AddWithValue("limit", 1);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner.Trim());
        command.Parameters.AddWithValue("leaseUntilUtc", (DateTimeOffset.UtcNow + lease).ToString("O"));
        command.Parameters.AddWithValue("source", (object?)normalizedSource ?? DBNull.Value);
        command.Parameters.AddWithValue("playlistId", (object?)normalizedPlaylistId ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", kindFilter);
        command.Parameters.AddWithValue(
            "excludedJobIdsJson",
            JsonSerializer.Serialize(excludedJobIds?.Distinct().ToArray() ?? []));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<WatchlistSyncJobDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(await ReadWatchlistSyncJobAsync(reader, cancellationToken));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return jobs;
    }

    public async Task<DateTimeOffset?> GetNextWatchlistSyncJobDueUtcAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT MIN(due_at)
FROM (
    SELECT next_attempt_utc AS due_at
    FROM watchlist_sync_job
    WHERE lower(status) IN ('pending', 'retry')
    UNION ALL
    SELECT lease_until_utc AS due_at
    FROM watchlist_sync_job
    WHERE lower(status) = 'processing'
      AND lease_until_utc IS NOT NULL
);", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return null;
        }

        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : ParseDateTimeOffsetInvariant(text);
    }

    public async Task<WatchlistStateDriftReport> DetectWatchlistStateDriftAsync(
        int maxSyncAttempts,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT
    (SELECT COUNT(*)
       FROM playlist_watch_target_sync_state state
      WHERE lower(state.status) = 'applied'
        AND NOT EXISTS (
            SELECT 1 FROM playlist_watch_target_membership m
             WHERE m.source = state.source
               AND m.source_id = state.source_id
               AND lower(m.target_service) = lower(state.target_service))) AS applied_without_membership,
    (SELECT COUNT(*)
       FROM playlist_watch_target_sync_state state
      WHERE lower(state.status) = 'applied'
        AND EXISTS (
            SELECT 1
              FROM playlist_watch_track t
              JOIN playlist_watch_configured_sync_targets cst
                ON cst.source = t.source
               AND cst.source_id = t.source_id
               AND cst.target = lower(state.target_service)
             WHERE t.source = state.source
               AND t.source_id = state.source_id
               AND t.local_track_id IS NOT NULL
               AND NOT EXISTS (
                    SELECT 1
                      FROM playlist_watch_target_membership m
                     WHERE m.source = t.source
                       AND m.source_id = t.source_id
                       AND m.track_source_id = t.track_source_id
                       AND lower(m.target_service) = lower(state.target_service)
                       AND lower(m.sync_status) = 'playlist_synced'))) AS applied_with_incomplete_membership,
    (SELECT COUNT(*)
       FROM (SELECT DISTINCT m.source, m.source_id, lower(m.target_service) AS target_service
               FROM playlist_watch_target_membership m
              WHERE lower(m.sync_status) = 'playlist_synced') verified
      WHERE NOT EXISTS (
            SELECT 1 FROM playlist_watch_target_sync_state state
             WHERE state.source = verified.source
               AND state.source_id = verified.source_id
               AND lower(state.target_service) = verified.target_service
               AND lower(state.status) = 'applied')) AS membership_without_applied,
    (SELECT COUNT(*)
       FROM playlist_watch_target_membership m
      WHERE NOT EXISTS (
            SELECT 1 FROM playlist_watch_track t
             WHERE t.source = m.source
               AND t.source_id = m.source_id
               AND t.track_source_id = m.track_source_id)) AS orphaned_membership,
    (SELECT COUNT(*)
       FROM playlist_watch_target_membership m
      WHERE NOT EXISTS (
            SELECT 1 FROM playlist_watch_configured_sync_targets cst
             WHERE cst.source = m.source
               AND cst.source_id = m.source_id
               AND cst.target = lower(m.target_service))) AS membership_for_unconfigured_target,
    (SELECT COUNT(*)
       FROM watchlist_sync_job job
      WHERE lower(job.status) = 'blocked'
        AND job.attempt_count < @maxAttempts) AS blocked_below_attempt_cap;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("maxAttempts", Math.Max(1, maxSyncAttempts));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new WatchlistStateDriftReport(0, 0, 0, 0, 0, 0);
        }

        return new WatchlistStateDriftReport(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
    }

    public async Task<int> RepairWatchlistSyncBacklogAsync(
        int maxSyncAttempts,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var reopenBlockedBelowCap = new SqliteCommand(@"
UPDATE watchlist_sync_job
SET status='retry',
    lease_owner=NULL,
    lease_until_utc=NULL,
    next_attempt_utc=CURRENT_TIMESTAMP,
    last_error=COALESCE(last_error, 'Recovered blocked target synchronization job before retry cap.'),
    updated_at=CURRENT_TIMESTAMP
WHERE attempt_count < @maxSyncAttempts
  AND lower(status)='blocked';", connection, transaction);
        reopenBlockedBelowCap.Parameters.AddWithValue("maxSyncAttempts", Math.Max(1, maxSyncAttempts));
        var reopenedBlocked = await reopenBlockedBelowCap.ExecuteNonQueryAsync(cancellationToken);

        await using var recover = new SqliteCommand(@"
UPDATE watchlist_sync_job
SET status='retry',
    lease_owner=NULL,
    lease_until_utc=NULL,
    next_attempt_utc=CURRENT_TIMESTAMP,
    last_error=COALESCE(last_error, 'Recovered expired target synchronization lease.'),
    updated_at=CURRENT_TIMESTAMP
WHERE lower(status)='processing'
  AND datetime(lease_until_utc) <= datetime('now');", connection, transaction);
        var repaired = await recover.ExecuteNonQueryAsync(cancellationToken);

        await using (var finalizeVerifiedMembership = new SqliteCommand(@"
INSERT INTO playlist_watch_target_sync_state (
    source,source_id,target_service,applied_snapshot_id,status,last_error,
    applied_kind,applied_membership_hash,applied_source_playlist_id)
SELECT job.source,
       job.playlist_id,
       lower(job.target_service),
       job.snapshot_id,
       'applied',
       NULL,
       CASE WHEN EXISTS (
           SELECT 1 FROM playlist_watch_track missing
           WHERE missing.source=job.source
             AND missing.source_id=job.playlist_id
             AND missing.local_track_id IS NULL)
           THEN 'partial' ELSE 'full' END,
       NULL,
       NULL
FROM watchlist_sync_job job
WHERE lower(job.track_id)='playlist'
  AND NULLIF(trim(job.snapshot_id), '') IS NOT NULL
  AND EXISTS (
      SELECT 1 FROM playlist_watch_target_membership membership
      WHERE membership.source=job.source
        AND membership.source_id=job.playlist_id
        AND lower(membership.target_service)=lower(job.target_service)
        AND lower(membership.sync_status)='playlist_synced'
        AND datetime(membership.updated_at) >= datetime(job.created_at))
  AND NOT EXISTS (
      SELECT 1 FROM playlist_watch_track track
      WHERE track.source=job.source
        AND track.source_id=job.playlist_id
        AND track.local_track_id IS NOT NULL
        AND NOT EXISTS (
            SELECT 1 FROM playlist_watch_target_membership membership
            WHERE membership.source=track.source
              AND membership.source_id=track.source_id
              AND membership.track_source_id=track.track_source_id
              AND lower(membership.target_service)=lower(job.target_service)
              AND lower(membership.sync_status)='playlist_synced'
              AND datetime(membership.updated_at) >= datetime(job.created_at)))
ON CONFLICT(source,source_id,target_service) DO UPDATE SET
    applied_snapshot_id=excluded.applied_snapshot_id,
    status='applied',
    last_error=NULL,
    applied_kind=excluded.applied_kind,
    updated_at=CURRENT_TIMESTAMP;", connection, transaction))
        {
            repaired += await finalizeVerifiedMembership.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var removeFinalizedJobs = new SqliteCommand(@"
DELETE FROM watchlist_sync_job
WHERE lower(track_id)='playlist'
  AND EXISTS (
      SELECT 1 FROM playlist_watch_target_sync_state state
      WHERE state.source=watchlist_sync_job.source
        AND state.source_id=watchlist_sync_job.playlist_id
        AND lower(state.target_service)=lower(watchlist_sync_job.target_service)
        AND lower(state.status)='applied'
        AND state.applied_snapshot_id=watchlist_sync_job.snapshot_id);", connection, transaction))
        {
            repaired += await removeFinalizedJobs.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var removeObsolete = new SqliteCommand(@"
DELETE FROM watchlist_sync_job
WHERE NOT EXISTS (
    SELECT 1
    FROM playlist_watchlist playlist
    JOIN playlist_watch_preferences preference
      ON preference.source=playlist.source
     AND preference.source_id=playlist.source_id
    JOIN json_each(CASE
        WHEN json_valid(preference.sync_targets_json)
             AND json_array_length(preference.sync_targets_json) > 0
            THEN preference.sync_targets_json
        ELSE json_array(preference.service)
    END) configured
      ON lower(trim(configured.value))=lower(watchlist_sync_job.target_service)
    WHERE playlist.source=watchlist_sync_job.source
      AND playlist.source_id=watchlist_sync_job.playlist_id
);", connection, transaction);
        repaired += await removeObsolete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return repaired + reopenedBlocked;
    }

    private static async Task<WatchlistSyncJobDto> ReadWatchlistSyncJobAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths = Array.Empty<string>();
        if (!await reader.IsDBNullAsync(6, cancellationToken))
        {
            paths = JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [];
        }
        return new WatchlistSyncJobDto(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt64(5), paths,
            reader.GetInt32(7), ParseDateTimeOffsetInvariant(reader.GetString(8)),
            await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
            await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10),
            await reader.IsDBNullAsync(11, cancellationToken) ? "pending" : reader.GetString(11),
            await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12),
            await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetString(13));
    }

    internal static string FormatAppliedKind(WatchlistAppliedKind appliedKind)
        => appliedKind switch
        {
            WatchlistAppliedKind.Partial => "partial",
            WatchlistAppliedKind.WaitingForSeed => "waiting_for_seed",
            _ => "full"
        };

    public async Task<bool> CompleteWatchlistSyncJobAsync(long id, string leaseOwner, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand("DELETE FROM watchlist_sync_job WHERE id=@id AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> CompleteWatchlistPlaylistSyncJobAsync(
        WatchlistSyncJobDto job,
        string leaseOwner,
        WatchlistAppliedKind appliedKind,
        string? membershipHash,
        string? sourcePlaylistId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(job.TrackId, "playlist", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(job.SnapshotId)
            || !TryNormalizePlaylistWatchKey(job.Source, job.PlaylistId, out var source, out var playlistId))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var persist = new SqliteCommand(@"
INSERT INTO playlist_watch_target_sync_state (
    source,source_id,target_service,applied_snapshot_id,status,last_error,
    applied_kind,applied_membership_hash,applied_source_playlist_id)
VALUES (@source,@sourceId,@targetService,@snapshotId,'applied',NULL,
    @appliedKind,@membershipHash,@sourcePlaylistId)
ON CONFLICT(source,source_id,target_service) DO UPDATE SET
    applied_snapshot_id=COALESCE(
        NULLIF(trim(excluded.applied_snapshot_id), ''),
        playlist_watch_target_sync_state.applied_snapshot_id),
    status='applied',
    last_error=NULL,
    applied_kind=excluded.applied_kind,
    applied_membership_hash=excluded.applied_membership_hash,
    applied_source_playlist_id=excluded.applied_source_playlist_id,
    updated_at=CURRENT_TIMESTAMP;", connection, transaction);
        persist.Parameters.AddWithValue(SourceField, source);
        persist.Parameters.AddWithValue(SourceIdField, playlistId);
        persist.Parameters.AddWithValue("targetService", job.TargetService.Trim().ToLowerInvariant());
        persist.Parameters.AddWithValue("snapshotId", job.SnapshotId.Trim());
        persist.Parameters.AddWithValue("appliedKind", FormatAppliedKind(appliedKind));
        persist.Parameters.AddWithValue(
            "membershipHash",
            string.IsNullOrWhiteSpace(membershipHash) ? DBNull.Value : membershipHash.Trim());
        persist.Parameters.AddWithValue(
            "sourcePlaylistId",
            string.IsNullOrWhiteSpace(sourcePlaylistId) ? DBNull.Value : sourcePlaylistId.Trim());
        await persist.ExecuteNonQueryAsync(cancellationToken);

        await using var complete = new SqliteCommand(
            "DELETE FROM watchlist_sync_job WHERE id=@id AND lease_owner=@leaseOwner;",
            connection,
            transaction);
        complete.Parameters.AddWithValue("id", job.Id);
        complete.Parameters.AddWithValue("leaseOwner", leaseOwner);
        var completed = await complete.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (completed)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        return completed;
    }

    public async Task<WatchlistSyncJobStatusCounts> GetWatchlistSyncJobStatusCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT
    SUM(CASE WHEN (lower(status) IN ('pending', 'retry') AND datetime(next_attempt_utc) <= datetime('now'))
                   OR (lower(status) = 'processing' AND datetime(lease_until_utc) <= datetime('now')) THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status) = 'processing' AND datetime(lease_until_utc) > datetime('now') THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status) = 'retry' AND datetime(next_attempt_utc) > datetime('now') THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status) = 'processing' AND datetime(lease_until_utc) <= datetime('now') THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status) = 'repair_required' THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status) = 'blocked' THEN 1 ELSE 0 END),
    MIN(CASE WHEN lower(status) IN ('pending','retry','processing','repair_required') THEN created_at END),
    (SELECT last_error FROM watchlist_sync_job WHERE last_error IS NOT NULL AND trim(last_error) <> '' ORDER BY updated_at DESC, id DESC LIMIT 1)
FROM watchlist_sync_job;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new WatchlistSyncJobStatusCounts(0, 0, 0);
        }
        return new WatchlistSyncJobStatusCounts(
            await reader.IsDBNullAsync(0, cancellationToken) ? 0 : reader.GetInt32(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? 0 : reader.GetInt32(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? 0 : reader.GetInt32(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? 0 : reader.GetInt32(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? 0 : reader.GetInt32(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? 0 : reader.GetInt32(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : ParseDateTimeOffsetInvariant(reader.GetString(6)),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7));
    }

    public async Task<IReadOnlyList<WatchlistSyncJobDto>> GetWatchlistSyncJobsAsync(
        string source,
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, playlistId, out var normalizedSource, out var normalizedPlaylistId))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
SELECT id,source,playlist_id,track_id,target_service,destination_folder_id,final_file_paths_json,
       attempt_count,next_attempt_utc,queue_uuid,lease_owner,status,last_error,snapshot_id
FROM watchlist_sync_job
WHERE source=@source AND playlist_id=@playlistId
ORDER BY CASE lower(target_service)
             WHEN 'plex' THEN 0
             WHEN 'jellyfin' THEN 1
             WHEN 'navidrome' THEN 2
             ELSE 3
         END,
         track_id,
         id;", connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("playlistId", normalizedPlaylistId);
        var jobs = new List<WatchlistSyncJobDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(await ReadWatchlistSyncJobAsync(reader, cancellationToken));
        }

        return jobs;
    }

    public async Task<bool> RetryWatchlistSyncJobAsync(long id, string leaseOwner, int attempts, DateTimeOffset nextAttemptUtc, string? error, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"UPDATE watchlist_sync_job SET attempt_count=@attempts,status='retry',lease_owner=NULL,lease_until_utc=NULL,next_attempt_utc=@next,last_error=@error,updated_at=CURRENT_TIMESTAMP WHERE id=@id AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("attempts", attempts);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("next", nextAttemptUtc.ToString("O")); command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RenewWatchlistSyncJobLeaseAsync(long id, string leaseOwner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_sync_job
SET lease_until_utc=@leaseUntilUtc, updated_at=CURRENT_TIMESTAMP
WHERE id=@id AND lower(status)='processing' AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("leaseUntilUtc", (DateTimeOffset.UtcNow + lease).ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> BlockWatchlistSyncJobAsync(long id, string leaseOwner, string reason, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE watchlist_sync_job
SET status='blocked', lease_owner=NULL, lease_until_utc=NULL, last_error=@reason, updated_at=CURRENT_TIMESTAMP
WHERE id=@id AND lease_owner=@leaseOwner;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("reason", reason);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteObsoleteWatchlistSyncJobAsync(
        WatchlistSyncJobDto job,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var deleteJob = new SqliteCommand(
            "DELETE FROM watchlist_sync_job WHERE id=@id AND lease_owner=@leaseOwner;",
            connection,
            transaction);
        deleteJob.Parameters.AddWithValue("id", job.Id);
        deleteJob.Parameters.AddWithValue("leaseOwner", leaseOwner);
        var deleted = await deleteJob.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted && !string.Equals(job.TrackId, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            await using var deleteMembership = new SqliteCommand(@"
DELETE FROM playlist_watch_target_membership
WHERE source=@source AND source_id=@sourceId AND track_source_id=@trackId AND lower(target_service)=@target;", connection, transaction);
            deleteMembership.Parameters.AddWithValue(SourceField, job.Source);
            deleteMembership.Parameters.AddWithValue(SourceIdField, job.PlaylistId);
            deleteMembership.Parameters.AddWithValue("trackId", job.TrackId);
            deleteMembership.Parameters.AddWithValue("target", job.TargetService.Trim().ToLowerInvariant());
            await deleteMembership.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<WatchlistHistoryDto?> AddWatchlistHistoryAsync(
        WatchlistHistoryInsert entry,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(entry.Source, entry.SourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO watchlist_history (source, watch_type, source_id, name, collection_type, track_count, status, artist_name, item_key, created_at)
VALUES (@source, @watchType, @sourceId, @name, @collectionType, @trackCount, @status, @artistName, @itemKey, @createdAt)
RETURNING id, created_at;";
        await using var command = new SqliteCommand(sql, connection);
        var createdAt = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("watchType", entry.WatchType);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("name", entry.Name);
        command.Parameters.AddWithValue("collectionType", entry.CollectionType);
        command.Parameters.AddWithValue(TrackCountField, entry.TrackCount);
        command.Parameters.AddWithValue("status", entry.Status);
        command.Parameters.AddWithValue("artistName", (object?)entry.ArtistName ?? DBNull.Value);
        command.Parameters.AddWithValue("itemKey", (object?)entry.ItemKey ?? DBNull.Value);
        command.Parameters.AddWithValue("createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var created = await reader.IsDBNullAsync(1, cancellationToken)
            ? createdAt
            : ParseUtcDateTimeOffsetInvariant(reader.GetString(1));
        return new WatchlistHistoryDto(
            reader.GetInt64(0),
            normalizedSource,
            entry.WatchType,
            normalizedSourceId,
            entry.Name,
            entry.CollectionType,
            entry.TrackCount,
            entry.Status,
            entry.ArtistName,
            created,
            entry.ItemKey);
    }

    public async Task<int> GetWatchlistHistoryCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"SELECT COUNT(*) FROM watchlist_history;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    public async Task<int> PruneWatchlistHistoryAsync(
        DateTimeOffset cutoffUtc,
        int maximumRows,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var oldCommand = new SqliteCommand(
            "DELETE FROM watchlist_history WHERE datetime(created_at) < datetime(@cutoffUtc);",
            connection,
            transaction);
        oldCommand.Parameters.AddWithValue("cutoffUtc", cutoffUtc.ToString("O"));
        var deleted = await oldCommand.ExecuteNonQueryAsync(cancellationToken);
        await using var overflowCommand = new SqliteCommand(@"
DELETE FROM watchlist_history
WHERE id NOT IN (
    SELECT id FROM watchlist_history ORDER BY created_at DESC,id DESC LIMIT @maximumRows
);", connection, transaction);
        overflowCommand.Parameters.AddWithValue("maximumRows", Math.Clamp(maximumRows, 1000, 100000));
        deleted += await overflowCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<IReadOnlyList<WatchlistHistoryDto>> GetWatchlistHistoryAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT id,
       source,
       watch_type,
       source_id,
       name,
       collection_type,
       track_count,
       status,
       artist_name,
       item_key,
       created_at
FROM watchlist_history
ORDER BY created_at DESC
LIMIT @limit OFFSET @offset;";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        return await ReadWatchlistHistoryAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<WatchlistHistoryDto>> GetWatchlistHistorySinceAsync(
        long sinceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT id,
       source,
       watch_type,
       source_id,
       name,
       collection_type,
       track_count,
       status,
       artist_name,
       item_key,
       created_at
FROM watchlist_history
WHERE id > @sinceId
ORDER BY id DESC
LIMIT @limit;";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("sinceId", Math.Max(0, sinceId));
        command.Parameters.AddWithValue("limit", limit);
        return await ReadWatchlistHistoryAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<WatchlistHistoryDto>> ReadWatchlistHistoryAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<WatchlistHistoryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var created = await reader.IsDBNullAsync(10, cancellationToken) ? DateTimeOffset.MinValue : ParseUtcDateTimeOffsetInvariant(reader.GetString(10));
            items.Add(new WatchlistHistoryDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
                created,
                await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9)));
        }

        return items;
    }

    public async Task<string?> GetArtistSourceIdAsync(long artistId, string source, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT source_id
FROM artist_source
WHERE artist_id = @artistId
  AND source = @source
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue(SourceField, source);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    public async Task<long?> FindArtistIdBySourceIdAsync(string source, string sourceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT artist_id
FROM artist_source
WHERE source = @source
  AND source_id = @sourceId
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source.Trim());
        command.Parameters.AddWithValue("sourceId", sourceId.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task<long?> FindArtistIdByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id
FROM artist
WHERE name = @name COLLATE NOCASE
ORDER BY id
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("name", name.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task<IReadOnlySet<long>> GetArtistIdsWithSourceAsync(string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new HashSet<long>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT artist_id
FROM artist_source
WHERE source = @source;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artistIds = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artistIds.Add(reader.GetInt64(0));
        }

        return artistIds;
    }

    public async Task<long?> GetArtistIdBySourceIdAsync(string source, string sourceId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT artist_id
FROM artist_source
WHERE source = @source
  AND source_id = @sourceId
LIMIT 1;";
        return await QueryNullableLongBySourceIdAsync(source, sourceId, sql, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetArtistIdsBySourceIdAsync(string source, string sourceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return Array.Empty<long>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT artist_id
FROM artist_source
WHERE source = @source
  AND source_id = @sourceId
ORDER BY artist_id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source.Trim());
        command.Parameters.AddWithValue("sourceId", sourceId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artistIds = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artistIds.Add(reader.GetInt64(0));
        }

        return artistIds;
    }

    public async Task UpsertArtistSourceIdAsync(long artistId, string source, string sourceId, CancellationToken cancellationToken = default)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string artistExistsSql = @"
SELECT 1
FROM artist
WHERE id = @artistId
LIMIT 1;";
        const string deleteSql = @"
DELETE FROM artist_source
WHERE artist_id = @artistId
  AND source = @source
  AND source_id <> @sourceId;";
        const string moveSql = @"
UPDATE artist_source
SET artist_id = @artistId
WHERE source = @source
  AND source_id = @sourceId
  AND artist_id <> @artistId;";
        const string upsertSql = @"
INSERT INTO artist_source (artist_id, source, source_id)
VALUES (@artistId, @source, @sourceId)
ON CONFLICT(artist_id, source) DO UPDATE SET
    source_id = excluded.source_id;";

        await using (var artistExistsCommand = new SqliteCommand(artistExistsSql, connection))
        {
            artistExistsCommand.Parameters.AddWithValue("artistId", artistId);
            var existsResult = await artistExistsCommand.ExecuteScalarAsync(cancellationToken);
            if (existsResult is null || existsResult == DBNull.Value)
            {
                return;
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteCommand = new SqliteCommand(deleteSql, connection, (SqliteTransaction)transaction))
        {
            deleteCommand.Parameters.AddWithValue("artistId", artistId);
            deleteCommand.Parameters.AddWithValue(SourceField, source);
            deleteCommand.Parameters.AddWithValue(SourceIdField, sourceId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var moveCommand = new SqliteCommand(moveSql, connection, (SqliteTransaction)transaction))
        {
            moveCommand.Parameters.AddWithValue("artistId", artistId);
            moveCommand.Parameters.AddWithValue(SourceField, source);
            moveCommand.Parameters.AddWithValue(SourceIdField, sourceId);
            await moveCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsertCommand = new SqliteCommand(upsertSql, connection, (SqliteTransaction)transaction))
        {
            upsertCommand.Parameters.AddWithValue("artistId", artistId);
            upsertCommand.Parameters.AddWithValue(SourceField, source);
            upsertCommand.Parameters.AddWithValue(SourceIdField, sourceId);
            try
            {
                await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException ex) when (IsForeignKeyViolation(ex))
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch (Exception rollbackException) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(rollbackException))
                {
                    // best effort only
                }

                return;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static bool IsForeignKeyViolation(SqliteException ex)
    {
        return ex.SqliteErrorCode == 19
            && ex.Message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase);
    }

    public async Task RemoveArtistSourceAsync(long artistId, string source, CancellationToken cancellationToken = default)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
DELETE FROM artist_source
WHERE artist_id = @artistId
  AND source = @source;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue(SourceField, source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AlbumDetailDto?> GetAlbumAsync(long albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT al.id,
       al.artist_id,
       al.title,
       al.preferred_cover_path,
       COALESCE(
           (
               SELECT GROUP_CONCAT(folder_name, '|')
               FROM (
                   SELECT DISTINCT f.display_name AS folder_name
                   FROM track t_local
                   JOIN track_local tl_local ON tl_local.track_id = t_local.id
                   JOIN audio_file af_local ON af_local.id = tl_local.audio_file_id
                   JOIN folder f ON f.id = af_local.folder_id
                   WHERE t_local.album_id = al.id
                     AND f.enabled = TRUE
                   ORDER BY folder_name
               )
           ),
           ''
       ) AS local_folders
FROM album al
WHERE al.id = @albumId
  AND EXISTS (
      SELECT 1
      FROM track t_visible
      JOIN track_local tl_visible ON tl_visible.track_id = t_visible.id
      JOIN audio_file af_visible ON af_visible.id = tl_visible.audio_file_id
      JOIN folder f_visible ON f_visible.id = af_visible.folder_id
      WHERE t_visible.album_id = al.id
        AND f_visible.enabled = TRUE
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("albumId", albumId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var foldersRaw = await reader.IsDBNullAsync(4, cancellationToken) ? string.Empty : reader.GetString(4);
            var folders = string.IsNullOrWhiteSpace(foldersRaw)
                ? Array.Empty<string>()
                : foldersRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new AlbumDetailDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                folders);
        }

        return null;
    }

    public async Task<IReadOnlyList<TrackDto>> GetAlbumTracksAsync(long albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       t.album_id,
       t.title,
       t.duration_ms,
       t.disc,
       t.track_no,
       t.lyrics_status,
       EXISTS (
           SELECT 1
           FROM track_local tl
           JOIN audio_file af ON af.id = tl.audio_file_id
           JOIN folder f ON f.id = af.folder_id
           WHERE tl.track_id = t.id
             AND f.enabled = TRUE
       ) AS available_locally
FROM track t
WHERE t.album_id = @albumId
  AND EXISTS (
      SELECT 1
      FROM track_local tl_visible
      JOIN audio_file af_visible ON af_visible.id = tl_visible.audio_file_id
      JOIN folder f_visible ON f_visible.id = af_visible.folder_id
      WHERE tl_visible.track_id = t.id
        AND f_visible.enabled = TRUE
  )
ORDER BY t.disc NULLS FIRST, t.track_no NULLS FIRST, t.title;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("albumId", albumId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tracks = new List<TrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tracks.Add(new TrackDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt32(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt32(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5),
                reader.GetBoolean(7),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6)));
        }

        return tracks;
    }

    private static async Task<AlbumTrackAudioInfoDto> ReadAlbumTrackAudioInfoRowAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var trackId = reader.GetInt64(0);
        var audioFileId = await reader.IsDBNullAsync(1, cancellationToken) ? default(long?) : reader.GetInt64(1);
        var channels = await reader.IsDBNullAsync(8, cancellationToken) ? (int?)null : reader.GetInt32(8);
        var codec = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3);
        var extension = await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4);
        var rawPath = await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10);
        var relativePath = await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11);
        var rootPath = await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12);
        var filePath = BuildAbsolutePath(rootPath, relativePath, rawPath);
        var variant = ResolveAudioVariant(
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            channels,
            filePath,
            codec,
            extension);
        var hasAtmosVariant = string.Equals(variant, AtmosVariant, StringComparison.OrdinalIgnoreCase);
        var hasStereoVariant = !hasAtmosVariant;

        return new AlbumTrackAudioInfoDto(
            trackId,
            audioFileId,
            variant,
            codec,
            extension,
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt32(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetInt32(7),
            channels,
            await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetInt32(9),
            string.IsNullOrWhiteSpace(filePath) ? rawPath : filePath,
            hasStereoVariant,
            hasAtmosVariant);
    }

    public async Task<IReadOnlyDictionary<long, AlbumTrackAudioInfoDto>> GetAlbumTrackAudioInfoAsync(long albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id AS track_id,
       af.id AS audio_file_id,
       af.audio_variant,
       af.codec,
       af.extension,
       af.bitrate_kbps,
       af.sample_rate_hz,
       af.bits_per_sample,
       af.channels,
       af.quality_rank,
       af.path,
       af.relative_path,
       f.root_path
FROM track t
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE t.album_id = @albumId
  AND f.enabled = TRUE
ORDER BY t.id,
         af.quality_rank DESC NULLS LAST,
         af.size DESC,
         af.id DESC;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("albumId", albumId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var map = new Dictionary<long, AlbumTrackAudioInfoDto>();
        var variantsByTrack = new Dictionary<long, (bool HasStereo, bool HasAtmos)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var parsed = await ReadAlbumTrackAudioInfoRowAsync(reader, cancellationToken);
            var trackId = parsed.TrackId;
            var rowHasAtmos = parsed.HasAtmosVariant;
            var rowHasStereo = parsed.HasStereoVariant;

            var mergedVariants = variantsByTrack.TryGetValue(trackId, out var existingVariants)
                ? (existingVariants.HasStereo || rowHasStereo, existingVariants.HasAtmos || rowHasAtmos)
                : (rowHasStereo, rowHasAtmos);
            variantsByTrack[trackId] = mergedVariants;

            if (map.ContainsKey(trackId))
            {
                continue;
            }

            map[trackId] = parsed with { HasStereoVariant = false, HasAtmosVariant = false };
        }

        foreach (var entry in map.ToList())
        {
            if (!variantsByTrack.TryGetValue(entry.Key, out var variants))
            {
                continue;
            }

            map[entry.Key] = entry.Value with
            {
                HasStereoVariant = variants.HasStereo,
                HasAtmosVariant = variants.HasAtmos
            };
        }

        return map;
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<AlbumTrackAudioInfoDto>>> GetAlbumTrackAudioVariantsAsync(long albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id AS track_id,
       af.id AS audio_file_id,
       af.audio_variant,
       af.codec,
       af.extension,
       af.bitrate_kbps,
       af.sample_rate_hz,
       af.bits_per_sample,
       af.channels,
       af.quality_rank,
       af.path,
       af.relative_path,
       f.root_path
FROM track t
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE t.album_id = @albumId
  AND f.enabled = TRUE
ORDER BY t.id,
         af.quality_rank DESC NULLS LAST,
         af.size DESC,
         af.id DESC;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("albumId", albumId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new Dictionary<long, List<AlbumTrackAudioInfoDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = await ReadAlbumTrackAudioInfoRowAsync(reader, cancellationToken);
            var trackId = row.TrackId;

            if (!result.TryGetValue(trackId, out var list))
            {
                list = new List<AlbumTrackAudioInfoDto>();
                result[trackId] = list;
            }

            list.Add(row);
        }

        return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<AlbumTrackAudioInfoDto>)kvp.Value);
    }

    private static string ResolveAudioVariant(
        string? storedVariant,
        int? channels,
        string? filePath,
        string? codec = null,
        string? extension = null)
        => AudioVariantResolver.ResolveAudioVariant(storedVariant, channels, filePath, codec, extension);

    private static string? NormalizeAudioVariant(string? value)
        => AudioVariantResolver.NormalizeAudioVariant(value);

    private static string BuildVariantKey(long trackId, long? audioFileId, int fallbackIndex)
        => audioFileId.HasValue && audioFileId.Value > 0
            ? $"{trackId}:{audioFileId.Value}"
            : $"{trackId}:{fallbackIndex}";

    public async Task<IReadOnlyDictionary<long, TrackSourceLinksDto>> GetAlbumTrackSourceLinksAsync(long albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       COALESCE(MAX(CASE WHEN ts.source = 'deezer' THEN ts.source_id END), t.deezer_id) AS deezer_track_id,
       MAX(CASE WHEN ts.source = 'spotify' THEN ts.source_id END) AS spotify_track_id,
       MAX(CASE WHEN ts.source = 'apple' THEN ts.source_id END) AS apple_track_id,
       MAX(CASE WHEN ts.source = 'isrc' THEN ts.source_id END) AS isrc,
       MAX(CASE WHEN ts.source = 'deezer' THEN ts.url END) AS deezer_url,
       MAX(CASE WHEN ts.source = 'spotify' THEN ts.url END) AS spotify_url,
       MAX(CASE WHEN ts.source = 'apple' THEN ts.url END) AS apple_url
FROM track t
LEFT JOIN track_source ts ON ts.track_id = t.id
WHERE t.album_id = @albumId
GROUP BY t.id, t.deezer_id;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("albumId", albumId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<long, TrackSourceLinksDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            result[trackId] = new TrackSourceLinksDto(
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7));
        }

        return result;
    }

    public async Task<TrackSourceLinksDto?> GetTrackSourceLinksAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       COALESCE(MAX(CASE WHEN ts.source = 'deezer' THEN ts.source_id END), t.deezer_id) AS deezer_track_id,
       MAX(CASE WHEN ts.source = 'spotify' THEN ts.source_id END) AS spotify_track_id,
       MAX(CASE WHEN ts.source = 'apple' THEN ts.source_id END) AS apple_track_id,
       MAX(CASE WHEN ts.source = 'isrc' THEN ts.source_id END) AS isrc,
       MAX(CASE WHEN ts.source = 'deezer' THEN ts.url END) AS deezer_url,
       MAX(CASE WHEN ts.source = 'spotify' THEN ts.url END) AS spotify_url,
       MAX(CASE WHEN ts.source = 'apple' THEN ts.url END) AS apple_url
FROM track t
LEFT JOIN track_source ts ON ts.track_id = t.id
WHERE t.id = @trackId
GROUP BY t.id, t.deezer_id
LIMIT 1;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TrackSourceLinksDto(
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7));
    }

    public async Task<IReadOnlyList<ArtistSpotifyMatchSignalDto>> GetArtistSpotifyMatchSignalsAsync(
        long artistId,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        if (artistId <= 0 || limit <= 0)
        {
            return Array.Empty<ArtistSpotifyMatchSignalDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       t.title,
       MAX(CASE WHEN ts.source = 'spotify' THEN ts.source_id END) AS spotify_track_id,
       COALESCE(MAX(CASE WHEN ts.source = 'isrc' THEN ts.source_id END), t.tag_isrc) AS isrc,
       t.tag_artist,
       t.tag_album_artist
FROM track t
JOIN album al ON al.id = t.album_id
LEFT JOIN track_source ts ON ts.track_id = t.id
WHERE al.artist_id = @artistId
GROUP BY t.id, t.title, t.tag_isrc, t.tag_artist, t.tag_album_artist
ORDER BY t.id DESC
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ArtistSpotifyMatchSignalDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ArtistSpotifyMatchSignalDto(
                reader.GetInt64(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5)));
        }

        return results;
    }

    public async Task<IReadOnlyList<OfflineTrackSearchDto>> SearchTracksAsync(string likeQuery, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.title,
       ar.name,
       al.title,
       al.preferred_cover_path,
       t.deezer_id
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
WHERE LOWER(t.title) LIKE LOWER(@like) ESCAPE '\'
   OR LOWER(ar.name) LIKE LOWER(@like) ESCAPE '\'
   OR LOWER(al.title) LIKE LOWER(@like) ESCAPE '\'
LIMIT 200;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("like", likeQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<OfflineTrackSearchDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OfflineTrackSearchDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4)));
        }

        return results;
    }

    public async Task<IReadOnlyList<TrackSearchResultDto>> SearchTracksWithIdsAsync(string likeQuery, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT t.id,
       t.title,
       ar.name,
       al.title,
       t.duration_ms,
       al.preferred_cover_path
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1
  AND (
       LOWER(t.title) LIKE LOWER(@like) ESCAPE '\'
    OR LOWER(ar.name) LIKE LOWER(@like) ESCAPE '\'
    OR LOWER(al.title) LIKE LOWER(@like) ESCAPE '\'
  )
LIMIT 200;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("like", likeQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<TrackSearchResultDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TrackSearchResultDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt32(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5)));
        }

        return results;
    }

    public async Task<TrackAudioInfoDto?> GetTrackAudioInfoAsync(long trackId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT t.id,
       t.title,
       ar.name,
       al.title,
       t.duration_ms,
       al.preferred_cover_path,
       af.folder_id,
       f.root_path,
       af.relative_path,
       af.path
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE t.id = @trackId
ORDER BY f.enabled DESC
LIMIT 1;";
        return await QuerySingleTrackAsync(sql, trackId, ReadTrackAudioInfoAsync, cancellationToken);
    }

    public async Task<IReadOnlyList<OfflineAlbumSearchDto>> SearchAlbumsAsync(string likeQuery, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT al.title,
       ar.name,
       al.preferred_cover_path,
       al.deezer_id
FROM album al
JOIN artist ar ON ar.id = al.artist_id
WHERE LOWER(al.title) LIKE LOWER(@like) ESCAPE '\'
   OR LOWER(ar.name) LIKE LOWER(@like) ESCAPE '\'
LIMIT 200;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("like", likeQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<OfflineAlbumSearchDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OfflineAlbumSearchDto(
                reader.GetString(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3)));
        }

        return results;
    }

    public async Task<IReadOnlyList<OfflineArtistSearchDto>> SearchArtistsAsync(string likeQuery, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT name,
       preferred_image_path,
       deezer_id
FROM artist
WHERE LOWER(name) LIKE LOWER(@like) ESCAPE '\'
LIMIT 200;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("like", likeQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<OfflineArtistSearchDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OfflineArtistSearchDto(
                reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2)));
        }

        return results;
    }

    public async Task<int?> GetBestLocalQualityRankForTrackAsync(
        long trackId,
        string? audioVariant = null,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return null;
        }

        var requireAtmosVariant = NormalizeAudioVariantFlag(audioVariant);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = $@"
SELECT MAX(af.quality_rank)
FROM track t
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
WHERE t.id = @trackId
  AND (
      @{RequireAtmosField} IS NULL
      OR (
          CASE
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
              WHEN LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                   OR ((LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                        OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.mlp'))
                       AND COALESCE(af.channels, 0) > 2)
                  THEN 1
              ELSE 0
          END
      ) = @{RequireAtmosField}
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull or null ? null : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<QualityScanTrackDto>> GetQualityScanTracksAsync(
        string scope,
        long? folderId,
        string? minFormat = null,
        int? minBitDepth = null,
        int? minSampleRateHz = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<long>? targetTrackIds = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var normalizedMinFormatRank = NormalizeQualityScanFormatRank(minFormat);
        var normalizedMinBitDepth = NormalizePositiveInt(minBitDepth);
        var normalizedMinSampleRateHz = NormalizePositiveInt(minSampleRateHz);
        var normalizedTargetTrackIds = targetTrackIds?
            .Where(static id => id > 0)
            .Distinct()
            .OrderBy(static id => id)
            .ToList();
        const string sql = @"
	WITH track_rows AS (
    SELECT t.id AS track_id,
           t.title AS track_title,
           t.tag_isrc AS isrc,
           t.duration_ms AS duration_ms,
           ar.name AS artist_name,
           ar.id AS album_artist_id,
           al.id AS album_id,
           al.title AS album_title,
           af.id AS audio_file_id,
           af.path AS audio_file_path,
           af.quality_rank AS quality_rank,
           af.codec AS codec,
           af.extension AS extension,
           af.bitrate_kbps AS bitrate_kbps,
           af.bits_per_sample AS bits_per_sample,
           af.sample_rate_hz AS sample_rate_hz,
           af.size AS file_size,
           COALESCE(t.tag_disc, 1) AS disc_number,
           COALESCE(t.track_no, t.tag_track_no) AS track_number,
           t.tag_track_total AS track_total,
           CASE
               WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 4
               WHEN (
                   LOWER(COALESCE(af.codec, '')) LIKE '%flac%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%alac%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%pcm%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%wave%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%wav%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%aiff%'
                   OR LOWER(COALESCE(af.extension, '')) IN ('.flac', '.wav', '.aiff', '.aif', '.alac')
               ) AND (
                   COALESCE(af.bits_per_sample, 0) >= 24
                   OR COALESCE(af.sample_rate_hz, 0) > 48000
               ) THEN 3
               WHEN (
                   LOWER(COALESCE(af.codec, '')) LIKE '%flac%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%alac%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%pcm%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%wave%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%wav%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%aiff%'
                   OR LOWER(COALESCE(af.extension, '')) IN ('.flac', '.wav', '.aiff', '.aif', '.alac')
               ) THEN 2
               WHEN COALESCE(af.quality_rank, 0) > 0 THEN 1
               ELSE 0
           END AS format_rank,
           f.id AS folder_id,
           CASE
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'atmos'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%atmos%' THEN 5
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('max_hires_192', 'hires_96', 'hi_res_lossless', 'hi_res', '27', '7')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi_res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi-res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24 bit%' THEN 4
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('alac', 'cd_lossless', 'flac', 'lossless', '9', '6')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%lossless%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%flac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%alac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16 bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%cd%' THEN 3
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('aac_lc', 'aac', 'mp3_320', 'high', '5', '3')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%aac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%320%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%vorbis%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%opus%' THEN 2
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('mp3_128', 'mp3_96', 'low', '1')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%128%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%96%' THEN 1
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = 'video'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%video%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) = 'podcast'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%podcast%' THEN 0
               ELSE 3
           END AS desired_quality,
           f.desired_quality_value AS desired_quality_value
    FROM track t
    JOIN album al ON al.id = t.album_id
    JOIN artist ar ON ar.id = al.artist_id
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
	    LEFT JOIN artist_watchlist aw ON aw.artist_id = ar.id
	    WHERE (@folderId IS NULL OR f.id = @folderId)
	      AND (@targetTrackIdsJson IS NULL OR t.id IN (SELECT value FROM json_each(@targetTrackIdsJson)))
	      AND LOWER(COALESCE(f.desired_quality_value, '')) NOT IN ('video', 'podcast')
	      AND (@scope <> 'watchlist' OR aw.artist_id IS NOT NULL)
	),
best_track_rows AS (
    SELECT tr.*,
           ROW_NUMBER() OVER (
               PARTITION BY tr.track_id
               ORDER BY COALESCE(tr.quality_rank, 0) DESC,
                        COALESCE(tr.format_rank, 0) DESC,
                        COALESCE(tr.bits_per_sample, 0) DESC,
                        COALESCE(tr.sample_rate_hz, 0) DESC,
                        COALESCE(tr.bitrate_kbps, 0) DESC,
                        COALESCE(tr.file_size, 0) DESC,
                        tr.audio_file_id DESC
           ) AS row_num
    FROM track_rows tr
)
SELECT br.track_id,
       br.track_title,
       br.artist_name,
       br.album_title,
       COALESCE(br.isrc, '') AS isrc,
       br.duration_ms,
       COALESCE(br.quality_rank, 0) AS best_quality,
       COALESCE(
           (SELECT tr2.desired_quality
            FROM track_rows tr2
            WHERE tr2.track_id = br.track_id
            ORDER BY tr2.desired_quality DESC
            LIMIT 1),
           0
       ) AS desired_quality,
       COALESCE(
           (SELECT tr2.desired_quality_value
            FROM track_rows tr2
            WHERE tr2.track_id = br.track_id
            ORDER BY tr2.desired_quality DESC
            LIMIT 1),
           '27'
       ) AS desired_quality_value,
       COALESCE(@folderId,
           (SELECT tr3.folder_id
            FROM track_rows tr3
            WHERE tr3.track_id = br.track_id
            ORDER BY tr3.desired_quality DESC
            LIMIT 1)
       ) AS destination_folder_id,
       COALESCE(br.format_rank, 0) AS best_format_rank,
       CASE
           WHEN COALESCE(br.format_rank, 0) >= 4 THEN 'atmos'
           WHEN COALESCE(br.format_rank, 0) >= 3 THEN 'hi_res_lossless'
           WHEN COALESCE(br.format_rank, 0) >= 2 THEN 'lossless'
           WHEN COALESCE(br.format_rank, 0) >= 1 THEN 'lossy'
           ELSE 'unknown'
       END AS best_format_tier,
       br.codec,
       br.extension,
       br.bitrate_kbps,
       br.bits_per_sample,
       br.sample_rate_hz,
       br.album_id,
       br.album_artist_id,
       br.audio_file_id,
       br.audio_file_path,
       br.disc_number,
       br.track_number,
       br.track_total
FROM best_track_rows br
WHERE br.row_num = 1
  AND (@minFormatRank IS NULL OR COALESCE(br.format_rank, 0) = 0 OR COALESCE(br.format_rank, 0) < @minFormatRank)
  AND (@minBitDepth IS NULL OR COALESCE(br.bits_per_sample, 0) = 0 OR COALESCE(br.bits_per_sample, 0) < @minBitDepth)
  AND (@minSampleRateHz IS NULL OR COALESCE(br.sample_rate_hz, 0) = 0 OR COALESCE(br.sample_rate_hz, 0) < @minSampleRateHz)
ORDER BY br.artist_name,
         br.album_id,
         COALESCE(br.disc_number, 1),
         COALESCE(br.track_number, 2147483647),
         br.track_id;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("scope", scope ?? string.Empty);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("minFormatRank", (object?)normalizedMinFormatRank ?? DBNull.Value);
        command.Parameters.AddWithValue("minBitDepth", (object?)normalizedMinBitDepth ?? DBNull.Value);
        command.Parameters.AddWithValue("minSampleRateHz", (object?)normalizedMinSampleRateHz ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "targetTrackIdsJson",
            normalizedTargetTrackIds is { Count: > 0 } ? SerializeJsonArray(normalizedTargetTrackIds) : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<QualityScanTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(await ReadQualityScanTrackDtoAsync(reader, cancellationToken));
        }

        return results;
    }

    public async Task<IReadOnlyList<MissingCoreMetadataFileDto>> GetMissingCoreMetadataFilesAsync(
        IReadOnlyCollection<long> folderIds,
        CancellationToken cancellationToken = default)
    {
        var scopedFolderIds = folderIds
            .Where(static id => id > 0)
            .Distinct()
            .ToList();
        if (scopedFolderIds.Count == 0)
        {
            return Array.Empty<MissingCoreMetadataFileDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id AS track_id,
       af.id AS audio_file_id,
       f.id AS folder_id,
       af.path,
       t.title,
       t.tag_title,
       ar.name AS artist_name,
       t.tag_artist,
       al.title AS album_title,
       t.tag_album,
       t.tag_album_artist,
       t.track_no,
       t.tag_track_no
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.id IN (
    SELECT value
    FROM json_each(@folderIdsJson)
)
  AND LOWER(COALESCE(f.desired_quality_value, '')) NOT IN ('video', 'podcast')
ORDER BY f.id, ar.name, al.title, COALESCE(t.track_no, t.tag_track_no, 999999), t.title;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("folderIdsJson", SerializeJsonArray(scopedFolderIds));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<MissingCoreMetadataFileDto>();
        var seenFiles = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var audioFileId = reader.GetInt64(1);
            if (!seenFiles.Add(audioFileId))
            {
                continue;
            }

            var filePath = reader.GetString(3);
            var title = await ReadNullableStringAsync(reader, 5, cancellationToken);
            var artist = await ReadNullableStringAsync(reader, 7, cancellationToken);
            var album = await ReadNullableStringAsync(reader, 9, cancellationToken);
            var albumArtist = await ReadNullableStringAsync(reader, 10, cancellationToken);
            var trackNumber = await ReadNullableIntAsync(reader, 12, cancellationToken);
            var repair = BuildMissingCoreMetadataRepair(filePath, title, artist, album, albumArtist, trackNumber);
            if (repair.Fields.Count == 0)
            {
                continue;
            }

            var hasCoreFieldGap = repair.Fields.Exists(static field =>
                !string.Equals(field, "Filename prefix", StringComparison.OrdinalIgnoreCase));
            if (!hasCoreFieldGap || !File.Exists(filePath))
            {
                continue;
            }

            results.Add(new MissingCoreMetadataFileDto(
                reader.GetInt64(0),
                audioFileId,
                reader.GetInt64(2),
                filePath,
                repair.Fields,
                repair.Score));
        }

        return results
            .OrderByDescending(static file => file.RepairScore)
            .ThenBy(static file => file.FolderId)
            .ThenBy(static file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (List<string> Fields, int Score) BuildMissingCoreMetadataRepair(
        string filePath,
        string? title,
        string? artist,
        string? album,
        string? albumArtist,
        int? trackNumber)
    {
        var fields = new List<string>();
        var score = 0;
        AddWeakMetadataRepair(fields, ref score, "Title", title, 30);
        AddWeakMetadataRepair(fields, ref score, "Artist", artist, 45);
        AddWeakMetadataRepair(fields, ref score, "Album", album, 25);
        AddWeakMetadataRepair(fields, ref score, "Album Artist", albumArtist, 55);
        if (!trackNumber.HasValue || trackNumber.Value <= 0)
        {
            fields.Add("Track number");
            score += 20;
        }

        if (TrackIdentityTrust.HasRepeatedNumericFilenamePrefix(filePath))
        {
            fields.Add("Filename prefix");
            score += 40;
        }

        return (fields, score);
    }

    private static void AddWeakMetadataRepair(
        ICollection<string> fields,
        ref int score,
        string field,
        string? value,
        int weight)
    {
        if (!IsMissingOrWeakMetadata(value))
        {
            return;
        }

        fields.Add(field);
        score += weight;
    }

    private static async Task<QualityScanTrackDto> ReadQualityScanTrackDtoAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var bestFormatRank = NormalizePositiveInt(await ReadNullableIntAsync(reader, 10, cancellationToken));
        var bestBitrateKbps = NormalizePositiveInt(await ReadNullableIntAsync(reader, 14, cancellationToken));
        var bestBitsPerSample = NormalizePositiveInt(await ReadNullableIntAsync(reader, 15, cancellationToken));
        var bestSampleRateHz = NormalizePositiveInt(await ReadNullableIntAsync(reader, 16, cancellationToken));

        return new QualityScanTrackDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await ReadNullableStringAsync(reader, 4, cancellationToken) ?? string.Empty,
            await ReadNullableIntAsync(reader, 5, cancellationToken),
            await ReadNullableIntAsync(reader, 6, cancellationToken) ?? 0,
            await ReadNullableIntAsync(reader, 7, cancellationToken) ?? 0,
            await ReadNullableStringAsync(reader, 8, cancellationToken) ?? string.Empty,
            await ReadNullableInt64Async(reader, 9, cancellationToken),
            bestFormatRank,
            await ReadNullableStringAsync(reader, 11, cancellationToken) ?? "unknown",
            await ReadNullableStringAsync(reader, 12, cancellationToken),
            await ReadNullableStringAsync(reader, 13, cancellationToken),
            bestBitrateKbps,
            bestBitsPerSample,
            bestSampleRateHz,
            reader.GetInt64(17),
            reader.GetInt64(18),
            reader.GetInt64(19),
            await ReadNullableStringAsync(reader, 20, cancellationToken) ?? string.Empty,
            await ReadNullableIntAsync(reader, 21, cancellationToken),
            await ReadNullableIntAsync(reader, 22, cancellationToken),
            await ReadNullableIntAsync(reader, 23, cancellationToken));
    }

    private static int? NormalizeQualityScanFormatRank(string? minFormat)
    {
        var normalized = minFormat?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "lossy" => 1,
            "lossless" => 2,
            "hi_res_lossless" => 3,
            "hi-res-lossless" => 3,
            "hires_lossless" => 3,
            "hires" => 3,
            "hi_res" => 3,
            AtmosVariant => 4,
            _ => null
        };
    }

    private static int? NormalizePositiveInt(int? value)
    {
        return value.HasValue && value.Value > 0
            ? value.Value
            : null;
    }

    public async Task<bool> IsQueuedAsync(string artistName, string trackTitle, int? durationMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = $@"
SELECT EXISTS(
    SELECT 1
    FROM download_task dt
WHERE LOWER(dt.artist_name) = LOWER(@artistName)
  AND LOWER(dt.track_title) = LOWER(@trackTitle)
  AND dt.status IN ('queued', 'running', 'paused')
  AND (@{DurationMsField} IS NULL OR dt.duration_ms IS NULL OR ABS(dt.duration_ms - @{DurationMsField}) <= 2000)
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        command.Parameters.AddWithValue(DurationMsField, (object?)durationMs ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
    }

    public async Task<bool> ExistsTrackSourceAsync(
        string source,
        string sourceId,
        string? audioVariant = null,
        CancellationToken cancellationToken = default)
    {
        var requireAtmosVariant = NormalizeAudioVariantFlag(audioVariant);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string defaultSql = @"
SELECT f.root_path, af.relative_path, af.path
    FROM track_source te
    JOIN track_local tl ON tl.track_id = te.track_id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE te.source = @source
      AND te.source_id = @sourceId;";
        const string variantSql = @"
SELECT f.root_path, af.relative_path, af.path
    FROM track_source te
    JOIN track_local tl ON tl.track_id = te.track_id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE te.source = @source
      AND te.source_id = @sourceId
      AND (
          CASE
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
              WHEN (
                  LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                  OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                  OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                  OR (
                      (
                          LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                          OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                      )
                      AND af.channels IS NOT NULL
                      AND af.channels > 2
                  )
                  OR (
                      (
                          LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/atmos/%'
                          OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/dolby atmos/%'
                          OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/spatial/%'
                          OR LOWER(COALESCE(af.path, '')) LIKE '%atmos%'
                      )
                      AND (
                          (af.channels IS NOT NULL AND af.channels > 2)
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                          OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                          OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                      )
                  )
              ) THEN 1
              ELSE 0
          END
      ) = @requireAtmos;";
        await using var command = new SqliteCommand(requireAtmosVariant.HasValue ? variantSql : defaultSql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        if (requireAtmosVariant.HasValue)
        {
            command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.Value);
        }
        return await AnyStoredAudioFileExistsAsync(command, cancellationToken);
    }

    public sealed record LibraryExistenceInput(
        string? Isrc,
        string? TrackTitle,
        string? ArtistName,
        int? DurationMs,
        string? Source = null,
        string? SourceId = null,
        string? AlbumTitle = null,
        bool? Explicit = null);

    public sealed record LocalTrackIdentityResult(
        long? LocalTrackId,
        string MatchType,
        string Reason,
        IReadOnlyList<long> CandidateTrackIds,
        int? BestQualityRank = null)
    {
        public bool Exists => LocalTrackId.HasValue || CandidateTrackIds.Count > 0;
        public bool IsAmbiguous => string.Equals(MatchType, "ambiguous", StringComparison.Ordinal);
    }

    public sealed record LocalTrackResolutionCandidate(
        long TrackId,
        string FilePath,
        string RootPath,
        long? FolderId,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        int QualityRank,
        int MetadataRichness,
        string? Isrc,
        IReadOnlyDictionary<string, string> SourceIds,
        IReadOnlySet<string> PopulatedTags);

    private sealed record LocalTrackMetadataCandidate(
        long TrackId,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        int QualityRank,
        int MetadataRichness);

    private const string LocalTrackCandidateQualitySql = @"
MAX(COALESCE(
    af.quality_rank,
    CASE
        WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 5
        WHEN COALESCE(af.bits_per_sample, 0) >= 24 OR COALESCE(af.sample_rate_hz, 0) > 48000 THEN 4
        WHEN COALESCE(af.bits_per_sample, 0) >= 16
          OR LOWER(COALESCE(af.codec, '')) LIKE '%flac%'
          OR LOWER(COALESCE(af.codec, '')) LIKE '%alac%'
          OR LOWER(COALESCE(af.codec, '')) LIKE '%lossless%'
          OR LOWER(COALESCE(af.codec, '')) LIKE '%pcm%' THEN 3
        WHEN COALESCE(af.bitrate_kbps, af.bitrate, 0) >= 192 THEN 2
        ELSE 1
    END,
    0))";

    private const string LocalTrackBatchVariantPredicateSql = @"
(
    i.require_atmos IS NULL
    OR i.require_atmos = 2
    OR (
        CASE
            WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos'
              OR COALESCE(af.channels, 0) > 2
              OR LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
              OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
            THEN 1
            ELSE 0
        END
    ) = i.require_atmos
)";

    private const string LocalTrackBatchCandidateQualitySql = @"
MAX(
    COALESCE(
        af.quality_rank,
        CASE
            WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 5
            WHEN COALESCE(af.bits_per_sample, 0) >= 24 OR COALESCE(af.sample_rate_hz, 0) > 48000 THEN 4
            WHEN COALESCE(af.bits_per_sample, 0) >= 16
              OR LOWER(COALESCE(af.codec, '')) LIKE '%flac%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%alac%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%lossless%'
              OR LOWER(COALESCE(af.codec, '')) LIKE '%pcm%' THEN 3
            WHEN COALESCE(af.bitrate_kbps, af.bitrate, 0) >= 192 THEN 2
            ELSE 1
        END,
        0)
    - CASE
        WHEN i.require_atmos = 2
         AND (
            LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos'
            OR COALESCE(af.channels, 0) > 2
            OR LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
            OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
            OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
            OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
            OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
            OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
            OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
         )
        THEN 100
        ELSE 0
      END
)";

    private const string LocalTrackMetadataRichnessSql = @"
(
    CASE WHEN NULLIF(TRIM(t.tag_title), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_artist), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_album), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_album_artist), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_version), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_label), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_catalog_number), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN t.tag_bpm IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_key), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN t.tag_track_total IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN COALESCE(t.tag_duration_ms, t.duration_ms) IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN t.tag_year IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN t.tag_track_no IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN t.tag_disc IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_genre), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_isrc), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_release_date), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_publish_date), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_url), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_release_id), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_track_id), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.tag_meta_tagged_date), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.lyrics_status), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.lyrics_type), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.lyrics_unsynced), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.lyrics_synced), '') IS NOT NULL THEN 1 ELSE 0 END +
    CASE WHEN NULLIF(TRIM(t.metadata_json), '') IS NOT NULL AND TRIM(t.metadata_json) <> '{}' THEN 1 ELSE 0 END +
    (SELECT COUNT(*) FROM track_genre richness_genre WHERE richness_genre.track_id=t.id) +
    (SELECT COUNT(*) FROM track_style richness_style WHERE richness_style.track_id=t.id) +
    (SELECT COUNT(*) FROM track_mood richness_mood WHERE richness_mood.track_id=t.id) +
    (SELECT COUNT(*) FROM track_remixer richness_remixer WHERE richness_remixer.track_id=t.id) +
    (SELECT COUNT(*) FROM track_other_tag richness_other WHERE richness_other.track_id=t.id)
)";

    private sealed record LocalTrackBatchLookup(
        int Index,
        LibraryExistenceInput Input,
        string TrackTitle,
        string ArtistName,
        string ArtistSearch,
        string? NormalizedIsrc,
        string? NormalizedSource,
        string? NormalizedSourceId,
        int? RequireAtmosVariant);

    private static readonly LocalTrackIdentityResult LocalLibraryNotConfiguredIdentity =
        new(null, "none", "The local library is not configured.", Array.Empty<long>());

    private static readonly LocalTrackIdentityResult MissingTrackMetadataIdentity =
        new(null, "none", "Title and artist metadata are required.", Array.Empty<long>());

    private static readonly LocalTrackIdentityResult NoLocalTrackMetadataMatchIdentity =
        new(null, "none", "No local track matched the stored or tagged metadata.", Array.Empty<long>());

    private static bool HasResolvedLocalIdentityDecision(LocalTrackIdentityResult result)
        => result.LocalTrackId.HasValue || result.IsAmbiguous;

    public async Task<LocalTrackIdentityResult> ResolveLocalTrackIdentityAsync(
        LibraryExistenceInput input,
        long? libraryId = null,
        long? folderId = null,
        string? audioVariant = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new LocalTrackIdentityResult(null, "none", "The local library is not configured.", Array.Empty<long>());
        }

        var requireAtmosVariant = NormalizeAudioVariantFlag(audioVariant);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var exactIdentity = await FindExactLocalTrackByIsrcAsync(
            connection, input.Isrc, libraryId, folderId, requireAtmosVariant, cancellationToken);
        if (exactIdentity is not null)
        {
            return exactIdentity;
        }

        exactIdentity = await FindExactLocalTrackIdAsync(
            connection, input.Source, input.SourceId, libraryId, folderId, requireAtmosVariant, cancellationToken);
        if (exactIdentity is not null)
        {
            return exactIdentity;
        }

        return await ResolveLocalTrackByMetadataAsync(connection, input, libraryId, folderId, requireAtmosVariant, cancellationToken);
    }

    private static async Task<LocalTrackIdentityResult?> FindExactLocalTrackByIsrcAsync(
        SqliteConnection connection,
        string? isrc,
        long? libraryId,
        long? folderId,
        int? requireAtmosVariant,
        CancellationToken cancellationToken)
    {
        var normalizedIsrc = isrc?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedIsrc) || normalizedIsrc.Length > 64)
        {
            return null;
        }

        var sql = $@"
SELECT t.id,
       COALESCE(NULLIF(t.tag_title, ''), t.title),
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name),
       COALESCE(NULLIF(t.tag_album, ''), al.title),
       COALESCE(t.tag_duration_ms, t.duration_ms, MAX(af.duration_ms)),
       {LocalTrackCandidateQualitySql},
       {LocalTrackMetadataRichnessSql}
FROM track t
JOIN album al ON al.id=t.album_id
JOIN artist ar ON ar.id=al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
LEFT JOIN track_source ts ON ts.track_id = t.id AND LOWER(ts.source) = 'isrc'
WHERE f.enabled = TRUE
  AND (@libraryId IS NULL OR f.library_id = @libraryId)
  AND (@folderId IS NULL OR f.id = @folderId)
  AND (LOWER(t.tag_isrc) = LOWER(@isrc) OR LOWER(ts.source_id) = LOWER(@isrc))
  AND (
      @requireAtmos IS NULL
      OR (
          CASE
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
              WHEN LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                   OR ((LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                        OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.mlp'))
                       AND COALESCE(af.channels, 0) > 2)
                  THEN 1
              ELSE 0
          END
      ) = @requireAtmos
  )
GROUP BY t.id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("isrc", normalizedIsrc);
        command.Parameters.AddWithValue(LibraryIdField, (object?)libraryId ?? DBNull.Value);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        var candidates = await ReadLocalTrackIdentityCandidatesAsync(command, cancellationToken);
        return candidates.Count == 0
            ? null
            : BuildLocalTrackIdentityResult(candidates.Select(static candidate => (candidate, 0)).ToList(), "isrc", "Matched the stored ISRC.");
    }

    private static async Task<LocalTrackIdentityResult?> FindExactLocalTrackIdAsync(
        SqliteConnection connection,
        string? source,
        string? sourceId,
        long? libraryId,
        long? folderId,
        int? requireAtmosVariant,
        CancellationToken cancellationToken)
    {
        var normalizedSource = source?.Trim();
        var normalizedSourceId = sourceId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSource)
            || string.IsNullOrWhiteSpace(normalizedSourceId)
            || normalizedSource.Length > 64
            || normalizedSourceId.Length > 256)
        {
            return null;
        }

        var sql = $@"
SELECT t.id,
       COALESCE(NULLIF(t.tag_title, ''), t.title),
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name),
       COALESCE(NULLIF(t.tag_album, ''), al.title),
       COALESCE(t.tag_duration_ms, t.duration_ms, MAX(af.duration_ms)),
       {LocalTrackCandidateQualitySql},
       {LocalTrackMetadataRichnessSql}
FROM track_source ts
JOIN track t ON t.id=ts.track_id
JOIN album al ON al.id=t.album_id
JOIN artist ar ON ar.id=al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = TRUE
  AND (@libraryId IS NULL OR f.library_id = @libraryId)
  AND (@folderId IS NULL OR f.id = @folderId)
  AND LOWER(ts.source) = LOWER(@source)
  AND (
      LOWER(ts.source_id) = LOWER(@sourceId)
      OR INSTR(';' || LOWER(ts.source_id) || ';', ';' || LOWER(@sourceId) || ';') > 0
  )
  AND (
      @requireAtmos IS NULL
      OR (
          CASE
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
              WHEN LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                   OR ((LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                        OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.mlp'))
                       AND COALESCE(af.channels, 0) > 2)
                  THEN 1
              ELSE 0
          END
      ) = @requireAtmos
  )
GROUP BY t.id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue(LibraryIdField, (object?)libraryId ?? DBNull.Value);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        var candidates = await ReadLocalTrackIdentityCandidatesAsync(command, cancellationToken);
        return candidates.Count == 0
            ? null
            : BuildLocalTrackIdentityResult(candidates.Select(static candidate => (candidate, 0)).ToList(), "source_id", "Matched the stored source track ID.");
    }

    private static async Task<LocalTrackIdentityResult> ResolveLocalTrackByMetadataAsync(
        SqliteConnection connection,
        LibraryExistenceInput input,
        long? libraryId,
        long? folderId,
        int? requireAtmosVariant,
        CancellationToken cancellationToken)
    {
        if (!TryBuildTrackLookup(input, out var trackTitle, out var artistName, out var artistSearch))
        {
            return new LocalTrackIdentityResult(null, "none", "Title and artist metadata are required.", Array.Empty<long>());
        }

        var sql = $@"
SELECT
       t.id,
       COALESCE(NULLIF(t.tag_title, ''), t.title) AS match_title,
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name) AS match_artist,
       COALESCE(NULLIF(t.tag_album, ''), al.title) AS match_album,
       COALESCE(t.tag_duration_ms, t.duration_ms, MAX(af.duration_ms)) AS match_duration_ms,
       {LocalTrackCandidateQualitySql} AS quality_rank,
       {LocalTrackMetadataRichnessSql} AS metadata_richness
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = TRUE
  AND (@libraryId IS NULL OR f.library_id = @libraryId)
  AND (@folderId IS NULL OR f.id = @folderId)
  AND (
      LOWER(ar.name) LIKE LOWER(@artistSearch)
      OR LOWER(COALESCE(t.tag_artist, '')) LIKE LOWER(@artistSearch)
      OR LOWER(COALESCE(t.tag_album_artist, '')) LIKE LOWER(@artistSearch)
  )
  AND (
      @requireAtmos IS NULL
      OR (
          CASE
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
              WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
              WHEN LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                   OR ((LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                        OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                        OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.mlp'))
                       AND COALESCE(af.channels, 0) > 2)
                  THEN 1
              ELSE 0
          END
      ) = @requireAtmos
  )
GROUP BY t.id
LIMIT 100;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(ArtistSearchParameter, artistSearch);
        command.Parameters.AddWithValue(LibraryIdField, (object?)libraryId ?? DBNull.Value);
        command.Parameters.AddWithValue(FolderIdParameter, (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var scored = new List<(LocalTrackMetadataCandidate Candidate, int Score)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidate = new LocalTrackMetadataCandidate(
                reader.GetInt64(0),
                await ReadNullableStringAsync(reader, 1, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 2, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 3, cancellationToken) ?? string.Empty,
                await ReadNullableIntAsync(reader, 4, cancellationToken),
                await ReadNullableIntAsync(reader, 5, cancellationToken) ?? 0,
                await ReadNullableIntAsync(reader, 6, cancellationToken) ?? 0);

            if (!TryScoreLocalTrackMetadataCandidate(input, trackTitle, artistName, candidate, out var score))
            {
                continue;
            }

            scored.Add((candidate, score));
        }

        if (scored.Count == 0)
        {
            return new LocalTrackIdentityResult(null, "none", "No local track matched the stored or tagged metadata.", Array.Empty<long>());
        }

        return BuildLocalTrackMetadataIdentityResult(scored);
    }

    private static bool TryScoreLocalTrackMetadataCandidate(
        LibraryExistenceInput input,
        string trackTitle,
        string artistName,
        LocalTrackMetadataCandidate candidate,
        out int score)
    {
        score = 0;
        if (!TrackTitleMatcher.ArtistsMatch(artistName, candidate.Artist)
            || !TrackTitleMatcher.TitlesMatch(trackTitle, candidate.Title))
        {
            return false;
        }

        score = 1000;
        if (!string.IsNullOrWhiteSpace(input.AlbumTitle)
            && TrackTitleMatcher.TitlesMatch(input.AlbumTitle, candidate.Album))
        {
            score += 100;
        }

        if (input.DurationMs.HasValue && candidate.DurationMs.HasValue)
        {
            var difference = Math.Abs(input.DurationMs.Value - candidate.DurationMs.Value);
            score += difference <= 2000 ? 75 : difference <= 10000 ? 25 : 0;
        }

        return true;
    }

    private static LocalTrackIdentityResult BuildLocalTrackMetadataIdentityResult(
        IReadOnlyList<(LocalTrackMetadataCandidate Candidate, int Score)> scored)
        => BuildLocalTrackIdentityResult(scored, null, null);

    private static LocalTrackIdentityResult BuildLocalTrackIdentityResult(
        IReadOnlyList<(LocalTrackMetadataCandidate Candidate, int Score)> scored,
        string? exactMatchType,
        string? exactReason)
    {
        var ordered = scored
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Candidate.QualityRank)
            .ThenByDescending(static item => item.Candidate.MetadataRichness)
            .ToList();
        var best = ordered[0];
        var competing = ordered
            .Where(item => item.Score == best.Score
                && item.Candidate.QualityRank == best.Candidate.QualityRank
                && item.Candidate.MetadataRichness == best.Candidate.MetadataRichness)
            .Select(static item => item.Candidate.TrackId)
            .Distinct()
            .ToArray();
        if (competing.Length > 1)
        {
            return new LocalTrackIdentityResult(
                null,
                "ambiguous",
                "Multiple local files have equal identity confidence, audio quality, and metadata completeness.",
                competing,
                best.Candidate.QualityRank);
        }

        return new LocalTrackIdentityResult(
            best.Candidate.TrackId,
            exactMatchType ?? (best.Score >= 1100 ? "metadata_exact" : "metadata_equivalent"),
            exactReason ?? "Matched the stored and tagged title, artist, album, and duration metadata.",
            new[] { best.Candidate.TrackId },
            best.Candidate.QualityRank);
    }

    private static async Task<List<LocalTrackMetadataCandidate>> ReadLocalTrackIdentityCandidatesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var candidates = new List<LocalTrackMetadataCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new LocalTrackMetadataCandidate(
                reader.GetInt64(0),
                await ReadNullableStringAsync(reader, 1, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 2, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 3, cancellationToken) ?? string.Empty,
                await ReadNullableIntAsync(reader, 4, cancellationToken),
                await ReadNullableIntAsync(reader, 5, cancellationToken) ?? 0,
                await ReadNullableIntAsync(reader, 6, cancellationToken) ?? 0));
        }

        return candidates;
    }

    public async Task<IReadOnlyList<LocalTrackResolutionCandidate>> GetLocalTrackResolutionCandidatesAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedIds = trackIds.Where(static id => id > 0).Distinct().OrderBy(static id => id).ToArray();
        if (!IsConfigured || normalizedIds.Length == 0)
        {
            return Array.Empty<LocalTrackResolutionCandidate>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = (SqliteTransaction)transaction;
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS temp_local_resolution_track_id (track_id INTEGER PRIMARY KEY); DELETE FROM temp_local_resolution_track_id;";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT OR IGNORE INTO temp_local_resolution_track_id(track_id) VALUES (@trackId);";
            var parameter = insert.Parameters.Add("trackId", SqliteType.Integer);
            foreach (var trackId in normalizedIds)
            {
                parameter.Value = trackId;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var candidates = new List<LocalTrackResolutionCandidate>(normalizedIds.Length);
        const string sql = @"
SELECT t.id,
       af.path,
       af.relative_path,
       f.root_path,
       f.id,
       COALESCE(NULLIF(t.tag_title, ''), t.title),
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name),
       COALESCE(NULLIF(t.tag_album, ''), al.title),
       COALESCE(t.tag_duration_ms, t.duration_ms, af.duration_ms),
       COALESCE(
           af.quality_rank,
           CASE
               WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 5
               WHEN COALESCE(af.bits_per_sample, 0) >= 24 OR COALESCE(af.sample_rate_hz, 0) > 48000 THEN 4
               WHEN COALESCE(af.bits_per_sample, 0) >= 16
                 OR LOWER(COALESCE(af.codec, '')) LIKE '%flac%'
                 OR LOWER(COALESCE(af.codec, '')) LIKE '%alac%'
                 OR LOWER(COALESCE(af.codec, '')) LIKE '%lossless%'
                 OR LOWER(COALESCE(af.codec, '')) LIKE '%pcm%' THEN 3
               WHEN COALESCE(af.bitrate_kbps, af.bitrate, 0) >= 192 THEN 2
               ELSE 1
           END),
       " + LocalTrackMetadataRichnessSql + @",
       NULLIF(TRIM(t.tag_isrc), ''),
       (CASE WHEN NULLIF(TRIM(COALESCE(t.tag_title, t.title)), '') IS NOT NULL THEN 'Title|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(COALESCE(t.tag_artist, ar.name)), '') IS NOT NULL THEN 'Artist|Artists|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(COALESCE(t.tag_album, al.title)), '') IS NOT NULL THEN 'Album|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_album_artist), '') IS NOT NULL THEN 'AlbumArtist|' ELSE '' END) ||
       (CASE WHEN COALESCE(t.tag_track_no, t.track_no) IS NOT NULL THEN 'TrackNumber|' ELSE '' END) ||
       (CASE WHEN t.tag_track_total IS NOT NULL THEN 'TrackTotal|' ELSE '' END) ||
       (CASE WHEN COALESCE(t.tag_disc, t.disc) IS NOT NULL THEN 'DiscNumber|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_genre), '') IS NOT NULL THEN 'Genre|' ELSE '' END) ||
       (CASE WHEN t.tag_year IS NOT NULL THEN 'Year|Date|' ELSE '' END) ||
       (CASE WHEN COALESCE(t.tag_duration_ms, t.duration_ms, af.duration_ms) IS NOT NULL THEN 'Duration|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_isrc), '') IS NOT NULL THEN 'Isrc|RecordingId|' ELSE '' END) ||
       (CASE WHEN t.tag_bpm IS NOT NULL THEN 'Bpm|Tempo|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_key), '') IS NOT NULL THEN 'Key|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_label), '') IS NOT NULL THEN 'Label|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_catalog_number), '') IS NOT NULL THEN 'CatalogNumber|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_release_date), '') IS NOT NULL THEN 'ReleaseDate|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_publish_date), '') IS NOT NULL THEN 'PublishDate|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_url), '') IS NOT NULL THEN 'Url|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_release_id), '') IS NOT NULL THEN 'ReleaseId|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.tag_track_id), '') IS NOT NULL THEN 'TrackId|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.lyrics_unsynced), '') IS NOT NULL THEN 'UnsyncedLyrics|' ELSE '' END) ||
       (CASE WHEN NULLIF(TRIM(t.lyrics_synced), '') IS NOT NULL THEN 'SyncedLyrics|' ELSE '' END)
FROM temp_local_resolution_track_id requested
JOIN track t ON t.id = requested.track_id
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = TRUE
ORDER BY t.id,
         COALESCE(
             af.quality_rank,
             CASE
                 WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 5
                 WHEN COALESCE(af.bits_per_sample, 0) >= 24 OR COALESCE(af.sample_rate_hz, 0) > 48000 THEN 4
                 WHEN COALESCE(af.bits_per_sample, 0) >= 16
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%flac%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%alac%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%lossless%'
                   OR LOWER(COALESCE(af.codec, '')) LIKE '%pcm%' THEN 3
                 WHEN COALESCE(af.bitrate_kbps, af.bitrate, 0) >= 192 THEN 2
                 ELSE 1
             END) DESC,
         af.size DESC,
         af.id DESC;";
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var seen = new HashSet<long>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var trackId = reader.GetInt64(0);
                if (!seen.Add(trackId))
                {
                    continue;
                }

                var storedPath = await ReadNullableStringAsync(reader, 1, cancellationToken);
                var relativePath = await ReadNullableStringAsync(reader, 2, cancellationToken);
                var rootPath = await ReadNullableStringAsync(reader, 3, cancellationToken);
                var filePath = BuildAbsolutePath(rootPath, relativePath, storedPath) ?? string.Empty;
                var identity = await GetLocalTrackIdentityAsync(trackId, cancellationToken);
                candidates.Add(new LocalTrackResolutionCandidate(
                    trackId,
                    filePath,
                    rootPath ?? string.Empty,
                    await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt64(4),
                    await ReadNullableStringAsync(reader, 5, cancellationToken) ?? string.Empty,
                    await ReadNullableStringAsync(reader, 6, cancellationToken) ?? string.Empty,
                    await ReadNullableStringAsync(reader, 7, cancellationToken) ?? string.Empty,
                    await ReadNullableIntAsync(reader, 8, cancellationToken),
                    await ReadNullableIntAsync(reader, 9, cancellationToken) ?? 0,
                    await ReadNullableIntAsync(reader, 10, cancellationToken) ?? 0,
                    await ReadNullableStringAsync(reader, 11, cancellationToken),
                    identity?.SourceIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    (await ReadNullableStringAsync(reader, 12, cancellationToken) ?? string.Empty)
                        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return candidates;
    }

    public async Task<IReadOnlyList<bool>> ExistsInLibraryAsync(
        IReadOnlyList<LibraryExistenceInput> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            return Array.Empty<bool>();
        }

        var results = new bool[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            results[index] = (await ResolveLocalTrackIdentityAsync(
                inputs[index], cancellationToken: cancellationToken)).Exists;
        }

        return results;
    }

    public async Task<IReadOnlyList<LocalTrackIdentityResult>> ResolveLocalTrackIdentitiesAsync(
        IReadOnlyList<LibraryExistenceInput> inputs,
        CancellationToken cancellationToken = default,
        string? audioVariant = null)
    {
        if (inputs.Count == 0)
        {
            return Array.Empty<LocalTrackIdentityResult>();
        }

        if (!IsConfigured)
        {
            return Enumerable
                .Repeat(LocalLibraryNotConfiguredIdentity, inputs.Count)
                .ToArray();
        }

        var results = Enumerable
            .Repeat(NoLocalTrackMetadataMatchIdentity, inputs.Count)
            .ToArray();
        var lookups = BuildLocalTrackBatchLookups(
            inputs,
            results,
            string.Equals(audioVariant?.Trim(), "stereo_preferred", StringComparison.OrdinalIgnoreCase)
                ? 2
                : NormalizeAudioVariantFlag(audioVariant));
        if (lookups.Count == 0)
        {
            return results;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CreateLocalTrackBatchInputAsync(connection, lookups, cancellationToken);
        try
        {
            await ResolveBatchIsrcMatchesAsync(connection, results, cancellationToken);
            await ResolveBatchSourceMatchesAsync(connection, results, cancellationToken);
            await ResolveBatchMetadataMatchesAsync(connection, lookups, results, cancellationToken);
        }
        finally
        {
            await DropLocalTrackBatchInputAsync(connection, cancellationToken);
        }

        return results;
    }

    private static IReadOnlyList<LocalTrackBatchLookup> BuildLocalTrackBatchLookups(
        IReadOnlyList<LibraryExistenceInput> inputs,
        LocalTrackIdentityResult[] results,
        int? requireAtmosVariant)
    {
        var lookups = new List<LocalTrackBatchLookup>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var normalizedIsrc = NormalizeBatchExactValue(input.Isrc, 64);
            var normalizedSource = NormalizeBatchExactValue(input.Source, 64);
            var normalizedSourceId = NormalizeBatchExactValue(input.SourceId, 256);
            if (!TryBuildTrackLookup(input, out var trackTitle, out var artistName, out var artistSearch))
            {
                results[index] = MissingTrackMetadataIdentity;
                lookups.Add(new LocalTrackBatchLookup(
                    index,
                    input,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    normalizedIsrc,
                    normalizedSource,
                    normalizedSourceId,
                    requireAtmosVariant));
                continue;
            }

            lookups.Add(new LocalTrackBatchLookup(
                index,
                input,
                trackTitle,
                artistName,
                artistSearch,
                normalizedIsrc,
                normalizedSource,
                normalizedSourceId,
                requireAtmosVariant));
        }

        return lookups;
    }

    private static string? NormalizeBatchExactValue(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength
            ? null
            : normalized;
    }

    private static async Task CreateLocalTrackBatchInputAsync(
        SqliteConnection connection,
        IReadOnlyList<LocalTrackBatchLookup> lookups,
        CancellationToken cancellationToken)
    {
        await using (var create = new SqliteCommand(@"
DROP TABLE IF EXISTS temp_local_track_identity_input;
CREATE TEMP TABLE temp_local_track_identity_input (
    input_index INTEGER PRIMARY KEY,
    isrc TEXT,
    source TEXT,
    source_id TEXT,
    artist_search TEXT,
    require_atmos INTEGER
);", connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = new SqliteCommand(@"
INSERT INTO temp_local_track_identity_input (input_index, isrc, source, source_id, artist_search, require_atmos)
VALUES (@inputIndex, @isrc, @source, @sourceId, @artistSearch, @requireAtmos);", connection, transaction);
        var indexParameter = insert.Parameters.Add("inputIndex", SqliteType.Integer);
        var isrcParameter = insert.Parameters.Add("isrc", SqliteType.Text);
        var sourceParameter = insert.Parameters.Add("source", SqliteType.Text);
        var sourceIdParameter = insert.Parameters.Add("sourceId", SqliteType.Text);
        var artistSearchParameter = insert.Parameters.Add("artistSearch", SqliteType.Text);
        var requireAtmosParameter = insert.Parameters.Add("requireAtmos", SqliteType.Integer);
        foreach (var lookup in lookups)
        {
            indexParameter.Value = lookup.Index;
            isrcParameter.Value = (object?)lookup.NormalizedIsrc ?? DBNull.Value;
            sourceParameter.Value = (object?)lookup.NormalizedSource ?? DBNull.Value;
            sourceIdParameter.Value = (object?)lookup.NormalizedSourceId ?? DBNull.Value;
            artistSearchParameter.Value = string.IsNullOrWhiteSpace(lookup.ArtistSearch)
                ? (object)DBNull.Value
                : lookup.ArtistSearch;
            requireAtmosParameter.Value = (object?)lookup.RequireAtmosVariant ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task DropLocalTrackBatchInputAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand("DROP TABLE IF EXISTS temp_local_track_identity_input;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ResolveBatchIsrcMatchesAsync(
        SqliteConnection connection,
        LocalTrackIdentityResult[] results,
        CancellationToken cancellationToken)
    {
        var sql = $@"
SELECT i.input_index,
       t.id,
       COALESCE(NULLIF(t.tag_title, ''), t.title),
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name),
       COALESCE(NULLIF(t.tag_album, ''), al.title),
       COALESCE(t.tag_duration_ms, t.duration_ms, MAX(af.duration_ms)),
       {LocalTrackBatchCandidateQualitySql},
       {LocalTrackMetadataRichnessSql}
FROM temp_local_track_identity_input i
JOIN track t
JOIN album al ON al.id=t.album_id
JOIN artist ar ON ar.id=al.artist_id
JOIN track_local tl ON tl.track_id=t.id
JOIN audio_file af ON af.id=tl.audio_file_id
JOIN folder f ON f.id=af.folder_id
LEFT JOIN track_source ts ON ts.track_id=t.id AND LOWER(ts.source)='isrc'
WHERE f.enabled=TRUE
  AND i.isrc IS NOT NULL
  AND {LocalTrackBatchVariantPredicateSql}
  AND (LOWER(t.tag_isrc)=LOWER(i.isrc) OR LOWER(ts.source_id)=LOWER(i.isrc))
GROUP BY i.input_index, t.id;";
        await ResolveBatchExactMatchesAsync(connection, results, sql, "isrc", "Matched the stored ISRC.", cancellationToken);
    }

    private static async Task ResolveBatchSourceMatchesAsync(
        SqliteConnection connection,
        LocalTrackIdentityResult[] results,
        CancellationToken cancellationToken)
    {
        var sql = $@"
SELECT i.input_index,
       t.id,
       COALESCE(NULLIF(t.tag_title, ''), t.title),
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name),
       COALESCE(NULLIF(t.tag_album, ''), al.title),
       COALESCE(t.tag_duration_ms, t.duration_ms, MAX(af.duration_ms)),
       {LocalTrackBatchCandidateQualitySql},
       {LocalTrackMetadataRichnessSql}
FROM temp_local_track_identity_input i
JOIN track_source ts ON LOWER(ts.source)=LOWER(i.source)
JOIN track t ON t.id=ts.track_id
JOIN album al ON al.id=t.album_id
JOIN artist ar ON ar.id=al.artist_id
JOIN track_local tl ON tl.track_id=t.id
JOIN audio_file af ON af.id=tl.audio_file_id
JOIN folder f ON f.id=af.folder_id
WHERE f.enabled=TRUE
  AND i.source IS NOT NULL
  AND i.source_id IS NOT NULL
  AND {LocalTrackBatchVariantPredicateSql}
  AND (LOWER(ts.source_id)=LOWER(i.source_id)
       OR INSTR(';' || LOWER(ts.source_id) || ';', ';' || LOWER(i.source_id) || ';') > 0)
GROUP BY i.input_index, t.id;";
        await ResolveBatchExactMatchesAsync(connection, results, sql, "source_id", "Matched the stored source track ID.", cancellationToken);
    }

    private static async Task ResolveBatchExactMatchesAsync(
        SqliteConnection connection,
        LocalTrackIdentityResult[] results,
        string sql,
        string matchType,
        string reason,
        CancellationToken cancellationToken)
    {
        var candidatesByInput = new Dictionary<int, List<(LocalTrackMetadataCandidate Candidate, int Score)>>();
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var inputIndex = reader.GetInt32(0);
            if (inputIndex < 0 || inputIndex >= results.Length || HasResolvedLocalIdentityDecision(results[inputIndex]))
            {
                continue;
            }

            var candidate = new LocalTrackMetadataCandidate(
                reader.GetInt64(1),
                await ReadNullableStringAsync(reader, 2, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 3, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 4, cancellationToken) ?? string.Empty,
                await ReadNullableIntAsync(reader, 5, cancellationToken),
                await ReadNullableIntAsync(reader, 6, cancellationToken) ?? 0,
                await ReadNullableIntAsync(reader, 7, cancellationToken) ?? 0);
            if (!candidatesByInput.TryGetValue(inputIndex, out var candidates))
            {
                candidates = [];
                candidatesByInput[inputIndex] = candidates;
            }
            candidates.Add((candidate, 0));
        }

        foreach (var (inputIndex, candidates) in candidatesByInput)
        {
            results[inputIndex] = BuildLocalTrackIdentityResult(candidates, matchType, reason);
        }
    }

    private static async Task ResolveBatchMetadataMatchesAsync(
        SqliteConnection connection,
        IReadOnlyList<LocalTrackBatchLookup> lookups,
        LocalTrackIdentityResult[] results,
        CancellationToken cancellationToken)
    {
        var lookupByIndex = lookups.ToDictionary(static lookup => lookup.Index);
        var scoredByInput = new Dictionary<int, List<(LocalTrackMetadataCandidate Candidate, int Score)>>();
        var sql = $@"
SELECT i.input_index,
       t.id,
       COALESCE(NULLIF(t.tag_title, ''), t.title) AS match_title,
       COALESCE(NULLIF(t.tag_artist, ''), NULLIF(t.tag_album_artist, ''), ar.name) AS match_artist,
       COALESCE(NULLIF(t.tag_album, ''), al.title) AS match_album,
       COALESCE(t.tag_duration_ms, t.duration_ms, MAX(af.duration_ms)) AS match_duration_ms,
       {LocalTrackBatchCandidateQualitySql} AS quality_rank,
       {LocalTrackMetadataRichnessSql} AS metadata_richness
FROM temp_local_track_identity_input i
JOIN track t
JOIN album al ON al.id=t.album_id
JOIN artist ar ON ar.id=al.artist_id
JOIN track_local tl ON tl.track_id=t.id
JOIN audio_file af ON af.id=tl.audio_file_id
JOIN folder f ON f.id=af.folder_id
WHERE f.enabled=TRUE
  AND i.artist_search IS NOT NULL
  AND {LocalTrackBatchVariantPredicateSql}
  AND (LOWER(ar.name) LIKE LOWER(i.artist_search)
       OR LOWER(COALESCE(t.tag_artist, '')) LIKE LOWER(i.artist_search)
       OR LOWER(COALESCE(t.tag_album_artist, '')) LIKE LOWER(i.artist_search))
GROUP BY i.input_index, t.id
ORDER BY i.input_index
LIMIT 100000;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var inputIndex = reader.GetInt32(0);
            if (inputIndex < 0
                || inputIndex >= results.Length
                || HasResolvedLocalIdentityDecision(results[inputIndex])
                || !lookupByIndex.TryGetValue(inputIndex, out var lookup)
                || string.IsNullOrWhiteSpace(lookup.TrackTitle)
                || string.IsNullOrWhiteSpace(lookup.ArtistName))
            {
                continue;
            }

            var candidate = new LocalTrackMetadataCandidate(
                reader.GetInt64(1),
                await ReadNullableStringAsync(reader, 2, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 3, cancellationToken) ?? string.Empty,
                await ReadNullableStringAsync(reader, 4, cancellationToken) ?? string.Empty,
                await ReadNullableIntAsync(reader, 5, cancellationToken),
                await ReadNullableIntAsync(reader, 6, cancellationToken) ?? 0,
                await ReadNullableIntAsync(reader, 7, cancellationToken) ?? 0);

            if (!TryScoreLocalTrackMetadataCandidate(lookup.Input, lookup.TrackTitle, lookup.ArtistName, candidate, out var score))
            {
                continue;
            }

            if (!scoredByInput.TryGetValue(inputIndex, out var scored))
            {
                scored = new List<(LocalTrackMetadataCandidate Candidate, int Score)>();
                scoredByInput[inputIndex] = scored;
            }
            scored.Add((candidate, score));
        }

        foreach (var (inputIndex, scored) in scoredByInput)
        {
            if (HasResolvedLocalIdentityDecision(results[inputIndex]) || scored.Count == 0)
            {
                continue;
            }

            results[inputIndex] = BuildLocalTrackMetadataIdentityResult(scored);
        }
    }

    public async Task<IReadOnlyList<bool>> ExistsInLibraryAsync(
        long libraryId,
        long? folderId,
        IReadOnlyList<LibraryExistenceInput> inputs,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || inputs.Count == 0)
        {
            return Array.Empty<bool>();
        }

        var results = new bool[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            results[index] = (await ResolveLocalTrackIdentityAsync(
                inputs[index],
                libraryId,
                folderId,
                cancellationToken: cancellationToken)).Exists;
        }

        return results;
    }

    private static bool TryBuildTrackLookup(
        LibraryExistenceInput input,
        out string trackTitle,
        out string artistName,
        out string artistSearch)
    {
        trackTitle = input.TrackTitle?.Trim() ?? string.Empty;
        artistName = input.ArtistName?.Trim() ?? string.Empty;
        artistSearch = string.Empty;
        if (string.IsNullOrWhiteSpace(trackTitle) || string.IsNullOrWhiteSpace(artistName))
        {
            return false;
        }

        var primaryArtist = ArtistNameNormalizer.ExtractPrimaryArtist(artistName);
        artistSearch = $"%{(string.IsNullOrWhiteSpace(primaryArtist) ? artistName : primaryArtist).Trim()}%";
        return true;
    }


    public async Task<bool> ExistsTrackSourceInFolderAsync(
        string source,
        string sourceId,
        long folderId,
        string? audioVariant = null,
        CancellationToken cancellationToken = default)
    {
        var requireAtmosVariant = NormalizeAudioVariantFlag(audioVariant);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT f.root_path, af.relative_path, af.path
    FROM track_source te
    JOIN track_local tl ON tl.track_id = te.track_id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE te.source = @source
      AND te.source_id = @sourceId
      AND af.folder_id = @folderId
      AND (
          @requireAtmos IS NULL
          OR (
              CASE
                  WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
                  WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
                  WHEN (
                      LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                      OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                      OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                      OR (
                          (
                              LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                              OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                          )
                          AND af.channels IS NOT NULL
                          AND af.channels > 2
                      )
                      OR (
                          (
                              LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/atmos/%'
                              OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/dolby atmos/%'
                              OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/spatial/%'
                              OR LOWER(COALESCE(af.path, '')) LIKE '%atmos%'
                          )
                          AND (
                              (af.channels IS NOT NULL AND af.channels > 2)
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                              OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                          )
                      )
                  ) THEN 1
                  ELSE 0
              END
          ) = @requireAtmos
      )
;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        return await AnyStoredAudioFileExistsAsync(command, cancellationToken);
    }

    public async Task<bool> ExistsArtistSourceAsync(string source, string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT EXISTS(
    SELECT 1
    FROM artist_source ae
    WHERE ae.source = @source
      AND ae.source_id = @sourceId
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
    }

    public async Task<bool> ExistsAlbumSourceAsync(string source, string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT EXISTS(
    SELECT 1
    FROM album_source ae
    WHERE ae.source = @source
      AND ae.source_id = @sourceId
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
    }

    public async Task<bool> ExistsTrackByAlbumSourceAsync(
        string source,
        string albumSourceId,
        string trackTitle,
        string? artistSourceId,
        string? audioVariant = null,
        CancellationToken cancellationToken = default)
    {
        return await ExistsTrackByAlbumSourceCoreAsync(
            source,
            albumSourceId,
            trackTitle,
            artistSourceId,
            folderId: null,
            audioVariant,
            cancellationToken);
    }

    public async Task<bool> ExistsTrackByAlbumSourceInFolderAsync(
        string source,
        string albumSourceId,
        string trackTitle,
        string? artistSourceId,
        long folderId,
        string? audioVariant = null,
        CancellationToken cancellationToken = default)
    {
        return await ExistsTrackByAlbumSourceCoreAsync(
            source,
            albumSourceId,
            trackTitle,
            artistSourceId,
            folderId,
            audioVariant,
            cancellationToken);
    }

    private async Task<bool> ExistsTrackByAlbumSourceCoreAsync(
        string source,
        string albumSourceId,
        string trackTitle,
        string? artistSourceId,
        long? folderId,
        string? audioVariant,
        CancellationToken cancellationToken)
    {
        var requireAtmosVariant = NormalizeAudioVariantFlag(audioVariant);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT f.root_path, af.relative_path, af.path
    FROM album_source als
    JOIN album al ON al.id = als.album_id
    JOIN track t ON t.album_id = al.id
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    WHERE als.source = @source
      AND als.source_id = @albumSourceId
      AND LOWER(t.title) = LOWER(@trackTitle)
      AND (@folderId IS NULL OR af.folder_id = @folderId)
      AND (
          @requireAtmos IS NULL
          OR (
              CASE
                  WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'atmos' THEN 1
                  WHEN LOWER(TRIM(COALESCE(af.audio_variant, ''))) = 'stereo' THEN 0
                  WHEN (
                      LOWER(COALESCE(af.codec, '')) LIKE '%dolby atmos%'
                      OR LOWER(COALESCE(af.codec, '')) LIKE '%joc%'
                      OR LOWER(COALESCE(af.codec, '')) LIKE '%atmos%'
                      OR (
                          (
                              LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                              OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                          )
                          AND af.channels IS NOT NULL
                          AND af.channels > 2
                      )
                      OR (
                          (
                              LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/atmos/%'
                              OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/dolby atmos/%'
                              OR LOWER(REPLACE(COALESCE(af.path, ''), '\', '/')) LIKE '%/spatial/%'
                              OR LOWER(COALESCE(af.path, '')) LIKE '%atmos%'
                          )
                          AND (
                              (af.channels IS NOT NULL AND af.channels > 2)
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ec-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%eac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac-3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%ac3%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%truehd%'
                              OR LOWER(COALESCE(af.codec, '')) LIKE '%mlp%'
                              OR LOWER(COALESCE(af.extension, '')) IN ('.ec3', '.ac3', '.mlp')
                          )
                      )
                  ) THEN 1
                  ELSE 0
              END
          ) = @requireAtmos
      )
      AND (
          @artistSourceId IS NULL
          OR EXISTS (
              SELECT 1
              FROM artist_source ars
              WHERE ars.artist_id = al.artist_id
                AND ars.source = @source
                AND ars.source_id = @artistSourceId
          )
      )
;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue("albumSourceId", albumSourceId);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        command.Parameters.AddWithValue("artistSourceId", string.IsNullOrWhiteSpace(artistSourceId) ? (object)DBNull.Value : artistSourceId);
        command.Parameters.AddWithValue(FolderIdParameter, folderId.HasValue ? folderId.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        return await AnyStoredAudioFileExistsAsync(command, cancellationToken);
    }

    private static async Task<bool> AnyStoredAudioFileExistsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rootPath = await ReadNullableStringAsync(reader, 0, cancellationToken);
            var relativePath = await ReadNullableStringAsync(reader, 1, cancellationToken);
            var rawPath = await ReadNullableStringAsync(reader, 2, cancellationToken);
            var fullPath = BuildAbsolutePath(rootPath, relativePath, rawPath);
            if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
            {
                return true;
            }
        }

        return false;
    }

    private static int? NormalizeAudioVariantFlag(string? audioVariant)
    {
        var normalized = audioVariant?.Trim().ToLowerInvariant();
        return normalized switch
        {
            AtmosVariant => 1,
            "stereo" => 0,
            _ => null
        };
    }

    public async Task IngestLocalScanAsync(
        IReadOnlyList<FolderDto> folders,
        IReadOnlyList<LocalArtistScanDto> artists,
        IReadOnlyList<LocalAlbumScanDto> albums,
        IReadOnlyList<LocalTrackScanDto> tracks,
        bool pruneMissingArtists = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Video and podcast destinations are not part of the music library surface.
        // Purge any previously indexed local content from those folders before ingest.
        await PurgeNonLibraryFolderLocalContentAsync(connection, transaction, cancellationToken);

        var folderByDisplay = folders.ToDictionary(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase);
        var folderRoots = BuildFolderRoots(folders);
        var artistIdByName = await BuildArtistIdMapAsync(connection, transaction, artists, cancellationToken);
        var albumIdByKey = await BuildAlbumIdMapAsync(
            connection,
            transaction,
            albums,
            artistIdByName,
            folderByDisplay,
            cancellationToken);
        await IngestTrackRowsAsync(
            connection,
            transaction,
            tracks,
            artistIdByName,
            albumIdByKey,
            folderRoots,
            cancellationToken);

        await NormalizeTrackDurationsAsync(connection, transaction, cancellationToken);

        if (pruneMissingArtists)
        {
            await PruneMissingArtistsAsync(connection, transaction, artists, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static List<FolderRoot> BuildFolderRoots(IReadOnlyList<FolderDto> folders)
    {
        return folders
            .Select(folder => new FolderRoot(folder.Id, NormalizeRoot(folder.RootPath), folder.RootPath))
            .OrderByDescending(item => item.Root.Length)
            .ToList();
    }

    private static async Task<Dictionary<string, long>> BuildArtistIdMapAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LocalArtistScanDto> artists,
        CancellationToken cancellationToken)
    {
        var artistIdByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var artist in artists)
        {
            var artistId = await GetOrCreateArtistAsync(connection, transaction, artist, cancellationToken);
            artistIdByName[artist.Name] = artistId;
        }

        return artistIdByName;
    }

    private static async Task<Dictionary<string, long>> BuildAlbumIdMapAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LocalAlbumScanDto> albums,
        Dictionary<string, long> artistIdByName,
        Dictionary<string, FolderDto> folderByDisplay,
        CancellationToken cancellationToken)
    {
        var albumIdByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in albums)
        {
            if (!artistIdByName.TryGetValue(album.ArtistName, out var artistId))
            {
                continue;
            }

            var albumId = await GetOrCreateAlbumAsync(connection, transaction, artistId, album, cancellationToken);
            albumIdByKey[BuildAlbumKey(album.ArtistName, album.Title)] = albumId;
            foreach (var folder in album.LocalFolders
                .Select(folderName => folderByDisplay.TryGetValue(folderName, out var folder) ? folder : null)
                .Where(folder => folder is not null))
            {
                await EnsureAlbumLocalAsync(connection, transaction, albumId, folder!.Id, cancellationToken);
            }
        }

        return albumIdByKey;
    }

    private static async Task IngestTrackRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LocalTrackScanDto> tracks,
        IReadOnlyDictionary<string, long> artistIdByName,
        IReadOnlyDictionary<string, long> albumIdByKey,
        IReadOnlyList<FolderRoot> folderRoots,
        CancellationToken cancellationToken)
    {
        foreach (var track in tracks)
        {
            if (!TryResolveTrackCatalogIds(track, artistIdByName, albumIdByKey, out var artistId, out var albumId))
            {
                continue;
            }

            var folderRoot = FindFolderForPath(folderRoots, track.FilePath);
            if (folderRoot is null)
            {
                continue;
            }

            var trackId = await UpsertTrackAndLocalFileAsync(
                connection,
                transaction,
                albumId,
                folderRoot,
                track,
                cancellationToken);
            await IngestTrackSourcesAsync(connection, transaction, trackId, track, cancellationToken);
            await EnsureArtistAndAlbumSourcesAsync(connection, transaction, artistId, albumId, track, cancellationToken);
            if (!track.IsUnchanged)
            {
                await ReplaceTrackMultiTagsAsync(connection, transaction, trackId, track, cancellationToken);
            }
        }
    }

    private static bool TryResolveTrackCatalogIds(
        LocalTrackScanDto track,
        IReadOnlyDictionary<string, long> artistIdByName,
        IReadOnlyDictionary<string, long> albumIdByKey,
        out long artistId,
        out long albumId)
    {
        artistId = default;
        albumId = default;
        var albumKey = BuildAlbumKey(track.ArtistName, track.AlbumTitle);
        return albumIdByKey.TryGetValue(albumKey, out albumId)
               && artistIdByName.TryGetValue(track.ArtistName, out artistId);
    }

    private static async Task<long> UpsertTrackAndLocalFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long albumId,
        FolderRoot folderRoot,
        LocalTrackScanDto track,
        CancellationToken cancellationToken)
    {
        var trackId = await GetOrCreateTrackAsync(connection, transaction, albumId, track, cancellationToken);
        var relativePath = ComputeRelativePath(folderRoot.Root, track.FilePath);
        var audioFileId = await UpsertAudioFileAsync(
            connection,
            transaction,
            new AudioFileUpsertInput(
                track.FilePath,
                relativePath,
                folderRoot.Id,
                track.DurationMs,
                track.Codec,
                track.BitrateKbps,
                track.SampleRateHz,
                track.BitsPerSample,
                track.Channels,
                track.QualityRank,
                track.AudioVariant,
                track.IsUnchanged),
            cancellationToken);
        await EnsureTrackLocalAsync(connection, transaction, trackId, audioFileId, cancellationToken);
        return trackId;
    }

    private static async Task IngestTrackSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long trackId,
        LocalTrackScanDto track,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(track.DeezerTrackId))
        {
            await EnsureTrackSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(trackId, DeezerSource, track.DeezerTrackId!, null, null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            await EnsureTrackSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(trackId, "isrc", track.Isrc!, null, null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.SpotifyTrackId))
        {
            await EnsureTrackSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(trackId, SpotifySource, track.SpotifyTrackId!, BuildTrackUrl(SpotifySource, track.SpotifyTrackId), null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.AppleTrackId))
        {
            await EnsureTrackSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(trackId, AppleSource, track.AppleTrackId!, BuildTrackUrl(AppleSource, track.AppleTrackId), null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.Source) &&
            !string.IsNullOrWhiteSpace(track.SourceId))
        {
            var source = track.Source.Trim().ToLowerInvariant();
            if (!string.Equals(source, DeezerSource, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureTrackSourceAsync(
                    connection,
                    transaction,
                    new SourceUpsertInput(trackId, source, track.SourceId!, null, null),
                    cancellationToken);
            }
        }
    }

    private static async Task PruneMissingArtistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LocalArtistScanDto> artists,
        CancellationToken cancellationToken)
    {
        const string createTempSql = "CREATE TEMP TABLE IF NOT EXISTS scan_artist_keep (name TEXT PRIMARY KEY);";
        await ExecuteNonQueryAsync(connection, transaction, createTempSql, cancellationToken);
        const string clearTempSql = "DELETE FROM scan_artist_keep;";
        await ExecuteNonQueryAsync(connection, transaction, clearTempSql, cancellationToken);

        const string insertTempSql = "INSERT OR IGNORE INTO scan_artist_keep (name) VALUES (@name);";
        foreach (var artist in artists)
        {
            await using var insertCommand = new SqliteCommand(insertTempSql, connection, transaction);
            insertCommand.Parameters.AddWithValue("name", artist.Name);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteArtistsSql = @"
DELETE FROM artist
WHERE LOWER(name) NOT IN (SELECT LOWER(name) FROM scan_artist_keep);";
        await ExecuteNonQueryAsync(connection, transaction, deleteArtistsSql, cancellationToken);
    }

    private static async Task PurgeNonLibraryFolderLocalContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string selectFolderSql = @"
SELECT id
FROM folder
WHERE LOWER(TRIM(COALESCE(desired_quality_value, ''))) IN ('video', 'podcast')
   OR (
       desired_quality = 0
       AND (desired_quality_value IS NULL OR TRIM(desired_quality_value) = '')
   );";
        var folderIds = new List<long>();
        await using (var selectCommand = new SqliteCommand(selectFolderSql, connection, transaction))
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                folderIds.Add(reader.GetInt64(0));
            }
        }

        if (folderIds.Count == 0)
        {
            return;
        }

        const string createTempSql = "CREATE TEMP TABLE IF NOT EXISTS purge_non_library_folder (id INTEGER PRIMARY KEY);";
        await using (var createCommand = new SqliteCommand(createTempSql, connection, transaction))
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearTempSql = "DELETE FROM purge_non_library_folder;";
        await using (var clearCommand = new SqliteCommand(clearTempSql, connection, transaction))
        {
            await clearCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertTempSql = "INSERT OR IGNORE INTO purge_non_library_folder (id) VALUES (@id);";
        foreach (var folderId in folderIds)
        {
            await using var insertCommand = new SqliteCommand(insertTempSql, connection, transaction);
            insertCommand.Parameters.AddWithValue("id", folderId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteAlbumLocalSql = @"
DELETE FROM album_local
WHERE folder_id IN (SELECT id FROM purge_non_library_folder);";
        await using (var deleteAlbumLocalCommand = new SqliteCommand(deleteAlbumLocalSql, connection, transaction))
        {
            await deleteAlbumLocalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteTrackLocalSql = @"
DELETE FROM track_local
WHERE audio_file_id IN (
    SELECT af.id
    FROM audio_file af
    JOIN purge_non_library_folder p ON p.id = af.folder_id
);";
        await using (var deleteTrackLocalCommand = new SqliteCommand(deleteTrackLocalSql, connection, transaction))
        {
            await deleteTrackLocalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteAudioFileSql = @"
DELETE FROM audio_file
WHERE folder_id IN (SELECT id FROM purge_non_library_folder);";
        await using (var deleteAudioFileCommand = new SqliteCommand(deleteAudioFileSql, connection, transaction))
        {
            await deleteAudioFileCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await CleanupOrphansAsync(connection, transaction, cancellationToken);
    }

    private async Task EnsureSettingsRowAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureLibrarySettingsColumnsAsync(connection, cancellationToken);
        const string sql = "INSERT INTO library_settings (id) VALUES (1) ON CONFLICT DO NOTHING;";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureLibrarySettingsColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string pragmaSql = "PRAGMA table_info(library_settings);";
        await using (var pragmaCommand = new SqliteCommand(pragmaSql, connection))
        await using (var reader = await pragmaCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!await reader.IsDBNullAsync(1, cancellationToken))
                {
                    columns.Add(reader.GetString(1));
                }
            }
        }

        if (!columns.Contains("live_preview_ingest"))
        {
            const string alterSql = "ALTER TABLE library_settings ADD COLUMN live_preview_ingest INTEGER NOT NULL DEFAULT FALSE;";
            await using var alterCommand = new SqliteCommand(alterSql, connection);
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
            columns.Add("live_preview_ingest");
        }

        if (!columns.Contains("enable_signal_analysis"))
        {
            const string alterSql = "ALTER TABLE library_settings ADD COLUMN enable_signal_analysis INTEGER NOT NULL DEFAULT FALSE;";
            await using var alterCommand = new SqliteCommand(alterSql, connection);
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task EnsureQualityScannerAutomationSettingsRowAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO quality_scan_automation_settings (
    id,
    enabled,
    interval_minutes,
    scope,
    queue_atmos_alternatives,
    cooldown_minutes
)
VALUES (1, 0, 1440, 'watchlist', 0, 1440)
ON CONFLICT DO NOTHING;";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureScanRowAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "INSERT INTO library_scan_state (id) VALUES (1) ON CONFLICT DO NOTHING;";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeQualityScannerScope(string? scope)
    {
        if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "all";
        }

        return "watchlist";
    }

    private static string NormalizeBlocklistField(string? field)
    {
        var normalized = (field ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            TrackType => TrackType,
            TitleField => TrackType,
            ArtistType => ArtistType,
            AlbumType => AlbumType,
            GenreType => GenreType,
            _ => string.Empty
        };
    }

    private static string NormalizeBlocklistValue(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var compact = string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.ToLowerInvariant();
    }

    private static DateTimeOffset? ParseDateTimeOffsetOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, ParseDateStyles, out var parsed)
            ? parsed
            : null;
    }

    private static (bool ConvertEnabled, string? ConvertFormat, string? ConvertBitrate) NormalizeFolderConvertSettings(
        bool convertEnabled,
        string? convertFormat,
        string? convertBitrate)
    {
        if (!convertEnabled)
        {
            return (false, null, null);
        }

        return (
            true,
            NormalizeFolderConvertFormat(convertFormat),
            NormalizeFolderConvertBitrate(convertBitrate));
    }

    private sealed record FolderCommonParameters(
        string RootPath,
        string DisplayName,
        bool Enabled,
        long? LibraryId,
        int DesiredQualityNumeric,
        string DesiredQuality,
        bool ConvertEnabled,
        string? ConvertFormat,
        string? ConvertBitrate);

    private sealed record AudioFileUpsertInput(
        string FilePath,
        string RelativePath,
        long FolderId,
        int? DurationMs,
        string? Codec,
        int? BitrateKbps,
        int? SampleRateHz,
        int? BitsPerSample,
        int? Channels,
        int? QualityRank,
        string? AudioVariant,
        bool PreserveUnchangedTimestamp);

    private sealed record SourceUpsertInput(
        long EntityId,
        string Source,
        string SourceId,
        string? Url,
        string? Data);

    private static void AddFolderCommonParameters(
        SqliteCommand command,
        FolderCommonParameters parameters)
    {
        command.Parameters.AddWithValue("rootPath", parameters.RootPath);
        command.Parameters.AddWithValue("displayName", parameters.DisplayName);
        command.Parameters.AddWithValue("enabled", parameters.Enabled);
        command.Parameters.AddWithValue(LibraryIdField, (object?)parameters.LibraryId ?? DBNull.Value);
        command.Parameters.AddWithValue("desiredQualityNumeric", parameters.DesiredQualityNumeric);
        command.Parameters.AddWithValue("desiredQualityValue", parameters.DesiredQuality);
        command.Parameters.AddWithValue("convertEnabled", parameters.ConvertEnabled);
        command.Parameters.AddWithValue("convertFormat", (object?)parameters.ConvertFormat ?? DBNull.Value);
        command.Parameters.AddWithValue("convertBitrate", (object?)parameters.ConvertBitrate ?? DBNull.Value);
    }

    private static bool IsMusicFolderQuality(string? desiredQuality)
    {
        var normalized = desiredQuality?.Trim().ToLowerInvariant();
        return normalized is null
            || (!normalized.Contains("video", StringComparison.Ordinal)
                && !normalized.Contains("podcast", StringComparison.Ordinal));
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Library DB connection string not configured.");
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string pragmas = @"
PRAGMA foreign_keys=ON;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=30000;";
        await using var command = new SqliteCommand(pragmas, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long?> EnsureLibraryAsync(SqliteConnection connection, string? libraryName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return null;
        }

        const string lookupSql = "SELECT id FROM library WHERE name = @name;";
        await using (var lookup = new SqliteCommand(lookupSql, connection))
        {
            lookup.Parameters.AddWithValue("name", libraryName);
            var existing = await lookup.ExecuteScalarAsync(cancellationToken);
            if (existing is long existingId)
            {
                return existingId;
            }
            if (existing is int existingInt)
            {
                return existingInt;
            }
        }

        const string insertSql = "INSERT INTO library (name) VALUES (@name) RETURNING id;";
        await using var insert = new SqliteCommand(insertSql, connection);
        insert.Parameters.AddWithValue("name", libraryName);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        return inserted is long insertedId ? insertedId : Convert.ToInt64(inserted);
    }

    private static string ResolveCanonicalLibraryName(string? requestedName, string displayName)
    {
        var normalized = string.IsNullOrWhiteSpace(requestedName)
            ? displayName?.Trim()
            : requestedName.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "Library" : normalized;
    }

    private static async Task<long?> ResolveExistingFolderLibraryIdAsync(
        SqliteConnection connection,
        long folderId,
        string? requestedName,
        string displayName,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT library_id FROM folder WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", folderId);
        var existing = await command.ExecuteScalarAsync(cancellationToken);
        if (existing is not null and not DBNull)
        {
            return Convert.ToInt64(existing, CultureInfo.InvariantCulture);
        }

        return await EnsureLibraryAsync(
            connection,
            ResolveCanonicalLibraryName(requestedName, displayName),
            cancellationToken);
    }

    private static async Task CleanupOrphansAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string deleteTargetMetadata = @"
DELETE FROM media_server_track_variant_metadata
WHERE NOT EXISTS (
    SELECT 1 FROM track_local tl WHERE tl.track_id = media_server_track_variant_metadata.track_id
);
DELETE FROM media_server_track_metadata
WHERE NOT EXISTS (
    SELECT 1 FROM track_local tl WHERE tl.track_id = media_server_track_metadata.track_id
);
DELETE FROM track_plex_metadata
WHERE NOT EXISTS (
    SELECT 1 FROM track_local tl WHERE tl.track_id = track_plex_metadata.track_id
);";
        await using (var command = new SqliteCommand(deleteTargetMetadata, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteTracks = @"
DELETE FROM track
WHERE NOT EXISTS (
    SELECT 1 FROM track_local tl WHERE tl.track_id = track.id
);";
        await using (var command = new SqliteCommand(deleteTracks, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteAlbums = @"
DELETE FROM album
WHERE NOT EXISTS (
    SELECT 1 FROM track t WHERE t.album_id = album.id
) AND NOT EXISTS (
    SELECT 1 FROM album_local al WHERE al.album_id = album.id
);";
        await using (var command = new SqliteCommand(deleteAlbums, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteArtists = @"
DELETE FROM artist
WHERE NOT EXISTS (
    SELECT 1 FROM album al WHERE al.artist_id = artist.id
);";
        await using (var command = new SqliteCommand(deleteArtists, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task NullFolderReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        CancellationToken cancellationToken)
    {
        const string clearScanJobSql = @"
UPDATE scan_job
SET folder_id = NULL
WHERE folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearScanJobSql, connection, transaction))
        {
            command.Parameters.AddWithValue(FolderIdParameter, folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearPlaylistWatchPreferencesSql = @"
UPDATE playlist_watch_preferences
SET destination_folder_id = CASE
        WHEN destination_folder_id = @folderId THEN NULL
        ELSE destination_folder_id
    END,
    atmos_destination_folder_id = CASE
        WHEN atmos_destination_folder_id = @folderId THEN NULL
        ELSE atmos_destination_folder_id
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE destination_folder_id = @folderId
   OR atmos_destination_folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearPlaylistWatchPreferencesSql, connection, transaction))
        {
            command.Parameters.AddWithValue(FolderIdParameter, folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearArtistWatchlistSql = @"
UPDATE artist_watchlist
SET destination_folder_id = CASE WHEN destination_folder_id = @folderId THEN NULL ELSE destination_folder_id END,
    atmos_destination_folder_id = CASE WHEN atmos_destination_folder_id = @folderId THEN NULL ELSE atmos_destination_folder_id END
WHERE destination_folder_id = @folderId
   OR atmos_destination_folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearArtistWatchlistSql, connection, transaction))
        {
            command.Parameters.AddWithValue(FolderIdParameter, folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearQualityScanAutomationSql = @"
UPDATE quality_scan_automation_settings
SET folder_id = NULL,
    updated_at = CURRENT_TIMESTAMP
WHERE folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearQualityScanAutomationSql, connection, transaction))
        {
            command.Parameters.AddWithValue(FolderIdParameter, folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearQualityScanRunSql = @"
UPDATE quality_scan_run
SET folder_id = NULL,
    updated_at = CURRENT_TIMESTAMP
WHERE folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearQualityScanRunSql, connection, transaction))
        {
            command.Parameters.AddWithValue(FolderIdParameter, folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearQualityScanActionSql = @"
UPDATE quality_scan_action_log
SET destination_folder_id = NULL
WHERE destination_folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearQualityScanActionSql, connection, transaction))
        {
            command.Parameters.AddWithValue(FolderIdParameter, folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string selectPlaylistRoutingSql = @"
SELECT source,
       source_id,
       routing_rules_json
FROM playlist_watch_preferences
WHERE routing_rules_json IS NOT NULL
  AND TRIM(routing_rules_json) <> '';";
        var playlistRoutingUpdates = new List<(string Source, string SourceId, string? RoutingRulesJson)>();
        await using (var command = new SqliteCommand(selectPlaylistRoutingSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var source = reader.GetString(0);
                var sourceId = reader.GetString(1);
                var routingRulesJson = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(routingRulesJson))
                {
                    continue;
                }

                List<PlaylistTrackRoutingRule>? rules;
                try
                {
                    rules = JsonSerializer.Deserialize<List<PlaylistTrackRoutingRule>>(routingRulesJson);
                }
                catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
                {
                    continue;
                }

                if (rules is null || rules.Count == 0)
                {
                    continue;
                }

                var filteredRules = rules
                    .Where(rule => rule.DestinationFolderId != folderId)
                    .ToList();
                if (filteredRules.Count == rules.Count)
                {
                    continue;
                }

                playlistRoutingUpdates.Add((
                    source,
                    sourceId,
                    filteredRules.Count > 0 ? JsonSerializer.Serialize(filteredRules) : null));
            }
        }

        const string updatePlaylistRoutingSql = @"
UPDATE playlist_watch_preferences
SET routing_rules_json = @routingRulesJson,
    updated_at = CURRENT_TIMESTAMP
WHERE source = @source
  AND source_id = @sourceId;";
        foreach (var update in playlistRoutingUpdates)
        {
            await using var command = new SqliteCommand(updatePlaylistRoutingSql, connection, transaction);
            command.Parameters.AddWithValue("routingRulesJson", (object?)update.RoutingRulesJson ?? DBNull.Value);
            command.Parameters.AddWithValue("source", update.Source);
            command.Parameters.AddWithValue("sourceId", update.SourceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string BuildAlbumKey(string artistName, string albumTitle)
        => $"{artistName}|{albumTitle}";

    private static bool HaveRoutingRulesChanged(
        IReadOnlyList<PlaylistTrackRoutingRule>? current,
        List<PlaylistTrackRoutingRule>? updated)
    {
        if (current == null || current.Count == 0)
        {
            return updated != null && updated.Count > 0;
        }

        if (updated == null || updated.Count != current.Count)
        {
            return true;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (!Equals(current[i], updated[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeRoot(string rootPath)
    {
        var normalized = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized + Path.DirectorySeparatorChar;
    }

    private static FolderRoot? FindFolderForPath(IReadOnlyList<FolderRoot> folderRoots, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        return folderRoots.FirstOrDefault(item => fullPath.StartsWith(item.Root, StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeRelativePath(string normalizedRootWithTrailingSeparator, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var relative = fullPath.StartsWith(normalizedRootWithTrailingSeparator, StringComparison.OrdinalIgnoreCase)
            ? fullPath[normalizedRootWithTrailingSeparator.Length..]
            : Path.GetFileName(fullPath);
        return relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
    }

    private static async Task<long> GetOrCreateArtistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArtistScanDto artist,
        CancellationToken cancellationToken)
    {
        const string selectSql = "SELECT id, preferred_image_path FROM artist WHERE LOWER(name) = LOWER(@name) LIMIT 1;";
        await using var selectCommand = new SqliteCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("name", artist.Name);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var existingPath = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
            await reader.DisposeAsync();

            var updatedPath = ImagePathPreference.ChooseBetterImage(existingPath, artist.ImagePath);
            if (!string.Equals(existingPath, updatedPath, StringComparison.OrdinalIgnoreCase))
            {
                const string updateSql = "UPDATE artist SET preferred_image_path = @path, updated_at = CURRENT_TIMESTAMP WHERE id = @id;";
                await using var updateCommand = new SqliteCommand(updateSql, connection, transaction);
                updateCommand.Parameters.AddWithValue("path", (object?)updatedPath ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("id", id);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            return id;
        }

        await reader.DisposeAsync();

        const string insertSql = @"
INSERT INTO artist (name, preferred_image_path)
VALUES (@name, @path)
RETURNING id;";
        await using var insertCommand = new SqliteCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("name", artist.Name);
        insertCommand.Parameters.AddWithValue("path", (object?)artist.ImagePath ?? DBNull.Value);
        var insertedId = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(insertedId);
    }

    private static async Task<long> GetOrCreateAlbumAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long artistId,
        LocalAlbumScanDto album,
        CancellationToken cancellationToken)
    {
        const string selectSql = @"SELECT id, preferred_cover_path, has_animated_artwork
FROM album
WHERE artist_id = @artistId AND LOWER(title) = LOWER(@title)
LIMIT 1;";
        await using var selectCommand = new SqliteCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("artistId", artistId);
        selectCommand.Parameters.AddWithValue(TitleField, album.Title);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var existingPath = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
            var existingAnimatedArtwork = !await reader.IsDBNullAsync(2, cancellationToken) && reader.GetInt64(2) != 0;
            await reader.DisposeAsync();

            var updatedPath = ImagePathPreference.ChooseBetterImage(existingPath, album.PreferredCoverPath);
            if (!string.Equals(existingPath, updatedPath, StringComparison.OrdinalIgnoreCase))
            {
                const string updateSql = "UPDATE album SET preferred_cover_path = @path, has_animated_artwork = @hasAnimatedArtwork, updated_at = CURRENT_TIMESTAMP WHERE id = @id;";
                await using var updateCommand = new SqliteCommand(updateSql, connection, transaction);
                updateCommand.Parameters.AddWithValue("path", (object?)updatedPath ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("hasAnimatedArtwork", existingAnimatedArtwork || album.HasAnimatedArtwork);
                updateCommand.Parameters.AddWithValue("id", id);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            else if (album.HasAnimatedArtwork && !existingAnimatedArtwork)
            {
                const string animatedSql = "UPDATE album SET has_animated_artwork = 1, updated_at = CURRENT_TIMESTAMP WHERE id = @id;";
                await using var animatedCommand = new SqliteCommand(animatedSql, connection, transaction);
                animatedCommand.Parameters.AddWithValue("id", id);
                await animatedCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            return id;
        }

        await reader.DisposeAsync();

        const string insertSql = @"
INSERT INTO album (artist_id, title, preferred_cover_path, has_animated_artwork)
VALUES (@artistId, @title, @path, @hasAnimatedArtwork)
RETURNING id;";
        await using var insertCommand = new SqliteCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("artistId", artistId);
        insertCommand.Parameters.AddWithValue(TitleField, album.Title);
        insertCommand.Parameters.AddWithValue("path", (object?)album.PreferredCoverPath ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("hasAnimatedArtwork", album.HasAnimatedArtwork);
        var insertedId = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(insertedId);
    }

    private static async Task<long> GetOrCreateTrackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long albumId,
        LocalTrackScanDto track,
        CancellationToken cancellationToken)
    {
        const string selectSql = @"
SELECT id, duration_ms, lyrics_status, deezer_id,
       lyrics_type,
       tag_title, tag_artist, tag_album, tag_album_artist,
       tag_version, tag_label, tag_catalog_number, tag_bpm, tag_key,
       tag_track_total, tag_duration_ms, tag_year, tag_track_no, tag_disc,
       tag_genre, tag_isrc, tag_release_date, tag_publish_date, tag_url,
       tag_release_id, tag_track_id, tag_meta_tagged_date,
       lyrics_unsynced, lyrics_synced
FROM track
WHERE album_id = @albumId
  AND LOWER(title) = LOWER(@title)
  AND track_no IS NOT DISTINCT FROM @trackNo
  AND disc IS NOT DISTINCT FROM @disc
LIMIT 1;";
        await using var selectCommand = new SqliteCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("albumId", albumId);
        selectCommand.Parameters.AddWithValue(TitleField, track.Title);
        selectCommand.Parameters.AddWithValue("trackNo", (object?)track.TrackNo ?? DBNull.Value);
        selectCommand.Parameters.AddWithValue("disc", (object?)track.Disc ?? DBNull.Value);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        var normalizedTrackDurationMs = track.DurationMs.HasValue && track.DurationMs.Value > 0
            ? track.DurationMs.Value
            : (int?)null;
        if (await reader.ReadAsync(cancellationToken))
        {
            var existing = await ReadExistingTrackRecordAsync(reader, cancellationToken);
            await reader.DisposeAsync();

            var shouldUpdate = ShouldUpdateTrack(existing, track, normalizedTrackDurationMs);

            if (shouldUpdate)
            {
                const string updateSql = $@"
UPDATE track
SET duration_ms = COALESCE(@{DurationMsField}, duration_ms),
    lyrics_status = @lyricsStatus,
    lyrics_type = @lyricsType,
    deezer_id = @deezerId,
    tag_title = @tagTitle,
    tag_artist = @tagArtist,
    tag_album = @tagAlbum,
    tag_album_artist = @tagAlbumArtist,
    tag_version = @tagVersion,
    tag_label = @tagLabel,
    tag_catalog_number = @tagCatalogNumber,
    tag_bpm = @tagBpm,
    tag_key = @tagKey,
    tag_track_total = @tagTrackTotal,
    tag_duration_ms = @tagDurationMs,
    tag_year = @tagYear,
    tag_track_no = @tagTrackNo,
    tag_disc = @tagDisc,
    tag_genre = @tagGenre,
    tag_isrc = @tagIsrc,
    tag_release_date = @tagReleaseDate,
    tag_publish_date = @tagPublishDate,
    tag_url = @tagUrl,
    tag_release_id = @tagReleaseId,
    tag_track_id = @tagTrackId,
    tag_meta_tagged_date = @tagMetaTaggedDate,
    lyrics_unsynced = @lyricsUnsynced,
    lyrics_synced = @lyricsSynced,
    updated_at = CURRENT_TIMESTAMP
WHERE id = @id;";
                await using var updateCommand = new SqliteCommand(updateSql, connection, transaction);
                AddTrackParameters(updateCommand, track, normalizedTrackDurationMs);
                updateCommand.Parameters.AddWithValue("id", existing.Id);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            return existing.Id;
        }

        await reader.DisposeAsync();

        const string insertSql = @"
INSERT INTO track (album_id, title, duration_ms, disc, track_no, lyrics_status, lyrics_type, deezer_id,
                   tag_title, tag_artist, tag_album, tag_album_artist, tag_version, tag_label,
                   tag_catalog_number, tag_bpm, tag_key, tag_track_total, tag_duration_ms,
                   tag_year, tag_track_no, tag_disc, tag_genre, tag_isrc, tag_release_date,
                   tag_publish_date, tag_url, tag_release_id, tag_track_id, tag_meta_tagged_date,
                   lyrics_unsynced, lyrics_synced)
VALUES (@albumId, @title, @duration, @disc, @trackNo, @lyricsStatus, @lyricsType, @deezerId,
        @tagTitle, @tagArtist, @tagAlbum, @tagAlbumArtist, @tagVersion, @tagLabel,
        @tagCatalogNumber, @tagBpm, @tagKey, @tagTrackTotal, @tagDurationMs,
        @tagYear, @tagTrackNo, @tagDisc, @tagGenre, @tagIsrc, @tagReleaseDate,
        @tagPublishDate, @tagUrl, @tagReleaseId, @tagTrackId, @tagMetaTaggedDate,
        @lyricsUnsynced, @lyricsSynced)
RETURNING id;";
        await using var insertCommand = new SqliteCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("albumId", albumId);
        insertCommand.Parameters.AddWithValue(TitleField, track.Title);
        AddTrackParameters(insertCommand, track, normalizedTrackDurationMs);
        var insertedId = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(insertedId);
    }

    private static async Task<ExistingTrackRecord> ReadExistingTrackRecordAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        return new ExistingTrackRecord(
            reader.GetInt64(0),
            await ReadNullableIntAsync(reader, 1, cancellationToken),
            await ReadNullableStringAsync(reader, 2, cancellationToken),
            await ReadNullableStringAsync(reader, 3, cancellationToken),
            await ReadNullableStringAsync(reader, 4, cancellationToken),
            await ReadNullableStringAsync(reader, 5, cancellationToken),
            await ReadNullableStringAsync(reader, 6, cancellationToken),
            await ReadNullableStringAsync(reader, 7, cancellationToken),
            await ReadNullableStringAsync(reader, 8, cancellationToken),
            await ReadNullableStringAsync(reader, 9, cancellationToken),
            await ReadNullableStringAsync(reader, 10, cancellationToken),
            await ReadNullableStringAsync(reader, 11, cancellationToken),
            await ReadNullableIntAsync(reader, 12, cancellationToken),
            await ReadNullableStringAsync(reader, 13, cancellationToken),
            await ReadNullableIntAsync(reader, 14, cancellationToken),
            await ReadNullableIntAsync(reader, 15, cancellationToken),
            await ReadNullableIntAsync(reader, 16, cancellationToken),
            await ReadNullableIntAsync(reader, 17, cancellationToken),
            await ReadNullableIntAsync(reader, 18, cancellationToken),
            await ReadNullableStringAsync(reader, 19, cancellationToken),
            await ReadNullableStringAsync(reader, 20, cancellationToken),
            await ReadNullableStringAsync(reader, 21, cancellationToken),
            await ReadNullableStringAsync(reader, 22, cancellationToken),
            await ReadNullableStringAsync(reader, 23, cancellationToken),
            await ReadNullableStringAsync(reader, 24, cancellationToken),
            await ReadNullableStringAsync(reader, 25, cancellationToken),
            await ReadNullableStringAsync(reader, 26, cancellationToken),
            await ReadNullableStringAsync(reader, 27, cancellationToken),
            await ReadNullableStringAsync(reader, 28, cancellationToken));
    }

    private static async Task<string?> ReadNullableStringAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        return await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);
    }

    private static bool IsMissing(params string?[] values)
    {
        return values.All(static value => string.IsNullOrWhiteSpace(value));
    }

    private static bool IsMissingOrWeakMetadata(string? value)
        => TrackIdentityTrust.IsWeakMetadataValue(value);

    private static string[] ReadDelimitedValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string? NormalizeScanFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<int?> ReadNullableIntAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        return await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt32(ordinal);
    }

    private static async Task<long?> ReadNullableInt64Async(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        return await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt64(ordinal);
    }

    private static async Task<double?> ReadNullableDoubleAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        return await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetDouble(ordinal);
    }

    private static bool ShouldUpdateTrack(ExistingTrackRecord existing, LocalTrackScanDto track, int? normalizedTrackDurationMs)
    {
        return (normalizedTrackDurationMs.HasValue && existing.DurationMs != normalizedTrackDurationMs.Value)
               || !TextEquals(existing.LyricsStatus, track.LyricsStatus)
               || !TextEquals(existing.DeezerId, track.DeezerTrackId)
               || !TextEquals(existing.LyricsType, track.LyricsType)
               || !TextEquals(existing.TagTitle, track.TagTitle)
               || !TextEquals(existing.TagArtist, track.TagArtist)
               || !TextEquals(existing.TagAlbum, track.TagAlbum)
               || !TextEquals(existing.TagAlbumArtist, track.TagAlbumArtist)
               || !TextEquals(existing.TagVersion, track.TagVersion)
               || !TextEquals(existing.TagLabel, track.TagLabel)
               || !TextEquals(existing.TagCatalogNumber, track.TagCatalogNumber)
               || existing.TagBpm != track.TagBpm
               || !TextEquals(existing.TagKey, track.TagKey)
               || existing.TagTrackTotal != track.TagTrackTotal
               || existing.TagDurationMs != track.TagDurationMs
               || existing.TagYear != track.TagYear
               || existing.TagTrackNo != track.TagTrackNo
               || existing.TagDisc != track.TagDisc
               || !TextEquals(existing.TagGenre, track.TagGenre)
               || !TextEquals(existing.TagIsrc, track.TagIsrc)
               || !TextEquals(existing.TagReleaseDate, track.TagReleaseDate)
               || !TextEquals(existing.TagPublishDate, track.TagPublishDate)
               || !TextEquals(existing.TagUrl, track.TagUrl)
               || !TextEquals(existing.TagReleaseId, track.TagReleaseId)
               || !TextEquals(existing.TagTrackId, track.TagTrackId)
               || !TextEquals(existing.TagMetaTaggedDate, track.TagMetaTaggedDate)
               || !TextEquals(existing.LyricsUnsynced, track.LyricsUnsynced)
               || !TextEquals(existing.LyricsSynced, track.LyricsSynced);
    }

    private static bool TextEquals(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTrackParameters(SqliteCommand command, LocalTrackScanDto track, int? normalizedTrackDurationMs)
    {
        command.Parameters.AddWithValue("duration", (object?)normalizedTrackDurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue(DurationMsField, (object?)normalizedTrackDurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("disc", (object?)track.Disc ?? DBNull.Value);
        command.Parameters.AddWithValue("trackNo", (object?)track.TrackNo ?? DBNull.Value);
        command.Parameters.AddWithValue("lyricsStatus", (object?)track.LyricsStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("lyricsType", (object?)track.LyricsType ?? DBNull.Value);
        command.Parameters.AddWithValue("deezerId", (object?)track.DeezerTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("tagTitle", (object?)track.TagTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("tagArtist", (object?)track.TagArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("tagAlbum", (object?)track.TagAlbum ?? DBNull.Value);
        command.Parameters.AddWithValue("tagAlbumArtist", (object?)track.TagAlbumArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("tagVersion", (object?)track.TagVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("tagLabel", (object?)track.TagLabel ?? DBNull.Value);
        command.Parameters.AddWithValue("tagCatalogNumber", (object?)track.TagCatalogNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("tagBpm", (object?)track.TagBpm ?? DBNull.Value);
        command.Parameters.AddWithValue("tagKey", (object?)track.TagKey ?? DBNull.Value);
        command.Parameters.AddWithValue("tagTrackTotal", (object?)track.TagTrackTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("tagDurationMs", (object?)track.TagDurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("tagYear", (object?)track.TagYear ?? DBNull.Value);
        command.Parameters.AddWithValue("tagTrackNo", (object?)track.TagTrackNo ?? DBNull.Value);
        command.Parameters.AddWithValue("tagDisc", (object?)track.TagDisc ?? DBNull.Value);
        command.Parameters.AddWithValue("tagGenre", (object?)track.TagGenre ?? DBNull.Value);
        command.Parameters.AddWithValue("tagIsrc", (object?)track.TagIsrc ?? DBNull.Value);
        command.Parameters.AddWithValue("tagReleaseDate", (object?)track.TagReleaseDate ?? DBNull.Value);
        command.Parameters.AddWithValue("tagPublishDate", (object?)track.TagPublishDate ?? DBNull.Value);
        command.Parameters.AddWithValue("tagUrl", (object?)track.TagUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("tagReleaseId", (object?)track.TagReleaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("tagTrackId", (object?)track.TagTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("tagMetaTaggedDate", (object?)track.TagMetaTaggedDate ?? DBNull.Value);
        command.Parameters.AddWithValue("lyricsUnsynced", (object?)track.LyricsUnsynced ?? DBNull.Value);
        command.Parameters.AddWithValue("lyricsSynced", (object?)track.LyricsSynced ?? DBNull.Value);
    }

    private static async Task<long> UpsertAudioFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AudioFileUpsertInput input,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(input.FilePath);
        var size = fileInfo.Exists ? fileInfo.Length : 0;
        var mtime = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow;
        var extension = Path.GetExtension(input.FilePath);

        const string sql = @"
INSERT INTO audio_file (path, relative_path, folder_id, size, mtime, duration_ms, codec, bitrate_kbps, extension, sample_rate_hz, bits_per_sample, channels, quality_rank, audio_variant, updated_at)
VALUES (@path, @relativePath, @folderId, @size, @mtime, @duration, @codec, @bitrateKbps, @extension, @sampleRateHz, @bitsPerSample, @channels, @qualityRank, @audioVariant, CURRENT_TIMESTAMP)
ON CONFLICT (folder_id, relative_path) DO UPDATE
SET path = EXCLUDED.path,
    size = EXCLUDED.size,
    mtime = EXCLUDED.mtime,
    duration_ms = COALESCE(EXCLUDED.duration_ms, audio_file.duration_ms),
    codec = COALESCE(EXCLUDED.codec, audio_file.codec),
    bitrate_kbps = COALESCE(EXCLUDED.bitrate_kbps, audio_file.bitrate_kbps),
    extension = EXCLUDED.extension,
    sample_rate_hz = COALESCE(EXCLUDED.sample_rate_hz, audio_file.sample_rate_hz),
    bits_per_sample = COALESCE(EXCLUDED.bits_per_sample, audio_file.bits_per_sample),
    channels = COALESCE(EXCLUDED.channels, audio_file.channels),
    quality_rank = COALESCE(EXCLUDED.quality_rank, audio_file.quality_rank),
    audio_variant = COALESCE(EXCLUDED.audio_variant, audio_file.audio_variant),
    updated_at = CASE
        WHEN @preserveUnchangedTimestamp THEN audio_file.updated_at
        ELSE CURRENT_TIMESTAMP
    END
        RETURNING id;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("path", input.FilePath);
        command.Parameters.AddWithValue("relativePath", input.RelativePath);
        command.Parameters.AddWithValue(FolderIdParameter, input.FolderId);
        command.Parameters.AddWithValue("size", size);
        command.Parameters.AddWithValue("mtime", mtime);
        command.Parameters.AddWithValue("duration", (object?)input.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("codec", (object?)input.Codec ?? DBNull.Value);
        command.Parameters.AddWithValue("bitrateKbps", (object?)input.BitrateKbps ?? DBNull.Value);
        command.Parameters.AddWithValue("extension", (object?)extension ?? DBNull.Value);
        command.Parameters.AddWithValue("sampleRateHz", (object?)input.SampleRateHz ?? DBNull.Value);
        command.Parameters.AddWithValue("bitsPerSample", (object?)input.BitsPerSample ?? DBNull.Value);
        command.Parameters.AddWithValue("channels", (object?)input.Channels ?? DBNull.Value);
        command.Parameters.AddWithValue("qualityRank", (object?)input.QualityRank ?? DBNull.Value);
        command.Parameters.AddWithValue("audioVariant", (object?)NormalizeAudioVariant(input.AudioVariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("preserveUnchangedTimestamp", input.PreserveUnchangedTimestamp);
        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(insertedId);
    }

    private static async Task NormalizeTrackDurationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = @"
WITH best_duration AS (
    SELECT tl.track_id AS track_id, MAX(af.duration_ms) AS duration_ms
    FROM track_local tl
    JOIN audio_file af ON af.id = tl.audio_file_id
    WHERE af.duration_ms IS NOT NULL
      AND af.duration_ms > 0
    GROUP BY tl.track_id
)
UPDATE track
SET duration_ms = (SELECT best_duration.duration_ms FROM best_duration WHERE best_duration.track_id = track.id),
    updated_at = CURRENT_TIMESTAMP
WHERE id IN (SELECT track_id FROM best_duration)
  AND (
      duration_ms IS NULL
      OR duration_ms <= 0
      OR duration_ms <> (SELECT best_duration.duration_ms FROM best_duration WHERE best_duration.track_id = track.id)
  );";
        await using var command = new SqliteCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTrackSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceUpsertInput input,
        CancellationToken cancellationToken)
    {
        await UpsertEntitySourceRecordAsync(
            connection,
            transaction,
            input,
            table: "track_source",
            cancellationToken);
    }

    private static async Task EnsureArtistSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceUpsertInput input,
        CancellationToken cancellationToken)
        => await EnsureEntitySourceAsync(
            connection,
            transaction,
            input,
            table: "artist_source",
            cancellationToken);

    private static async Task EnsureAlbumSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceUpsertInput input,
        CancellationToken cancellationToken)
        => await EnsureEntitySourceAsync(
            connection,
            transaction,
            input,
            table: "album_source",
            cancellationToken);

    private static async Task EnsureEntitySourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceUpsertInput input,
        string table,
        CancellationToken cancellationToken)
        => await UpsertEntitySourceRecordAsync(
            connection,
            transaction,
            input,
            table,
            cancellationToken);

    public async Task UpsertTrackSourceLinkAsync(
        long trackId,
        string source,
        string sourceId,
        string? url,
        string? data = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpsertEntitySourceRecordAsync(
            connection,
            transaction: null,
            new SourceUpsertInput(trackId, source, sourceId, url, data),
            table: "track_source",
            cancellationToken);
    }

    public async Task UpsertAlbumSourceLinkAsync(
        long albumId,
        string source,
        string sourceId,
        string? url,
        string? data = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpsertEntitySourceRecordAsync(
            connection,
            transaction: null,
            new SourceUpsertInput(albumId, source, sourceId, url, data),
            table: "album_source",
            cancellationToken);
    }

    public async Task UpsertArtistSourceLinkAsync(
        long artistId,
        string source,
        string sourceId,
        string? url,
        string? data = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpsertEntitySourceRecordAsync(
            connection,
            transaction: null,
            new SourceUpsertInput(artistId, source, sourceId, url, data),
            table: "artist_source",
            cancellationToken);
    }

    private static async Task UpsertEntitySourceRecordAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SourceUpsertInput input,
        string table,
        CancellationToken cancellationToken)
    {
        var sql = ResolveEntitySourceSql(table);
        var normalizedSource = input.Source.Trim().ToLowerInvariant();
        var normalizedSourceId = input.SourceId.Trim();

        await using (var deleteCurrent = new SqliteCommand(sql.DeleteCurrentSql, connection, transaction))
        {
            deleteCurrent.Parameters.AddWithValue(EntityIdParameter, input.EntityId);
            deleteCurrent.Parameters.AddWithValue(SourceField, normalizedSource);
            deleteCurrent.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            await deleteCurrent.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateBySourceId = new SqliteCommand(sql.UpdateBySourceIdSql, connection, transaction))
        {
            updateBySourceId.Parameters.AddWithValue(EntityIdParameter, input.EntityId);
            updateBySourceId.Parameters.AddWithValue(SourceField, normalizedSource);
            updateBySourceId.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            updateBySourceId.Parameters.AddWithValue("url", (object?)input.Url ?? DBNull.Value);
            updateBySourceId.Parameters.AddWithValue("data", (object?)input.Data ?? DBNull.Value);
            var updated = await updateBySourceId.ExecuteNonQueryAsync(cancellationToken);
            if (updated > 0)
            {
                return;
            }
        }

        await using (var updateByEntity = new SqliteCommand(sql.UpdateByEntitySql, connection, transaction))
        {
            updateByEntity.Parameters.AddWithValue(EntityIdParameter, input.EntityId);
            updateByEntity.Parameters.AddWithValue(SourceField, normalizedSource);
            updateByEntity.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
            updateByEntity.Parameters.AddWithValue("url", (object?)input.Url ?? DBNull.Value);
            updateByEntity.Parameters.AddWithValue("data", (object?)input.Data ?? DBNull.Value);
            var updated = await updateByEntity.ExecuteNonQueryAsync(cancellationToken);
            if (updated > 0)
            {
                return;
            }
        }

        await using var insert = new SqliteCommand(sql.InsertSql, connection, transaction);
        insert.Parameters.AddWithValue(EntityIdParameter, input.EntityId);
        insert.Parameters.AddWithValue(SourceField, normalizedSource);
        insert.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        insert.Parameters.AddWithValue("url", (object?)input.Url ?? DBNull.Value);
        insert.Parameters.AddWithValue("data", (object?)input.Data ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static (string DeleteCurrentSql, string UpdateBySourceIdSql, string UpdateByEntitySql, string InsertSql) ResolveEntitySourceSql(string table)
        => table switch
        {
            "track_source" => (
                DeleteCurrentSql: @"
DELETE FROM track_source
WHERE track_id = @entityId
  AND source = @source
  AND source_id <> @sourceId;",
                UpdateBySourceIdSql: @"
UPDATE track_source
SET track_id = @entityId,
    url = COALESCE(NULLIF(@url, ''), track_source.url),
    data = COALESCE(NULLIF(@data, ''), track_source.data)
WHERE source = @source
  AND source_id = @sourceId;",
                UpdateByEntitySql: @"
UPDATE track_source
SET source_id = @sourceId,
    url = COALESCE(NULLIF(@url, ''), track_source.url),
    data = COALESCE(NULLIF(@data, ''), track_source.data)
WHERE track_id = @entityId
  AND source = @source;",
                InsertSql: @"
INSERT INTO track_source (track_id, source, source_id, url, data)
VALUES (@entityId, @source, @sourceId, @url, @data);"),
            "album_source" => (
                DeleteCurrentSql: @"
DELETE FROM album_source
WHERE album_id = @entityId
  AND source = @source
  AND source_id <> @sourceId;",
                UpdateBySourceIdSql: @"
UPDATE album_source
SET album_id = @entityId,
    url = COALESCE(NULLIF(@url, ''), album_source.url),
    data = COALESCE(NULLIF(@data, ''), album_source.data)
WHERE source = @source
  AND source_id = @sourceId;",
                UpdateByEntitySql: @"
UPDATE album_source
SET source_id = @sourceId,
    url = COALESCE(NULLIF(@url, ''), album_source.url),
    data = COALESCE(NULLIF(@data, ''), album_source.data)
WHERE album_id = @entityId
  AND source = @source;",
                InsertSql: @"
INSERT INTO album_source (album_id, source, source_id, url, data)
VALUES (@entityId, @source, @sourceId, @url, @data);"),
            "artist_source" => (
                DeleteCurrentSql: @"
DELETE FROM artist_source
WHERE artist_id = @entityId
  AND source = @source
  AND source_id <> @sourceId;",
                UpdateBySourceIdSql: @"
UPDATE artist_source
SET artist_id = @entityId,
    url = COALESCE(NULLIF(@url, ''), artist_source.url),
    data = COALESCE(NULLIF(@data, ''), artist_source.data)
WHERE source = @source
  AND source_id = @sourceId;",
                UpdateByEntitySql: @"
UPDATE artist_source
SET source_id = @sourceId,
    url = COALESCE(NULLIF(@url, ''), artist_source.url),
    data = COALESCE(NULLIF(@data, ''), artist_source.data)
WHERE artist_id = @entityId
  AND source = @source;",
                InsertSql: @"
INSERT INTO artist_source (artist_id, source, source_id, url, data)
VALUES (@entityId, @source, @sourceId, @url, @data);"),
            _ => throw new InvalidOperationException($"Unsupported source mapping table '{table}'.")
        };

    private static string? BuildTrackUrl(string source, string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        return source.Trim().ToLowerInvariant() switch
        {
            DeezerSource => $"https://www.deezer.com/track/{sourceId.Trim()}",
            SpotifySource => $"https://open.spotify.com/track/{sourceId.Trim()}",
            AppleSource => $"https://music.apple.com/us/song/{sourceId.Trim()}",
            _ => null
        };
    }

    private static string? BuildAlbumUrl(string source, string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        return source.Trim().ToLowerInvariant() switch
        {
            DeezerSource => $"https://www.deezer.com/album/{sourceId.Trim()}",
            SpotifySource => $"https://open.spotify.com/album/{sourceId.Trim()}",
            AppleSource => $"https://music.apple.com/us/album/{sourceId.Trim()}",
            _ => null
        };
    }

    private static string? BuildArtistUrl(string source, string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        return source.Trim().ToLowerInvariant() switch
        {
            DeezerSource => $"https://www.deezer.com/artist/{sourceId.Trim()}",
            SpotifySource => $"https://open.spotify.com/artist/{sourceId.Trim()}",
            AppleSource => $"https://music.apple.com/us/artist/{sourceId.Trim()}",
            _ => null
        };
    }

    private static async Task EnsureArtistAndAlbumSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long artistId,
        long albumId,
        LocalTrackScanDto track,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(track.DeezerArtistId))
        {
            await EnsureArtistSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(artistId, DeezerSource, track.DeezerArtistId!, BuildArtistUrl(DeezerSource, track.DeezerArtistId), null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.SpotifyArtistId))
        {
            await EnsureArtistSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(artistId, SpotifySource, track.SpotifyArtistId!, BuildArtistUrl(SpotifySource, track.SpotifyArtistId), null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.AppleArtistId))
        {
            await EnsureArtistSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(artistId, AppleSource, track.AppleArtistId!, BuildArtistUrl(AppleSource, track.AppleArtistId), null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.DeezerAlbumId))
        {
            await EnsureAlbumSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(albumId, DeezerSource, track.DeezerAlbumId!, BuildAlbumUrl(DeezerSource, track.DeezerAlbumId), null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.SpotifyAlbumId))
        {
            await EnsureAlbumSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(albumId, SpotifySource, track.SpotifyAlbumId!, BuildAlbumUrl(SpotifySource, track.SpotifyAlbumId), null),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.AppleAlbumId))
        {
            await EnsureAlbumSourceAsync(
                connection,
                transaction,
                new SourceUpsertInput(albumId, AppleSource, track.AppleAlbumId!, BuildAlbumUrl(AppleSource, track.AppleAlbumId), null),
                cancellationToken);
        }
    }

    private static async Task ReplaceTrackMultiTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long trackId,
        LocalTrackScanDto track,
        CancellationToken cancellationToken)
    {
        await DeleteTrackTagsAsync(connection, transaction, TrackGenreTable, trackId, cancellationToken);
        await DeleteTrackTagsAsync(connection, transaction, TrackStyleTable, trackId, cancellationToken);
        await DeleteTrackTagsAsync(connection, transaction, TrackMoodTable, trackId, cancellationToken);
        await DeleteTrackTagsAsync(connection, transaction, TrackRemixerTable, trackId, cancellationToken);
        await DeleteTrackTagsAsync(connection, transaction, TrackOtherTagTable, trackId, cancellationToken);

        await InsertTrackTagValuesAsync(connection, transaction, TrackGenreTable, trackId, track.TagGenres, cancellationToken);
        await InsertTrackTagValuesAsync(connection, transaction, TrackStyleTable, trackId, track.TagStyles, cancellationToken);
        await InsertTrackTagValuesAsync(connection, transaction, TrackMoodTable, trackId, track.TagMoods, cancellationToken);
        await InsertTrackTagValuesAsync(connection, transaction, TrackRemixerTable, trackId, track.TagRemixers, cancellationToken);
        await InsertTrackOtherTagsAsync(connection, transaction, trackId, track.TagOtherTags, cancellationToken);
    }

    private static async Task DeleteTrackTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        long trackId,
        CancellationToken cancellationToken)
    {
        var sql = ResolveDeleteTrackTagsSql(table);
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTrackTagValuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        long trackId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var sql = ResolveInsertTrackTagValuesSql(table);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            await using var command = new SqliteCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(TrackIdField, trackId);
            command.Parameters.AddWithValue("value", value.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string ResolveDeleteTrackTagsSql(string table)
        => table switch
        {
            TrackGenreTable => "DELETE FROM track_genre WHERE track_id = @trackId;",
            TrackStyleTable => "DELETE FROM track_style WHERE track_id = @trackId;",
            TrackMoodTable => "DELETE FROM track_mood WHERE track_id = @trackId;",
            TrackRemixerTable => "DELETE FROM track_remixer WHERE track_id = @trackId;",
            TrackOtherTagTable => "DELETE FROM track_other_tag WHERE track_id = @trackId;",
            _ => throw new InvalidOperationException($"Unsupported track tag table '{table}'.")
        };

    private static string ResolveInsertTrackTagValuesSql(string table)
        => table switch
        {
            TrackGenreTable => "INSERT INTO track_genre (track_id, value) VALUES (@trackId, @value) ON CONFLICT DO NOTHING;",
            TrackStyleTable => "INSERT INTO track_style (track_id, value) VALUES (@trackId, @value) ON CONFLICT DO NOTHING;",
            TrackMoodTable => "INSERT INTO track_mood (track_id, value) VALUES (@trackId, @value) ON CONFLICT DO NOTHING;",
            TrackRemixerTable => "INSERT INTO track_remixer (track_id, value) VALUES (@trackId, @value) ON CONFLICT DO NOTHING;",
            _ => throw new InvalidOperationException($"Unsupported track tag table '{table}'.")
        };

    private static async Task InsertTrackOtherTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long trackId,
        IReadOnlyList<LocalTrackOtherTag> tags,
        CancellationToken cancellationToken)
    {
        if (tags is null || tags.Count == 0)
        {
            return;
        }

        const string sql = @"
INSERT INTO track_other_tag (track_id, tag_key, tag_value)
VALUES (@trackId, @key, @value)
ON CONFLICT DO NOTHING;";
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Key) || string.IsNullOrWhiteSpace(tag.Value))
            {
                continue;
            }
            await using var command = new SqliteCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(TrackIdField, trackId);
            command.Parameters.AddWithValue("key", tag.Key.Trim());
            command.Parameters.AddWithValue("value", tag.Value.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureTrackLocalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long trackId,
        long audioFileId,
        CancellationToken cancellationToken)
    {
        const string deleteStaleSql = @"
DELETE FROM track_local
WHERE audio_file_id = @audioFileId
  AND track_id <> @trackId;";
        await using (var deleteCommand = new SqliteCommand(deleteStaleSql, connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("audioFileId", audioFileId);
            deleteCommand.Parameters.AddWithValue(TrackIdField, trackId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = @"
INSERT INTO track_local (track_id, audio_file_id)
VALUES (@trackId, @audioFileId)
ON CONFLICT DO NOTHING;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        command.Parameters.AddWithValue("audioFileId", audioFileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureAlbumLocalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long albumId,
        long folderId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
INSERT INTO album_local (album_id, folder_id)
VALUES (@albumId, @folderId)
ON CONFLICT DO NOTHING;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("albumId", albumId);
        command.Parameters.AddWithValue(FolderIdParameter, folderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static int NormalizeDesiredQualityRank(string? desiredQuality)
    {
        var normalized = QualityCatalog.NormalizeLibraryFolderQualityValue(desiredQuality).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return 3;
        }

        var tierRank = QualityCatalog.GetLibraryFolderLocalRank(normalized);
        if (tierRank.HasValue)
        {
            return tierRank.Value;
        }

        return normalized switch
        {
            AtmosVariant => 5,
            "alac" => 3,
            "flac" => 3,
            "lossless" => 3,
            "hi_res_lossless" => 4,
            "27" => 4,
            "9" => 3,
            "7" => 4,
            "6" => 3,
            "aac" => 2,
            "3" => 2,
            "1" => 1,
            FolderContentVideo => 0,
            FolderContentPodcast => 0,
            _ => int.TryParse(normalized, out var parsed)
                ? parsed switch
                {
                    >= 5 => 5,
                    4 => 4,
                    < 0 => 0,
                    _ => parsed
                }
                : MediaQualityInference.InferLocalQualityRankFromText(normalized, AtmosVariant, treatPodcastAsVideo: true) ?? 3
        };
    }

    private static string? NormalizeFolderConvertFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = normalized switch
        {
            "m4a-aac" => "m4a",
            "m4a-alac" => "alac",
            "musepack" => "mpc",
            _ => normalized
        };

        return SupportedFolderConvertFormats.Contains(normalized)
            ? normalized
            : null;
    }

    private static string? NormalizeFolderConvertBitrate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = value.Trim().Replace(" ", string.Empty).ToLowerInvariant();
        if (compact == "auto")
        {
            return "AUTO";
        }

        if (compact.EndsWith("kbps", StringComparison.Ordinal)
            || compact.EndsWith("kb/s", StringComparison.Ordinal))
        {
            compact = compact[..^4];
        }
        else if (compact.EndsWith('k'))
        {
            compact = compact[..^1];
        }

        if (!int.TryParse(compact, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        var normalized = parsed.ToString(CultureInfo.InvariantCulture);
        return SupportedFolderConvertBitrates.Contains(normalized)
            ? normalized
            : null;
    }

    private static bool RequiresAutoTagProfile(string? desiredQuality)
    {
        var normalized = (desiredQuality ?? string.Empty).Trim();
        return !string.Equals(normalized, FolderContentVideo, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, FolderContentPodcast, StringComparison.OrdinalIgnoreCase);
    }

    // --- Mood Bucket methods ---

    public async Task UpsertMoodBucketAsync(long trackId, string mood, double score, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO mood_bucket (track_id, mood, score, updated_at_utc)
VALUES (@trackId, @mood, @score, @updatedAt)
ON CONFLICT(track_id, mood) DO UPDATE SET score = @score, updated_at_utc = @updatedAt;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        command.Parameters.AddWithValue("mood", mood);
        command.Parameters.AddWithValue("score", score);
        command.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteMoodBucketsForTrackAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM mood_bucket WHERE track_id = @trackId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteMoodBucketAsync(long trackId, string mood, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM mood_bucket WHERE track_id = @trackId AND mood = @mood;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        command.Parameters.AddWithValue("mood", mood);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(long TrackId, double Score)>> GetMoodBucketTrackIdsAsync(
        string mood,
        int limit,
        long? libraryId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = @"
SELECT mb.track_id, mb.score
FROM mood_bucket mb
WHERE mb.mood = @mood AND mb.score >= 0.5";
        if (libraryId.HasValue)
        {
            sql += @"
  AND EXISTS (SELECT 1 FROM track_analysis ta WHERE ta.track_id = mb.track_id AND ta.library_id = @libraryId)";
        }
        sql += @"
ORDER BY mb.score DESC
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("mood", mood);
        command.Parameters.AddWithValue("limit", limit);
        if (libraryId.HasValue)
        {
            command.Parameters.AddWithValue(LibraryIdField, libraryId.Value);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<(long, double)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetInt64(0), reader.GetDouble(1)));
        }
        return results;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetMoodBucketCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT mood, COUNT(*) as cnt
FROM mood_bucket
WHERE score >= 0.5
GROUP BY mood;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results[reader.GetString(0)] = reader.GetInt32(1);
        }
        return results;
    }

    public async Task<IReadOnlyList<long>> GetUnbucketedAnalyzedTrackIdsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT ta.track_id
FROM track_analysis ta
WHERE ta.status IN ('complete', 'completed')
  AND NOT EXISTS (SELECT 1 FROM mood_bucket mb WHERE mb.track_id = ta.track_id)
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    public async Task<IReadOnlyList<long>> FindTrackIdsByArtistNamesAsync(
        IReadOnlyList<string> artistNames,
        long excludeTrackId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (artistNames.Count == 0 || limit <= 0)
        {
            return Array.Empty<long>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id
FROM track t
JOIN album a ON a.id = t.album_id
JOIN artist ar ON ar.id = a.artist_id
WHERE EXISTS (
    SELECT 1
    FROM json_each(@artistNamesJson)
    WHERE ar.name = value COLLATE NOCASE
)
  AND t.id <> @excludeTrackId
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistNamesJson", SerializeJsonArray(artistNames));
        command.Parameters.AddWithValue("excludeTrackId", excludeTrackId);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    public async Task<string?> GetArtistNameForTrackAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT ar.name
FROM track t
JOIN album a ON a.id = t.album_id
JOIN artist ar ON ar.id = a.artist_id
WHERE t.id = @trackId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull or null ? null : result.ToString();
    }

    public async Task ResetAllAnalysisAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var cmd = new SqliteCommand("DELETE FROM mood_bucket;", connection, (SqliteTransaction)transaction))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var cmd = new SqliteCommand("DELETE FROM track_analysis;", connection, (SqliteTransaction)transaction))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ArtistMetadataPolicyDto> GetArtistMetadataPolicyAsync(
        long artistId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT sync_blocked, ocr_text_art_blocking_enabled, selected_targets_json
FROM artist_metadata_policy
WHERE artist_id = @artistId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ArtistMetadataPolicyDto(artistId, false, true, Array.Empty<string>());
        }

        var targetsJson = await ReadNullableStringAsync(reader, 2, cancellationToken);
        return new ArtistMetadataPolicyDto(
            artistId,
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0,
            DeserializeArtistMetadataTargetList(targetsJson));
    }

    public async Task SetArtistMetadataSyncBlockedAsync(
        long artistId,
        bool blocked,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_metadata_policy (artist_id, sync_blocked, updated_at)
VALUES (@artistId, @blocked, CURRENT_TIMESTAMP)
ON CONFLICT(artist_id) DO UPDATE SET
    sync_blocked = excluded.sync_blocked,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("blocked", blocked ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetArtistMetadataOcrTextArtBlockingAsync(
        long artistId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_metadata_policy (artist_id, ocr_text_art_blocking_enabled, updated_at)
VALUES (@artistId, @enabled, CURRENT_TIMESTAMP)
ON CONFLICT(artist_id) DO UPDATE SET
    ocr_text_art_blocking_enabled = excluded.ocr_text_art_blocking_enabled,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("enabled", enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(string? Biography, string? Source)> GetArtistStoredBiographyAsync(
        long artistId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT apple_biography
FROM artist
WHERE id = @artistId
  AND apple_biography IS NOT NULL
  AND TRIM(apple_biography) <> ''
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? (null, null) : (Convert.ToString(result), "apple");
    }

    public async Task<IReadOnlyList<(long ArtistId, string? OriginalUrl)>> GetArtistArtworkOriginalUrlsAsync(
        string role,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT artist_id, original_url
FROM artist_artwork_cache
WHERE role = @role
  AND original_url IS NOT NULL
  AND TRIM(original_url) <> '';";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("role", role);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<(long, string?)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return results;
    }

    public async Task UpsertArtistArtworkCacheAsync(
        ArtistArtworkCacheUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_artwork_cache (
    artist_id, role, identity, source, original_url, local_path, content_hash,
    width, height, ocr_status, detected_text, text_art_blocked, user_blocked, last_seen_at)
VALUES (
    @artistId, @role, @identity, @source, @originalUrl, @localPath, @contentHash,
    @width, @height, @ocrStatus, @detectedText, @textArtBlocked, @userBlocked, CURRENT_TIMESTAMP)
ON CONFLICT(artist_id, role, identity) DO UPDATE SET
    source = COALESCE(excluded.source, source),
    original_url = COALESCE(excluded.original_url, original_url),
    local_path = COALESCE(excluded.local_path, local_path),
    content_hash = COALESCE(excluded.content_hash, content_hash),
    width = COALESCE(excluded.width, width),
    height = COALESCE(excluded.height, height),
    ocr_status = COALESCE(excluded.ocr_status, ocr_status),
    detected_text = COALESCE(excluded.detected_text, detected_text),
    text_art_blocked = excluded.text_art_blocked,
    user_blocked = CASE WHEN user_blocked = 1 THEN 1 ELSE excluded.user_blocked END,
    last_seen_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", input.ArtistId);
        command.Parameters.AddWithValue("role", input.Role);
        command.Parameters.AddWithValue("identity", input.Identity);
        command.Parameters.AddWithValue("source", (object?)input.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("originalUrl", (object?)input.OriginalUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("localPath", (object?)input.LocalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("contentHash", (object?)input.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("width", (object?)input.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("height", (object?)input.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("ocrStatus", (object?)input.OcrStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("detectedText", (object?)input.DetectedText ?? DBNull.Value);
        command.Parameters.AddWithValue("textArtBlocked", input.TextArtBlocked ? 1 : 0);
        command.Parameters.AddWithValue("userBlocked", input.UserBlocked ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArtistArtworkProvenanceDto?> GetArtistArtworkProvenanceAsync(
        long artistId,
        string role,
        string? localPath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT source, original_url, local_path, width, height
FROM artist_artwork_cache
WHERE artist_id = @artistId
  AND role = @role
  AND (@localPath IS NULL OR local_path = @localPath)
ORDER BY last_seen_at DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("localPath", (object?)localPath ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ArtistArtworkProvenanceDto(
            await ReadNullableStringAsync(reader, 0, cancellationToken),
            await ReadNullableStringAsync(reader, 1, cancellationToken),
            await ReadNullableStringAsync(reader, 2, cancellationToken),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt32(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt32(4));
    }

    public async Task<IReadOnlyList<ArtistArtworkCacheDto>> GetArtistArtworkCacheAsync(
        long artistId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT role, identity, source, original_url, local_path, content_hash,
       width, height, ocr_status, detected_text, text_art_blocked, user_blocked, last_seen_at
FROM artist_artwork_cache
WHERE artist_id = @artistId
ORDER BY source COLLATE NOCASE, last_seen_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ArtistArtworkCacheDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ArtistArtworkCacheDto(
                artistId,
                reader.GetString(0),
                reader.GetString(1),
                await ReadNullableStringAsync(reader, 2, cancellationToken),
                await ReadNullableStringAsync(reader, 3, cancellationToken),
                await ReadNullableStringAsync(reader, 4, cancellationToken),
                await ReadNullableStringAsync(reader, 5, cancellationToken),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt32(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetInt32(7),
                await ReadNullableStringAsync(reader, 8, cancellationToken),
                await ReadNullableStringAsync(reader, 9, cancellationToken),
                reader.GetInt64(10) != 0,
                reader.GetInt64(11) != 0,
                reader.GetString(12)));
        }

        return results;
    }

    public async Task SetArtistArtworkBlockedAsync(
        long artistId,
        string role,
        string identity,
        bool blocked,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_artwork_cache (artist_id, role, identity, user_blocked, last_seen_at)
VALUES (@artistId, @role, @identity, @blocked, CURRENT_TIMESTAMP)
ON CONFLICT(artist_id, role, identity) DO UPDATE SET
    user_blocked = excluded.user_blocked,
    last_seen_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("identity", identity);
        command.Parameters.AddWithValue("blocked", blocked ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsArtistArtworkBlockedAsync(
        long artistId,
        string role,
        string identity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT user_blocked
FROM artist_artwork_cache
WHERE artist_id = @artistId AND role = @role AND identity = @identity;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("identity", identity);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull && Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    public async Task UpsertArtistBiographyCacheAsync(
        long artistId,
        string source,
        string? biography,
        bool selected,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_biography_cache (artist_id, source, biography, selected, fetched_at)
VALUES (@artistId, @source, @biography, @selected, CURRENT_TIMESTAMP)
ON CONFLICT(artist_id, source) DO UPDATE SET
    biography = excluded.biography,
    selected = excluded.selected,
    fetched_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("biography", (object?)biography ?? DBNull.Value);
        command.Parameters.AddWithValue("selected", selected ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArtistBiographyCacheDto?> GetArtistBiographyCacheAsync(
        long artistId,
        string? preferredSource,
        bool allowFallback,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT source, biography, selected, fetched_at
FROM artist_biography_cache
WHERE artist_id = @artistId
  AND biography IS NOT NULL
  AND TRIM(biography) <> ''
  AND (@allowFallback = 1 OR source = @preferredSource)
ORDER BY
    CASE WHEN source = @preferredSource THEN 0 ELSE 1 END,
    selected DESC,
    fetched_at DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        command.Parameters.AddWithValue("preferredSource", (object?)preferredSource ?? DBNull.Value);
        command.Parameters.AddWithValue("allowFallback", allowFallback ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ArtistBiographyCacheDto(
            artistId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2) != 0,
            reader.GetString(3));
    }

    public async Task UpsertArtistServerSyncStateAsync(
        ArtistServerSyncStateUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO artist_server_sync_state (
    artist_id, server, last_cache_refresh_utc, last_sync_utc, last_avatar_hash,
    last_background_hash, last_biography_hash, avatar_rotation_index,
    background_rotation_index, last_result, last_error, updated_at)
VALUES (
    @artistId, @server, @lastCacheRefreshUtc, @lastSyncUtc, @lastAvatarHash,
    @lastBackgroundHash, @lastBiographyHash, @avatarRotationIndex,
    @backgroundRotationIndex, @lastResult, @lastError, CURRENT_TIMESTAMP)
ON CONFLICT(artist_id, server) DO UPDATE SET
    last_cache_refresh_utc = COALESCE(excluded.last_cache_refresh_utc, last_cache_refresh_utc),
    last_sync_utc = COALESCE(excluded.last_sync_utc, last_sync_utc),
    last_avatar_hash = COALESCE(excluded.last_avatar_hash, last_avatar_hash),
    last_background_hash = COALESCE(excluded.last_background_hash, last_background_hash),
    last_biography_hash = COALESCE(excluded.last_biography_hash, last_biography_hash),
    avatar_rotation_index = excluded.avatar_rotation_index,
    background_rotation_index = excluded.background_rotation_index,
    last_result = excluded.last_result,
    last_error = excluded.last_error,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", input.ArtistId);
        command.Parameters.AddWithValue("server", input.Server);
        command.Parameters.AddWithValue("lastCacheRefreshUtc", ToDbDate(input.LastCacheRefreshUtc));
        command.Parameters.AddWithValue("lastSyncUtc", ToDbDate(input.LastSyncUtc));
        command.Parameters.AddWithValue("lastAvatarHash", (object?)input.LastAvatarHash ?? DBNull.Value);
        command.Parameters.AddWithValue("lastBackgroundHash", (object?)input.LastBackgroundHash ?? DBNull.Value);
        command.Parameters.AddWithValue("lastBiographyHash", (object?)input.LastBiographyHash ?? DBNull.Value);
        command.Parameters.AddWithValue("avatarRotationIndex", input.AvatarRotationIndex);
        command.Parameters.AddWithValue("backgroundRotationIndex", input.BackgroundRotationIndex);
        command.Parameters.AddWithValue("lastResult", (object?)input.LastResult ?? DBNull.Value);
        command.Parameters.AddWithValue("lastError", (object?)input.LastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ManualUnavailableTrackDto>> GetManualUnavailableTracksAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<ManualUnavailableTrackDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id,
       queue_uuid,
       title,
       artist,
       album,
       album_artist,
       isrc,
       engine,
       source_service,
       source_url,
       deezer_track_id,
       spotify_track_id,
       apple_track_id,
       qobuz_track_id,
       tidal_track_id,
       amazon_track_id,
       destination_folder_id,
       expected_final_path,
       quality,
       content_type,
       reason,
       payload_json,
       first_unavailable_at_utc,
       COALESCE(next_retry_at_utc, datetime(added_at_utc, '+7 days')) AS next_retry_at_utc,
       added_at_utc,
       updated_at_utc
FROM manual_unavailable_track
ORDER BY added_at_utc ASC, id ASC;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ManualUnavailableTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(await ReadManualUnavailableTrackAsync(reader, cancellationToken));
        }

        return results;
    }

    public async Task<IReadOnlyList<ManualUnavailableTrackDto>> GetDueManualUnavailableTracksAsync(
        DateTimeOffset dueAtUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || limit <= 0)
        {
            return Array.Empty<ManualUnavailableTrackDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id,
       queue_uuid,
       title,
       artist,
       album,
       album_artist,
       isrc,
       engine,
       source_service,
       source_url,
       deezer_track_id,
       spotify_track_id,
       apple_track_id,
       qobuz_track_id,
       tidal_track_id,
       amazon_track_id,
       destination_folder_id,
       expected_final_path,
       quality,
       content_type,
       reason,
       payload_json,
       first_unavailable_at_utc,
       COALESCE(next_retry_at_utc, datetime(added_at_utc, '+7 days')) AS next_retry_at_utc,
       added_at_utc,
       updated_at_utc
FROM manual_unavailable_track
WHERE COALESCE(next_retry_at_utc, datetime(added_at_utc, '+7 days')) <= @dueAtUtc
ORDER BY next_retry_at_utc ASC, id ASC
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("dueAtUtc", dueAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ManualUnavailableTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(await ReadManualUnavailableTrackAsync(reader, cancellationToken));
        }

        return results;
    }

    public async Task<bool> IsManualUnavailableTrackMonitoredAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid) || !IsConfigured)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(
            "SELECT 1 FROM manual_unavailable_track WHERE queue_uuid = @queueUuid LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid.Trim());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<ManualUnavailableTrackDto?> UpsertManualUnavailableTrackAsync(
        ManualUnavailableTrackUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(input.QueueUuid))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var nowText = now.ToString("O", CultureInfo.InvariantCulture);
        var nextRetryText = (input.NextRetryAtUtc ?? now.AddDays(7)).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        const string sql = @"
INSERT INTO manual_unavailable_track (
    queue_uuid, title, artist, album, album_artist, isrc, engine, source_service, source_url,
    deezer_track_id, spotify_track_id, apple_track_id, qobuz_track_id, tidal_track_id, amazon_track_id,
    destination_folder_id, expected_final_path, quality, content_type, reason, payload_json,
    first_unavailable_at_utc, next_retry_at_utc, added_at_utc, updated_at_utc)
VALUES (
    @queueUuid, @title, @artist, @album, @albumArtist, @isrc, @engine, @sourceService, @sourceUrl,
    @deezerTrackId, @spotifyTrackId, @appleTrackId, @qobuzTrackId, @tidalTrackId, @amazonTrackId,
    @destinationFolderId, @expectedFinalPath, @quality, @contentType, @reason, @payloadJson,
    @now, @nextRetryAtUtc, @now, @now)
ON CONFLICT(queue_uuid) DO UPDATE SET
    title = excluded.title,
    artist = excluded.artist,
    album = excluded.album,
    album_artist = excluded.album_artist,
    isrc = excluded.isrc,
    engine = excluded.engine,
    source_service = excluded.source_service,
    source_url = excluded.source_url,
    deezer_track_id = excluded.deezer_track_id,
    spotify_track_id = excluded.spotify_track_id,
    apple_track_id = excluded.apple_track_id,
    qobuz_track_id = excluded.qobuz_track_id,
    tidal_track_id = excluded.tidal_track_id,
    amazon_track_id = excluded.amazon_track_id,
    destination_folder_id = excluded.destination_folder_id,
    expected_final_path = excluded.expected_final_path,
    quality = excluded.quality,
    content_type = excluded.content_type,
    reason = excluded.reason,
    payload_json = excluded.payload_json,
    next_retry_at_utc = excluded.next_retry_at_utc,
    updated_at_utc = excluded.updated_at_utc
RETURNING id,
          queue_uuid,
          title,
          artist,
          album,
          album_artist,
          isrc,
          engine,
          source_service,
          source_url,
          deezer_track_id,
          spotify_track_id,
          apple_track_id,
          qobuz_track_id,
          tidal_track_id,
          amazon_track_id,
          destination_folder_id,
          expected_final_path,
          quality,
          content_type,
          reason,
          payload_json,
          first_unavailable_at_utc,
          next_retry_at_utc,
          added_at_utc,
          updated_at_utc;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", input.QueueUuid.Trim());
        command.Parameters.AddWithValue("title", NormalizeRequiredText(input.Title, "Unknown Track"));
        command.Parameters.AddWithValue("artist", NormalizeRequiredText(input.Artist, "Unknown Artist"));
        command.Parameters.AddWithValue("album", ToDbText(input.Album));
        command.Parameters.AddWithValue("albumArtist", ToDbText(input.AlbumArtist));
        command.Parameters.AddWithValue("isrc", ToDbText(input.Isrc));
        command.Parameters.AddWithValue("engine", ToDbText(input.Engine));
        command.Parameters.AddWithValue("sourceService", ToDbText(input.SourceService));
        command.Parameters.AddWithValue("sourceUrl", ToDbText(input.SourceUrl));
        command.Parameters.AddWithValue("deezerTrackId", ToDbText(input.DeezerId));
        command.Parameters.AddWithValue("spotifyTrackId", ToDbText(input.SpotifyId));
        command.Parameters.AddWithValue("appleTrackId", ToDbText(input.AppleId));
        command.Parameters.AddWithValue("qobuzTrackId", ToDbText(input.QobuzId));
        command.Parameters.AddWithValue("tidalTrackId", ToDbText(input.TidalId));
        command.Parameters.AddWithValue("amazonTrackId", ToDbText(input.AmazonId));
        command.Parameters.AddWithValue("destinationFolderId", input.DestinationFolderId.HasValue ? input.DestinationFolderId.Value : DBNull.Value);
        command.Parameters.AddWithValue("expectedFinalPath", ToDbText(input.ExpectedFinalPath));
        command.Parameters.AddWithValue("quality", ToDbText(input.Quality));
        command.Parameters.AddWithValue("contentType", ToDbText(input.ContentType));
        command.Parameters.AddWithValue("reason", ToDbText(input.Reason));
        command.Parameters.AddWithValue("payloadJson", ToDbText(input.PayloadJson));
        command.Parameters.AddWithValue("now", nowText);
        command.Parameters.AddWithValue("nextRetryAtUtc", nextRetryText);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadManualUnavailableTrackAsync(reader, cancellationToken)
            : null;
    }

    public async Task<bool> DeleteManualUnavailableTrackAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || id <= 0)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand("DELETE FROM manual_unavailable_track WHERE id = @id;", connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ScheduleManualUnavailableTrackRetryAsync(
        long id,
        DateTimeOffset nextRetryAtUtc,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || id <= 0)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(@"
UPDATE manual_unavailable_track
SET next_retry_at_utc = @nextRetryAtUtc,
    reason = COALESCE(@reason, reason),
    updated_at_utc = @updatedAtUtc
WHERE id = @id;", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("nextRetryAtUtc", nextRetryAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("reason", ToDbText(reason));
        command.Parameters.AddWithValue("updatedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<ManualUnavailableTrackDto> ReadManualUnavailableTrackAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
            await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
            await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
            await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10),
            await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11),
            await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12),
            await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetString(13),
            await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetString(14),
            await reader.IsDBNullAsync(15, cancellationToken) ? null : reader.GetString(15),
            await reader.IsDBNullAsync(16, cancellationToken) ? null : reader.GetInt64(16),
            await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetString(17),
            await reader.IsDBNullAsync(18, cancellationToken) ? null : reader.GetString(18),
            await reader.IsDBNullAsync(19, cancellationToken) ? null : reader.GetString(19),
            await reader.IsDBNullAsync(20, cancellationToken) ? null : reader.GetString(20),
            await reader.IsDBNullAsync(21, cancellationToken) ? null : reader.GetString(21),
            ParseUtcDateTimeOffsetInvariant(reader.GetString(22)),
            ParseUtcDateTimeOffsetInvariant(reader.GetString(23)),
            ParseUtcDateTimeOffsetInvariant(reader.GetString(24)),
            ParseUtcDateTimeOffsetInvariant(reader.GetString(25)));

    private static object ToDbDate(DateTimeOffset? value)
        => value.HasValue ? value.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static object ToDbText(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string NormalizeRequiredText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static IReadOnlyList<string> DeserializeArtistMetadataTargetList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private sealed record FolderRoot(long Id, string Root, string RootPath);
}

public sealed record ArtistMetadataPolicyDto(
    long ArtistId,
    bool SyncBlocked,
    bool OcrTextArtBlockingEnabled,
    IReadOnlyList<string> SelectedTargets);

public sealed record ArtistArtworkCacheUpsertInput(
    long ArtistId,
    string Role,
    string Identity,
    string? Source,
    string? OriginalUrl,
    string? LocalPath,
    string? ContentHash,
    int? Width,
    int? Height,
    string? OcrStatus,
    string? DetectedText,
    bool TextArtBlocked,
    bool UserBlocked);

public sealed record ArtistArtworkProvenanceDto(
    string? Source,
    string? OriginalUrl,
    string? LocalPath,
    int? Width,
    int? Height);

public sealed record ArtistArtworkCacheDto(
    long ArtistId,
    string Role,
    string Identity,
    string? Source,
    string? OriginalUrl,
    string? LocalPath,
    string? ContentHash,
    int? Width,
    int? Height,
    string? OcrStatus,
    string? DetectedText,
    bool TextArtBlocked,
    bool UserBlocked,
    string LastSeenAt);

public sealed record ArtistBiographyCacheDto(
    long ArtistId,
    string Source,
    string Biography,
    bool Selected,
    string FetchedAt);

public sealed record ArtistServerSyncStateUpsertInput(
    long ArtistId,
    string Server,
    DateTimeOffset? LastCacheRefreshUtc,
    DateTimeOffset? LastSyncUtc,
    string? LastAvatarHash,
    string? LastBackgroundHash,
    string? LastBiographyHash,
    int AvatarRotationIndex,
    int BackgroundRotationIndex,
    string? LastResult,
    string? LastError);
