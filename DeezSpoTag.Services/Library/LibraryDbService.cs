using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Collections.Generic;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Library;
public sealed class LibraryDbService
{
    private const string ArtistTable = "artist";
    private const string AlbumTable = "album";
    private const string TrackTable = "track";
    private const string AudioFileTable = "audio_file";
    private const string DownloadTaskTable = "download_task";
    private const string FolderTable = "folder";
    private const string PlaylistWatchStateTable = "playlist_watch_state";
    private const string PlaylistWatchPreferencesTable = "playlist_watch_preferences";
    private const string PlaylistWatchTrackTable = "playlist_watch_track";
    private const string PlaylistWatchlistTable = "playlist_watchlist";
    private const string PlaylistWatchIgnoreTable = "playlist_watch_ignore";
    private const string WatchlistHistoryTable = "watchlist_history";
    private const string TrackAnalysisTable = "track_analysis";
    private const string LibraryTable = "library";
    private const string TextType = "TEXT";
    private const string IntegerType = "INTEGER";
    private const string RealType = "REAL";
    private const string SourceIdColumn = "source_id";
    private const string ExternalIdColumn = "external_id";
    private readonly IConfiguration _configuration;
    private readonly ILogger<LibraryDbService> _logger;

    public LibraryDbService(IConfiguration configuration, ILogger<LibraryDbService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? _configuration.GetConnectionString("Library");
        var connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Library DB connection string not configured; skipping schema setup.");
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        var schemaPath = Path.Join(baseDir, "Schema", "library.sql");
        if (!File.Exists(schemaPath))
        {
            schemaPath = Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "DeezSpoTag.Services", "Library", "Schema", "library.sql");
            schemaPath = Path.GetFullPath(schemaPath);
        }

        if (!File.Exists(schemaPath))
        {
            _logger.LogWarning("Library schema file not found; skipping schema setup.");
            return;
        }

        var schemaSql = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqliteCommand(schemaSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ApplyMigrationsAsync(connection, cancellationToken);
        _logger.LogInformation("Library DB schema ensured.");
    }

