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

public sealed class LibraryDbWatchlistMigrationTests : IAsyncLifetime
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
