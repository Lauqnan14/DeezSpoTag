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
    private const string TrackLocalTable = "track_local";
    private const string AudioFileTable = "audio_file";
    private const string DownloadTaskTable = "download_task";
    private const string FolderTable = "folder";
    private const string PlaylistWatchStateTable = "playlist_watch_state";
    private const string PlaylistWatchPreferencesTable = "playlist_watch_preferences";
    private const string PlaylistWatchTrackTable = "playlist_watch_track";
    private const string PlaylistWatchlistTable = "playlist_watchlist";
    private const string PlaylistWatchIgnoreTable = "playlist_watch_ignore";
    private const string WatchlistHistoryTable = "watchlist_history";
    private const string ArtistWatchlistTable = "artist_watchlist";
    private const string TrackAnalysisTable = "track_analysis";
    private const string LibraryTable = "library";
    private const string DownloadBlocklistTable = "download_blocklist";
    private const string TrackShazamCacheTable = "track_shazam_cache";
    private const string TextType = "TEXT";
    private const string IntegerType = "INTEGER";
    private const string BigIntType = "BIGINT";
    private const string LibraryIdColumn = "library_id";
    private const string RealType = "REAL";
    private const string SourceIdColumn = "source_id";
    private const string ExternalIdColumn = "external_id";
    private const string ArtistIdColumn = "artist_id";
    private const string AlbumIdColumn = "album_id";
    private const string DeezerIdColumn = "deezer_id";
    private const string UpdatedAtColumn = "updated_at";
    private static readonly Dictionary<string, (string Table, string Column, bool Unique)> KnownIndexDefinitions =
        new Dictionary<string, (string Table, string Column, bool Unique)>(StringComparer.Ordinal)
        {
            ["idx_audio_file_folder_relative"] = (AudioFileTable, "folder_id, relative_path", true),
            ["idx_download_task_isrc"] = (DownloadTaskTable, "isrc", false),
            ["idx_download_task_deezer_track"] = (DownloadTaskTable, "deezer_track_id", false),
            ["idx_download_task_deezer_album"] = (DownloadTaskTable, "deezer_album_id", false),
            ["idx_download_task_deezer_artist"] = (DownloadTaskTable, "deezer_artist_id", false),
            ["idx_download_task_spotify_track"] = (DownloadTaskTable, "spotify_track_id", false),
            ["idx_download_task_spotify_album"] = (DownloadTaskTable, "spotify_album_id", false),
            ["idx_download_task_spotify_artist"] = (DownloadTaskTable, "spotify_artist_id", false),
            ["idx_download_task_apple_track"] = (DownloadTaskTable, "apple_track_id", false),
            ["idx_download_task_apple_album"] = (DownloadTaskTable, "apple_album_id", false),
            ["idx_download_task_apple_artist"] = (DownloadTaskTable, "apple_artist_id", false),
            ["idx_download_task_destination_folder"] = (DownloadTaskTable, "destination_folder_id", false),
            ["idx_folder_library_id"] = (FolderTable, LibraryIdColumn, false),
            ["idx_download_blocklist_field"] = (DownloadBlocklistTable, "field, is_enabled", false),
            ["idx_download_blocklist_normalized"] = (DownloadBlocklistTable, "normalized_value, is_enabled", false),
            ["idx_track_shazam_cache_status"] = (TrackShazamCacheTable, "status", false),
            ["idx_track_shazam_cache_scanned"] = (TrackShazamCacheTable, "scanned_at_utc", false),
            ["idx_album_artist_id"] = (AlbumTable, ArtistIdColumn, false),
            ["idx_track_album_id"] = (TrackTable, AlbumIdColumn, false),
            ["idx_track_local_audio_file_id"] = (TrackLocalTable, "audio_file_id", false),
            ["idx_artist_name_nocase"] = (ArtistTable, "name COLLATE NOCASE", false)
            ,["idx_artist_watchlist_spotify_id"] = (ArtistWatchlistTable, "spotify_id", false)
            ,["idx_artist_watchlist_deezer_id"] = (ArtistWatchlistTable, DeezerIdColumn, false)
            ,["idx_playlist_watchlist_created"] = (PlaylistWatchlistTable, "created_at", false)
            ,["idx_playlist_watch_preferences_updated"] = (PlaylistWatchPreferencesTable, UpdatedAtColumn, false)
            ,["idx_playlist_watch_state_updated"] = (PlaylistWatchStateTable, UpdatedAtColumn, false)
            ,["idx_playlist_watch_track_source_status"] = (PlaylistWatchTrackTable, "source, source_id, status", false)
            ,["idx_watchlist_history_source_created"] = (WatchlistHistoryTable, "source, created_at", false)
        };
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
        await EnsureColumnAsync(connection, ArtistTable, DeezerIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "metadata_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "preferred_background_path", TextType, cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_name_nocase", ArtistTable, "name COLLATE NOCASE", unique: false, cancellationToken);

        await EnsureColumnAsync(connection, AlbumTable, DeezerIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, AlbumTable, "metadata_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, AlbumTable, "has_animated_artwork", $"{IntegerType} DEFAULT 0", cancellationToken);
        await EnsureIndexAsync(connection, "idx_album_artist_id", AlbumTable, ArtistIdColumn, unique: false, cancellationToken);

        await EnsureColumnAsync(connection, TrackTable, DeezerIdColumn, TextType, cancellationToken);
        await EnsureColumnsAsync(
            connection,
            TrackTable,
            cancellationToken,
            ("lyrics_type", TextType),
            ("tag_title", TextType),
            ("tag_artist", TextType),
            ("tag_album", TextType),
            ("tag_album_artist", TextType),
            ("tag_version", TextType),
            ("tag_label", TextType),
            ("tag_catalog_number", TextType),
            ("tag_bpm", IntegerType),
            ("tag_key", TextType),
            ("tag_track_total", IntegerType),
            ("tag_duration_ms", IntegerType),
            ("tag_year", IntegerType),
            ("tag_track_no", IntegerType),
            ("tag_disc", IntegerType),
            ("tag_genre", TextType),
            ("tag_isrc", TextType),
            ("tag_release_date", TextType),
            ("tag_publish_date", TextType),
            ("tag_url", TextType),
            ("tag_release_id", TextType),
            ("tag_track_id", TextType),
            ("tag_meta_tagged_date", TextType),
            ("lyrics_unsynced", TextType),
            ("lyrics_synced", TextType),
            ("metadata_json", TextType));
        await EnsureIndexAsync(connection, "idx_track_album_id", TrackTable, AlbumIdColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_track_local_audio_file_id", TrackLocalTable, "audio_file_id", unique: false, cancellationToken);

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

        foreach (var source in new[] { "deezer", "spotify", "apple" })
        {
            await EnsureColumnAsync(connection, DownloadTaskTable, $"{source}_track_id", TextType, cancellationToken);
            await EnsureColumnAsync(connection, DownloadTaskTable, $"{source}_album_id", TextType, cancellationToken);
            await EnsureColumnAsync(connection, DownloadTaskTable, $"{source}_artist_id", TextType, cancellationToken);
        }

        await EnsureColumnAsync(connection, DownloadTaskTable, "destination_folder_id", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "final_destinations_json", TextType, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_isrc", DownloadTaskTable, "isrc", unique: false, cancellationToken);

        foreach (var source in new[] { "deezer", "spotify", "apple" })
        {
            await EnsureIndexAsync(connection, $"idx_download_task_{source}_track", DownloadTaskTable, $"{source}_track_id", unique: false, cancellationToken);
            await EnsureIndexAsync(connection, $"idx_download_task_{source}_album", DownloadTaskTable, $"{source}_album_id", unique: false, cancellationToken);
            await EnsureIndexAsync(connection, $"idx_download_task_{source}_artist", DownloadTaskTable, $"{source}_artist_id", unique: false, cancellationToken);
        }

        await EnsureIndexAsync(connection, "idx_download_task_destination_folder", DownloadTaskTable, "destination_folder_id", unique: false, cancellationToken);

        await EnsureColumnAsync(connection, FolderTable, LibraryIdColumn, BigIntType, cancellationToken);
        await EnsureIndexAsync(connection, "idx_folder_library_id", FolderTable, LibraryIdColumn, unique: false, cancellationToken);
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
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "preferred_engine", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "download_variant_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "sync_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "atmos_destination_folder_id", BigIntType, cancellationToken);
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
        await NormalizeWatchlistKeysAsync(connection, cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_watchlist_spotify_id", ArtistWatchlistTable, "spotify_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_watchlist_deezer_id", ArtistWatchlistTable, DeezerIdColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watchlist_created", PlaylistWatchlistTable, "created_at", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_preferences_updated", PlaylistWatchPreferencesTable, UpdatedAtColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_state_updated", PlaylistWatchStateTable, UpdatedAtColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_track_source_status", PlaylistWatchTrackTable, "source, source_id, status", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_watchlist_history_source_created", WatchlistHistoryTable, "source, created_at", unique: false, cancellationToken);
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
        await EnsureIndexAsync(connection, "idx_download_blocklist_field", DownloadBlocklistTable, "field, is_enabled", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_blocklist_normalized", DownloadBlocklistTable, "normalized_value, is_enabled", unique: false, cancellationToken);

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
        await EnsureIndexAsync(connection, "idx_track_shazam_cache_status", TrackShazamCacheTable, "status", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_track_shazam_cache_scanned", TrackShazamCacheTable, "scanned_at_utc", unique: false, cancellationToken);

        await MigrateSourceMappingTablesAsync(connection, cancellationToken);

        await EnsureColumnsAsync(
            connection,
            TrackAnalysisTable,
            cancellationToken,
            ("analysis_mode", TextType),
            ("analysis_version", TextType),
            ("mood_tags", TextType),
            ("mood_happy", RealType),
            ("mood_sad", RealType),
            ("mood_relaxed", RealType),
            ("mood_aggressive", RealType),
            ("mood_party", RealType),
            ("mood_acoustic", RealType),
            ("mood_electronic", RealType),
            ("valence", RealType),
            ("arousal", RealType),
            ("beats_count", IntegerType),
            ("key", TextType),
            ("key_scale", TextType),
            ("key_strength", RealType),
            ("loudness", RealType),
            ("dynamic_range", RealType),
            ("danceability", RealType),
            ("instrumentalness", RealType),
            ("acousticness", RealType),
            ("speechiness", RealType),
            ("danceability_ml", RealType),
            ("essentia_genres", TextType),
            ("lastfm_tags", TextType),
            // Vibe analysis - new Essentia model fields
            ("approachability", RealType),
            ("engagement", RealType),
            ("voice_instrumental", RealType),
            ("tonal_atonal", RealType),
            ("valence_ml", RealType),
            ("arousal_ml", RealType),
            ("dynamic_complexity", RealType),
            ("loudness_ml", RealType));

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

        await EnsureTrackValueTableAsync(connection, "track_genre", cancellationToken);
        await EnsureTrackValueTableAsync(connection, "track_style", cancellationToken);
        await EnsureTrackValueTableAsync(connection, "track_mood", cancellationToken);
        await EnsureTrackValueTableAsync(connection, "track_remixer", cancellationToken);

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

    private static Task EnsureTrackValueTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var createSql = $@"
CREATE TABLE IF NOT EXISTS {tableName} (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);";
        return EnsureTableAsync(connection, createSql, cancellationToken);
    }

    private static async Task EnsureIndexAsync(
        SqliteConnection connection,
        string indexName,
        string table,
        string column,
        bool unique,
        CancellationToken cancellationToken)
    {
        var sql = ResolveCreateIndexSql(indexName, table, column, unique);
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

    private static async Task EnsureColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken,
        params (string Column, string Type)[] columns)
    {
        foreach (var (column, type) in columns)
        {
            await EnsureColumnAsync(connection, table, column, type, cancellationToken);
        }
    }

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

        var sql = ResolveBackfillLegacySql(table, column, legacyColumn);
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BackfillFolderLibraryLinksAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, FolderTable, cancellationToken)
            || !await TableExistsAsync(connection, LibraryTable, cancellationToken)
            || !await ColumnExistsAsync(connection, FolderTable, LibraryIdColumn, cancellationToken))
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
        await CopySourceMappingAsync(connection, "artist_external", "artist_source", ArtistIdColumn, cancellationToken);
        await CopySourceMappingAsync(connection, "album_external", "album_source", AlbumIdColumn, cancellationToken);
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
        var sql = ResolveCopySourceMappingSql(legacyTable, newTable, idColumn, legacyHasSourceId);
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ResolveCreateIndexSql(string indexName, string table, string column, bool unique)
    {
        if (!KnownIndexDefinitions.TryGetValue(indexName, out var definition)
            || !string.Equals(definition.Table, table, StringComparison.Ordinal)
            || !string.Equals(definition.Column, column, StringComparison.Ordinal)
            || definition.Unique != unique)
        {
            throw new InvalidOperationException(
                $"Unsupported index migration: name='{indexName}' table='{table}' column='{column}' unique={unique}.");
        }

        var uniqueSql = unique ? "UNIQUE " : string.Empty;
        return $"CREATE {uniqueSql}INDEX IF NOT EXISTS {indexName} ON {table} ({column});";
    }

    private static string ResolveBackfillLegacySql(string table, string column, string legacyColumn)
    {
        if (!IsSupportedLegacyBackfillTable(table)
            || !string.Equals(column, SourceIdColumn, StringComparison.Ordinal)
            || !string.Equals(legacyColumn, ExternalIdColumn, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported legacy backfill migration: table='{table}', column='{column}', legacy='{legacyColumn}'.");
        }

        return BuildBackfillLegacySql(table, column, legacyColumn);
    }

    private static string ResolveCopySourceMappingSql(string legacyTable, string newTable, string idColumn, bool legacyHasSourceId)
    {
        if (!IsSupportedSourceMappingMigration(legacyTable, newTable, idColumn))
        {
            throw new InvalidOperationException(
                $"Unsupported source mapping migration: legacy='{legacyTable}', new='{newTable}', id='{idColumn}', hasSourceId={legacyHasSourceId}.");
        }

        var sourceValueColumn = legacyHasSourceId ? SourceIdColumn : ExternalIdColumn;
        return BuildCopySourceMappingSql(legacyTable, newTable, idColumn, sourceValueColumn);
    }

    private static bool IsSupportedLegacyBackfillTable(string table)
        => table is PlaylistWatchlistTable
            or PlaylistWatchPreferencesTable
            or PlaylistWatchStateTable
            or PlaylistWatchTrackTable
            or PlaylistWatchIgnoreTable
            or WatchlistHistoryTable;

    private static string BuildBackfillLegacySql(string table, string sourceColumn, string legacyColumn) => $@"
UPDATE {table}
SET {sourceColumn} = {legacyColumn}
WHERE ({sourceColumn} IS NULL OR {sourceColumn} = '')
  AND {legacyColumn} IS NOT NULL
  AND {legacyColumn} <> '';";

    private static bool IsSupportedSourceMappingMigration(string legacyTable, string newTable, string idColumn)
        => (legacyTable, newTable, idColumn) is
            ("artist_external", "artist_source", ArtistIdColumn)
            or ("album_external", "album_source", AlbumIdColumn)
            or ("track_external", "track_source", "track_id");

    private static string BuildCopySourceMappingSql(
        string legacyTable,
        string newTable,
        string idColumn,
        string sourceValueColumn) => $@"
INSERT OR IGNORE INTO {newTable} ({idColumn}, source, {SourceIdColumn})
SELECT {idColumn}, source, {sourceValueColumn}
FROM {legacyTable}
WHERE {sourceValueColumn} IS NOT NULL AND {sourceValueColumn} <> '';";

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

    private static async Task NormalizeWatchlistKeysAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Deduplicate by canonical source/source_id (or source/source_id/track_source_id) before normalization
        // to avoid unique/primary key conflicts during updates.
        await ExecuteIfTableExistsAsync(connection, PlaylistWatchlistTable, @"
DELETE FROM playlist_watchlist
WHERE id NOT IN (
    SELECT MAX(id)
    FROM playlist_watchlist
    GROUP BY LOWER(TRIM(source)), TRIM(source_id)
);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchPreferencesTable, @"
DELETE FROM playlist_watch_preferences
WHERE rowid NOT IN (
    SELECT MAX(rowid)
    FROM playlist_watch_preferences
    GROUP BY LOWER(TRIM(source)), TRIM(source_id)
);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchStateTable, @"
DELETE FROM playlist_watch_state
WHERE rowid NOT IN (
    SELECT MAX(rowid)
    FROM playlist_watch_state
    GROUP BY LOWER(TRIM(source)), TRIM(source_id)
);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, "playlist_track_candidate_cache", @"
DELETE FROM playlist_track_candidate_cache
WHERE rowid NOT IN (
    SELECT MAX(rowid)
    FROM playlist_track_candidate_cache
    GROUP BY LOWER(TRIM(source)), TRIM(source_id)
);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchTrackTable, @"
DELETE FROM playlist_watch_track
WHERE rowid NOT IN (
    SELECT MAX(rowid)
    FROM playlist_watch_track
    GROUP BY LOWER(TRIM(source)), TRIM(source_id), TRIM(track_source_id)
);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchIgnoreTable, @"
DELETE FROM playlist_watch_ignore
WHERE rowid NOT IN (
    SELECT MAX(rowid)
    FROM playlist_watch_ignore
    GROUP BY LOWER(TRIM(source)), TRIM(source_id), TRIM(track_source_id)
);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchlistTable, @"
UPDATE playlist_watchlist
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchPreferencesTable, @"
UPDATE playlist_watch_preferences
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchStateTable, @"
UPDATE playlist_watch_state
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, "playlist_track_candidate_cache", @"
UPDATE playlist_track_candidate_cache
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchTrackTable, @"
UPDATE playlist_watch_track
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id),
    track_source_id = TRIM(track_source_id)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id)
   OR track_source_id <> TRIM(track_source_id);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchIgnoreTable, @"
UPDATE playlist_watch_ignore
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id),
    track_source_id = TRIM(track_source_id)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id)
   OR track_source_id <> TRIM(track_source_id);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, WatchlistHistoryTable, @"
