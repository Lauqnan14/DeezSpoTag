using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryDbWatchlistMigrationTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watch-migration-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnsureSchema_NormalizesLegacyWatchlistKeys_And_EnsuresIndexes()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO playlist_watchlist (source, source_id, name) VALUES (' SPOTIFY ', ' pl-123 ', 'One');
INSERT INTO playlist_watchlist (source, source_id, name) VALUES ('spotify', 'pl-123', 'Two');
INSERT INTO playlist_watch_preferences (source, source_id) VALUES (' SPOTIFY ', ' pl-123 ');
INSERT INTO playlist_watch_preferences (source, source_id) VALUES ('spotify', 'pl-123');
INSERT INTO playlist_watch_track (source, source_id, track_source_id, status) VALUES (' SPOTIFY ', ' pl-123 ', ' tr-1 ', 'queued');
INSERT INTO playlist_watch_track (source, source_id, track_source_id, status) VALUES ('spotify', 'pl-123', 'tr-1', 'completed');
INSERT INTO watchlist_history (source, watch_type, source_id, name, collection_type, track_count, status)
VALUES (' SPOTIFY ', 'playlist', ' pl-123 ', 'Legacy', 'playlist', 1, 'queued');
INSERT INTO artist_watchlist (artist_id, artist_name, spotify_id, deezer_id)
VALUES (1, 'Artist One', ' sp-1 ', ' dz-1 ');
";
            await command.ExecuteNonQueryAsync();
        }

        // Re-run schema to execute migrations against legacy rows.
        await dbService.EnsureSchemaAsync();

        var repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
        Assert.True(await repository.IsPlaylistWatchlistedAsync("spotify", "pl-123"));

        var watchlist = await repository.GetPlaylistWatchlistAsync();
        var matching = watchlist.Where(item => item.Source == "spotify" && item.SourceId == "pl-123").ToList();
        Assert.Single(matching);

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();

            Assert.True(await IndexExistsAsync(connection, "idx_artist_watchlist_spotify_id"));
            Assert.True(await IndexExistsAsync(connection, "idx_artist_watchlist_deezer_id"));
            Assert.True(await IndexExistsAsync(connection, "idx_playlist_watchlist_created"));
            Assert.True(await IndexExistsAsync(connection, "idx_playlist_watch_preferences_updated"));
            Assert.True(await IndexExistsAsync(connection, "idx_playlist_watch_state_updated"));
            Assert.True(await IndexExistsAsync(connection, "idx_playlist_watch_track_source_status"));
            Assert.True(await IndexExistsAsync(connection, "idx_watchlist_history_source_created"));
            Assert.True(await IndexExistsAsync(connection, "idx_watchlist_sync_job_due"));
            Assert.True(await IndexExistsAsync(connection, "idx_watchlist_reconciliation_request_updated"));
            Assert.True(await TableExistsAsync(connection, "playlist_watch_artwork_state"));
            Assert.True(await TableExistsAsync(connection, "playlist_watch_artwork_target_state"));

            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT source, source_id FROM playlist_watch_preferences LIMIT 1;
SELECT source, source_id, track_source_id FROM playlist_watch_track WHERE source='spotify' AND source_id='pl-123' LIMIT 1;
SELECT source, source_id FROM watchlist_history ORDER BY id DESC LIMIT 1;
SELECT spotify_id, deezer_id FROM artist_watchlist WHERE artist_id=1;";
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("spotify", reader.GetString(0));
            Assert.Equal("pl-123", reader.GetString(1));

            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("spotify", reader.GetString(0));
            Assert.Equal("pl-123", reader.GetString(1));
            Assert.Equal("tr-1", reader.GetString(2));

            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("spotify", reader.GetString(0));
            Assert.Equal("pl-123", reader.GetString(1));

            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("sp-1", reader.GetString(0));
            Assert.Equal("dz-1", reader.GetString(1));
        }
    }

    [Fact]
    public async Task EnsureSchema_MergesDuplicateArtistWatchRowsIntoCurrentCanonicalArtist()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
DROP INDEX idx_artist_watchlist_spotify_id;
DROP INDEX idx_artist_watchlist_deezer_id;
INSERT INTO artist(id,name) VALUES (1211,'50 Cent');
INSERT INTO artist_source(artist_id,source,source_id) VALUES (1211,'spotify','3q7HBObVc0L8jNeTe5Gofh');
INSERT INTO artist_watchlist(artist_id,artist_name,spotify_id,destination_folder_id,preferred_engine)
VALUES (57,'50 Cent','3q7HBObVc0L8jNeTe5Gofh',7,'tidal'),
       (1211,'50 Cent','3q7HBObVc0L8jNeTe5Gofh',NULL,NULL);