    private static async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, ArtistTable, "deezer_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "metadata_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "preferred_background_path", TextType, cancellationToken);

        await EnsureColumnAsync(connection, AlbumTable, "deezer_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, AlbumTable, "metadata_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, AlbumTable, "has_animated_artwork", $"{IntegerType} DEFAULT 0", cancellationToken);

        await EnsureColumnAsync(connection, TrackTable, "deezer_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "lyrics_type", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_title", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_artist", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_album", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_album_artist", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_version", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_label", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_catalog_number", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_bpm", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_key", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_track_total", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_duration_ms", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_year", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_track_no", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_disc", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_genre", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_isrc", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_release_date", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_publish_date", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_url", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_release_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_track_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "tag_meta_tagged_date", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "lyrics_unsynced", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "lyrics_synced", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackTable, "metadata_json", TextType, cancellationToken);

        await EnsureColumnAsync(connection, AudioFileTable, "extension", TextType, cancellationToken);
        await EnsureColumnAsync(connection, AudioFileTable, "relative_path", TextType, cancellationToken);
        await EnsureColumnAsync(connection, AudioFileTable, "audio_variant", TextType, cancellationToken);
        await EnsureIndexAsync(connection, "idx_audio_file_folder_relative", AudioFileTable, "folder_id, relative_path", unique: true, cancellationToken);
        await BackfillAudioFileRelativePathsAsync(connection, cancellationToken);
        await BackfillAudioFileVariantsAsync(connection, cancellationToken);

        await EnsureColumnAsync(connection, DownloadTaskTable, "lyrics_status", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "file_extension", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "bitrate_kbps", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "content_type", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "isrc", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "deezer_track_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "deezer_album_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "deezer_artist_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "spotify_track_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "spotify_album_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "spotify_artist_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "apple_track_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "apple_album_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "apple_artist_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "destination_folder_id", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "final_destinations_json", TextType, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_isrc", DownloadTaskTable, "isrc", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_deezer_track", DownloadTaskTable, "deezer_track_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_deezer_album", DownloadTaskTable, "deezer_album_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_deezer_artist", DownloadTaskTable, "deezer_artist_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_spotify_track", DownloadTaskTable, "spotify_track_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_spotify_album", DownloadTaskTable, "spotify_album_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_spotify_artist", DownloadTaskTable, "spotify_artist_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_apple_track", DownloadTaskTable, "apple_track_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_apple_album", DownloadTaskTable, "apple_album_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_apple_artist", DownloadTaskTable, "apple_artist_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_destination_folder", DownloadTaskTable, "destination_folder_id", unique: false, cancellationToken);

        await EnsureColumnAsync(connection, FolderTable, "library_id", "BIGINT", cancellationToken);
        await EnsureIndexAsync(connection, "idx_folder_library_id", FolderTable, "library_id", unique: false, cancellationToken);
        await EnsureColumnAsync(connection, FolderTable, "auto_tag_profile_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, FolderTable, "auto_tag_enabled", $"{IntegerType} DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, FolderTable, "desired_quality", $"{IntegerType} DEFAULT 27", cancellationToken);
        await EnsureColumnAsync(connection, FolderTable, "desired_quality_value", TextType, cancellationToken);
        await EnsureColumnAsync(connection, FolderTable, "convert_enabled", $"{IntegerType} DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, FolderTable, "convert_format", TextType, cancellationToken);
        await EnsureColumnAsync(connection, FolderTable, "convert_bitrate", TextType, cancellationToken);
        await BackfillFolderLibraryLinksAsync(connection, cancellationToken);

        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "batch_next_offset", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "batch_processing_snapshot_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchlistTable, "description", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "autotag_profile", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "preferred_engine", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "download_variant_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "update_artwork", $"{IntegerType} DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "reuse_saved_artwork", $"{IntegerType} DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "routing_rules_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "ignore_rules_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "status", $"{TextType} DEFAULT 'queued'", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchlistTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchIgnoreTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, WatchlistHistoryTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS playlist_track_candidate_cache (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    snapshot_id TEXT,
    candidates_json TEXT NOT NULL,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id)
);", cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchlistTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchPreferencesTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchStateTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchTrackTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchIgnoreTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, WatchlistHistoryTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS download_blocklist (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    field TEXT NOT NULL,
    value TEXT NOT NULL,
    normalized_value TEXT NOT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (field, normalized_value)
);", cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_blocklist_field", "download_blocklist", "field, is_enabled", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_blocklist_normalized", "download_blocklist", "normalized_value, is_enabled", unique: false, cancellationToken);

        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS track_shazam_cache (
    track_id BIGINT PRIMARY KEY REFERENCES track(id) ON DELETE CASCADE,
    shazam_track_id TEXT,
    title TEXT,
    artist TEXT,
    isrc TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    related_tracks_json TEXT,
    scanned_at_utc TEXT,
    error TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);", cancellationToken);
        await EnsureIndexAsync(connection, "idx_track_shazam_cache_status", "track_shazam_cache", "status", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_track_shazam_cache_scanned", "track_shazam_cache", "scanned_at_utc", unique: false, cancellationToken);

        await MigrateSourceMappingTablesAsync(connection, cancellationToken);

        await EnsureColumnAsync(connection, TrackAnalysisTable, "analysis_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "analysis_version", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_tags", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_happy", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_sad", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_relaxed", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_aggressive", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_party", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_acoustic", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "mood_electronic", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "valence", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "arousal", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "beats_count", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "key", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "key_scale", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "key_strength", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "loudness", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "dynamic_range", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "danceability", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "instrumentalness", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "acousticness", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "speechiness", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "danceability_ml", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "essentia_genres", TextType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "lastfm_tags", TextType, cancellationToken);

        // Vibe analysis - new Essentia model fields
        await EnsureColumnAsync(connection, TrackAnalysisTable, "approachability", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "engagement", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "voice_instrumental", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "tonal_atonal", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "valence_ml", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "arousal_ml", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "dynamic_complexity", RealType, cancellationToken);
        await EnsureColumnAsync(connection, TrackAnalysisTable, "loudness_ml", RealType, cancellationToken);

        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS track_plex_metadata (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    plex_rating_key TEXT,
    user_rating INTEGER,
    genres_json TEXT,
    moods_json TEXT,
    updated_at_utc TEXT,
    PRIMARY KEY (track_id)
);", cancellationToken);

        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS track_genre (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);", cancellationToken);

        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS track_style (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);", cancellationToken);

        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS track_mood (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);", cancellationToken);

        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS track_remixer (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);", cancellationToken);

        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS track_other_tag (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    tag_key TEXT NOT NULL,
    tag_value TEXT NOT NULL,
    PRIMARY KEY (track_id, tag_key, tag_value)
);", cancellationToken);

    }

    private static async Task EnsureTableAsync(
        SqliteConnection connection,
        string createSql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(createSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureIndexAsync(
        SqliteConnection connection,
        string indexName,
        string table,
        string column,
        bool unique,
        CancellationToken cancellationToken)
    {
        var prefix = unique ? "CREATE UNIQUE INDEX" : "CREATE INDEX";
        var sql = $"{prefix} IF NOT EXISTS {indexName} ON {table} ({column});";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string type,
        CancellationToken cancellationToken)
        => await SqliteSchemaUtils.EnsureColumnAsync(connection, table, column, type, cancellationToken);

    private static async Task BackfillColumnFromLegacyAsync(
        SqliteConnection connection,
        string table,
        string column,
        string legacyColumn,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(connection, table, column, cancellationToken)
            || !await ColumnExistsAsync(connection, table, legacyColumn, cancellationToken))
        {
            return;
        }

        var sql = $@"
UPDATE {table}
SET {column} = {legacyColumn}
WHERE ({column} IS NULL OR {column} = '')
  AND {legacyColumn} IS NOT NULL
  AND {legacyColumn} <> '';";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BackfillFolderLibraryLinksAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, FolderTable, cancellationToken)
            || !await TableExistsAsync(connection, LibraryTable, cancellationToken)
            || !await ColumnExistsAsync(connection, FolderTable, "library_id", cancellationToken))
        {
            return;
        }

        const string insertLibrariesSql = @"
INSERT INTO library (name)
SELECT DISTINCT COALESCE(NULLIF(TRIM(display_name), ''), 'Library')
FROM folder
WHERE library_id IS NULL
ON CONFLICT(name) DO NOTHING;";
        await using (var insertLibraries = new SqliteCommand(insertLibrariesSql, connection))
        {
            await insertLibraries.ExecuteNonQueryAsync(cancellationToken);
        }

        const string assignLibrariesSql = @"
UPDATE folder
SET library_id = (
    SELECT l.id
    FROM library l
    WHERE l.name = COALESCE(NULLIF(TRIM(folder.display_name), ''), 'Library')
    LIMIT 1
)
WHERE library_id IS NULL;";
        await using var assignLibraries = new SqliteCommand(assignLibrariesSql, connection);
        await assignLibraries.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("$name", table);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    private static async Task MigrateSourceMappingTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await CopySourceMappingAsync(connection, "artist_external", "artist_source", "artist_id", cancellationToken);
        await CopySourceMappingAsync(connection, "album_external", "album_source", "album_id", cancellationToken);
        await CopySourceMappingAsync(connection, "track_external", "track_source", "track_id", cancellationToken);
    }

    private static async Task CopySourceMappingAsync(
        SqliteConnection connection,
        string legacyTable,
        string newTable,
        string idColumn,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, legacyTable, cancellationToken)
            || !await TableExistsAsync(connection, newTable, cancellationToken))
        {
            return;
        }

        var legacyHasSourceId = await ColumnExistsAsync(connection, legacyTable, SourceIdColumn, cancellationToken);
        var legacyIdColumn = legacyHasSourceId ? SourceIdColumn : ExternalIdColumn;
        var sql = $@"
