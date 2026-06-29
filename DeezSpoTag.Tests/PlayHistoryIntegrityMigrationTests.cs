using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlayHistoryIntegrityMigrationTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;

    public Task InitializeAsync()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-play-history-migration-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "library.db");
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
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnsureSchema_RebuildsCanonicalHistory_AndRejectsDuplicateEvents()
    {
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
DELETE FROM app_schema_migration WHERE migration_id = 'play-history-event-identity-v1';
INSERT INTO plex_user (id, username, plex_user_id, plex_server_url)
VALUES (1, 'user', 'user-1', 'http://plex.test');
INSERT INTO artist (id, name) VALUES (1, 'Artist');
INSERT INTO album (id, artist_id, title) VALUES (1, 1, 'Album');
INSERT INTO track (id, album_id, title) VALUES (42, 1, 'Track');
DROP TABLE play_history;
CREATE TABLE play_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    library_id BIGINT,
    plex_user_id BIGINT,
    track_id BIGINT,
    plex_track_key TEXT,
    plex_rating_key TEXT,
    played_at_utc TEXT NOT NULL,
    play_duration_ms INTEGER,
    source TEXT NOT NULL DEFAULT 'plex',
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (plex_user_id, plex_track_key, played_at_utc)
);
INSERT INTO play_history
    (plex_user_id, track_id, plex_track_key, plex_rating_key, played_at_utc, source)
VALUES
    (1, NULL, NULL, 'rating-1', '2026-06-01T10:00:00.0000000+00:00', 'plex'),
    (1, 42, NULL, 'rating-1', '2026-06-01T10:00:00.0000000+00:00', 'plex'),
    (1, NULL, NULL, NULL, '2026-06-01T11:00:00.0000000+00:00', 'plex');";
            await command.ExecuteNonQueryAsync();
        }

        await dbService.EnsureSchemaAsync();
        await dbService.EnsureSchemaAsync();

        await using var migrated = new SqliteConnection($"Data Source={_dbPath}");
        await migrated.OpenAsync();
        await using (var command = migrated.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*), track_id, event_key FROM play_history;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(42, reader.GetInt64(1));
            Assert.Equal("rating:rating-1|2026-06-01T10:00:00.0000000+00:00", reader.GetString(2));
        }

        Assert.True(await IndexExistsAsync(migrated, "idx_play_history_user_source_time"));
        Assert.True(await IndexExistsAsync(migrated, "idx_play_history_user_library_time"));
        Assert.True(await IndexExistsAsync(migrated, "idx_play_history_user_library_track_time"));
        Assert.False(await IndexExistsAsync(migrated, "idx_play_history_user"));
        Assert.False(await IndexExistsAsync(migrated, "idx_play_history_played_at"));
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name;";
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync() is not null;
    }
}