INSERT INTO artist_watch_album(artist_id,source,album_source_id) VALUES (57,'spotify','album-1');
INSERT INTO artist_watch_state(artist_id,last_run_status) VALUES (57,'complete');";
            await command.ExecuteNonQueryAsync();
        }

        await dbService.EnsureSchemaAsync();

        await using var verify = new SqliteConnection($"Data Source={_dbPath}");
        await verify.OpenAsync();
        await using var commandVerify = verify.CreateCommand();
        commandVerify.CommandText = @"
SELECT COUNT(*), MIN(artist_id), MAX(destination_folder_id), MAX(preferred_engine)
FROM artist_watchlist WHERE spotify_id='3q7HBObVc0L8jNeTe5Gofh';
SELECT artist_id FROM artist_watch_album WHERE album_source_id='album-1';
SELECT artist_id FROM artist_watch_state WHERE last_run_status='complete';
SELECT COUNT(*) FROM pragma_index_list('artist_watchlist') WHERE name='idx_artist_watchlist_spotify_id' AND [unique]=1;";
        await using var reader = await commandVerify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1211, reader.GetInt64(1));
        Assert.Equal(7, reader.GetInt64(2));
        Assert.Equal("tidal", reader.GetString(3));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1211, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1211, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name LIMIT 1;";
        command.Parameters.AddWithValue("name", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    [Fact]
    public async Task EnsureSchema_MigratesResolvedLegacyIdentityRowsAndDropsDuplicateLedger()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE watchlist_shared_identity (
    local_track_id INTEGER NOT NULL,
    target_service TEXT NOT NULL,
    target_item_id TEXT,
    status TEXT NOT NULL,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY(local_track_id,target_service)
);
INSERT INTO watchlist_shared_identity(local_track_id,target_service,target_item_id,status)
VALUES (701,'plex','plex-701','resolved'),(702,'plex',NULL,'pending_refresh');";
            await command.ExecuteNonQueryAsync();
        }

        await dbService.EnsureSchemaAsync();

        await using var verify = new SqliteConnection($"Data Source={_dbPath}");
        await verify.OpenAsync();
        Assert.False(await TableExistsAsync(verify, "watchlist_shared_identity"));
        await using var migrated = verify.CreateCommand();
        migrated.CommandText = @"
SELECT track_id,service,target_item_id
FROM media_server_track_metadata
WHERE track_id IN (701,702)
ORDER BY track_id;";
        await using var reader = await migrated.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(701, reader.GetInt64(0));
        Assert.Equal("plex", reader.GetString(1));
        Assert.Equal("plex-701", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task EnsureSchema_AddsAndBackfillsWatchTrackUpdatedAt_ForLegacyDatabase()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE playlist_watch_track (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'queued',
    PRIMARY KEY (source, source_id, track_source_id)
);
INSERT INTO playlist_watch_track (source, source_id, track_source_id, status)
VALUES ('spotify', 'legacy-playlist', 'legacy-track', 'queued');";
            await command.ExecuteNonQueryAsync();
        }

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var verifyConnection = new SqliteConnection($"Data Source={_dbPath}");
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = @"
SELECT updated_at
FROM playlist_watch_track
WHERE source = 'spotify'
  AND source_id = 'legacy-playlist'
  AND track_source_id = 'legacy-track';";
        var updatedAt = await verifyCommand.ExecuteScalarAsync();

        Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(updatedAt)));

        await using var migrationCommand = verifyConnection.CreateCommand();
        migrationCommand.CommandText = @"
SELECT
    EXISTS(SELECT 1 FROM pragma_table_info('playlist_watch_track') WHERE name='source_position'),
    EXISTS(SELECT 1 FROM pragma_table_info('playlist_watch_track') WHERE name='candidate_revision'),
    EXISTS(SELECT 1 FROM pragma_table_info('playlist_watch_track') WHERE name='mapping_status'),
    EXISTS(SELECT 1 FROM sqlite_master WHERE type='index' AND name='idx_playlist_watch_track_admission');";
        await using var migrationReader = await migrationCommand.ExecuteReaderAsync();
        Assert.True(await migrationReader.ReadAsync());
        Assert.Equal(1L, migrationReader.GetInt64(0));
        Assert.Equal(1L, migrationReader.GetInt64(1));
        Assert.Equal(1L, migrationReader.GetInt64(2));
        Assert.Equal(1L, migrationReader.GetInt64(3));
    }

    [Fact]
    public async Task EnsureSchema_MigratesLegacySingleTargetStateAndSyncJobsToPerTargetStorage()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO playlist_watchlist (source, source_id, name)
