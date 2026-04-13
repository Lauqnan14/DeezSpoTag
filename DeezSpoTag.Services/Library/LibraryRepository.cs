using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Library;
public sealed class LibraryRepository
{
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
        string? Error);

    public sealed record PlayHistoryWriteInput(
        long PlexUserId,
        long? LibraryId,
        long? TrackId,
        string? PlexTrackKey,
        string? PlexRatingKey,
        DateTimeOffset PlayedAtUtc,
        int? DurationMs,
        string? MetadataJson);

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
        string? ConvertBitrate);

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
        string? PreferredEngine,
        string? DownloadVariantMode,
        string? SyncMode,
        bool UpdateArtwork,
        bool ReuseSavedArtwork,
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules = null,
        IReadOnlyList<PlaylistTrackBlockRule>? IgnoreRules = null);

    public sealed record PlaylistWatchStateUpsertInput(
        string Source,
        string SourceId,
        string? SnapshotId,
        int? TrackCount,
        int? BatchNextOffset,
        string? BatchProcessingSnapshotId,
        DateTimeOffset? LastCheckedUtc);

    private const DateTimeStyles ParseDateStyles = DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces;
    private const string ArtistType = "artist";
    private const string AlbumType = "album";
    private const string TrackType = "track";
    private const string TitleField = "title";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private const string AtmosVariant = "atmos";
    private const string RequireAtmosField = "requireAtmos";
    private const string TrackIdField = "trackId";
    private const string SourceField = "source";
    private const string SourceIdField = "sourceId";
    private const string LibraryIdField = "libraryId";
    private const string DurationMsField = "durationMs";
    private const string TrackIdsJsonParameter = "trackIdsJson";
    private const string EntityIdParameter = "entityId";
    private const string TrackGenreTable = "track_genre";
    private const string TrackStyleTable = "track_style";
    private const string TrackMoodTable = "track_mood";
    private const string TrackRemixerTable = "track_remixer";
    private const string TrackOtherTagTable = "track_other_tag";
    private static readonly HashSet<string> SupportedFolderConvertFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3",
        "aac",
        "alac",
        "ogg",
        "opus",
        "flac",
        "wav"
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
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('hi_res_lossless', '27', '7')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi_res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi-res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24 bit%' THEN 4
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('alac', 'flac', 'lossless', '9', '6')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%lossless%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%flac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%alac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16 bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%cd%' THEN 3
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('aac', '3')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%aac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%320%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%vorbis%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%opus%' THEN 2
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = '1'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%128%' THEN 1
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
        command.Parameters.AddWithValue("folderId", folderId);

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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var artistsRemoved = await CountRowsAsync(connection, transaction, ArtistType, cancellationToken);
        var albumsRemoved = await CountRowsAsync(connection, transaction, AlbumType, cancellationToken);
        var tracksRemoved = await CountRowsAsync(connection, transaction, TrackType, cancellationToken);

        const string sql = @"
