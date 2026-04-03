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

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name;";
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }
}