VALUES ('spotify', 'legacy-targets', 'Legacy Targets');
INSERT INTO playlist_watch_preferences (source, source_id, service, sync_targets_json)
VALUES ('spotify', 'legacy-targets', 'plex', '[""plex"",""jellyfin""]');
INSERT INTO playlist_watch_track (source, source_id, track_source_id, status, local_track_id, identity_status)
VALUES ('spotify', 'legacy-targets', 'track-1', 'completed', 42, 'identity_verified');
ALTER TABLE playlist_watch_track ADD COLUMN target_service TEXT;
ALTER TABLE playlist_watch_track ADD COLUMN target_playlist_id TEXT;
ALTER TABLE playlist_watch_track ADD COLUMN target_item_id TEXT;
ALTER TABLE playlist_watch_track ADD COLUMN sync_status TEXT;
UPDATE playlist_watch_track
SET target_service='plex', target_playlist_id='plex-list', target_item_id='plex-track', sync_status='playlist_synced'
WHERE source='spotify' AND source_id='legacy-targets' AND track_source_id='track-1';
DROP INDEX IF EXISTS idx_watchlist_sync_job_due;
DROP TABLE watchlist_sync_job;
CREATE TABLE watchlist_sync_job (
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
);
INSERT INTO watchlist_sync_job (source, playlist_id, track_id)
VALUES ('spotify', 'legacy-targets', 'track-1');";
            await command.ExecuteNonQueryAsync();
        }

        await dbService.EnsureSchemaAsync();

        await using var verifyConnection = new SqliteConnection($"Data Source={_dbPath}");
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = @"
SELECT COUNT(*) FROM pragma_table_info('playlist_watch_track')
WHERE name IN ('target_service', 'target_playlist_id', 'target_item_id', 'sync_status');
SELECT target_service, target_playlist_id, target_item_id, sync_status
FROM playlist_watch_target_membership
WHERE source='spotify' AND source_id='legacy-targets' AND track_source_id='track-1';
SELECT group_concat(target_service, ',')
FROM (SELECT target_service FROM watchlist_sync_job ORDER BY target_service);
SELECT COUNT(*) FROM pragma_table_info('watchlist_sync_job')
WHERE name IN ('queue_uuid', 'lease_owner', 'status', 'lease_until_utc');
SELECT COUNT(*) FROM sqlite_master
WHERE type='table' AND name='watchlist_reconciliation_request';";
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal("plex", reader.GetString(0));
        Assert.Equal("plex-list", reader.GetString(1));
        Assert.Equal("plex-track", reader.GetString(2));
        Assert.Equal("playlist_synced", reader.GetString(3));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal("jellyfin,plex", reader.GetString(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
    }

    [Fact]
    public async Task EnsureSchema_UpgradesLegacyManualUnavailableRetrySchemaBeforeCreatingIndex()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE manual_unavailable_track (
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
    added_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
INSERT INTO manual_unavailable_track (
    queue_uuid, title, artist, first_unavailable_at_utc, added_at_utc, updated_at_utc)
VALUES (
    'legacy-queue', 'Legacy Track', 'Legacy Artist',
    '2026-07-01T00:00:00+00:00', '2026-07-01T00:00:00+00:00', '2026-07-01T00:00:00+00:00');";
            await command.ExecuteNonQueryAsync();
        }

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var verifyConnection = new SqliteConnection($"Data Source={_dbPath}");
        await verifyConnection.OpenAsync();
        Assert.True(await IndexExistsAsync(verifyConnection, "idx_manual_unavailable_track_retry"));

        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = @"
SELECT next_retry_at_utc, title
FROM manual_unavailable_track
WHERE queue_uuid = 'legacy-queue';";
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.IsDBNullAsync(0));
        Assert.Equal("Legacy Track", reader.GetString(1));
    }

    [Fact]
    public async Task EnsureSchema_UpgradesLegacyWatchlistHistoryBeforeCreatingItemKeyIndex()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE watchlist_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    watch_type TEXT NOT NULL,
    source_id TEXT NOT NULL,
    name TEXT NOT NULL,
    collection_type TEXT NOT NULL,
    track_count INTEGER NOT NULL,
    status TEXT NOT NULL,
    artist_name TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