DELETE FROM track_local;
DELETE FROM album_local;
DELETE FROM audio_file;
DELETE FROM track_source;
DELETE FROM album_source;
DELETE FROM artist_source;
DELETE FROM track;
DELETE FROM album;
DELETE FROM artist;
DELETE FROM match_candidate;
DELETE FROM scan_job;
DELETE FROM library_log;
DELETE FROM artist_page_cache;
DELETE FROM artist_page_genre;
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
            command.Parameters.AddWithValue("folderId", folderId);
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

        await CleanupOrphansAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return missingIds.Count;
    }

    private static async Task<List<long>> FindMissingAudioFileIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? folderId,
        CancellationToken cancellationToken)
    {
        const string selectSql = @"
SELECT id, path
FROM audio_file
WHERE @folderId IS NULL OR folder_id = @folderId;";
        await using var selectCommand = new SqliteCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
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
        command.Parameters.AddWithValue("folderId", folderId);
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
        var path = await reader.IsDBNullAsync(1, cancellationToken) ? string.Empty : reader.GetString(1);
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

    public async Task<LibrarySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT fuzzy_threshold, include_all_folders, live_preview_ingest, enable_signal_analysis FROM library_settings WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var threshold = await reader.IsDBNullAsync(0, cancellationToken) ? 0.85m : Convert.ToDecimal(reader.GetDouble(0));
            var includeAll = !await reader.IsDBNullAsync(1, cancellationToken) && reader.GetBoolean(1);
            var livePreviewIngest = !await reader.IsDBNullAsync(2, cancellationToken) && reader.GetBoolean(2);
            var enableSignalAnalysis = !await reader.IsDBNullAsync(3, cancellationToken) && reader.GetBoolean(3);
            return new LibrarySettingsDto(threshold, includeAll, livePreviewIngest, enableSignalAnalysis);
        }

        return new LibrarySettingsDto(0.85m, true, false, false);
    }

    public async Task<LibrarySettingsDto> UpdateSettingsAsync(LibrarySettingsDto settings, CancellationToken cancellationToken = default)
    {
        await EnsureSettingsRowAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE library_settings
SET fuzzy_threshold = @threshold,
    include_all_folders = @includeAll,
    live_preview_ingest = @livePreviewIngest,
    enable_signal_analysis = @enableSignalAnalysis,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("threshold", (double)settings.FuzzyThreshold);
        command.Parameters.AddWithValue("includeAll", settings.IncludeAllFolders);
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
                360,
                "watchlist",
                null,
                false,
                1440,
                null,
                null);
        }

        return new QualityScannerAutomationSettingsDto(
            !await reader.IsDBNullAsync(0, cancellationToken) && reader.GetBoolean(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? 360 : Math.Clamp(reader.GetInt32(1), 15, 10080),
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
        command.Parameters.AddWithValue("folderId", (object?)settings.FolderId ?? DBNull.Value);
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
        command.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
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
        return string.IsNullOrWhiteSpace(qualityValue)
            ? numericQuality.ToString(CultureInfo.InvariantCulture)
            : qualityValue;
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
        command.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<string>> GetLibraryDeezerTrackSourceIdsAsync(
        long libraryId,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
const string sql = @"
WITH candidate_ids AS (
    SELECT CASE
               WHEN NULLIF(TRIM(ts.source_id), '') IS NOT NULL
                    AND TRIM(ts.source_id) GLOB '[0-9]*'
                    AND TRIM(ts.source_id) NOT GLOB '*[^0-9]*'
                    THEN TRIM(ts.source_id)
               WHEN NULLIF(TRIM(t.deezer_id), '') IS NOT NULL
                    AND TRIM(t.deezer_id) GLOB '[0-9]*'
                    AND TRIM(t.deezer_id) NOT GLOB '*[^0-9]*'
                    THEN TRIM(t.deezer_id)
               ELSE NULL
           END AS deezer_source_id
    FROM track t
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    LEFT JOIN track_source ts
           ON ts.track_id = t.id
          AND ts.source = 'deezer'
    WHERE f.library_id = @libraryId
      AND (@folderId IS NULL OR f.id = @folderId)
)
SELECT DISTINCT deezer_source_id
FROM candidate_ids
WHERE deezer_source_id IS NOT NULL;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sourceIds = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (await reader.IsDBNullAsync(0, cancellationToken))
            {
                continue;
            }

            var sourceId = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                sourceIds.Add(sourceId);
            }
        }

        return sourceIds;
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

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT af.path,
       af.relative_path,
       f.root_path
FROM track_local tl
JOIN audio_file af ON af.id = tl.audio_file_id
LEFT JOIN folder f ON f.id = af.folder_id
WHERE tl.track_id = @trackId
ORDER BY af.quality_rank DESC NULLS LAST, af.size DESC, af.id DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(TrackIdField, trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var path = await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetString(0);
        var relativePath = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
        var rootPath = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
        var resolved = BuildAbsolutePath(rootPath, relativePath, path);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return null;
        }

        return resolved;
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
       c.error
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
        command.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
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
            await ReadNullableStringAsync(reader, 8, cancellationToken));
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
        command.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
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
    error
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
    @error
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
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        var relatedTracksJson = input.RelatedTracks is { Count: > 0 } ? JsonSerializer.Serialize(input.RelatedTracks) : null;
        command.Parameters.AddWithValue(TrackIdField, input.TrackId);
        command.Parameters.AddWithValue("shazamTrackId", (object?)input.ShazamTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue(TitleField, (object?)input.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("artist", (object?)input.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("isrc", (object?)input.Isrc ?? DBNull.Value);
        command.Parameters.AddWithValue("status", string.IsNullOrWhiteSpace(input.Status) ? "pending" : input.Status.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("relatedTracksJson", (object?)relatedTracksJson ?? DBNull.Value);
        command.Parameters.AddWithValue("scannedAtUtc", input.ScannedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("error", (object?)input.Error ?? DBNull.Value);
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
        command.Parameters.AddWithValue("folderId", folderRoot.Id);
        command.Parameters.AddWithValue("relative", relative);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            return null;
        }
        return Convert.ToInt64(result);
    }

    public async Task AddPlayHistoryAsync(
        PlayHistoryWriteInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = $@"
INSERT OR IGNORE INTO play_history
    (library_id, plex_user_id, track_id, plex_track_key, plex_rating_key, played_at_utc, play_duration_ms, source, metadata_json)
VALUES
    (@libraryId, @plexUserId, @trackId, @plexTrackKey, @plexRatingKey, @playedAtUtc, @{DurationMsField}, 'plex', @metadataJson);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(LibraryIdField, (object?)input.LibraryId ?? DBNull.Value);
        command.Parameters.AddWithValue("plexUserId", input.PlexUserId);
        command.Parameters.AddWithValue(TrackIdField, (object?)input.TrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("plexTrackKey", (object?)input.PlexTrackKey ?? DBNull.Value);
        command.Parameters.AddWithValue("plexRatingKey", (object?)input.PlexRatingKey ?? DBNull.Value);
        command.Parameters.AddWithValue("playedAtUtc", input.PlayedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(DurationMsField, (object?)input.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("metadataJson", (object?)input.MetadataJson ?? DBNull.Value);
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
        CancellationToken cancellationToken = default)
    {
        if (allowedHours.Count == 0)
        {
            return Array.Empty<PlayHistoryEntryDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT ph.track_id,
       ph.played_at_utc
FROM play_history ph
WHERE ph.plex_user_id = @plexUserId
  AND ph.library_id = @libraryId
  AND ph.track_id IS NOT NULL
  AND ph.played_at_utc >= @lookbackStart
  AND ph.played_at_utc < @excludeAfter
  AND CAST(strftime('%H', ph.played_at_utc) AS INTEGER) IN (
      SELECT CAST(value AS INTEGER)
      FROM json_each(@allowedHoursJson)
  )
ORDER BY ph.played_at_utc DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue("lookbackStart", lookbackStartUtc.ToString("O"));
        command.Parameters.AddWithValue("excludeAfter", excludeAfterUtc.ToString("O"));
        command.Parameters.AddWithValue("allowedHoursJson", SerializeJsonArray(allowedHours));

        var results = new List<PlayHistoryEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var playedAt = ParseDateTimeOffsetInvariant(reader.GetString(1));
            results.Add(new PlayHistoryEntryDto(reader.GetInt64(0), playedAt));
        }

        return results;
    }

    public async Task<IReadOnlySet<long>> GetPlayedTrackIdsSinceAsync(
        long plexUserId,
        long libraryId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT DISTINCT ph.track_id
FROM play_history ph
WHERE ph.plex_user_id = @plexUserId
  AND ph.library_id = @libraryId
  AND ph.track_id IS NOT NULL
  AND ph.played_at_utc >= @sinceUtc;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("plexUserId", plexUserId);
        command.Parameters.AddWithValue(LibraryIdField, libraryId);
        command.Parameters.AddWithValue("sinceUtc", sinceUtc.ToString("O"));

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
       t.duration_ms
FROM track t
JOIN album a ON a.id = t.album_id
JOIN artist ar ON ar.id = a.artist_id
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
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5)));
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
SELECT track_id,
       plex_rating_key
FROM play_history
WHERE track_id IN (
    SELECT CAST(value AS INTEGER)
    FROM json_each(@trackIdsJson)
)
  AND plex_rating_key IS NOT NULL
ORDER BY played_at_utc DESC;";
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
WHERE source = @source
  AND source_id IN (
      SELECT value
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
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackTitle))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = $@"
SELECT al.id
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
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt64(result);
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
SELECT plex_rating_key,
       track_id
FROM play_history
WHERE plex_rating_key IN (
    SELECT value
    FROM json_each(@ratingKeysJson)
)
  AND track_id IS NOT NULL
ORDER BY played_at_utc DESC;";
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

    public async Task<IReadOnlyList<TrackAnalysisInputDto>> GetTracksForAnalysisAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
LEFT JOIN track_analysis ta ON ta.track_id = t.id
WHERE f.enabled = 1
  AND (ta.status IS NULL OR ta.status IN ('pending', 'failed', 'error'))
ORDER BY t.id, af.quality_rank DESC NULLS LAST, af.size DESC, af.id DESC
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<TrackAnalysisInputDto>();
        var seenTrackIds = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetInt64(0);
            if (!seenTrackIds.Add(trackId))
            {
                continue;
            }

            var filePath = await ReadAudioFilePathAsync(reader, 2, 3, 4, cancellationToken);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }
            results.Add(new TrackAnalysisInputDto(
                trackId,
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetInt64(1),
                filePath,
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5)));
        }
        return results;
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
ORDER BY f.enabled DESC, af.quality_rank DESC NULLS LAST, af.size DESC, af.id DESC
LIMIT 1;";
        return await QuerySingleTrackAsync(sql, trackId, ReadTrackAnalysisInputAsync, cancellationToken);
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
WHERE f.enabled = 1;";
        const string analyzedSql = @"
