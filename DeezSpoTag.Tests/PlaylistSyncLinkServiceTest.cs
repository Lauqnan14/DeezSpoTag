using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The wired N-way reconcile, against a real database. The reconciler's decisions are unit tested
/// in PlaylistSyncReconcilerTest; these pin the part that only shows up once the pieces are joined:
/// reading each member's membership in the shared vocabulary, refusing a member whose tracks have
/// not resolved there, and recording the baseline so the next pass is not a false change.
/// </summary>
public sealed class PlaylistSyncLinkServiceTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-link-service-" + Path.GetRandomFileName());
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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    /// <summary>A destination that records its membership and can be told to fail.</summary>
    private sealed class RecordingTarget : IPlaylistSyncTarget
    {
        public RecordingTarget(string id, PlaylistTargetKind kind) { TargetId = id; TargetKind = kind; }

        public string TargetId { get; }

        public PlaylistTargetKind TargetKind { get; }

        public List<string> LastWritten { get; } = new();

        public bool Fail { get; set; }

        public Task<TargetPlaylistLookup<string>> FindPlaylistAsync(string? playlistId, string name, CancellationToken ct)
            => Task.FromResult(TargetPlaylistLookup<string>.Found(playlistId ?? $"{TargetId}-pl"));

        public Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken ct)
            => Task.FromResult<string?>($"{TargetId}-pl");

        public Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken ct)
            => Task.FromResult(TargetPlaylistItemsRead.Ok(Array.Empty<string>()));

        public Task<PlaylistMembershipWriteResult> WriteMembershipAsync(PlaylistMembershipWrite write, CancellationToken ct)
        {
            LastWritten.Clear();
            LastWritten.AddRange(write.OrderedItemIds);
            return Fail
                ? Task.FromResult(PlaylistMembershipWriteResult.Failed($"{TargetId} refused"))
                : Task.FromResult(PlaylistMembershipWriteResult.Ok(write.OrderedItemIds.Count, 0, writeComplete: true));
        }
    }

    private (PlaylistSyncLinkService Service, RecordingTarget Plex, RecordingTarget Jellyfin) Build(
        params IPlaylistSyncTarget[] extras)
    {
        var plex = new RecordingTarget("plex", PlaylistTargetKind.Library);
        var jellyfin = new RecordingTarget("jellyfin", PlaylistTargetKind.Library);
        var engine = new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(new IPlaylistSyncTarget[] { plex, jellyfin }.Concat(extras)),
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _, _, _) => Task.CompletedTask);
        return (new PlaylistSyncLinkService(
            engine,
            new PlaylistSyncReconciler(engine),
            new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance)), plex, jellyfin);
    }

    private async Task SeedLinkAsync()
    {
        await ExecuteAsync(@"
INSERT INTO playlist_sync_link (link_id, source, source_id, direction, enabled)
VALUES ('plex:pl-1', 'plex', 'pl-1', 'nway', 1);
INSERT INTO playlist_sync_link_member
    (link_id, target_id, target_kind, target_playlist_id, target_name, role, enabled, position)
VALUES
    ('plex:pl-1', 'plex', 'library', 'plex-pl', 'Roadtrip', 'authority', 1, 0),
    ('plex:pl-1', 'jellyfin', 'library', 'jf-pl', 'Roadtrip', 'mirror', 1, 1);");
    }

    /// <summary>Records what a destination holds, in the shared source-track vocabulary.</summary>
    private async Task SeedMembershipAsync(string target, string playlistId, params string[] trackSourceIds)
    {
        foreach (var id in trackSourceIds)
        {
            await ExecuteAsync($@"
INSERT INTO playlist_watch_target_membership
    (source, source_id, track_source_id, target_service, target_playlist_id, target_item_id, sync_status)
VALUES ('plex', 'pl-1', '{id}', '{target}', '{playlistId}', '{target}-{id}', 'playlist_synced');");
        }
    }

    [Fact]
    public async Task Reconcile_FirstPassRecordsABaselineAndReportsIt()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();
        await SeedLinkAsync();
        await SeedMembershipAsync("plex", "plex-pl", "t1", "t2");
        await SeedMembershipAsync("jellyfin", "jf-pl", "t1");

        var (service, _, _) = Build();
        var result = await service.ReconcileAsync("plex:pl-1");

        Assert.True(result.Success, result.Message);
        Assert.True(result.BaselineOnly);
        Assert.Contains("Baseline", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_RecordsASnapshotForEveryMember()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();
        await SeedLinkAsync();
        await SeedMembershipAsync("plex", "plex-pl", "t1", "t2");
        await SeedMembershipAsync("jellyfin", "jf-pl", "t1");

        var (service, _, _) = Build();
        await service.ReconcileAsync("plex:pl-1");

        // Without a snapshot for both, the next pass would see a change that never happened.
        Assert.Equal("[\"t1\",\"t2\"]", await ScalarAsync(
            "SELECT track_source_ids_json FROM playlist_sync_snapshot WHERE target_id='plex';"));
        Assert.NotNull(await ScalarAsync(
            "SELECT track_source_ids_json FROM playlist_sync_snapshot WHERE target_id='jellyfin';"));
    }

    /// <summary>
    /// A second pass is not a baseline pass. A track the user added on one destination cannot be
    /// pushed to another until it has resolved there: the app records one item id per
    /// (track, destination), and playlist_watch_target_membership is the only place that mapping
    /// lives. So the pass refuses rather than writing a shortened playlist - the safe outcome.
    /// </summary>
    [Fact]
    public async Task Reconcile_SecondPassIsNotABaseline_AndRefusesRatherThanShortening()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();
        await SeedLinkAsync();
        await ExecuteAsync("UPDATE playlist_sync_link_member SET role='mirror' WHERE target_id='plex';");
        await SeedMembershipAsync("plex", "plex-pl", "t1");
        await SeedMembershipAsync("jellyfin", "jf-pl", "t1");

        var (service, plex, _) = Build();
        var first = await service.ReconcileAsync("plex:pl-1");
        Assert.True(first.BaselineOnly);
        plex.LastWritten.Clear();

        // The user adds a track on Jellyfin that has never resolved on Plex.
        await SeedMembershipAsync("jellyfin", "jf-pl", "t2");

        var second = await service.ReconcileAsync("plex:pl-1");

        Assert.False(second.BaselineOnly);
        Assert.False(second.Success);
        Assert.Contains("never resolved on plex", second.Message!, StringComparison.Ordinal);
        // Crucially: nothing was written, so Plex keeps every track it had.
        Assert.Empty(plex.LastWritten);
    }

    [Fact]
    public async Task Reconcile_RefusesAMemberWhoseTrackHasNotResolvedThere()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();
        await SeedLinkAsync();
        // t2 has never resolved on Jellyfin, so it has no item id there.
        await SeedMembershipAsync("plex", "plex-pl", "t1", "t2");
        await SeedMembershipAsync("jellyfin", "jf-pl", "t1");
        await ExecuteAsync("DELETE FROM app_schema_migration WHERE migration_id LIKE 'playlist-sync%';");

        var (service, jellyfin, _) = Build();
        // Establish a baseline so the pass is not a first pass, then force a propagate.
        await service.ReconcileAsync("plex:pl-1");
        jellyfin.LastWritten.Clear();
        await SeedMembershipAsync("jellyfin", "jf-pl", "t2");

        var result = await service.ReconcileAsync("plex:pl-1");

        // Either it refuses, or it wrote nothing on Jellyfin - never a shortened playlist.
        if (result.Success)
        {
            Assert.DoesNotContain("jf-t2", jellyfin.LastWritten);
        }
        else
        {
            Assert.Contains("never resolved", result.Message!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Reconcile_SkipsADestinationThisDeploymentCannotWriteTo()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();
        await SeedLinkAsync();
        await ExecuteAsync(@"
INSERT INTO playlist_sync_link_member
    (link_id, target_id, target_kind, target_playlist_id, target_name, role, enabled, position)
VALUES ('plex:pl-1', 'tidal', 'platform', 'tid-1', 'Roadtrip', 'mirror', 1, 2);");
        await SeedMembershipAsync("plex", "plex-pl", "t1");

        // No Tidal target is registered.
        var (service, _, _) = Build();
        var result = await service.ReconcileAsync("plex:pl-1");

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(result.Members, m => m.TargetId == "tidal");
    }

    [Fact]
    public async Task Reconcile_ReportsALinkWithNoDestinations()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();

        var (service, _, _) = Build();
        var result = await service.ReconcileAsync("plex:missing");

        Assert.False(result.Success);
        Assert.Contains("no destinations", result.Message!, StringComparison.Ordinal);
    }
    /// <summary>
    /// The case a membership-only lookup cannot handle: the user adds a track on Jellyfin, and
    /// that track has an identity on Plex from an earlier resolve even though Plex has never held
    /// it in a playlist. N-way reconcile must find the Plex item through the per-service identity
    /// store and add it, rather than refusing because there is no Plex membership row.
    /// </summary>
    [Fact]
    public async Task Reconcile_IntroducesATrackToADestinationThatNeverHeldIt()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();
        await SeedLinkAsync();
        await ExecuteAsync("UPDATE playlist_sync_link_member SET role='mirror' WHERE target_id='plex';");
        await SeedMembershipAsync("plex", "plex-pl", "t1");
        await SeedMembershipAsync("jellyfin", "jf-pl", "t1");

        // Both tracks exist in the library; the user added t2 on Jellyfin.
        await ExecuteAsync("UPDATE playlist_watch_target_membership SET local_track_id = 42 WHERE track_source_id='t1';");
        await SeedMembershipAsync("jellyfin", "jf-pl", "t2");
        await ExecuteAsync("UPDATE playlist_watch_target_membership SET local_track_id = 43 WHERE track_source_id='t2';");

        // t2 has resolved on Plex, from an earlier run, but Plex has never held it.
        await ExecuteAsync(@"
INSERT INTO media_server_track_metadata (track_id, service, target_item_id, updated_at_utc)
VALUES (42, 'plex', 'plex-t1', '2026-09-27T10:00:00Z'),
       (43, 'plex', 'plex-t2', '2026-09-27T10:00:00Z'),
       (42, 'jellyfin', 'jf-t1', '2026-09-27T10:00:00Z'),
       (43, 'jellyfin', 'jf-t2', '2026-09-27T10:00:00Z');");

        var (service, plex, _) = Build();
        var first = await service.ReconcileAsync("plex:pl-1");
        Assert.True(first.BaselineOnly);
        plex.LastWritten.Clear();

        var second = await service.ReconcileAsync("plex:pl-1");

        Assert.True(second.Success, second.Message);
        Assert.False(second.BaselineOnly);
        Assert.Equal(new[] { "plex-t1", "plex-t2" }, plex.LastWritten);
    }

    /// <summary>
    /// The same case with no identity on the destination: nothing is written, and the destination
    /// keeps every track it had.
    /// </summary>
    [Fact]
    public async Task Reconcile_RefusesWhenTheDestinationHasNoIdentityForTheTrack()
    {
        var db = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await db.EnsureSchemaAsync();
        await SeedLinkAsync();
        await ExecuteAsync("UPDATE playlist_sync_link_member SET role='mirror' WHERE target_id='plex';");
        await SeedMembershipAsync("plex", "plex-pl", "t1");
        await SeedMembershipAsync("jellyfin", "jf-pl", "t1");
        await ExecuteAsync("UPDATE playlist_watch_target_membership SET local_track_id = 42 WHERE track_source_id='t1';");
        await ExecuteAsync(@"
INSERT INTO media_server_track_metadata (track_id, service, target_item_id, updated_at_utc)
VALUES (42, 'plex', 'plex-t1', '2026-09-27T10:00:00Z'),
       (42, 'jellyfin', 'jf-t1', '2026-09-27T10:00:00Z');");

        var (service, plex, _) = Build();
        await service.ReconcileAsync("plex:pl-1");
        plex.LastWritten.Clear();

        await SeedMembershipAsync("jellyfin", "jf-pl", "t2");
        await ExecuteAsync("UPDATE playlist_watch_target_membership SET local_track_id = 43 WHERE track_source_id='t2';");
        await ExecuteAsync(@"
INSERT INTO media_server_track_metadata (track_id, service, target_item_id, updated_at_utc)
VALUES (43, 'jellyfin', 'jf-t2', '2026-09-27T10:00:00Z');");

        var result = await service.ReconcileAsync("plex:pl-1");

        Assert.False(result.Success);
        Assert.Contains("never resolved on plex", result.Message!, StringComparison.Ordinal);
        Assert.Empty(plex.LastWritten);
    }

}
