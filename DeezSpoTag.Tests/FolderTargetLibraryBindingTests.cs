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

public sealed class FolderTargetLibraryBindingTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-automatic-scope-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Folder_Create_AssignsCanonicalLibraryImmediately_AndUpdatePreservesIt()
    {
        var folder = await AddFolderAsync("/music/gold", "Gold");

        Assert.NotNull(folder.LibraryId);
        Assert.Equal("Gold", folder.LibraryName);

        var updated = await _repository.UpdateFolderAsync(
            folder.Id,
            new LibraryRepository.FolderUpsertInput(
                "/music/gold", "Gold renamed", true, null, "27", false, null, null, "profile-2"));

        Assert.NotNull(updated);
        Assert.Equal(folder.LibraryId, updated!.LibraryId);
        Assert.Equal("Gold", updated.LibraryName);
    }

    [Fact]
    public async Task HistoryScope_UsesUniqueLocalPath_AndFailsClosedForDuplicateMetadata()
    {
        var first = await AddFolderAsync("/local/a", "Library A");
        var second = await AddFolderAsync("/local/b", "Library B");
        await SeedTrackAsync(101, first.Id, "/local/a/Artist/Album/Same.flac", "Artist/Album/Same.flac");
        await SeedTrackAsync(201, second.Id, "/local/b/Artist/Album/Same.flac", "Artist/Album/Same.flac");

        var pathMatch = await _repository.ResolveHistoryTrackScopeAsync(
            "/local/a/Artist/Album/Same.flac",
            new LibraryRepository.LibraryExistenceInput(null, "Same", "Artist", null),
            default);
        var ambiguous = await _repository.ResolveHistoryTrackScopeAsync(
            null,
            new LibraryRepository.LibraryExistenceInput(null, "Same", "Artist", null),
            default);

        Assert.True(pathMatch.Resolved);
        Assert.Equal(first.Id, pathMatch.FolderId);
        Assert.Equal(first.LibraryId, pathMatch.LibraryId);
        Assert.True(ambiguous.Ambiguous);
        Assert.Null(ambiguous.FolderId);
        Assert.Null(ambiguous.LibraryId);
    }

    [Fact]
    public async Task EnsureSchema_RemovesManualBindings_AndAddsAutomaticHistoryScope()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
ALTER TABLE folder ADD COLUMN plex_section_id TEXT;
ALTER TABLE folder ADD COLUMN jellyfin_library_id TEXT;
ALTER TABLE folder ADD COLUMN navidrome_library_id TEXT;
DROP INDEX IF EXISTS idx_play_history_remote_library_time;
ALTER TABLE play_history DROP COLUMN remote_library_id;";
            await command.ExecuteNonQueryAsync();
        }

        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

        await using var migrated = new SqliteConnection($"Data Source={_dbPath}");
        await migrated.OpenAsync();
        Assert.False(await ColumnExistsAsync(migrated, "folder", "plex_section_id"));
        Assert.False(await ColumnExistsAsync(migrated, "folder", "jellyfin_library_id"));
        Assert.False(await ColumnExistsAsync(migrated, "folder", "navidrome_library_id"));
        Assert.True(await ColumnExistsAsync(migrated, "play_history", "remote_library_id"));
        Assert.True(await IndexExistsAsync(migrated, "idx_play_history_remote_library_time"));
    }

    [Fact]
    public async Task HistoryRepair_CorrectsWrongScope_AndClearsCrossLibraryAmbiguity()
    {
        var first = await AddFolderAsync("/local/a", "Library A");
        var second = await AddFolderAsync("/local/b", "Library B");
        await SeedTrackAsync(301, first.Id, "/local/a/Artist/Album/Unique.flac", "Artist/Album/Unique.flac");
        await SeedTrackAsync(501, first.Id, "/local/a/Artist/Album/Only-A.flac", "Artist/Album/Only-A.flac");

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO audio_file (id, path, relative_path, folder_id)
VALUES (302, '/local/b/Artist/Album/Unique.flac', 'Artist/Album/Unique.flac', @secondFolderId);
INSERT INTO track_local (track_id, audio_file_id) VALUES (301, 302);
INSERT INTO plex_user (id, username, plex_user_id) VALUES (401, 'listener', 'listener');
INSERT INTO play_history
    (library_id, folder_id, plex_user_id, track_id, plex_track_key, event_key, played_at_utc, source)
VALUES
    (@wrongLibraryId, @wrongFolderId, 401, 301, 'remote-301', 'legacy-301', '2026-07-01T10:00:00Z', 'plex'),
    (@wrongLibraryId, @wrongFolderId, 401, 501, 'remote-501', 'legacy-501', '2026-07-01T11:00:00Z', 'plex');";
            command.Parameters.AddWithValue("secondFolderId", second.Id);
            command.Parameters.AddWithValue("wrongLibraryId", second.LibraryId!.Value);
            command.Parameters.AddWithValue("wrongFolderId", second.Id);
            await command.ExecuteNonQueryAsync();
        }

        await _repository.BackfillPlayHistoryLibraryIdsAsync();

        await using var verify = new SqliteConnection($"Data Source={_dbPath}");
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT track_id, library_id, folder_id FROM play_history ORDER BY track_id;";
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(await reader.IsDBNullAsync(1));
        Assert.True(await reader.IsDBNullAsync(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(501, reader.GetInt64(0));
        Assert.Equal(first.LibraryId!.Value, reader.GetInt64(1));
        Assert.Equal(first.Id, reader.GetInt64(2));
    }

    private Task<FolderDto> AddFolderAsync(string root, string name)
        => _repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            root, name, true, name, "27", false, null, null, "profile-1"));

    private async Task SeedTrackAsync(long id, long folderId, string path, string relativePath)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO artist (id, name) VALUES (@id, 'Artist');
INSERT INTO album (id, artist_id, title) VALUES (@id, @id, 'Album');
INSERT INTO track (id, album_id, title) VALUES (@id, @id, 'Same');
INSERT INTO audio_file (id, path, relative_path, folder_id) VALUES (@id, @path, @relativePath, @folderId);
INSERT INTO track_local (track_id, audio_file_id) VALUES (@id, @id);";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("path", path);
        command.Parameters.AddWithValue("relativePath", relativePath);
        command.Parameters.AddWithValue("folderId", folderId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @column;";
        command.Parameters.AddWithValue("column", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @index;";
        command.Parameters.AddWithValue("index", index);
        return await command.ExecuteScalarAsync() is not null;
    }
}