SELECT COUNT(DISTINCT ta.track_id)
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1 AND ta.status IN ('complete', 'completed');";
        const string errorSql = @"
SELECT COUNT(DISTINCT ta.track_id)
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1 AND ta.status IN ('error', 'failed');";
        const string lastRunSql = @"
SELECT MAX(ta.analyzed_at_utc)
FROM track_analysis ta
JOIN track t ON t.id = ta.track_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.enabled = 1 AND ta.analyzed_at_utc IS NOT NULL;";

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

    public async Task<LatestTrackAnalysisDto?> GetProcessingTrackAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT ta.track_id, ta.library_id
FROM track_analysis ta
WHERE ta.status = 'processing'
ORDER BY ta.track_id
LIMIT 1;";

        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var trackId = reader.GetInt64(0);
        var libraryId = await reader.IsDBNullAsync(1, cancellationToken) ? default(long?) : reader.GetInt64(1);

        var summary = await GetTrackSummariesAsync(new List<long> { trackId }, cancellationToken);
        var track = summary.Count > 0 ? summary[0] : null;
        if (track is null)
        {
            return null;
        }

        var processingAnalysis = new TrackAnalysisResultDto(
            trackId,
            libraryId,
            "processing",
            null, // energy
            null, // rms
            null, // zero crossing
            null, // spectral centroid
            null, // bpm
            null, // analyzed at
            null, // error
            null, // analysis mode
            null, // analysis version
            null, // mood tags
            null, // mood happy
            null, // mood sad
            null, // mood relaxed
            null, // mood aggressive
            null, // mood party
            null, // mood acoustic
            null, // mood electronic
            null, // valence
            null, // arousal
            null, // beats count
            null, // key
            null, // key scale
            null, // key strength
            null, // loudness
            null, // dynamic range
            null, // danceability
            null, // instrumentalness
            null, // acousticness
            null, // speechiness
            null, // danceability ml
            null, // essentia genres
            null, // lastfm tags
            null, // approachability
            null, // engagement
            null, // voice instrumental
            null, // tonal atonal
            null, // valence ml
            null, // arousal ml
            null, // dynamic complexity
            null  // loudness ml
        );

        return new LatestTrackAnalysisDto(track, processingAnalysis);
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
SELECT track_id, library_id, status, energy, rms, zero_crossing, spectral_centroid, bpm, beats_count, key, key_scale, key_strength, loudness, dynamic_range, danceability, instrumentalness, acousticness, speechiness, danceability_ml, valence, arousal, analyzed_at_utc, error, analysis_mode, analysis_version, mood_tags, mood_happy, mood_sad, mood_relaxed, mood_aggressive, mood_party, mood_acoustic, mood_electronic, essentia_genres, lastfm_tags
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
            results.Add(await ReadTrackAnalysisResultDtoAsync(reader, offset: 0, cancellationToken));
        }

        return results;
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
SELECT track_id, library_id, status, energy, rms, zero_crossing, spectral_centroid, bpm, beats_count, key, key_scale, key_strength, loudness, dynamic_range, danceability, instrumentalness, acousticness, speechiness, danceability_ml, valence, arousal, analyzed_at_utc, error, analysis_mode, analysis_version, mood_tags, mood_happy, mood_sad, mood_relaxed, mood_aggressive, mood_party, mood_acoustic, mood_electronic, essentia_genres, lastfm_tags
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
            var dto = await ReadTrackAnalysisResultDtoAsync(reader, offset: 0, cancellationToken);

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

    private static async Task<TrackAnalysisInputDto?> ReadTrackAnalysisInputAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var filePath = await ReadAudioFilePathAsync(reader, 2, 3, 4, cancellationToken);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return new TrackAnalysisInputDto(
            reader.GetInt64(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetInt64(1),
            filePath,
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetInt32(5));
    }

    private static async Task<TrackAudioInfoDto?> ReadTrackAudioInfoAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var filePath = await ReadAudioFilePathAsync(reader, 6, 7, 8, cancellationToken);
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
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5));
    }

    private static async Task<PlaylistWatchPreferenceDto> ReadPlaylistWatchPreferenceAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var updateArtwork = await reader.IsDBNullAsync(7, cancellationToken) || reader.GetInt32(7) != 0;
        var reuseSavedArtwork = !await reader.IsDBNullAsync(8, cancellationToken) && reader.GetInt32(8) != 0;
        var created = await reader.IsDBNullAsync(9, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(9));
        var updated = await reader.IsDBNullAsync(10, cancellationToken) ? created : ParseDateTimeOffsetInvariant(reader.GetString(10));
        var rulesJson = await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11);
        var rules = rulesJson is null ? null : JsonSerializer.Deserialize<List<PlaylistTrackRoutingRule>>(rulesJson);
        var ignoreRulesJson = await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12);
        var ignoreRules = ignoreRulesJson is null ? null : JsonSerializer.Deserialize<List<PlaylistTrackBlockRule>>(ignoreRulesJson);
        return new PlaylistWatchPreferenceDto(
            reader.GetString(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetInt64(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            updateArtwork,
            reuseSavedArtwork,
            created,
            updated,
            rules,
            ignoreRules);
    }

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

    public async Task<long?> GetMixCacheIdAsync(string mixId, long plexUserId, long libraryId, CancellationToken cancellationToken = default)
    {
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
        command.Parameters.AddWithValue("trackCount", input.TrackCount);
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
       t.duration_ms
FROM mix_item mi
LEFT JOIN track t ON t.id = mi.track_id
LEFT JOIN album al ON al.id = t.album_id
LEFT JOIN artist ar ON ar.id = al.artist_id
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
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt32(6)));
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var libraryId = await EnsureLibraryAsync(connection, input.LibraryName, cancellationToken);
        var desiredQualityNumeric = NormalizeDesiredQualityRank(input.DesiredQuality);
        var autoTagEnabled = !RequiresAutoTagProfile(input.DesiredQuality);
        var (normalizedConvertEnabled, normalizedConvertFormat, normalizedConvertBitrate) =
            NormalizeFolderConvertSettings(input.ConvertEnabled, input.ConvertFormat, input.ConvertBitrate);
        const string sql = @"