UPDATE watchlist_history
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id);", cancellationToken);

        await ExecuteIfTableExistsAsync(connection, ArtistWatchlistTable, @"
UPDATE artist_watchlist
SET spotify_id = TRIM(spotify_id),
    deezer_id = TRIM(deezer_id)
WHERE (spotify_id IS NOT NULL AND spotify_id <> TRIM(spotify_id))
   OR (deezer_id IS NOT NULL AND deezer_id <> TRIM(deezer_id));", cancellationToken);
    }

    private static async Task ExecuteIfTableExistsAsync(
        SqliteConnection connection,
        string table,
        string sql,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return;
        }

        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BackfillAudioFileVariantsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string atmosCodecPredicate = @"
(
    LOWER(COALESCE(codec, '')) LIKE '%ec-3%'
    OR LOWER(COALESCE(codec, '')) LIKE '%eac3%'
    OR LOWER(COALESCE(codec, '')) LIKE '%ac-3%'
    OR LOWER(COALESCE(codec, '')) LIKE '%ac3%'
    OR LOWER(COALESCE(codec, '')) LIKE '%truehd%'
    OR LOWER(COALESCE(codec, '')) LIKE '%mlp%'
    OR LOWER(COALESCE(extension, '')) IN ('.ec3', '.ac3', '.mlp')
)";
        const string atmosPathPredicate = @"
(
    LOWER(REPLACE(COALESCE(path, ''), '\', '/')) LIKE '%/atmos/%'
    OR LOWER(REPLACE(COALESCE(path, ''), '\', '/')) LIKE '%/dolby atmos/%'
    OR LOWER(REPLACE(COALESCE(path, ''), '\', '/')) LIKE '%/spatial/%'
    OR LOWER(COALESCE(path, '')) LIKE '%atmos%'
)";
        const string sql = @"
UPDATE audio_file
SET audio_variant = CASE
    WHEN (
        LOWER(COALESCE(codec, '')) LIKE '%dolby atmos%'
        OR LOWER(COALESCE(codec, '')) LIKE '%joc%'
        OR LOWER(COALESCE(codec, '')) LIKE '%atmos%'
    ) THEN 'atmos'
    WHEN (
        " + atmosCodecPredicate + @"
        AND channels IS NOT NULL
        AND channels > 2
    ) THEN 'atmos'
    WHEN (
        " + atmosPathPredicate + @"
        AND (
            (channels IS NOT NULL AND channels > 2)
            OR " + atmosCodecPredicate + @"
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