INSERT INTO watchlist_history (
    source, watch_type, source_id, name, collection_type, track_count, status)
VALUES (
    ' SPOTIFY ', 'playlist', ' legacy-playlist ', 'Legacy Playlist', 'playlist', 3, 'queued');";
            await command.ExecuteNonQueryAsync();
        }

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var verifyConnection = new SqliteConnection($"Data Source={_dbPath}");
        await verifyConnection.OpenAsync();
        Assert.True(await IndexExistsAsync(verifyConnection, "idx_watchlist_history_item_created"));

        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = @"
SELECT source, source_id, item_key
FROM watchlist_history
WHERE name = 'Legacy Playlist';";
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("spotify", reader.GetString(0));
        Assert.Equal("legacy-playlist", reader.GetString(1));
        Assert.Equal("playlist:spotify:legacy-playlist", reader.GetString(2));
    }

    private static readonly string[] ManualUnavailableMetadataColumns =
    [
        "cover_url",
        "duration_ms",
        "track_number",
        "track_total",
        "disc_number",
        "disc_total",
        "release_date",
        "explicit"
    ];

    /// <summary>
    /// Behavioural counterpart to the library.sql source assertions: a database created from the
    /// running code, inspected through SQLite itself.
    /// </summary>
    [Fact]
    public async Task EnsureSchema_CreatesManualUnavailableMetadataColumnsOnAFreshDatabase()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        var columns = await ReadColumnNamesAsync(connection, "manual_unavailable_track");

        foreach (var column in ManualUnavailableMetadataColumns)
        {
            Assert.Contains(column, columns);
        }
    }

    [Fact]
    public async Task EnsureSchema_BackfillsManualUnavailableMetadataFromLegacyPayload()
    {
        await CreateLegacyManualUnavailableTableAsync(
            "legacy-queue",
            @"{""Cover"":""https://example.test/original-album.jpg"",""DurationSeconds"":205," +
            @"""TrackNumber"":4,""TrackTotal"":12,""DiscNumber"":2,""DiscTotal"":3," +
            @"""ReleaseDate"":""2025-07-18"",""Explicit"":true}");

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT cover_url, duration_ms, track_number, track_total, disc_number, disc_total, release_date, explicit
FROM manual_unavailable_track
WHERE queue_uuid = 'legacy-queue';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("https://example.test/original-album.jpg", reader.GetString(0));
        Assert.Equal(205000, reader.GetInt32(1));
        Assert.Equal(4, reader.GetInt32(2));
        Assert.Equal(12, reader.GetInt32(3));
        Assert.Equal(2, reader.GetInt32(4));
        Assert.Equal(3, reader.GetInt32(5));
        Assert.Equal("2025-07-18", reader.GetString(6));
        Assert.Equal(1, reader.GetInt32(7));
    }

    [Fact]
    public async Task EnsureSchema_BackfillsManualUnavailableMetadataFromCamelCasePayload()
    {
        await CreateLegacyManualUnavailableTableAsync(
            "camel-queue",
            @"{""coverUrl"":""https://example.test/camel.jpg"",""durationMs"":42000," +
            @"""spotifyTrackNumber"":7,""spotifyTotalTracks"":9,""spotifyDiscNumber"":1," +
            @"""release_date"":""2024-01-02"",""explicit_lyrics"":""true""}");

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT cover_url, duration_ms, track_number, track_total, disc_number, release_date, explicit
FROM manual_unavailable_track
WHERE queue_uuid = 'camel-queue';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("https://example.test/camel.jpg", reader.GetString(0));
        Assert.Equal(42000, reader.GetInt32(1));
        Assert.Equal(7, reader.GetInt32(2));
        Assert.Equal(9, reader.GetInt32(3));
        Assert.Equal(1, reader.GetInt32(4));
        Assert.Equal("2024-01-02", reader.GetString(5));
        Assert.Equal(1, reader.GetInt32(6));
    }

    [Fact]
    public async Task EnsureSchema_DoesNotOverwriteExistingManualUnavailableMetadata()
    {
        await CreateLegacyManualUnavailableTableAsync(
            "kept-queue",
            @"{""Cover"":""https://example.test/payload.jpg"",""DurationSeconds"":205," +
            @"""TrackNumber"":4,""ReleaseDate"":""2025-07-18"",""Explicit"":true}");

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        // Re-point the normalised values at something the payload never mentions.
        await using (var seedConnection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await seedConnection.OpenAsync();
            await using var seed = seedConnection.CreateCommand();
            seed.CommandText = @"
UPDATE manual_unavailable_track
SET cover_url = 'https://example.test/kept.jpg',
    duration_ms = 111000,
    track_number = 2,
    release_date = '1999-12-31',
    explicit = 0
WHERE queue_uuid = 'kept-queue';";
            await seed.ExecuteNonQueryAsync();
        }

        // A second startup pass must leave every populated value alone.
        var rerun = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await rerun.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT cover_url, duration_ms, track_number, release_date, explicit
FROM manual_unavailable_track
WHERE queue_uuid = 'kept-queue';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("https://example.test/kept.jpg", reader.GetString(0));
        Assert.Equal(111000, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal("1999-12-31", reader.GetString(3));
        Assert.Equal(0, reader.GetInt32(4));
    }

    [Fact]
    public async Task EnsureSchema_LeavesMalformedManualUnavailablePayloadUntouched()
    {
        await CreateLegacyManualUnavailableTableAsync("broken-queue", "{ not json ");
        await CreateLegacyManualUnavailableTableAsync("empty-queue", "");
        // Valid JSON, but none of the metadata keys. Nothing may be invented from it.
        await CreateLegacyManualUnavailableTableAsync("bare-queue", @"{""title"":""Just A Title""}");

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT queue_uuid, cover_url, duration_ms, track_number, track_total, disc_number, disc_total, release_date, explicit
FROM manual_unavailable_track
WHERE queue_uuid IN ('broken-queue', 'empty-queue', 'bare-queue')
ORDER BY queue_uuid;";
        await using var reader = await command.ExecuteReaderAsync();
        var seen = 0;
        while (await reader.ReadAsync())
        {
            seen++;
            for (var ordinal = 1; ordinal < 9; ordinal++)
            {
                Assert.True(
                    await reader.IsDBNullAsync(ordinal),
                    $"column {ordinal} must stay unknown for {reader.GetString(0)}");
            }
        }

        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task ManualUnavailableRepository_RoundTripsNormalizedMetadata()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        var repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);

        var upserted = await repository.UpsertManualUnavailableTrackAsync(new ManualUnavailableTrackUpsertInput(
            QueueUuid: "round-trip-queue",
            Title: "Round Trip",
            Artist: "Round Artist",
            Album: "Round Album",
            AlbumArtist: "Round Album Artist",
            CoverUrl: "https://example.test/round-trip.jpg",
            DurationMs: 205000,
            TrackNumber: 4,
            TrackTotal: 12,
            DiscNumber: 2,
            DiscTotal: 3,
            ReleaseDate: "2025-07-18",
            Explicit: true,
            Isrc: "USSM12345678",
            Engine: "deezer",
            SourceService: "deezer",
            SourceUrl: "https://example.test/round-trip",
            DeezerId: "deezer-1",
            SpotifyId: "spotify-1",
            AppleId: null,
            QobuzId: null,
            TidalId: null,
            AmazonId: null,
            DestinationFolderId: 7,
            ExpectedFinalPath: "/music/Round Trip.flac",
            Quality: "FLAC",
            ContentType: "music",
            Reason: "unavailable",
            PayloadJson: @"{""Cover"":""https://example.test/round-trip.jpg""}"));

        Assert.NotNull(upserted);
        AssertManualUnavailableMetadata(upserted!, "https://example.test/round-trip.jpg");

        // Reading back through the query projections, not just the RETURNING clause.
        var all = await repository.GetManualUnavailableTracksAsync();
        var listed = Assert.Single(all, item => item.QueueUuid == "round-trip-queue");
        AssertManualUnavailableMetadata(listed, "https://example.test/round-trip.jpg");

        // The due-retries projection has to carry the same values as the plain listing.
        var due = await repository.GetDueManualUnavailableTracksAsync(DateTimeOffset.UtcNow.AddDays(30), 50);
        var dueTrack = Assert.Single(due, item => item.QueueUuid == "round-trip-queue");
        AssertManualUnavailableMetadata(dueTrack, "https://example.test/round-trip.jpg");
    }

    [Fact]
    public async Task ManualUnavailableRepository_UpsertRefreshesNormalizedMetadata()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        var repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);

        await repository.UpsertManualUnavailableTrackAsync(CreateUpsertInput(
            queueUuid: "refresh-queue",
            coverUrl: "https://example.test/stale.jpg",
            durationMs: 1000,
            trackNumber: 1,
            trackTotal: 2,
            discNumber: 1,
            discTotal: 1,
            releaseDate: "2000-01-01",
            @explicit: false));

        // A later failure of the same queue row must publish the metadata the newest payload carried,
        // not leave the first, poorer observation on screen.
        var refreshed = await repository.UpsertManualUnavailableTrackAsync(CreateUpsertInput(
            queueUuid: "refresh-queue",
            coverUrl: "https://example.test/fresh.jpg",
            durationMs: 205000,
            trackNumber: 4,
            trackTotal: 12,
            discNumber: 2,
            discTotal: 3,
            releaseDate: "2025-07-18",
            @explicit: true));

        Assert.NotNull(refreshed);
        Assert.Equal("https://example.test/fresh.jpg", refreshed!.CoverUrl);
        Assert.Equal(205000, refreshed.DurationMs);
        Assert.Equal(4, refreshed.TrackNumber);
        Assert.Equal(12, refreshed.TrackTotal);
        Assert.Equal(2, refreshed.DiscNumber);
        Assert.Equal(3, refreshed.DiscTotal);
        Assert.Equal("2025-07-18", refreshed.ReleaseDate);
        Assert.True(refreshed.Explicit);

        var reread = Assert.Single(
            await repository.GetManualUnavailableTracksAsync(),
            item => item.QueueUuid == "refresh-queue");
        Assert.Equal("https://example.test/fresh.jpg", reread.CoverUrl);
        Assert.Equal("2025-07-18", reread.ReleaseDate);
        Assert.True(reread.Explicit);
    }

    [Fact]
    public async Task ManualUnavailableRepository_PreservesUnknownExplicitAsNull()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        var repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);

        await repository.UpsertManualUnavailableTrackAsync(CreateUpsertInput(
            queueUuid: "unknown-explicit-queue",
            coverUrl: "https://example.test/clean.jpg",
            durationMs: 205000,
            trackNumber: 4,
            trackTotal: 12,
            discNumber: 2,
            discTotal: 3,
            releaseDate: "2025-07-18",
            @explicit: null));

        var stored = Assert.Single(
            await repository.GetManualUnavailableTracksAsync(),
            item => item.QueueUuid == "unknown-explicit-queue");

        // Not supplied upstream is not "false". Reading it back as false would file a clean track
        // under an explicit rating nobody reported.
        Assert.Null(stored.Explicit);
        Assert.Equal(205000, stored.DurationMs);
        Assert.Equal(4, stored.TrackNumber);
        Assert.Equal("2025-07-18", stored.ReleaseDate);
    }

    private static void AssertManualUnavailableMetadata(ManualUnavailableTrackDto track, string expectedCoverUrl)
    {
        Assert.Equal(expectedCoverUrl, track.CoverUrl);
        Assert.Equal(205000, track.DurationMs);
        Assert.Equal(4, track.TrackNumber);
        Assert.Equal(12, track.TrackTotal);
        Assert.Equal(2, track.DiscNumber);
        Assert.Equal(3, track.DiscTotal);
        Assert.Equal("2025-07-18", track.ReleaseDate);
        Assert.True(track.Explicit);

        // The columns sitting either side of the new block must not have shifted.
        Assert.Equal("Round Album", track.Album);
        Assert.Equal("Round Album Artist", track.AlbumArtist);
        Assert.Equal("USSM12345678", track.Isrc);
        Assert.Equal("deezer", track.Engine);
        Assert.Equal("deezer", track.SourceService);
        Assert.Equal("https://example.test/round-trip", track.SourceUrl);
        Assert.Equal("deezer-1", track.DeezerId);
        Assert.Equal("spotify-1", track.SpotifyId);
        Assert.Null(track.AppleId);
        Assert.Equal(7, track.DestinationFolderId);
        Assert.Equal("/music/Round Trip.flac", track.ExpectedFinalPath);
        Assert.Equal("FLAC", track.Quality);
        Assert.Equal("music", track.ContentType);
        Assert.Equal("unavailable", track.Reason);
        Assert.NotNull(track.PayloadJson);

        // The four trailing timestamps sit after payload_json and must still land in their own
        // columns; a single extra column anywhere would silently shift one of these.
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(track.FirstUnavailableAtUtc, now.AddMinutes(-5), now.AddMinutes(5));
        Assert.InRange(track.AddedAtUtc, now.AddMinutes(-5), now.AddMinutes(5));
        Assert.InRange(track.UpdatedAtUtc, now.AddMinutes(-5), now.AddMinutes(5));
        Assert.InRange(track.NextRetryAtUtc, now.AddDays(7).AddMinutes(-5), now.AddDays(7).AddMinutes(5));
    }

    private static ManualUnavailableTrackUpsertInput CreateUpsertInput(
        string queueUuid,
        string? coverUrl,
        int? durationMs,
        int? trackNumber,
        int? trackTotal,
        int? discNumber,
        int? discTotal,
        string? releaseDate,
        bool? @explicit)
        => new(
            QueueUuid: queueUuid,
            Title: "Metadata Track",
            Artist: "Metadata Artist",
            Album: "Metadata Album",
            AlbumArtist: "Metadata Album Artist",
            CoverUrl: coverUrl,
            DurationMs: durationMs,
            TrackNumber: trackNumber,
            TrackTotal: trackTotal,
            DiscNumber: discNumber,
            DiscTotal: discTotal,
            ReleaseDate: releaseDate,
            Explicit: @explicit,
            Isrc: "USSM12345678",
            Engine: "deezer",
            SourceService: "deezer",
            SourceUrl: "https://example.test/metadata",
            DeezerId: "deezer-1",
            SpotifyId: "spotify-1",
            AppleId: null,
            QobuzId: null,
            TidalId: null,
            AmazonId: null,
            DestinationFolderId: 7,
            ExpectedFinalPath: "/music/Metadata Track.flac",
            Quality: "FLAC",
            ContentType: "music",
            Reason: "unavailable",
            PayloadJson: "{}");

    [Fact]
    public async Task EnsureSchema_DoesNotBackfillTheQueueArtworkPlaceholderAsTrackArtwork()
    {
        // QueuePayloadBuilder writes this path into a payload's "cover" whenever no artwork was found.
        // It is a presentation placeholder, so repairing a record with it would put the
        // "Unavailable Tracks" playlist image on an individual track row.
        await CreateLegacyManualUnavailableTableAsync(
            "placeholder-only-queue",
            @"{""Cover"":""/images/unavailable/unavailable.jpg"",""DurationSeconds"":205}");
        await CreateLegacyManualUnavailableTableAsync(
            "placeholder-then-real-queue",
            @"{""cover"":""/images/default-cover.png"",""albumCover"":""https://example.test/real.jpg""}");

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT queue_uuid, cover_url
FROM manual_unavailable_track
WHERE queue_uuid IN ('placeholder-only-queue', 'placeholder-then-real-queue')
ORDER BY queue_uuid;";
        await using var reader = await command.ExecuteReaderAsync();
        var covers = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            covers.Add(
                reader.GetString(0),
                await reader.IsDBNullAsync(1) ? null : reader.GetString(1));
        }

        Assert.Equal(2, covers.Count);
        Assert.Null(covers["placeholder-only-queue"]);
        Assert.Equal("https://example.test/real.jpg", covers["placeholder-then-real-queue"]);
    }

    [Fact]
    public async Task EnsureSchema_PreservesStoredZeroAndNegativeMetadata()
    {
        // The model distinguishes "not supplied" (NULL) from a supplied value, so a committed 0 or
        // negative number is a fact and a repair must not revise it.
        await CreateLegacyManualUnavailableTableAsync(
            "zero-queue",
            @"{""Cover"":""https://example.test/payload.jpg"",""DurationSeconds"":205," +
            @"""TrackNumber"":4,""TrackTotal"":12,""DiscNumber"":2,""DiscTotal"":3," +
            @"""ReleaseDate"":""2025-07-18"",""Explicit"":true}");

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        // Commit zeros and a negative alongside the still-absent fields.
        await using (var seedConnection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await seedConnection.OpenAsync();
            await using var seed = seedConnection.CreateCommand();
            seed.CommandText = @"
UPDATE manual_unavailable_track
SET track_number = 0,
    duration_ms = 0,
    disc_total = -1,
    explicit = 0,
    cover_url = ''
WHERE queue_uuid = 'zero-queue';";
            await seed.ExecuteNonQueryAsync();
        }

        var rerun = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await rerun.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT track_number, duration_ms, disc_total, explicit, cover_url
FROM manual_unavailable_track
WHERE queue_uuid = 'zero-queue';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(-1, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));

        // An empty-string cover is still "no cover stored", so it is the one column a repair may fill.
        Assert.Equal("https://example.test/payload.jpg", reader.GetString(4));
    }

    [Fact]
    public async Task EnsureSchema_BackfillsManualUnavailableMetadataTheSameWayTheLivePathReadsIt()
    {
        // Every case the live path resolves, driven through the legacy repair instead. The two used to
        // disagree: the repair ignored numbers written as strings, and rounded fractional ones.
        await CreateLegacyManualUnavailableTableAsync(
            "string-number-queue",
            @"{""TrackNumber"":""12"",""DurationSeconds"":""205"",""DiscTotal"":""9""}");
        await CreateLegacyManualUnavailableTableAsync(
            "fractional-queue",
            @"{""TrackNumber"":4.7,""DurationSeconds"":205.5,""DiscTotal"":2.5}");

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT t.queue_uuid, t.track_number, t.duration_ms, t.disc_total
FROM manual_unavailable_track t
WHERE t.queue_uuid IN ('string-number-queue', 'fractional-queue')
ORDER BY t.queue_uuid;";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new Dictionary<string, (int? TrackNumber, int? DurationMs, int? DiscTotal)>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            rows.Add(
                reader.GetString(0),
                (
                    await reader.IsDBNullAsync(1) ? null : reader.GetInt32(1),
                    await reader.IsDBNullAsync(2) ? null : reader.GetInt32(2),
                    await reader.IsDBNullAsync(3) ? null : reader.GetInt32(3)));
        }

        Assert.Equal(2, rows.Count);

        // A whole number written as a string is a real number and must not be dropped.
        Assert.Equal(12, rows["string-number-queue"].TrackNumber);
        Assert.Equal(205000, rows["string-number-queue"].DurationMs);
        Assert.Equal(9, rows["string-number-queue"].DiscTotal);

        // A fractional value states no whole number, so it must be left unknown rather than rounded.
        Assert.Null(rows["fractional-queue"].TrackNumber);
        Assert.Null(rows["fractional-queue"].DurationMs);
        Assert.Null(rows["fractional-queue"].DiscTotal);
    }

    private async Task CreateLegacyManualUnavailableTableAsync(string queueUuid, string payloadJson)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
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
    added_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
INSERT INTO manual_unavailable_track (
    queue_uuid, title, artist, payload_json,
    first_unavailable_at_utc, added_at_utc, updated_at_utc)
VALUES (
    $queueUuid, 'Legacy Track', 'Legacy Artist', $payloadJson,
    '2026-07-01T00:00:00+00:00', '2026-07-01T00:00:00+00:00', '2026-07-01T00:00:00+00:00');";
        command.Parameters.AddWithValue("$queueUuid", queueUuid);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    [Fact]
    public async Task AddWatchlistHistoryAsync_ReturnsInsertedEntry_And_SinceQueryReturnsNewerRows()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        var repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);

        var first = await repository.AddWatchlistHistoryAsync(new WatchlistHistoryInsert(
            " spotify ",
            "playlist",
            " one ",
            "One",
            "playlist",
            2,
            "queued",
            ArtistName: null));
        var second = await repository.AddWatchlistHistoryAsync(new WatchlistHistoryInsert(
            "spotify",
            "playlist",
            "two",
            "Two",
            "playlist",
            3,
            "queued",
            ArtistName: null));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("spotify", first!.Source);
        Assert.Equal("one", first.SourceId);
        Assert.Equal(TimeSpan.Zero, first.CreatedAt.Offset);

        var newer = await repository.GetWatchlistHistorySinceAsync(first.Id, 50);

        var single = Assert.Single(newer);
        Assert.Equal(second!.Id, single.Id);
        Assert.Equal("two", single.SourceId);
    }

    [Fact]
    public async Task GetWatchlistHistoryAsync_TreatsLegacyOffsetlessTimestampAsUtc()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        var repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO watchlist_history (source, watch_type, source_id, name, collection_type, track_count, status, created_at)
VALUES ('spotify', 'playlist', 'legacy', 'Legacy', 'playlist', 1, 'queued', '2026-05-23 12:34:56');";
            await command.ExecuteNonQueryAsync();
        }

        var history = await repository.GetWatchlistHistoryAsync(10, 0);
        var entry = Assert.Single(history);
        Assert.Equal("legacy", entry.SourceId);
        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
        Assert.Equal(2026, entry.CreatedAt.Year);
        Assert.Equal(5, entry.CreatedAt.Month);
        Assert.Equal(23, entry.CreatedAt.Day);
        Assert.Equal(12, entry.CreatedAt.Hour);
        Assert.Equal(34, entry.CreatedAt.Minute);
        Assert.Equal(56, entry.CreatedAt.Second);
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name;";
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }
}
