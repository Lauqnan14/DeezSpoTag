using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NavidromeIdentityReconciliationTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "navidrome-identity-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
        }).Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_tempRoot, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReconciliationPersistsChangedIdAndLeavesUnchangedAndAmbiguousIdsIntact()
    {
        await SeedIdAsync(1, "old-id");
        await SeedIdAsync(2, "same-id");
        await SeedIdAsync(3, "ambiguous-a");
        await SeedIdAsync(4, "ambiguous-b");
        var before = await _repository.GetMediaServerItemIdsByTrackIdsAsync("navidrome", [1, 2, 3, 4]);
        var index = BuildIndex([
            new(1, "/music/Artist/One.flac", "Artist/One.flac", "One", "Artist", "Album", 120000, "old-id", 1000),
            new(2, "/music/Artist/Two.flac", "Artist/Two.flac", "Two", "Artist", "Album", 120000, "same-id", 1000),
            new(3, "/music/Artist/Shared.flac", "Artist/Shared.flac", "Shared", "Artist", "Album", 120000, "ambiguous-a", 1000),
            new(4, "/music/Artist/Shared.flac", "Artist/Shared.flac", "Shared", "Artist", "Album", 120000, "ambiguous-b", 1000)
        ]);

        var service = new MediaServerLibraryRefreshService(null!, null!, null!, null!, _repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var candidateType = typeof(MediaServerLibraryRefreshService).GetNestedType("TargetTrackIdentityCandidate", BindingFlags.NonPublic)!;
        var candidates = Array.CreateInstance(candidateType, 3);
        candidates.SetValue(Candidate(candidateType, "new-id", "/music/Artist/One.flac", "One"), 0);
        candidates.SetValue(Candidate(candidateType, "same-id", "/music/Artist/Two.flac", "Two"), 1);
        candidates.SetValue(Candidate(candidateType, "ambiguous-new", "/music/Artist/Shared.flac", "Shared"), 2);
        var ingest = typeof(MediaServerLibraryRefreshService).GetMethod("IngestTargetTracksAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)ingest.Invoke(service, ["navidrome", null, candidates, index, CancellationToken.None])!;

        var after = await _repository.GetMediaServerItemIdsByTrackIdsAsync("navidrome", [1, 2, 3, 4]);
        Assert.Equal("new-id", after[1]);
        Assert.Equal("same-id", after[2]);
        Assert.Equal("ambiguous-a", after[3]);
        Assert.Equal("ambiguous-b", after[4]);
        Assert.Equal(1, MediaServerRefreshOutboxService.CountChangedMappings(before, after));
        Assert.Equal(0, MediaServerRefreshOutboxService.CountChangedMappings(after, after));
    }

    [Fact]
    public async Task ReconciliationImportsCanonicalIdentityFromRequestedFolderOnly()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = @"
PRAGMA foreign_keys=OFF;
INSERT INTO folder(id,root_path,display_name,enabled,desired_quality_value) VALUES
    (7,'/music/stereo','Stereo',1,'flac'),
    (8,'/music/atmos','Atmos',1,'atmos');
INSERT INTO artist(id,name) VALUES(1,'Artist');
INSERT INTO album(id,artist_id,title) VALUES(1,1,'Album');
INSERT INTO track(id,album_id,title,duration_ms) VALUES(1902,1,'Song',120000);
INSERT INTO audio_file(id,path,relative_path,folder_id,size,duration_ms,quality_rank,audio_variant) VALUES
    (1,'/music/stereo/Artist/Album/old.flac','Artist/Album/old.flac',7,1000,120000,10,'stereo'),
    (2,'/music/stereo/Artist/Album/preferred.flac','Artist/Album/preferred.flac',7,2000,120000,20,'stereo'),
    (3,'/music/atmos/Artist/Album/song.m4a','Artist/Album/song.m4a',8,3000,120000,30,'atmos');
INSERT INTO track_local(track_id,audio_file_id) VALUES(1902,1),(1902,2),(1902,3);";
            await seed.ExecuteNonQueryAsync();
        }

        var scopedTracks = await _repository.GetTargetServerIdentityLocalTracksAsync("navidrome", 7);
        var selected = Assert.Single(scopedTracks);
        Assert.Equal("/music/stereo/Artist/Album/preferred.flac", selected.AbsolutePath);
        var before = await _repository.GetMediaServerItemIdsByTrackIdsAsync("navidrome", [1902]);
        var index = BuildIndex(scopedTracks);
        var service = new MediaServerLibraryRefreshService(null!, null!, null!, null!, _repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var candidateType = typeof(MediaServerLibraryRefreshService).GetNestedType("TargetTrackIdentityCandidate", BindingFlags.NonPublic)!;
        var candidates = Array.CreateInstance(candidateType, 1);
        candidates.SetValue(Candidate(candidateType, "navidrome-1902", "/music/stereo/Artist/Album/preferred.flac", "Song"), 0);
        var ingest = typeof(MediaServerLibraryRefreshService).GetMethod("IngestTargetTracksAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)ingest.Invoke(service, ["navidrome", 7L, candidates, index, CancellationToken.None])!;

        var after = await _repository.GetMediaServerItemIdsByTrackIdsAsync("navidrome", [1902]);
        Assert.Equal("navidrome-1902", after[1902]);
        Assert.Equal(1, MediaServerRefreshOutboxService.CountChangedMappings(before, after));
    }

    private async Task SeedIdAsync(long trackId, string targetId)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO media_server_track_metadata (track_id,service,target_item_id,updated_at_utc) VALUES (@id,'navidrome',@target,'2026-08-01T00:00:00Z');";
        command.Parameters.AddWithValue("id", trackId);
        command.Parameters.AddWithValue("target", targetId);
        await command.ExecuteNonQueryAsync();
    }

    private static object BuildIndex(IReadOnlyList<TargetServerIdentityLocalTrackDto> tracks)
    {
        var type = typeof(MediaServerLibraryRefreshService).GetNestedType("TargetIdentityLocalIndex", BindingFlags.NonPublic)!;
        return type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [tracks, null, null, true])!;
    }

    private static object Candidate(Type type, string id, string path, string title)
        => Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            binder: null, args: [id, path, title, "Artist", "Album", 120000, 1000L], culture: null)!;
}
