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
        _root = Path.Join(Path.GetTempPath(), "deezspotag-folder-binding-" + Path.GetRandomFileName());
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
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task FolderTargetLibraryBindings_RoundTrip_AndResolveBySource()
    {
        var folder = await _repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            RootPath: "/music/gold",
            DisplayName: "Gold",
            Enabled: true,
            LibraryName: "Gold",
            DesiredQuality: "27",
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null,
            AutoTagProfileId: "profile-1",
            PlexSectionId: " 4 ",
            JellyfinLibraryId: "jellyfin-gold",
            NavidromeLibraryId: "navidrome-gold"));

        Assert.Equal("4", folder.PlexSectionId);
        Assert.Equal("jellyfin-gold", folder.JellyfinLibraryId);
        Assert.Equal("navidrome-gold", folder.NavidromeLibraryId);

        var configured = await _repository.GetConfiguredEnabledMusicFoldersAsync();
        Assert.Contains(configured, candidate => candidate.Id == folder.Id);
        Assert.Equal(folder.Id, (await _repository.ResolveConfiguredFolderAsync("plex", "4"))?.Id);
        Assert.Equal(folder.Id, (await _repository.ResolveConfiguredFolderAsync("JELLYFIN", "jellyfin-gold"))?.Id);
        Assert.Equal(folder.Id, (await _repository.ResolveConfiguredFolderAsync("navidrome", "navidrome-gold"))?.Id);

        var updated = await _repository.UpdateFolderTargetLibrariesAsync(
            folder.Id,
            " 9 ",
            string.Empty,
            "navidrome-new");
        Assert.NotNull(updated);
        Assert.Equal("9", updated!.PlexSectionId);
        Assert.Null(updated.JellyfinLibraryId);
        Assert.Equal("navidrome-new", updated.NavidromeLibraryId);
    }

    [Fact]
    public async Task HistoryBackfill_BindsUnambiguousTrackToFolder_AndFolderScopedReads()
    {
        var folder = await _repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            RootPath: "/music/scoped",
            DisplayName: "Scoped",
            Enabled: true,
            LibraryName: "Scoped",
            DesiredQuality: "27",
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null,
            AutoTagProfileId: "profile-1"));
        Assert.NotNull(folder.LibraryId);

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO artist (id, name) VALUES (101, 'Artist');
INSERT INTO album (id, artist_id, title) VALUES (102, 101, 'Album');
INSERT INTO track (id, album_id, title) VALUES (103, 102, 'Track');
INSERT INTO audio_file (id, path, relative_path, folder_id)
VALUES (104, '/music/scoped/Artist/Album/Track.flac', 'Artist/Album/Track.flac', @folderId);
INSERT INTO track_local (track_id, audio_file_id) VALUES (103, 104);";
            command.Parameters.AddWithValue("folderId", folder.Id);
            await command.ExecuteNonQueryAsync();
        }

        var userId = await _repository.EnsurePlexUserAsync("user", "user-1", "http://plex.test", null);
        var playedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.True(await _repository.AddPlayHistoryAsync(new LibraryRepository.PlayHistoryWriteInput(
            PlexUserId: userId,
            LibraryId: null,
            TrackId: 103,
            PlexTrackKey: "/library/metadata/103",
            PlexRatingKey: "103",
            PlayedAtUtc: playedAt,
            DurationMs: 180000,
            MetadataJson: null)));

        Assert.Equal(1, await _repository.BackfillPlayHistoryLibraryIdsAsync());
        var scope = await _repository.GetFolderScopeForTrackAsync(103);
        Assert.Equal(new FolderLibraryScopeDto(folder.Id, folder.LibraryId.Value), scope);

        var entries = await _repository.GetPlayHistoryEntriesAsync(
            userId,
            folder.LibraryId.Value,
            playedAt.AddDays(-1),
            [10],
            playedAt.AddDays(1),
            folderId: folder.Id);
        Assert.Single(entries);

        var localTimeEntries = await _repository.GetPlayHistoryEntriesAsync(
            userId,
            folder.LibraryId.Value,
            playedAt.AddDays(-1),
            [13],
            playedAt.AddDays(1),
            folderId: folder.Id,
            localUtcOffset: TimeSpan.FromHours(3));
        Assert.Single(localTimeEntries);

        await using var verify = new SqliteConnection($"Data Source={_dbPath}");
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT library_id, folder_id FROM play_history LIMIT 1;";
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(folder.LibraryId.Value, reader.GetInt64(0));
        Assert.Equal(folder.Id, reader.GetInt64(1));
    }

    [Fact]
    public async Task EnsureSchema_AddsBindingAndHistoryScopeColumnsToExistingDatabase()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
DROP INDEX IF EXISTS idx_play_history_folder;
DROP INDEX IF EXISTS idx_play_history_user_folder_time;
ALTER TABLE folder DROP COLUMN plex_section_id;
ALTER TABLE folder DROP COLUMN jellyfin_library_id;
ALTER TABLE folder DROP COLUMN navidrome_library_id;
ALTER TABLE play_history DROP COLUMN folder_id;";
            await command.ExecuteNonQueryAsync();
        }

        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

        await using var migrated = new SqliteConnection($"Data Source={_dbPath}");
        await migrated.OpenAsync();
        Assert.True(await ColumnExistsAsync(migrated, "folder", "plex_section_id"));
        Assert.True(await ColumnExistsAsync(migrated, "folder", "jellyfin_library_id"));
        Assert.True(await ColumnExistsAsync(migrated, "folder", "navidrome_library_id"));
        Assert.True(await ColumnExistsAsync(migrated, "play_history", "folder_id"));
        Assert.True(await IndexExistsAsync(migrated, "idx_play_history_folder"));
        Assert.True(await IndexExistsAsync(migrated, "idx_play_history_user_folder_time"));
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
