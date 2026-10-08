using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The playlist sync link model. Ported from the reference implementation's PlaylistLink, which
/// stores its members as a provider_id -> playlist_id map so a new platform is data rather than
/// code. These pin the migration: an existing installation's per-server playlist id columns must
/// land in the generic member table so an already-synced playlist keeps pointing at the playlist
/// it created rather than creating a second one.
/// </summary>
public sealed class PlaylistSyncLinkMigrationTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-synclink-tests-" + Path.GetRandomFileName());
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

    private async Task<LibraryDbService> CreateServiceAsync()
        => new(_configuration, NullLogger<LibraryDbService>.Instance);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // A NULL column is a real state here (an unbound target has no playlist id yet),
            // so render it as empty rather than throwing on GetString.
            results.Add(string.Join(
                "|",
                Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? string.Empty : reader.GetString(i))));
        }

        return results;
    }

    [Fact]
    public async Task EnsureSchema_CreatesTheLinkAndMemberTables()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        var tables = await QueryAsync(@"
SELECT name FROM sqlite_master
WHERE type='table' AND name IN ('playlist_sync_link','playlist_sync_link_member')
ORDER BY name;");

        Assert.Equal(new[] { "playlist_sync_link", "playlist_sync_link_member" }, tables);

        var indexes = await QueryAsync(@"
SELECT name FROM sqlite_master
WHERE type='index' AND name IN ('idx_playlist_sync_link_source','idx_playlist_sync_link_member_target')
ORDER BY name;");

        Assert.Equal(new[] { "idx_playlist_sync_link_member_target", "idx_playlist_sync_link_source" }, indexes);
    }

    [Fact]
    public async Task Backfill_MovesEveryConfiguredTargetIntoAMember_RearingItsBoundPlaylistId()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        // An installation that already synced: two configured targets, one of them already bound.
        await ExecuteAsync(@"
INSERT INTO playlist_watch_preferences
    (source, source_id, service, sync_targets_json, sync_mode, plex_playlist_id, navidrome_playlist_id)
VALUES
    ('plex', 'pl-1', 'plex', '[""jellyfin"",""navidrome""]', 'mirror', 'plex-remote-1', 'nav-remote-1'),
    ('jellyfin', 'pl-2', 'jellyfin', '[""plex""]', 'append', NULL, NULL);");

        // The migration is marker-guarded, so clear it to replay the backfill over seeded data.
        await ExecuteAsync("DELETE FROM app_schema_migration WHERE migration_id='playlist-sync-link-v1';");

        await dbService.EnsureSchemaAsync();

        var links = await QueryAsync("SELECT source, source_id, direction FROM playlist_sync_link ORDER BY source;");
        Assert.Equal(2, links.Count);
        Assert.Contains(links, row => row == "jellyfin|pl-2|oneway");
        Assert.Contains(links, row => row == "plex|pl-1|oneway");

        var members = await QueryAsync(@"
SELECT link_id, target_id, COALESCE(target_playlist_id,''), sync_mode
FROM playlist_sync_link_member
ORDER BY link_id, target_id;");

        // The bound ids must survive, or the next pass would create a duplicate playlist.
        Assert.Contains(members, row => row == "plex:pl-1|jellyfin||mirror");
        Assert.Contains(members, row => row == "plex:pl-1|navidrome|nav-remote-1|mirror");
        // plex was configured as the service but is not in sync_targets_json, so the third
        // statement recovers the binding from the legacy column rather than dropping it.
        Assert.Contains(members, row => row == "plex:pl-1|plex|plex-remote-1|mirror");
        // Append is per member, taken from the source preference.
        Assert.Contains(members, row => row == "jellyfin:pl-2|plex||append");
    }

    [Fact]
    public async Task Backfill_IsIdempotent_SecondRunAddsNothing()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        await ExecuteAsync(@"
INSERT INTO playlist_watch_preferences (source, source_id, service, sync_targets_json, plex_playlist_id)
VALUES ('plex', 'pl-1', 'plex', '[""jellyfin"",""navidrome""]', 'plex-remote-1');");
        await ExecuteAsync("DELETE FROM app_schema_migration WHERE migration_id='playlist-sync-link-v1';");

        await dbService.EnsureSchemaAsync();
        var first = await QueryAsync("SELECT link_id, target_id FROM playlist_sync_link_member ORDER BY link_id, target_id;");

        // The marker is now set, so the normal path is a no-op.
        await dbService.EnsureSchemaAsync();
        var second = await QueryAsync("SELECT link_id, target_id FROM playlist_sync_link_member ORDER BY link_id, target_id;");

        Assert.Equal(first, second);
        Assert.Equal(3, second.Count);
    }

    [Fact]
    public async Task Backfill_SkipsPreferenceWithNoTargetsAndNoBoundIds()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        await ExecuteAsync(@"
INSERT INTO playlist_watch_preferences (source, source_id, service, sync_targets_json)
VALUES ('plex', 'pl-empty', 'plex', '[]'),
       ('plex', 'pl-null', 'plex', NULL);");

        var members = await QueryAsync("SELECT COUNT(*) FROM playlist_sync_link_member;");
        Assert.Equal("0", members.Single());
    }

    [Fact]
    public async Task MemberAcceptsAPlatformWithNoLegacyColumn_SoANewTargetIsDataNotASchemaChange()
    {
        // The reason this table exists: plex/jellyfin/navidrome each had a column and a branch in
        // the writer. A platform with no column must still be able to hold a binding.
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        await ExecuteAsync(@"
INSERT INTO playlist_watch_preferences (source, source_id, service, sync_targets_json)
VALUES ('plex', 'pl-1', 'plex', '[""ytmusic""]');
DELETE FROM app_schema_migration WHERE migration_id='playlist-sync-link-v1';");

        await dbService.EnsureSchemaAsync();

        var members = await QueryAsync(@"
SELECT target_id, target_playlist_id, sync_mode, role
FROM playlist_sync_link_member WHERE target_id='ytmusic';");

        Assert.Equal("ytmusic||mirror|mirror", members.Single());
    }

    [Fact]
    public async Task UpdateTargetPlaylistId_RoundTripsForAPlatformWithNoLegacyColumn()
    {
        // The defect this table exists to fix. The old writer had a switch over plex/jellyfin/
        // navidrome that returned null for anything else, so the id YouTube Music created was
        // written to nothing at all. The next sync therefore had no id to reuse and created
        // another playlist, every time.
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        var repository = CreateRepository();

        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync(
            "plex", "pl-1", "ytmusic", "yt-playlist-abc", CancellationToken.None);

        var resolved = await repository.GetPlaylistSyncTargetPlaylistIdAsync(
            "plex", "pl-1", "ytmusic", CancellationToken.None);

        Assert.Equal("yt-playlist-abc", resolved);
    }

    [Fact]
    public async Task UpdateTargetPlaylistId_StillWritesTheLegacyColumnForTheThreeThatHaveOne()
    {
        // The member table is the new source of truth, but readers that have not moved yet still
        // read these columns. Both must be written or a binding disappears for them.
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        var repository = CreateRepository();

        // The legacy column only exists on an existing preference row, which is the real flow:
        // PersistTargetPlaylistBindingAsync bails when the preference is null.
        await ExecuteAsync(@"
INSERT INTO playlist_watch_preferences (source, source_id, service, sync_targets_json)
VALUES ('plex', 'pl-1', 'plex', '[""jellyfin"",""navidrome""]');");

        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync("plex", "pl-1", "jellyfin", "jf-1", CancellationToken.None);
        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync("plex", "pl-1", "navidrome", "nav-1", CancellationToken.None);
        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync("plex", "pl-1", "plex", "px-1", CancellationToken.None);

        var columns = await QueryAsync(@"
SELECT plex_playlist_id, jellyfin_playlist_id, navidrome_playlist_id
FROM playlist_watch_preferences WHERE source='plex' AND source_id='pl-1';");

        Assert.Equal("px-1|jf-1|nav-1", columns.Single());
    }

    [Fact]
    public async Task UpdateTargetPlaylistId_OverwritesRatherThanDuplicating()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        var repository = CreateRepository();

        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync("plex", "pl-1", "ytmusic", "first", CancellationToken.None);
        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync("plex", "pl-1", "ytmusic", "second", CancellationToken.None);

        var count = await QueryAsync("SELECT COUNT(*) FROM playlist_sync_link_member WHERE target_id='ytmusic';");
        Assert.Equal("1", count.Single());

        var resolved = await repository.GetPlaylistSyncTargetPlaylistIdAsync(
            "plex", "pl-1", "ytmusic", CancellationToken.None);
        Assert.Equal("second", resolved);
    }

    [Fact]
    public async Task UpdateTargetPlaylistId_FallsBackToTheLegacyColumnWhenNoMemberRowExists()
    {
        // A binding written before the migration, or by a reader that has not moved, must still
        // resolve rather than look unbound and create a duplicate.
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        var repository = CreateRepository();

        await ExecuteAsync(@"
INSERT INTO playlist_watch_preferences (source, source_id, service, plex_playlist_id)
VALUES ('jellyfin', 'pl-9', 'plex', 'legacy-plex-id');");

        var resolved = await repository.GetPlaylistSyncTargetPlaylistIdAsync(
            "jellyfin", "pl-9", "plex", CancellationToken.None);

        Assert.Equal("legacy-plex-id", resolved);
    }

    [Fact]
    public async Task UpdateTargetPlaylistId_ClearsTheBindingWhenTheIdIsEmpty()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        var repository = CreateRepository();

        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync("plex", "pl-1", "ytmusic", "gone", CancellationToken.None);
        await repository.UpdatePlaylistWatchTargetPlaylistIdAsync("plex", "pl-1", "ytmusic", null, CancellationToken.None);

        var resolved = await repository.GetPlaylistSyncTargetPlaylistIdAsync(
            "plex", "pl-1", "ytmusic", CancellationToken.None);

        Assert.Null(resolved);
    }

    private LibraryRepository CreateRepository()
        => new(_configuration, NullLogger<LibraryRepository>.Instance);
    /// <summary>
    /// The link member must record whether the destination is a self-hosted library or a
    /// streaming platform. Without it a Plex playlist and a YouTube Music playlist are stored
    /// identically, and a link cannot tell a source-capable member from a destination-only one.
    /// </summary>
    [Fact]
    public async Task Backfill_RecordsTheKindOfEveryMember()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        await ExecuteAsync(@"
INSERT INTO playlist_watch_preferences (source, source_id, service, sync_targets_json)
VALUES ('plex', 'pl-1', 'plex', '[""jellyfin"",""navidrome"",""ytmusic""]');
DELETE FROM app_schema_migration WHERE migration_id='playlist-sync-link-v1';
DELETE FROM app_schema_migration WHERE migration_id='playlist-sync-target-kind-v2';");
        await dbService.EnsureSchemaAsync();

        var kinds = await QueryAsync(@"
SELECT target_id, target_kind FROM playlist_sync_link_member
ORDER BY target_id;");

        Assert.Equal(
            new[] { "jellyfin|library", "navidrome|library", "ytmusic|platform" },
            kinds);
    }

    /// <summary>
    /// A database that ran the first version of this migration has target_service and no kind.
    /// The v2 migration has to rename and backfill rather than fail or skip, or an existing
    /// install loses every destination binding.
    /// </summary>
    [Fact]
    public async Task Backfill_SurvivesAnInstallThatAlreadyRanTheV1Shape()
    {
        var dbService = await CreateServiceAsync();
        await dbService.EnsureSchemaAsync();

        await ExecuteAsync("DROP TABLE playlist_sync_link_member;");
        await ExecuteAsync(@"
CREATE TABLE playlist_sync_link_member (
    link_id TEXT NOT NULL,
    target_service TEXT NOT NULL,
    target_playlist_id TEXT,
    target_name TEXT,
    sync_mode TEXT NOT NULL DEFAULT 'mirror',
    role TEXT NOT NULL DEFAULT 'mirror',
    enabled INTEGER NOT NULL DEFAULT 1,
    position INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (link_id, target_service)
);
INSERT OR IGNORE INTO playlist_sync_link (link_id, source, source_id)
VALUES ('plex:pl-1', 'plex', 'pl-1');
INSERT INTO playlist_sync_link_member (link_id, target_service, target_playlist_id)
VALUES ('plex:pl-1', 'ytmusic', 'yt-1'), ('plex:pl-1', 'jellyfin', 'jf-1');
DELETE FROM app_schema_migration WHERE migration_id='playlist-sync-target-kind-v2';");
        await dbService.EnsureSchemaAsync();

        var rows = await QueryAsync(@"
SELECT target_id, target_kind, target_playlist_id FROM playlist_sync_link_member ORDER BY target_id;");

        Assert.Equal(new[] { "jellyfin|library|jf-1", "ytmusic|platform|yt-1" }, rows);
    }

}
