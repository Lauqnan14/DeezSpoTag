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
    private const string PlaylistWatchTargetMembershipTable = "playlist_watch_target_membership";
    private const string PlaylistWatchDownloadClaimTable = "playlist_watch_download_claim";
    private const string MediaServerTrackMetadataTable = "media_server_track_metadata";
    private const string WatchlistSourceCircuitStateTable = "watchlist_source_circuit_state";
    private const string PlaylistWatchlistTable = "playlist_watchlist";
    private const string PlaylistWatchIgnoreTable = "playlist_watch_ignore";
    private const string RecommendationRejectionTable = "recommendation_rejection";
    private const string WatchlistHistoryTable = "watchlist_history";
    private const string ArtistWatchlistTable = "artist_watchlist";
    private const string TrackAnalysisTable = "track_analysis";
    private const string LibraryTable = "library";
    private const string DownloadBlocklistTable = "download_blocklist";
    private const string ManualUnavailableTrackTable = "manual_unavailable_track";
    private const string TrackShazamCacheTable = "track_shazam_cache";
    private const string LibrarySettingsTable = "library_settings";
    private const string PlayHistoryTable = "play_history";
    private const string BackgroundJobStateTable = "background_job_state";
    private const string PlayHistoryIdentityMigrationId = "play-history-event-identity-v1";
    private const string MelodayAutomaticScopeMigrationId = "meloday-automatic-library-scope-v1";
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
    private const string CreatedAtColumn = "created_at";
    private const string UpdatedAtColumn = "updated_at";
    private const string DestinationFolderIdColumn = "destination_folder_id";
    private static readonly string[] DownloadSources = ["deezer", "spotify", "apple"];
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
            ["idx_download_task_destination_folder"] = (DownloadTaskTable, DestinationFolderIdColumn, false),
            ["idx_folder_library_id"] = (FolderTable, LibraryIdColumn, false),
            ["idx_artist_artwork_cache_artist_role"] = ("artist_artwork_cache", "artist_id, role", false),
            ["idx_artist_server_sync_state_artist"] = ("artist_server_sync_state", "artist_id", false),
            ["idx_download_blocklist_field"] = (DownloadBlocklistTable, "field, is_enabled", false),
            ["idx_download_blocklist_normalized"] = (DownloadBlocklistTable, "normalized_value, is_enabled", false),
            ["idx_manual_unavailable_track_added"] = (ManualUnavailableTrackTable, "added_at_utc DESC", false),
            ["idx_manual_unavailable_track_destination"] = (ManualUnavailableTrackTable, DestinationFolderIdColumn, false),
            ["idx_manual_unavailable_track_retry"] = (ManualUnavailableTrackTable, "next_retry_at_utc", false),
            ["idx_track_shazam_cache_status"] = (TrackShazamCacheTable, "status", false),
            ["idx_track_shazam_cache_scanned"] = (TrackShazamCacheTable, "scanned_at_utc", false),
            ["idx_album_artist_id"] = (AlbumTable, ArtistIdColumn, false),
            ["idx_track_album_id"] = (TrackTable, AlbumIdColumn, false),
            ["idx_track_local_audio_file_id"] = (TrackLocalTable, "audio_file_id", false),
            ["idx_artist_name_nocase"] = (ArtistTable, "name COLLATE NOCASE", false)
            ,
            ["idx_play_history_library"] = (PlayHistoryTable, LibraryIdColumn, false)
            ,
            ["idx_play_history_folder"] = (PlayHistoryTable, "folder_id", false)
            ,
            ["idx_play_history_user_source_time"] = (PlayHistoryTable, "plex_user_id, source, played_at_utc DESC", false)
            ,
            ["idx_play_history_remote_library_time"] = (PlayHistoryTable, "plex_user_id, source, remote_library_id, played_at_utc DESC", false)
            ,
            ["idx_play_history_user_library_time"] = (PlayHistoryTable, "plex_user_id, library_id, played_at_utc DESC", false)
            ,
            ["idx_play_history_user_library_track_time"] = (PlayHistoryTable, "plex_user_id, library_id, track_id, played_at_utc DESC", false)
            ,
            ["idx_play_history_user_folder_time"] = (PlayHistoryTable, "plex_user_id, folder_id, played_at_utc DESC", false)
            ,
            ["idx_background_job_state_due"] = (BackgroundJobStateTable, "status, next_due_at_utc", false)
            ,
            ["idx_artist_watchlist_spotify_id"] = (ArtistWatchlistTable, "spotify_id", false)
            ,
            ["idx_artist_watchlist_deezer_id"] = (ArtistWatchlistTable, DeezerIdColumn, false)
            ,
            ["idx_playlist_watchlist_created"] = (PlaylistWatchlistTable, CreatedAtColumn, false)
            ,
            ["idx_playlist_watchlist_priority"] = (PlaylistWatchlistTable, "sync_priority, created_at", false)
            ,
            ["idx_playlist_watch_preferences_updated"] = (PlaylistWatchPreferencesTable, UpdatedAtColumn, false)
            ,
            ["idx_playlist_watch_state_updated"] = (PlaylistWatchStateTable, UpdatedAtColumn, false)
            ,
            ["idx_playlist_watch_track_source_status"] = (PlaylistWatchTrackTable, "source, source_id, status", false)
            ,
            ["idx_playlist_watch_track_unavailable_retry"] = (PlaylistWatchTrackTable, "source, source_id, status, unavailable_next_retry_utc", false)
            ,
            ["idx_playlist_watch_target_membership_target"] = (PlaylistWatchTargetMembershipTable, "target_service, target_playlist_id", false)
            ,
            ["idx_playlist_watch_target_membership_track"] = (PlaylistWatchTargetMembershipTable, "source, source_id, track_source_id", false)
            ,
            ["idx_media_server_track_metadata_service_item"] = (MediaServerTrackMetadataTable, "service, target_item_id", false)
            ,
            ["idx_playlist_watch_download_claim_queue"] = (PlaylistWatchDownloadClaimTable, "queue_uuid, status", false)
            ,
            ["idx_watchlist_sync_job_due"] = ("watchlist_sync_job", "next_attempt_utc, id", false)
            ,
            ["idx_watchlist_source_circuit_open"] = (WatchlistSourceCircuitStateTable, "watch_type, is_open, open_until_utc", false)
            ,
            ["idx_recommendation_rejection_library"] = (RecommendationRejectionTable, "library_id, folder_id, station_id", false)
            ,
            ["idx_recommendation_rejection_rejected"] = (RecommendationRejectionTable, "rejected_at_utc", false)
            ,
            ["idx_watchlist_history_source_created"] = (WatchlistHistoryTable, "source, created_at", false)
            ,
            ["idx_watchlist_history_item_created"] = (WatchlistHistoryTable, "item_key, created_at", false)
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
        await EnsureColumnAsync(connection, PlayHistoryTable, "folder_id", BigIntType, cancellationToken);
        var playHistoryRebuilt = await MigratePlayHistoryIdentityAsync(connection, cancellationToken);
        await EnsureIndexAsync(connection, "idx_play_history_library", PlayHistoryTable, LibraryIdColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_play_history_folder", PlayHistoryTable, "folder_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_play_history_user_source_time", PlayHistoryTable, "plex_user_id, source, played_at_utc DESC", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_play_history_user_library_time", PlayHistoryTable, "plex_user_id, library_id, played_at_utc DESC", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_play_history_user_library_track_time", PlayHistoryTable, "plex_user_id, library_id, track_id, played_at_utc DESC", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_play_history_user_folder_time", PlayHistoryTable, "plex_user_id, folder_id, played_at_utc DESC", unique: false, cancellationToken);
        await DropIndexIfExistsAsync(connection, "idx_play_history_user", cancellationToken);
        await DropIndexIfExistsAsync(connection, "idx_play_history_played_at", cancellationToken);
        if (playHistoryRebuilt)
        {
            await VacuumAsync(connection, cancellationToken);
            await MarkMigrationCompletedAsync(connection, PlayHistoryIdentityMigrationId, cancellationToken);
            await CheckpointWalAsync(connection, cancellationToken);
        }
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS background_job_state (
    job_key TEXT NOT NULL PRIMARY KEY,
    status TEXT NOT NULL DEFAULT 'idle',
    last_started_at_utc TEXT,
    last_finished_at_utc TEXT,
    next_due_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);", cancellationToken);
        await EnsureIndexAsync(connection, "idx_background_job_state_due", BackgroundJobStateTable, "status, next_due_at_utc", unique: false, cancellationToken);

        await EnsureColumnAsync(connection, ArtistTable, DeezerIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "metadata_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "preferred_background_path", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "apple_biography", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "apple_biography_checked_at", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistTable, "lastfm_images_checked_at", TextType, cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_name_nocase", ArtistTable, "name COLLATE NOCASE", unique: false, cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS artist_metadata_policy (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    sync_blocked INTEGER NOT NULL DEFAULT 0,
    ocr_text_art_blocking_enabled INTEGER NOT NULL DEFAULT 1,
    selected_targets_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id)
);", cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS artist_artwork_cache (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    role TEXT NOT NULL,
    identity TEXT NOT NULL,
    source TEXT,
    original_url TEXT,
    local_path TEXT,
    content_hash TEXT,
    width INTEGER,
    height INTEGER,
    ocr_status TEXT,
    detected_text TEXT,
    text_art_blocked INTEGER NOT NULL DEFAULT 0,
    user_blocked INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id, role, identity)
);", cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS artist_biography_cache (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    source TEXT NOT NULL,
    biography TEXT,
    language TEXT,
    selected INTEGER NOT NULL DEFAULT 0,
    fetched_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id, source)
);", cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS artist_server_sync_state (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    server TEXT NOT NULL,
    last_cache_refresh_utc TEXT,
    last_sync_utc TEXT,
    last_avatar_hash TEXT,
    last_background_hash TEXT,
    last_biography_hash TEXT,
    avatar_rotation_index INTEGER NOT NULL DEFAULT 0,
    background_rotation_index INTEGER NOT NULL DEFAULT 0,
    last_result TEXT,
    last_error TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id, server)
);", cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_artwork_cache_artist_role", "artist_artwork_cache", "artist_id, role", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_server_sync_state_artist", "artist_server_sync_state", "artist_id", unique: false, cancellationToken);

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
        await BackfillAudioFileAtmosQualityRanksAsync(connection, cancellationToken);

        await EnsureColumnAsync(connection, DownloadTaskTable, "lyrics_status", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "file_extension", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "bitrate_kbps", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "content_type", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "isrc", TextType, cancellationToken);

        foreach (var source in DownloadSources)
        {
            await EnsureColumnAsync(connection, DownloadTaskTable, $"{source}_track_id", TextType, cancellationToken);
            await EnsureColumnAsync(connection, DownloadTaskTable, $"{source}_album_id", TextType, cancellationToken);
            await EnsureColumnAsync(connection, DownloadTaskTable, $"{source}_artist_id", TextType, cancellationToken);
        }

        await EnsureColumnAsync(connection, DownloadTaskTable, DestinationFolderIdColumn, IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "move_status", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "enrichment_status", TextType, cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "final_destinations_json", TextType, cancellationToken);
        await EnsureIndexAsync(connection, "idx_download_task_isrc", DownloadTaskTable, "isrc", unique: false, cancellationToken);

        foreach (var source in DownloadSources)
        {
            await EnsureIndexAsync(connection, $"idx_download_task_{source}_track", DownloadTaskTable, $"{source}_track_id", unique: false, cancellationToken);
            await EnsureIndexAsync(connection, $"idx_download_task_{source}_album", DownloadTaskTable, $"{source}_album_id", unique: false, cancellationToken);
            await EnsureIndexAsync(connection, $"idx_download_task_{source}_artist", DownloadTaskTable, $"{source}_artist_id", unique: false, cancellationToken);
        }

        await EnsureIndexAsync(connection, "idx_download_task_destination_folder", DownloadTaskTable, DestinationFolderIdColumn, unique: false, cancellationToken);

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
        await DropColumnIfExistsAsync(connection, FolderTable, "plex_section_id", cancellationToken);
        await DropColumnIfExistsAsync(connection, FolderTable, "jellyfin_library_id", cancellationToken);
        await DropColumnIfExistsAsync(connection, FolderTable, "navidrome_library_id", cancellationToken);
        await EnsureColumnAsync(connection, PlayHistoryTable, "remote_library_id", TextType, cancellationToken);
        await EnsureIndexAsync(
            connection,
            "idx_play_history_remote_library_time",
            PlayHistoryTable,
            "plex_user_id, source, remote_library_id, played_at_utc DESC",
            unique: false,
            cancellationToken);
        await BackfillPlayHistoryFolderLinksAsync(connection, cancellationToken);
        await MigrateMelodayAutomaticScopeAsync(connection, cancellationToken);

        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "batch_next_offset", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "batch_processing_snapshot_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "last_run_status", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "last_run_message", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "next_attempt_utc", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "consecutive_failures", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "ignored_blocked_track_count", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "rerouted_track_count", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, "presentation_updated_at", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchlistTable, "description", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchlistTable, "sync_priority", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchlistTable, "owner_name", TextType, cancellationToken);
        await BackfillPlaylistWatchlistPrioritiesAsync(connection, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "preferred_engine", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "download_engine_order_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "download_variant_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "sync_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "atmos_destination_folder_id", BigIntType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "sync_targets_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "update_artwork", $"{IntegerType} DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "reuse_saved_artwork", $"{IntegerType} DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "plex_playlist_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "jellyfin_playlist_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "navidrome_playlist_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "routing_rules_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, "ignore_rules_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "status", $"{TextType} DEFAULT 'queued'", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, UpdatedAtColumn, TextType, cancellationToken);
        await ExecuteIfTableExistsAsync(connection, PlaylistWatchTrackTable, @"
UPDATE playlist_watch_track
SET updated_at = CURRENT_TIMESTAMP
WHERE updated_at IS NULL OR TRIM(updated_at) = '';", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "unavailable_reason", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "unavailable_since_utc", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "unavailable_last_checked_utc", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "unavailable_next_retry_utc", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "unavailable_settings_fingerprint", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "local_track_id", BigIntType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "identity_status", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "identity_reason", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "target_service", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "target_playlist_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "target_item_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "sync_status", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "redirect_track_source_id", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "redirect_reason", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, "verified_at_utc", TextType, cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS playlist_watch_target_membership (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    target_service TEXT NOT NULL,
    target_playlist_id TEXT NOT NULL,
    target_item_id TEXT,
    local_track_id BIGINT,
    sync_status TEXT NOT NULL DEFAULT 'waiting_for_target',
    verified_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id, track_source_id, target_service)
);", cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS media_server_track_metadata (
    track_id BIGINT NOT NULL,
    service TEXT NOT NULL,
    target_item_id TEXT NOT NULL,
    file_path TEXT,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (track_id, service)
);", cancellationToken);
        await ExecuteIfTableExistsAsync(connection, PlaylistWatchTrackTable, @"
INSERT OR IGNORE INTO playlist_watch_target_membership (
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
SELECT source,
       source_id,
       track_source_id,
       target_service,
       target_playlist_id,
       target_item_id,
       local_track_id,
       COALESCE(sync_status, 'waiting_for_target'),
       COALESCE(verified_at_utc, CURRENT_TIMESTAMP),
       COALESCE(updated_at, CURRENT_TIMESTAMP)
FROM playlist_watch_track
WHERE target_service IS NOT NULL
  AND TRIM(target_service) <> ''
  AND target_playlist_id IS NOT NULL
  AND TRIM(target_playlist_id) <> '';", cancellationToken);
        await EnsureTableAsync(connection, @"
	CREATE TABLE IF NOT EXISTS playlist_watch_download_claim (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    queue_uuid TEXT NOT NULL,
    destination_folder_id BIGINT,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id, track_source_id, queue_uuid)
);", cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS watchlist_sync_job (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    track_id TEXT NOT NULL,
    destination_folder_id BIGINT,
    final_file_paths_json TEXT,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (source, playlist_id, track_id)
);", cancellationToken);
        await EnsureIndexAsync(connection, "idx_watchlist_sync_job_due", "watchlist_sync_job", "next_attempt_utc, id", unique: false, cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS watchlist_scheduler_state (
    watch_type TEXT NOT NULL PRIMARY KEY,
    active_source TEXT,
    active_source_id TEXT,
    active_started_utc TEXT,
    last_progress_utc TEXT,
    zero_queue_streak INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);", cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS watchlist_source_circuit_state (
    watch_type TEXT NOT NULL,
    source TEXT NOT NULL,
    is_open INTEGER NOT NULL DEFAULT 0,
    open_until_utc TEXT,
    reason TEXT,
    fingerprint TEXT,
    failure_count INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (watch_type, source)
);", cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchlistTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchPreferencesTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "destination_folder_id", BigIntType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "album_groups_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "top_songs_enabled", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "latest_releases_only", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "preferred_engine", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "routing_rules_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "atmos_destination_folder_id", BigIntType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "download_variant_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "top_songs_sync_mode", TextType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "download_discography_enabled", IntegerType, cancellationToken);
        await EnsureColumnAsync(connection, ArtistWatchlistTable, "ignore_rules_json", TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchStateTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchTrackTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, PlaylistWatchIgnoreTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, WatchlistHistoryTable, SourceIdColumn, TextType, cancellationToken);
        await EnsureColumnAsync(connection, WatchlistHistoryTable, "item_key", TextType, cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS playlist_track_candidate_cache (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    snapshot_id TEXT,
    candidates_json TEXT NOT NULL,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id)
);", cancellationToken);
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS recommendation_rejection (
    library_id BIGINT NOT NULL,
    folder_id BIGINT,
    station_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    isrc TEXT,
    title TEXT,
    artist TEXT,
    rejected_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (station_id, track_source_id)
);", cancellationToken);
        await EnsureIndexAsync(connection, "idx_recommendation_rejection_library", RecommendationRejectionTable, "library_id, folder_id, station_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_recommendation_rejection_rejected", RecommendationRejectionTable, "rejected_at_utc", unique: false, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchlistTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchPreferencesTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchStateTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchTrackTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, PlaylistWatchIgnoreTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillColumnFromLegacyAsync(connection, WatchlistHistoryTable, SourceIdColumn, ExternalIdColumn, cancellationToken);
        await BackfillWatchlistHistoryItemKeysAsync(connection, cancellationToken);
        await NormalizeWatchlistKeysAsync(connection, cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_watchlist_spotify_id", ArtistWatchlistTable, "spotify_id", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_artist_watchlist_deezer_id", ArtistWatchlistTable, DeezerIdColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watchlist_created", PlaylistWatchlistTable, CreatedAtColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watchlist_priority", PlaylistWatchlistTable, "sync_priority, created_at", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_preferences_updated", PlaylistWatchPreferencesTable, UpdatedAtColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_state_updated", PlaylistWatchStateTable, UpdatedAtColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_track_source_status", PlaylistWatchTrackTable, "source, source_id, status", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_track_unavailable_retry", PlaylistWatchTrackTable, "source, source_id, status, unavailable_next_retry_utc", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_playlist_watch_download_claim_queue", PlaylistWatchDownloadClaimTable, "queue_uuid, status", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_watchlist_source_circuit_open", WatchlistSourceCircuitStateTable, "watch_type, is_open, open_until_utc", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_watchlist_history_source_created", WatchlistHistoryTable, "source, created_at", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_watchlist_history_item_created", WatchlistHistoryTable, "item_key, created_at", unique: false, cancellationToken);
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
CREATE TABLE IF NOT EXISTS manual_unavailable_track (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    queue_uuid TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL,
    artist TEXT NOT NULL,
    album TEXT,
    album_artist TEXT,
    isrc TEXT,
    engine TEXT,
    source_service TEXT,
    source_url TEXT,
    deezer_track_id TEXT,
    spotify_track_id TEXT,
    apple_track_id TEXT,
    qobuz_track_id TEXT,
    tidal_track_id TEXT,
    amazon_track_id TEXT,
    destination_folder_id INTEGER,
    expected_final_path TEXT,
    quality TEXT,
    content_type TEXT,
    reason TEXT,
    payload_json TEXT,
    first_unavailable_at_utc TEXT NOT NULL,
    next_retry_at_utc TEXT NOT NULL,
    added_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);", cancellationToken);
        await EnsureColumnAsync(connection, ManualUnavailableTrackTable, "next_retry_at_utc", TextType, cancellationToken);
        await BackfillManualUnavailableRetryDeadlinesAsync(connection, cancellationToken);
        await EnsureIndexAsync(connection, "idx_manual_unavailable_track_added", ManualUnavailableTrackTable, "added_at_utc DESC", unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_manual_unavailable_track_destination", ManualUnavailableTrackTable, DestinationFolderIdColumn, unique: false, cancellationToken);
        await EnsureIndexAsync(connection, "idx_manual_unavailable_track_retry", ManualUnavailableTrackTable, "next_retry_at_utc", unique: false, cancellationToken);

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
        await MigrateLibrarySettingsSchemaAsync(connection, cancellationToken);

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

    private static async Task<bool> MigratePlayHistoryIdentityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureTableAsync(connection, @"
CREATE TABLE IF NOT EXISTS app_schema_migration (
    migration_id TEXT NOT NULL PRIMARY KEY,
    completed_at_utc TEXT NOT NULL
);", cancellationToken);

        await using (var checkCommand = new SqliteCommand(
            "SELECT 1 FROM app_schema_migration WHERE migration_id = @migrationId LIMIT 1;",
            connection))
        {
            checkCommand.Parameters.AddWithValue("migrationId", PlayHistoryIdentityMigrationId);
            if (await checkCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return false;
            }
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string rebuildSql = @"
DROP TABLE IF EXISTS play_history_canonical;
CREATE TABLE play_history_canonical (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    library_id BIGINT REFERENCES library(id) ON DELETE SET NULL,
    folder_id BIGINT REFERENCES folder(id) ON DELETE SET NULL,
    plex_user_id BIGINT NOT NULL REFERENCES plex_user(id) ON DELETE CASCADE,
    track_id BIGINT REFERENCES track(id) ON DELETE SET NULL,
    plex_track_key TEXT,
    plex_rating_key TEXT,
    event_key TEXT NOT NULL,
    played_at_utc TEXT NOT NULL,
    play_duration_ms INTEGER,
    source TEXT NOT NULL DEFAULT 'plex',
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (plex_user_id, source, event_key)
);
INSERT OR IGNORE INTO play_history_canonical
    (id, library_id, folder_id, plex_user_id, track_id, plex_track_key, plex_rating_key, event_key,
     played_at_utc, play_duration_ms, source, metadata_json, created_at)
SELECT ph.id,
       CASE WHEN l.id IS NOT NULL THEN ph.library_id END,
       CASE WHEN f.id IS NOT NULL THEN ph.folder_id END,
       ph.plex_user_id,
       CASE WHEN t.id IS NOT NULL THEN ph.track_id END,
       ph.plex_track_key,
       ph.plex_rating_key,
       CASE
           WHEN TRIM(COALESCE(ph.plex_track_key, '')) <> ''
               THEN 'key:' || TRIM(ph.plex_track_key)
           WHEN TRIM(COALESCE(ph.plex_rating_key, '')) <> ''
               THEN 'rating:' || TRIM(ph.plex_rating_key)
           WHEN t.id IS NOT NULL
               THEN 'track:' || CAST(ph.track_id AS TEXT)
       END || '|' || ph.played_at_utc,
       ph.played_at_utc,
       ph.play_duration_ms,
       LOWER(TRIM(COALESCE(NULLIF(ph.source, ''), 'plex'))),
       ph.metadata_json,
       ph.created_at
FROM play_history ph
JOIN plex_user pu ON pu.id = ph.plex_user_id
LEFT JOIN library l ON l.id = ph.library_id
LEFT JOIN folder f ON f.id = ph.folder_id
LEFT JOIN track t ON t.id = ph.track_id
WHERE ph.played_at_utc IS NOT NULL
  AND (
      TRIM(COALESCE(ph.plex_track_key, '')) <> ''
      OR TRIM(COALESCE(ph.plex_rating_key, '')) <> ''
      OR t.id IS NOT NULL
  )
ORDER BY CASE WHEN t.id IS NULL THEN 1 ELSE 0 END, ph.id;
DROP TABLE play_history;
ALTER TABLE play_history_canonical RENAME TO play_history;";
        await using (var rebuildCommand = new SqliteCommand(rebuildSql, connection, transaction))
        {
            rebuildCommand.CommandTimeout = 0;
            await rebuildCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task MarkMigrationCompletedAsync(
        SqliteConnection connection,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using var markerCommand = new SqliteCommand(@"
INSERT INTO app_schema_migration (migration_id, completed_at_utc)
VALUES (@migrationId, @completedAtUtc)
ON CONFLICT(migration_id) DO UPDATE SET completed_at_utc = excluded.completed_at_utc;", connection);
        markerCommand.Parameters.AddWithValue("migrationId", migrationId);
        markerCommand.Parameters.AddWithValue("completedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await markerCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropIndexIfExistsAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand($"DROP INDEX IF EXISTS {indexName};", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VacuumAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand("VACUUM;", connection)
        {
            CommandTimeout = 0
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CheckpointWalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", connection)
        {
            CommandTimeout = 0
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task DropColumnIfExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(connection, table, column, cancellationToken))
        {
            return;
        }

        await using var command = new SqliteCommand($"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\";", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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

    private static async Task BackfillWatchlistHistoryItemKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE watchlist_history
SET item_key = lower(trim(watch_type)) || ':' || lower(trim(source)) || ':' || trim(source_id)
WHERE lower(trim(watch_type)) = 'playlist'
  AND (item_key IS NULL OR trim(item_key) = '');";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BackfillManualUnavailableRetryDeadlinesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE manual_unavailable_track
SET next_retry_at_utc = datetime(added_at_utc, '+7 days')
WHERE next_retry_at_utc IS NULL OR trim(next_retry_at_utc) = '';";
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

    private static async Task BackfillPlayHistoryFolderLinksAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, PlayHistoryTable, cancellationToken)
            || !await ColumnExistsAsync(connection, PlayHistoryTable, "folder_id", cancellationToken))
        {
            return;
        }

        const string sql = @"
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
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateMelodayAutomaticScopeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var check = new SqliteCommand(
            "SELECT 1 FROM app_schema_migration WHERE migration_id = @migrationId LIMIT 1;",
            connection))
        {
            check.Parameters.AddWithValue("migrationId", MelodayAutomaticScopeMigrationId);
            if (await check.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return;
            }
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
DELETE FROM mix_item
WHERE mix_cache_id IN (
    SELECT id FROM mix_cache WHERE mix_id LIKE 'meloday-%'
);
DELETE FROM mix_cache WHERE mix_id LIKE 'meloday-%';";
        await using (var cleanup = new SqliteCommand(sql, connection, transaction))
        {
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var marker = new SqliteCommand(@"
INSERT INTO app_schema_migration (migration_id, completed_at_utc)
VALUES (@migrationId, @completedAtUtc);", connection, transaction))
        {
            marker.Parameters.AddWithValue("migrationId", MelodayAutomaticScopeMigrationId);
            marker.Parameters.AddWithValue("completedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
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

    private static async Task MigrateLibrarySettingsSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, LibrarySettingsTable, cancellationToken))
        {
            return;
        }

        await EnsureColumnAsync(connection, LibrarySettingsTable, "live_preview_ingest", $"{IntegerType} NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, LibrarySettingsTable, "enable_signal_analysis", $"{IntegerType} NOT NULL DEFAULT 0", cancellationToken);

        var columns = await GetTableColumnsAsync(connection, LibrarySettingsTable, cancellationToken);
        var hasLegacyColumns = columns.Contains("fuzzy_threshold") || columns.Contains("include_all_folders");
        if (!hasLegacyColumns)
        {
            return;
        }

        var preserveCreatedAt = columns.Contains(CreatedAtColumn);
        var preserveUpdatedAt = columns.Contains(UpdatedAtColumn);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string dropTempSql = "DROP TABLE IF EXISTS library_settings_migrated;";
        await using (var dropTempCommand = new SqliteCommand(dropTempSql, connection, transaction))
        {
            await dropTempCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string createTempSql = @"
CREATE TABLE library_settings_migrated (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    live_preview_ingest INTEGER NOT NULL DEFAULT FALSE,
    enable_signal_analysis INTEGER NOT NULL DEFAULT FALSE,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);";
        await using (var createTempCommand = new SqliteCommand(createTempSql, connection, transaction))
        {
            await createTempCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var copySql = ResolveLibrarySettingsCopySql(preserveCreatedAt, preserveUpdatedAt);
        await using (var copyCommand = new SqliteCommand(copySql, connection, transaction))
        {
            await copyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string ensureRowSql = "INSERT INTO library_settings_migrated (id) VALUES (1) ON CONFLICT(id) DO NOTHING;";
        await using (var ensureRowCommand = new SqliteCommand(ensureRowSql, connection, transaction))
        {
            await ensureRowCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string dropOldSql = "DROP TABLE library_settings;";
        await using (var dropOldCommand = new SqliteCommand(dropOldSql, connection, transaction))
        {
            await dropOldCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string renameSql = "ALTER TABLE library_settings_migrated RENAME TO library_settings;";
        await using (var renameCommand = new SqliteCommand(renameSql, connection, transaction))
        {
            await renameCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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

    private static string ResolveLibrarySettingsCopySql(bool preserveCreatedAt, bool preserveUpdatedAt)
    {
        if (preserveCreatedAt && preserveUpdatedAt)
        {
            return @"
INSERT INTO library_settings_migrated (id, live_preview_ingest, enable_signal_analysis, created_at, updated_at)
SELECT COALESCE(id, 1),
       COALESCE(live_preview_ingest, 0),
       COALESCE(enable_signal_analysis, 0),
       COALESCE(created_at, CURRENT_TIMESTAMP),
       COALESCE(updated_at, CURRENT_TIMESTAMP)
FROM library_settings;";
        }

        if (preserveCreatedAt)
        {
            return @"
INSERT INTO library_settings_migrated (id, live_preview_ingest, enable_signal_analysis, created_at, updated_at)
SELECT COALESCE(id, 1),
       COALESCE(live_preview_ingest, 0),
       COALESCE(enable_signal_analysis, 0),
       COALESCE(created_at, CURRENT_TIMESTAMP),
       CURRENT_TIMESTAMP
FROM library_settings;";
        }

        if (preserveUpdatedAt)
        {
            return @"
INSERT INTO library_settings_migrated (id, live_preview_ingest, enable_signal_analysis, created_at, updated_at)
SELECT COALESCE(id, 1),
       COALESCE(live_preview_ingest, 0),
       COALESCE(enable_signal_analysis, 0),
       CURRENT_TIMESTAMP,
       COALESCE(updated_at, CURRENT_TIMESTAMP)
FROM library_settings;";
        }

        return @"
INSERT INTO library_settings_migrated (id, live_preview_ingest, enable_signal_analysis, created_at, updated_at)
SELECT COALESCE(id, 1),
       COALESCE(live_preview_ingest, 0),
       COALESCE(enable_signal_analysis, 0),
       CURRENT_TIMESTAMP,
       CURRENT_TIMESTAMP
FROM library_settings;";
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

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string sql = "SELECT name FROM pragma_table_info(@tableName);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@tableName", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                columns.Add(reader.GetString(0));
            }
        }

        return columns;
    }

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

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchDownloadClaimTable, @"
DELETE FROM playlist_watch_download_claim
WHERE rowid NOT IN (
    SELECT MAX(rowid)
    FROM playlist_watch_download_claim
    GROUP BY LOWER(TRIM(source)), TRIM(source_id), TRIM(track_source_id), TRIM(queue_uuid)
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

        await ExecuteIfTableExistsAsync(connection, PlaylistWatchDownloadClaimTable, @"
UPDATE playlist_watch_download_claim
SET source = LOWER(TRIM(source)),
    source_id = TRIM(source_id),
    track_source_id = TRIM(track_source_id),
    queue_uuid = TRIM(queue_uuid)
WHERE source <> LOWER(TRIM(source))
   OR source_id <> TRIM(source_id)
   OR track_source_id <> TRIM(track_source_id)
   OR queue_uuid <> TRIM(queue_uuid);", cancellationToken);

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

    private static async Task BackfillPlaylistWatchlistPrioritiesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
WITH ordered AS (
    SELECT id,
           ROW_NUMBER() OVER (ORDER BY created_at DESC, id DESC)
               + COALESCE((SELECT MAX(sync_priority) FROM playlist_watchlist WHERE sync_priority > 0), 0) AS priority
    FROM playlist_watchlist
    WHERE sync_priority IS NULL OR sync_priority <= 0
)
UPDATE playlist_watchlist
SET sync_priority = (
    SELECT priority
    FROM ordered
    WHERE ordered.id = playlist_watchlist.id
)
WHERE id IN (SELECT id FROM ordered);";
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

    private static async Task BackfillAudioFileAtmosQualityRanksAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE audio_file
SET quality_rank = 5
WHERE LOWER(TRIM(COALESCE(audio_variant, ''))) = 'atmos'
  AND COALESCE(quality_rank, 0) < 5;";

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

}