INSERT INTO folder (root_path, display_name, enabled, library_id, desired_quality, desired_quality_value, auto_tag_enabled, convert_enabled, convert_format, convert_bitrate)
VALUES (@rootPath, @displayName, @enabled, @libraryId, @desiredQualityNumeric, @desiredQualityValue, @autoTagEnabled, @convertEnabled, @convertFormat, @convertBitrate)
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
        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        return (await GetFoldersAsync(cancellationToken)).First(folder => folder.Id == Convert.ToInt64(insertedId));
    }

    public async Task<FolderDto?> UpdateFolderAsync(
        long id,
        FolderUpsertInput input,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var libraryId = await EnsureLibraryAsync(connection, input.LibraryName, cancellationToken);
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
        command.Parameters.AddWithValue("folderId", folderId);
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
        command.Parameters.AddWithValue("folderId", folderId);
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
        countCommand.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
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
       a.preferred_background_path
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
        command.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
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
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3)));
        }

        return new ArtistPageDto(artists, totalCount, safePage, safePageSize);
    }

    public async Task<IReadOnlyList<AlbumDto>> GetArtistAlbumsAsync(long artistId, CancellationToken cancellationToken = default)
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
  )
GROUP BY al.id, al.artist_id, al.title, al.preferred_cover_path
ORDER BY al.title;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
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
       a.preferred_background_path
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
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3));
        }

        return null;
    }

    public async Task<IReadOnlyList<ArtistDetailDto>> GetArtistsMissingImageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT a.id, a.name, a.preferred_image_path, a.preferred_background_path
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
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3)));
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
       ws.last_checked_utc
FROM artist_watchlist w
LEFT JOIN artist a ON a.id = w.artist_id
LEFT JOIN artist_watch_state ws ON ws.artist_id = w.artist_id
ORDER BY w.created_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<WatchlistArtistDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var created = await reader.IsDBNullAsync(6, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(6));
            var lastChecked = await reader.IsDBNullAsync(7, cancellationToken) ? (DateTimeOffset?)null : ParseDateTimeOffsetInvariant(reader.GetString(7));
            items.Add(new WatchlistArtistDto(
                reader.GetInt64(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                created,
                lastChecked));
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
       w.created_at
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

        var created = await reader.IsDBNullAsync(6, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(6));
        return new WatchlistArtistDto(
            reader.GetInt64(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
            created);
    }

    public async Task<bool> RemoveWatchlistAsync(long artistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM artist_watchlist WHERE artist_id = @artistId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistId", artistId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
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
        const string sql = @"DELETE FROM artist_watchlist WHERE LOWER(spotify_id) = LOWER(@spotifyId);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("spotifyId", normalizedSpotifyId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
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
       updated_at
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
            updated);
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

    public async Task<IReadOnlyList<PlaylistWatchlistDto>> GetPlaylistWatchlistAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id,
       source,
       source_id,
       name,
       image_url,
       description,
       track_count,
       created_at
FROM playlist_watchlist
ORDER BY created_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PlaylistWatchlistDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var created = await reader.IsDBNullAsync(7, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(7));
            items.Add(new PlaylistWatchlistDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetInt32(6),
                created));
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
        string name,
        string? imageUrl,
        string? description,
        int? trackCount,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT OR IGNORE INTO playlist_watchlist (source, source_id, name, image_url, description, track_count)