INSERT OR IGNORE INTO {newTable} ({idColumn}, source, source_id)
SELECT {idColumn}, source, {legacyIdColumn}
FROM {legacyTable}
WHERE {legacyIdColumn} IS NOT NULL AND {legacyIdColumn} <> '';";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
        => await SqliteSchemaUtils.ColumnExistsAsync(connection, table, column, cancellationToken);

    private static async Task BackfillAudioFileRelativePathsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string selectSql = @"
SELECT af.id,
       af.path,
       f.root_path
FROM audio_file af
JOIN folder f ON f.id = af.folder_id
WHERE af.relative_path IS NULL OR af.relative_path = '';";

        await using var select = new SqliteCommand(selectSql, connection);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var updates = new List<(long Id, string RelativePath)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var fullPath = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
            var root = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var relative = TryComputeRelativePath(root, fullPath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            updates.Add((id, relative));
        }

        await reader.DisposeAsync();
        if (updates.Count == 0)
        {
            return;
        }

        const string updateSql = "UPDATE audio_file SET relative_path = @relative WHERE id = @id;";
        await using var update = new SqliteCommand(updateSql, connection);
        var idParam = update.CreateParameter();
        idParam.ParameterName = "id";
        update.Parameters.Add(idParam);
        var relParam = update.CreateParameter();
        relParam.ParameterName = "relative";
        update.Parameters.Add(relParam);

        foreach (var row in updates)
        {
            idParam.Value = row.Id;
            relParam.Value = row.RelativePath;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task BackfillAudioFileVariantsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE audio_file
SET audio_variant = CASE
    WHEN (
        LOWER(COALESCE(codec, '')) LIKE '%dolby atmos%'
        OR LOWER(COALESCE(codec, '')) LIKE '%joc%'
        OR LOWER(COALESCE(codec, '')) LIKE '%atmos%'
    ) THEN 'atmos'
    WHEN (
        (
            LOWER(COALESCE(codec, '')) LIKE '%ec-3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%eac3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%ac-3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%ac3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%truehd%'
            OR LOWER(COALESCE(codec, '')) LIKE '%mlp%'
            OR LOWER(COALESCE(extension, '')) IN ('.ec3', '.ac3', '.mlp')
        )
        AND channels IS NOT NULL
        AND channels > 2
    ) THEN 'atmos'
    WHEN (
        (
            LOWER(REPLACE(COALESCE(path, ''), '\', '/')) LIKE '%/atmos/%'
            OR LOWER(REPLACE(COALESCE(path, ''), '\', '/')) LIKE '%/dolby atmos/%'
            OR LOWER(REPLACE(COALESCE(path, ''), '\', '/')) LIKE '%/spatial/%'
            OR LOWER(COALESCE(path, '')) LIKE '%atmos%'
        )
        AND (
            (channels IS NOT NULL AND channels > 2)
            OR LOWER(COALESCE(codec, '')) LIKE '%ec-3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%eac3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%ac-3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%ac3%'
            OR LOWER(COALESCE(codec, '')) LIKE '%truehd%'
            OR LOWER(COALESCE(codec, '')) LIKE '%mlp%'
            OR LOWER(COALESCE(extension, '')) IN ('.ec3', '.ac3', '.mlp')
        )
    ) THEN 'atmos'
    ELSE 'stereo'
END
WHERE audio_variant IS NULL OR TRIM(audio_variant) = '';";

        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? TryComputeRelativePath(string rootPath, string fullPath)
    {
        try
        {
            var rootFull = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileFull = Path.GetFullPath(fullPath);
            if (!fileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var relative = Path.GetRelativePath(rootFull, fileFull);
            if (relative.StartsWith(".."))
            {
                return null;
            }

            return relative.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

}