VALUES (@source, @sourceId, @name, @imageUrl, @description, @trackCount);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("imageUrl", (object?)imageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("trackCount", (object?)trackCount ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        const string selectSql = @"
SELECT id,
       source,
       source_id,
       name,
       image_url,
       description,
       track_count,
       created_at
FROM playlist_watchlist
WHERE source = @source AND source_id = @sourceId
LIMIT 1;";
        await using var selectCommand = new SqliteCommand(selectSql, connection);
        selectCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        selectCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
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
            created);
    }

    public async Task UpdatePlaylistWatchlistMetadataAsync(
        string source,
        string sourceId,
        string? name,
        string? imageUrl,
        string? description,
        int? trackCount,
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
    image_url = COALESCE(@imageUrl, image_url),
    description = COALESCE(@description, description),
    track_count = COALESCE(@trackCount, track_count)
WHERE source = @source AND source_id = @sourceId;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("imageUrl", (object?)imageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("trackCount", (object?)trackCount ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
	       service,
	       preferred_engine,
	       download_variant_mode,
	       sync_mode,
	       update_artwork,
	       reuse_saved_artwork,
	       created_at,
	       updated_at,
       routing_rules_json,
       ignore_rules_json
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
	       service,
	       preferred_engine,
	       download_variant_mode,
	       sync_mode,
	       update_artwork,
	       reuse_saved_artwork,
	       created_at,
	       updated_at,
       routing_rules_json,
       ignore_rules_json
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
    {
        if (!TryNormalizePlaylistWatchKey(input.Source, input.SourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
	INSERT INTO playlist_watch_preferences (source, source_id, destination_folder_id, service, preferred_engine, download_variant_mode, sync_mode, update_artwork, reuse_saved_artwork, routing_rules_json, ignore_rules_json)
	        VALUES (@source, @sourceId, @destinationFolderId, @service, @preferredEngine, @downloadVariantMode, @syncMode, @updateArtwork, @reuseSavedArtwork, @routingRulesJson, @ignoreRulesJson)
	ON CONFLICT(source, source_id) DO UPDATE SET
	    destination_folder_id = excluded.destination_folder_id,
	    service = excluded.service,
	    preferred_engine = excluded.preferred_engine,
	    download_variant_mode = excluded.download_variant_mode,
	    sync_mode = excluded.sync_mode,
	    update_artwork = excluded.update_artwork,
	    reuse_saved_artwork = excluded.reuse_saved_artwork,
	    routing_rules_json = excluded.routing_rules_json,
	    ignore_rules_json = excluded.ignore_rules_json,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("destinationFolderId", (object?)input.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("service", (object?)input.Service ?? DBNull.Value);
        command.Parameters.AddWithValue("preferredEngine", (object?)input.PreferredEngine ?? DBNull.Value);
        command.Parameters.AddWithValue("downloadVariantMode", (object?)input.DownloadVariantMode ?? DBNull.Value);
        command.Parameters.AddWithValue("syncMode", (object?)input.SyncMode ?? DBNull.Value);
        command.Parameters.AddWithValue("updateArtwork", input.UpdateArtwork ? 1 : 0);
        command.Parameters.AddWithValue("reuseSavedArtwork", input.ReuseSavedArtwork ? 1 : 0);
        var rulesJson = input.RoutingRules is { Count: > 0 } ? JsonSerializer.Serialize(input.RoutingRules) : null;
        command.Parameters.AddWithValue("routingRulesJson", (object?)rulesJson ?? DBNull.Value);
        var ignoreRulesJson = input.IgnoreRules is { Count: > 0 } ? JsonSerializer.Serialize(input.IgnoreRules) : null;
        command.Parameters.AddWithValue("ignoreRulesJson", (object?)ignoreRulesJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetPlaylistWatchPreferenceAsync(normalizedSource, normalizedSourceId, cancellationToken);
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
       updated_at
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
INSERT INTO playlist_watch_state (source, source_id, snapshot_id, track_count, batch_next_offset, batch_processing_snapshot_id, last_checked_utc)
VALUES (@source, @sourceId, @snapshotId, @trackCount, @batchNextOffset, @batchProcessingSnapshotId, @lastCheckedUtc)
ON CONFLICT(source, source_id) DO UPDATE SET
    snapshot_id = excluded.snapshot_id,
    track_count = excluded.track_count,
    batch_next_offset = excluded.batch_next_offset,
    batch_processing_snapshot_id = excluded.batch_processing_snapshot_id,
    last_checked_utc = excluded.last_checked_utc,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("snapshotId", (object?)input.SnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("trackCount", (object?)input.TrackCount ?? DBNull.Value);
        command.Parameters.AddWithValue("batchNextOffset", (object?)input.BatchNextOffset ?? DBNull.Value);
        command.Parameters.AddWithValue("batchProcessingSnapshotId", (object?)input.BatchProcessingSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("lastCheckedUtc", input.LastCheckedUtc?.ToString("O") ?? (object)DBNull.Value);
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
       updated_at
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
            updated);
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
            updatedAt);
    }

    public async Task UpsertPlaylistTrackCandidateCacheAsync(
        string source,
        string sourceId,
        string? snapshotId,
        string candidatesJson,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(source, sourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO playlist_track_candidate_cache (source, source_id, snapshot_id, candidates_json)
VALUES (@source, @sourceId, @snapshotId, @candidatesJson)
ON CONFLICT(source, source_id) DO UPDATE SET
    snapshot_id = excluded.snapshot_id,
    candidates_json = excluded.candidates_json,
    updated_at = CURRENT_TIMESTAMP;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("snapshotId", (object?)snapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("candidatesJson", candidatesJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HashSet<string>> GetPlaylistWatchTrackIdsAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT track_source_id
FROM playlist_watch_track
WHERE source = @source AND source_id = @sourceId AND status = 'completed';";
        return await QueryPlaylistWatchTrackSourceIdsAsync(sql, source, sourceId, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        var normalizedTrack = NormalizeBlocklistValue(trackTitle);
        var normalizedArtist = NormalizeBlocklistValue(artistName);
        var normalizedAlbum = NormalizeBlocklistValue(albumTitle);
        if (string.IsNullOrWhiteSpace(normalizedTrack)
            && string.IsNullOrWhiteSpace(normalizedArtist)
            && string.IsNullOrWhiteSpace(normalizedAlbum))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT field, value
FROM download_blocklist
WHERE is_enabled = 1
  AND (
      (field = 'track' AND normalized_value = @track)
      OR (field = 'artist' AND normalized_value = @artist)
      OR (field = 'album' AND normalized_value = @album)
  )
ORDER BY id
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("track", (object?)normalizedTrack ?? DBNull.Value);
        command.Parameters.AddWithValue("artist", (object?)normalizedArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("album", (object?)normalizedAlbum ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DownloadBlocklistMatchDto(reader.GetString(0), reader.GetString(1));
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

        const string sql = @"
INSERT OR IGNORE INTO playlist_watch_track (source, source_id, track_source_id, isrc, status)
VALUES (@source, @sourceId, @trackSourceId, @isrc, 'queued');";
        await InsertPlaylistWatchRowsAsync(
            sql,
            source,
            sourceId,
            tracks,
            track => track.TrackSourceId,
            track => track.Isrc,
            cancellationToken);
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
INSERT OR IGNORE INTO playlist_watch_track (source, source_id, track_source_id, status)
VALUES (@source, @sourceId, @trackSourceId, @status);";
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
SET status = @status
WHERE source = @source AND source_id = @sourceId AND track_source_id = @trackSourceId;";
        await using var updateCommand = new SqliteCommand(updateSql, connection);
        updateCommand.Parameters.AddWithValue("status", status);
        updateCommand.Parameters.AddWithValue(SourceField, normalizedSource);
        updateCommand.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        updateCommand.Parameters.AddWithValue("trackSourceId", trackSourceId);
        var updated = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        return updated > 0;
    }

    public async Task AddWatchlistHistoryAsync(
        WatchlistHistoryInsert entry,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePlaylistWatchKey(entry.Source, entry.SourceId, out var normalizedSource, out var normalizedSourceId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
INSERT INTO watchlist_history (source, watch_type, source_id, name, collection_type, track_count, status, artist_name)
VALUES (@source, @watchType, @sourceId, @name, @collectionType, @trackCount, @status, @artistName);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, normalizedSource);
        command.Parameters.AddWithValue("watchType", entry.WatchType);
        command.Parameters.AddWithValue(SourceIdField, normalizedSourceId);
        command.Parameters.AddWithValue("name", entry.Name);
        command.Parameters.AddWithValue("collectionType", entry.CollectionType);
        command.Parameters.AddWithValue("trackCount", entry.TrackCount);
        command.Parameters.AddWithValue("status", entry.Status);
        command.Parameters.AddWithValue("artistName", (object?)entry.ArtistName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetWatchlistHistoryCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"SELECT COUNT(*) FROM watchlist_history;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<WatchlistHistoryDto>> GetWatchlistHistoryAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
       created_at
FROM watchlist_history
ORDER BY created_at DESC
LIMIT @limit OFFSET @offset;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<WatchlistHistoryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var created = await reader.IsDBNullAsync(9, cancellationToken) ? DateTimeOffset.MinValue : ParseDateTimeOffsetInvariant(reader.GetString(9));
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
                created));
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
                catch
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

    public async Task<IReadOnlyDictionary<long, TrackSourceLinksDto>> GetAlbumTrackSourceLinksAsync(long albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT t.id,
       COALESCE(MAX(CASE WHEN ts.source = 'deezer' THEN ts.source_id END), t.deezer_id) AS deezer_track_id,
       MAX(CASE WHEN ts.source = 'spotify' THEN ts.source_id END) AS spotify_track_id,
       MAX(CASE WHEN ts.source = 'apple' THEN ts.source_id END) AS apple_track_id,
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
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6));
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
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6));
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

    public async Task<int?> GetBestLocalQualityRankAsync(
        string artistName,
        string trackTitle,
        int? durationMs,
        string? artistPrimaryName = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = $@"
SELECT MAX(af.quality_rank)
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
WHERE (
        LOWER(ar.name) = LOWER(@artistName)
        OR (
            @artistPrimaryName IS NOT NULL
            AND @artistPrimaryName <> ''
            AND LOWER(ar.name) = LOWER(@artistPrimaryName)
        )
      )
  AND LOWER(t.title) = LOWER(@trackTitle)
  AND (@{DurationMsField} IS NULL OR t.duration_ms IS NULL OR ABS(t.duration_ms - @{DurationMsField}) <= 2000);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("artistPrimaryName", string.IsNullOrWhiteSpace(artistPrimaryName) ? (object)DBNull.Value : artistPrimaryName.Trim());
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        command.Parameters.AddWithValue(DurationMsField, (object?)durationMs ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull or null ? null : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<QualityScanTrackDto>> GetQualityScanTracksAsync(
        string scope,
        long? folderId,
        string? minFormat = null,
        int? minBitDepth = null,
        int? minSampleRateHz = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var normalizedMinFormatRank = NormalizeQualityScanFormatRank(minFormat);
        var normalizedMinBitDepth = NormalizePositiveInt(minBitDepth);
        var normalizedMinSampleRateHz = NormalizePositiveInt(minSampleRateHz);
        const string sql = @"
WITH track_rows AS (
    SELECT t.id AS track_id,
           t.title AS track_title,
           t.tag_isrc AS isrc,
           t.duration_ms AS duration_ms,
           ar.name AS artist_name,
           al.title AS album_title,
           af.id AS audio_file_id,
           af.quality_rank AS quality_rank,
           af.codec AS codec,
           af.extension AS extension,
           af.bitrate_kbps AS bitrate_kbps,
           af.bits_per_sample AS bits_per_sample,
           af.sample_rate_hz AS sample_rate_hz,
           af.size AS file_size,
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
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('hi_res_lossless', '27', '7')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi_res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%hi-res%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%24 bit%' THEN 4
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('alac', 'flac', 'lossless', '9', '6')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%lossless%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%flac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%alac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16-bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%16 bit%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%cd%' THEN 3
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) IN ('aac', '3')
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%aac%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%320%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%vorbis%'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%opus%' THEN 2
               WHEN LOWER(COALESCE(f.desired_quality_value, '')) = '1'
                    OR LOWER(COALESCE(f.desired_quality_value, '')) LIKE '%128%' THEN 1
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
       br.sample_rate_hz
FROM best_track_rows br
WHERE br.row_num = 1
  AND (@minFormatRank IS NULL OR COALESCE(br.format_rank, 0) = 0 OR COALESCE(br.format_rank, 0) < @minFormatRank)
  AND (@minBitDepth IS NULL OR COALESCE(br.bits_per_sample, 0) = 0 OR COALESCE(br.bits_per_sample, 0) < @minBitDepth)
  AND (@minSampleRateHz IS NULL OR COALESCE(br.sample_rate_hz, 0) = 0 OR COALESCE(br.sample_rate_hz, 0) < @minSampleRateHz)
ORDER BY br.artist_name, br.track_title;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("scope", scope ?? string.Empty);
        command.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("minFormatRank", (object?)normalizedMinFormatRank ?? DBNull.Value);
        command.Parameters.AddWithValue("minBitDepth", (object?)normalizedMinBitDepth ?? DBNull.Value);
        command.Parameters.AddWithValue("minSampleRateHz", (object?)normalizedMinSampleRateHz ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<QualityScanTrackDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(await ReadQualityScanTrackDtoAsync(reader, cancellationToken));
        }

        return results;
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
            bestSampleRateHz);
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
SELECT EXISTS(
    SELECT 1
    FROM track_source te
    WHERE te.source = @source
      AND te.source_id = @sourceId
);";
        const string variantSql = @"
SELECT EXISTS(
    SELECT 1
    FROM track_source te
    JOIN track_local tl ON tl.track_id = te.track_id
    JOIN audio_file af ON af.id = tl.audio_file_id
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
      ) = @requireAtmos
);";
        await using var command = new SqliteCommand(requireAtmosVariant.HasValue ? variantSql : defaultSql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        if (requireAtmosVariant.HasValue)
        {
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.Value);
        }
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
    }

    public sealed record LibraryExistenceInput(string? Isrc, string? TrackTitle, string? ArtistName, int? DurationMs);

    public async Task<IReadOnlyList<bool>> ExistsInLibraryAsync(
        IReadOnlyList<LibraryExistenceInput> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            return Array.Empty<bool>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string isrcSql = @"
SELECT EXISTS(
    SELECT 1
    FROM track t
    JOIN track_local tl ON tl.track_id = t.id
    LEFT JOIN track_source ts ON ts.track_id = t.id AND ts.source = 'isrc'
    WHERE (LOWER(t.tag_isrc) = LOWER(@isrc) OR LOWER(ts.source_id) = LOWER(@isrc))
);";
        const string trackSql = $@"
SELECT ar.name,
       t.title,
       t.duration_ms
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
WHERE LOWER(ar.name) LIKE LOWER(@artistSearch)
  AND (@{DurationMsField} IS NULL OR t.duration_ms IS NULL OR ABS(t.duration_ms - @{DurationMsField}) <= 2000)
LIMIT 100;";

        await using var isrcCommand = new SqliteCommand(isrcSql, connection);
        isrcCommand.Parameters.AddWithValue("isrc", string.Empty);
        await using var trackCommand = new SqliteCommand(trackSql, connection);
        trackCommand.Parameters.AddWithValue("artistSearch", string.Empty);
        trackCommand.Parameters.AddWithValue(DurationMsField, DBNull.Value);

        var results = new bool[inputs.Count];

        for (var i = 0; i < inputs.Count; i++)
        {
            results[i] = await ExistsInLibraryAsync(
                inputs[i],
                isrcCommand,
                trackCommand,
                cancellationToken);
        }

        return results;
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

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string isrcSql = @"
SELECT EXISTS(
    SELECT 1
    FROM track t
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
    JOIN folder f ON f.id = af.folder_id
    LEFT JOIN track_source ts ON ts.track_id = t.id AND ts.source = 'isrc'
    WHERE f.library_id = @libraryId
      AND (@folderId IS NULL OR f.id = @folderId)
      AND (LOWER(t.tag_isrc) = LOWER(@isrc) OR LOWER(ts.source_id) = LOWER(@isrc))
);";
        const string trackSql = $@"
SELECT ar.name,
       t.title,
       t.duration_ms
FROM track t
JOIN album al ON al.id = t.album_id
JOIN artist ar ON ar.id = al.artist_id
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE f.library_id = @libraryId
  AND (@folderId IS NULL OR f.id = @folderId)
  AND LOWER(ar.name) LIKE LOWER(@artistSearch)
  AND (@{DurationMsField} IS NULL OR t.duration_ms IS NULL OR ABS(t.duration_ms - @{DurationMsField}) <= 2000)
LIMIT 100;";

        await using var isrcCommand = new SqliteCommand(isrcSql, connection);
        isrcCommand.Parameters.AddWithValue("isrc", string.Empty);
        isrcCommand.Parameters.AddWithValue(LibraryIdField, libraryId);
        isrcCommand.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);

        await using var trackCommand = new SqliteCommand(trackSql, connection);
        trackCommand.Parameters.AddWithValue("artistSearch", string.Empty);
        trackCommand.Parameters.AddWithValue(DurationMsField, DBNull.Value);
        trackCommand.Parameters.AddWithValue(LibraryIdField, libraryId);
        trackCommand.Parameters.AddWithValue("folderId", (object?)folderId ?? DBNull.Value);

        var results = new bool[inputs.Count];

        for (var i = 0; i < inputs.Count; i++)
        {
            results[i] = await ExistsInLibraryAsync(
                inputs[i],
                isrcCommand,
                trackCommand,
                cancellationToken);
        }

        return results;
    }

    private static async Task<bool> ExistsInLibraryAsync(
        LibraryExistenceInput input,
        SqliteCommand isrcCommand,
        SqliteCommand trackCommand,
        CancellationToken cancellationToken)
    {
        if (await ExistsByIsrcAsync(isrcCommand, input.Isrc, cancellationToken))
        {
            return true;
        }

        if (!TryBuildTrackLookup(input, out var trackTitle, out var artistName, out var artistSearch))
        {
            return false;
        }

        trackCommand.Parameters["artistSearch"]!.Value = artistSearch;
        trackCommand.Parameters[DurationMsField]!.Value = input.DurationMs.HasValue ? input.DurationMs.Value : DBNull.Value;
        return await ExistsByArtistAndTitleAsync(trackCommand, artistName, trackTitle, cancellationToken);
    }

    private static async Task<bool> ExistsByIsrcAsync(
        SqliteCommand isrcCommand,
        string? isrc,
        CancellationToken cancellationToken)
    {
        var normalizedIsrc = isrc?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedIsrc))
        {
            return false;
        }

        isrcCommand.Parameters["isrc"]!.Value = normalizedIsrc;
        var result = await isrcCommand.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
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

    private static async Task<bool> ExistsByArtistAndTitleAsync(
        SqliteCommand trackCommand,
        string artistName,
        string trackTitle,
        CancellationToken cancellationToken)
    {
        await using var reader = await trackCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidateArtist = await ReadNullableStringAsync(reader, 0, cancellationToken) ?? string.Empty;
            var candidateTitle = await ReadNullableStringAsync(reader, 1, cancellationToken) ?? string.Empty;
            if (!TrackTitleMatcher.ArtistsMatch(artistName, candidateArtist))
            {
                continue;
            }

            if (TrackTitleMatcher.TitlesMatch(trackTitle, candidateTitle))
            {
                return true;
            }
        }

        return false;
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
SELECT EXISTS(
    SELECT 1
    FROM track_source te
    JOIN track_local tl ON tl.track_id = te.track_id
    JOIN audio_file af ON af.id = tl.audio_file_id
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
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue(SourceIdField, sourceId);
        command.Parameters.AddWithValue("folderId", folderId);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
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
SELECT EXISTS(
    SELECT 1
    FROM album_source als
    JOIN album al ON al.id = als.album_id
    JOIN track t ON t.album_id = al.id
    JOIN track_local tl ON tl.track_id = t.id
    JOIN audio_file af ON af.id = tl.audio_file_id
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
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(SourceField, source);
        command.Parameters.AddWithValue("albumSourceId", albumSourceId);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        command.Parameters.AddWithValue("artistSourceId", string.IsNullOrWhiteSpace(artistSourceId) ? (object)DBNull.Value : artistSourceId);
        command.Parameters.AddWithValue("folderId", folderId.HasValue ? folderId.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue(RequireAtmosField, requireAtmosVariant.HasValue ? requireAtmosVariant.Value : (object)DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
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
            foreach (var folderName in album.LocalFolders)
            {
                if (folderByDisplay.TryGetValue(folderName, out var folder))
                {
                    await EnsureAlbumLocalAsync(connection, transaction, albumId, folder.Id, cancellationToken);
                }
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
            await ReplaceTrackMultiTagsAsync(connection, transaction, trackId, track, cancellationToken);
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
                track.AudioVariant),
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
        const string sql = "INSERT INTO quality_scan_automation_settings (id) VALUES (1) ON CONFLICT DO NOTHING;";
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
        string? AudioVariant);

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

    private static async Task CleanupOrphansAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
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
            command.Parameters.AddWithValue("folderId", folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearPlaylistWatchPreferencesSql = @"
UPDATE playlist_watch_preferences
SET destination_folder_id = NULL,
    updated_at = CURRENT_TIMESTAMP
WHERE destination_folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearPlaylistWatchPreferencesSql, connection, transaction))
        {
            command.Parameters.AddWithValue("folderId", folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearQualityScanAutomationSql = @"
UPDATE quality_scan_automation_settings
SET folder_id = NULL,
    updated_at = CURRENT_TIMESTAMP
WHERE folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearQualityScanAutomationSql, connection, transaction))
        {
            command.Parameters.AddWithValue("folderId", folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearQualityScanRunSql = @"
UPDATE quality_scan_run
SET folder_id = NULL,
    updated_at = CURRENT_TIMESTAMP
WHERE folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearQualityScanRunSql, connection, transaction))
        {
            command.Parameters.AddWithValue("folderId", folderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string clearQualityScanActionSql = @"
UPDATE quality_scan_action_log
SET destination_folder_id = NULL
WHERE destination_folder_id = @folderId;";
        await using (var command = new SqliteCommand(clearQualityScanActionSql, connection, transaction))
        {
            command.Parameters.AddWithValue("folderId", folderId);
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
                catch
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
    updated_at = CURRENT_TIMESTAMP
        RETURNING id;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("path", input.FilePath);
        command.Parameters.AddWithValue("relativePath", input.RelativePath);
        command.Parameters.AddWithValue("folderId", input.FolderId);
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
        command.Parameters.AddWithValue("folderId", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static int NormalizeDesiredQualityRank(string? desiredQuality)
    {
        var normalized = (desiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return 3;
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
            "video" => 0,
            "podcast" => 0,
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
            "m4a" or "m4a-aac" => "aac",
            "m4a-alac" => "alac",
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
        return !string.Equals(normalized, "video", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "podcast", StringComparison.OrdinalIgnoreCase);
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

    private sealed record FolderRoot(long Id, string Root, string RootPath);
}
