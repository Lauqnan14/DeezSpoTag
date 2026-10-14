using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
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

    [Fact]
    public async Task SuccessfulReconciliationRemovesOnlyUnseenUnambiguousIds()
    {
        await SeedIdAsync(1, "removed");
        await SeedIdAsync(2, "still-present");
        await SeedIdAsync(3, "ambiguous");
        var index = BuildIndex([
            new(1, "/music/One.flac", "One.flac", "One", "Artist", "Album", 120000, "removed", 1000),
            new(2, "/music/Two.flac", "Two.flac", "Two", "Artist", "Album", 120000, "still-present", 1000),
            new(3, "/music/Three.flac", "Three.flac", "Three", "Artist", "Album", 120000, "ambiguous", 1000)
        ]);
        ((HashSet<string>)index.GetType().GetProperty("ObservedTargetItemIds")!.GetValue(index)!).Add("still-present");
        ((HashSet<long>)index.GetType().GetProperty("AmbiguousTrackIds")!.GetValue(index)!).Add(3);
        var service = new MediaServerLibraryRefreshService(null!, null!, null!, null!, _repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var complete = typeof(MediaServerLibraryRefreshService).GetMethod("RemoveStaleTargetIdentitiesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)complete.Invoke(service, ["navidrome", index, CancellationToken.None])!;
        var after = await _repository.GetMediaServerItemIdsByTrackIdsAsync("navidrome", [1, 2, 3]);
        Assert.False(after.ContainsKey(1));
        Assert.Equal("still-present", after[2]);
        Assert.Equal("ambiguous", after[3]);
    }

    [Theory]
    [InlineData("plex", false)]
    [InlineData("jellyfin", false)]
    [InlineData("plex", true)]
    [InlineData("jellyfin", true)]
    public async Task OrdinaryFetchReconcilesCompleteCoverageButPreservesIdsOnFailure(string target, bool unavailable)
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO folder(id,root_path,display_name,enabled) VALUES(7,'/music','Music',1);
                INSERT INTO artist(id,name) VALUES(1,'Artist');
                INSERT INTO album(id,artist_id,title) VALUES(1,1,'Album');
                INSERT INTO track(id,album_id,title,duration_ms) VALUES(1,1,'Song',120000);
                INSERT INTO audio_file(id,path,relative_path,folder_id,size,duration_ms) VALUES(1,'/music/Song.flac','Song.flac',7,1000,120000);
                INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);
                """;
            await seed.ExecuteNonQueryAsync();
        }
        await _repository.UpsertMediaServerTrackMetadataAsync([new(1,target,"old-id","/music/Song.flac",DateTimeOffset.UtcNow)]);
        using var handler = new IdentityListingHandler(unavailable);
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(null!,
            new PlexApiClient(NullLogger<PlexApiClient>.Instance, http), new JellyfinApiClient(http),
            new NavidromeApiClient(http), _repository, NullLogger<MediaServerLibraryRefreshService>.Instance);
        var method = typeof(MediaServerLibraryRefreshService).GetMethod(
            target == "plex" ? "UpdatePlexTrackMetadataIndexAsync" : "UpdateJellyfinTrackMetadataIndexAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        object?[] args = target == "plex"
            ? [new PlexAuth {Url="http://server",Token="token"}, new List<PlexLibrarySection> { new() {Key="1",Title="Music",Type="artist"} },7L,null,CancellationToken.None]
            : [new JellyfinAuth {Url="http://server",ApiKey="token",UserId="user"},7L,null,CancellationToken.None];
        if (unavailable)
            await Assert.ThrowsAsync<HttpRequestException>(() => (Task)method.Invoke(service,args)!);
        else
            await (Task)method.Invoke(service,args)!;
        var mapped = await _repository.GetMediaServerItemIdsByTrackIdsAsync(target,[1]);
        Assert.Equal(unavailable ? "old-id" : "new-id",mapped[1]);
        Assert.True(handler.TrackRequests > 0);
    }

    private sealed class IdentityListingHandler(bool unavailable, bool timeout = false) : HttpMessageHandler
    {
        public int TrackRequests { get; private set; }
        public Func<Task>? BeforeTracks { get; set; }
        public bool ShortPages { get; set; }
        public bool RepeatPage { get; set; }
        public bool? Scanning { get; set; } = false;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var xml = path.StartsWith("/library/sections", StringComparison.Ordinal);
            string payload;
            if (path.Contains("auth/login", StringComparison.Ordinal))
                payload = """{"token":"token"}""";
            else if (path.Contains("VirtualFolders", StringComparison.Ordinal))
                payload = "[]";
            else if (path.Contains("ScheduledTasks", StringComparison.Ordinal))
                payload = Scanning is null ? "{}" : Scanning == true
                    ? """[{"Key":"RefreshLibrary","State":"Running"}]""" : "[]";
            else if (path == "/library/sections")
                payload = """<MediaContainer><Directory key="1" title="Music" type="artist" refreshing="0" /></MediaContainer>""";
            else if (path.Contains("getScanStatus", StringComparison.Ordinal))
                payload = """{"subsonic-response":{"status":"ok","scanStatus":{"scanning":false}}}""";
            else
            {
                TrackRequests++;
                if (BeforeTracks is not null) await BeforeTracks();
                if (timeout) throw new TaskCanceledException("Track listing timed out.");
                if (unavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var query = request.RequestUri.Query;
                var first = query.Contains("StartIndex=0", StringComparison.Ordinal)
                    || query.Contains("Container-Start=0", StringComparison.Ordinal)
                    || query.Contains("_start=0", StringComparison.Ordinal);
                var second = query.Contains("StartIndex=1", StringComparison.Ordinal)
                    || query.Contains("Container-Start=1", StringComparison.Ordinal)
                    || query.Contains("_start=1", StringComparison.Ordinal);
                var hasTrack = first || RepeatPage || (ShortPages && second);
                var other = ShortPages && second && !RepeatPage;
                var id = other ? "other-id" : "new-id";
                var file = other ? "/music/Other.flac" : "/music/Song.flac";
                if (xml)
                    payload = hasTrack ? $"<MediaContainer><Track ratingKey='{id}' title='Song' duration='120000'><Media><Part file='{file}' size='1000' /></Media></Track></MediaContainer>" : "<MediaContainer/>";
                else if (path.Contains("api/song", StringComparison.Ordinal))
                    payload = hasTrack ? $"[{{\"id\":\"{id}\",\"title\":\"Song\",\"path\":\"{file}\",\"duration\":120}}]" : "[]";
                else
                    payload = hasTrack ? $"{{\"Items\":[{{\"Id\":\"{id}\",\"Name\":\"Song\",\"Path\":\"{file}\",\"RunTimeTicks\":1200000000}}]}}" : "{\"Items\":[]}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, xml ? "application/xml" : "application/json")
            };
        }
    }

    [Fact]
    public async Task FailedFolderRefreshBecomesEligibleAgainAfterCooldown()
    {
        await SeedRefreshLibraryAsync();
        var auth = await CreateJellyfinAuthAsync();
        var service = new MediaServerLibraryRefreshService(auth,null!,null!,null!,_repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var outbox = new MediaServerRefreshOutboxService(_repository,service,NullLogger<MediaServerRefreshOutboxService>.Instance);
        await _repository.EnqueueMediaServerRefreshAsync(7,"jellyfin",["/music/Song.flac"],[1],TimeSpan.Zero);
        var job = Assert.Single(await _repository.ClaimDueMediaServerRefreshesAsync(1,TimeSpan.FromMinutes(5),"worker"));
        Assert.True(await _repository.FailMediaServerRefreshAsync(job.Id,"worker","offline"));
        var queue = typeof(MediaServerRefreshOutboxService).GetMethod("QueueMissingFolderIdentitiesAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)queue.Invoke(outbox,[CancellationToken.None])!;
        Assert.Equal("failed",(await _repository.GetMediaServerRefreshOutboxAsync(job.Id))!.Status);
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE media_server_refresh_outbox SET updated_at=datetime('now','-11 minutes');";
            await command.ExecuteNonQueryAsync();
        }
        await (Task)queue.Invoke(outbox,[CancellationToken.None])!;
        Assert.Equal("pending",(await _repository.GetMediaServerRefreshOutboxAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task RequestedTrackCompletionDoesNotWaitForUnrelatedMissingFolderTracks()
    {
        await SeedRefreshLibraryAsync();
        var auth = await CreateJellyfinAuthAsync();
        using var handler = new IdentityListingHandler(false);
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(auth,null!,new JellyfinApiClient(http),null!,_repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var outbox = new MediaServerRefreshOutboxService(_repository,service,NullLogger<MediaServerRefreshOutboxService>.Instance);
        var owner = (string)typeof(MediaServerRefreshOutboxService).GetField("_leaseOwner",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(outbox)!;
        await _repository.EnqueueMediaServerRefreshAsync(7,"jellyfin",["/music/Song.flac"],[1],TimeSpan.Zero);
        var job = Assert.Single(await _repository.ClaimDueMediaServerRefreshesAsync(1,TimeSpan.FromMinutes(5),owner));
        Assert.True(await _repository.MarkMediaServerRefreshScanSubmittedAsync(job.Id,owner,DateTimeOffset.UtcNow));
        var process = typeof(MediaServerRefreshOutboxService).GetMethod("ProcessJobAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)process.Invoke(outbox,[job,CancellationToken.None])!;
        Assert.Equal("completed",(await _repository.GetMediaServerRefreshOutboxAsync(job.Id))!.Status);
        var coverage = Assert.Single(await _repository.GetTargetServerIdentityCoverageAsync(["jellyfin"],7));
        Assert.Equal(1,coverage.MappedTracks);
        Assert.Equal(1,coverage.MissingTracks);
    }

    [Fact]
    public async Task FetchReportsTimeoutAndPreservesIdsButHonoursCallerCancellation()
    {
        await SeedRefreshLibraryAsync();
        await _repository.UpsertMediaServerTrackMetadataAsync([new(1,"jellyfin","old-id","/music/Song.flac",DateTimeOffset.UtcNow)]);
        var auth = await CreateJellyfinAuthAsync();
        using var handler = new IdentityListingHandler(false, timeout: true);
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(auth,null!,new JellyfinApiClient(http),null!,_repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var result = await service.FetchTargetIdentitiesAsync("jellyfin",7,false,CancellationToken.None);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("old-id",(await _repository.GetMediaServerItemIdsByTrackIdsAsync("jellyfin",[1]))[1]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.FetchTargetIdentitiesAsync("jellyfin",7,false,cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestsAddedDuringFetchingSurviveCompletionAndRetry(bool unavailable)
    {
        await SeedRefreshLibraryAsync();
        var auth = await CreateJellyfinAuthAsync();
        using var handler = new IdentityListingHandler(unavailable);
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(auth,null!,new JellyfinApiClient(http),null!,_repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var outbox = new MediaServerRefreshOutboxService(_repository,service,NullLogger<MediaServerRefreshOutboxService>.Instance);
        var owner = (string)typeof(MediaServerRefreshOutboxService).GetField("_leaseOwner",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(outbox)!;
        await _repository.EnqueueMediaServerRefreshAsync(7,"jellyfin",["/music/Song.flac"],[1],TimeSpan.Zero);
        var job = Assert.Single(await _repository.ClaimDueMediaServerRefreshesAsync(1,TimeSpan.FromMinutes(5),owner));
        Assert.True(await _repository.MarkMediaServerRefreshScanSubmittedAsync(job.Id,owner,DateTimeOffset.UtcNow));
        handler.BeforeTracks = () => _repository.EnqueueMediaServerRefreshAsync(7,"jellyfin",["/music/Other.flac"],[2],TimeSpan.Zero);
        var process = typeof(MediaServerRefreshOutboxService).GetMethod("ProcessJobAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)process.Invoke(outbox,[job,CancellationToken.None])!;
        var remaining = (await _repository.GetMediaServerRefreshOutboxAsync(job.Id))!;
        Assert.Equal(unavailable ? "retry" : "pending",remaining.Status);
        Assert.Contains(2L,remaining.RequestedTrackIds);
        Assert.Contains("/music/Other.flac",remaining.ChangedFilePaths);
        if (!unavailable)
        {
            Assert.DoesNotContain(1L,remaining.RequestedTrackIds);
            Assert.Null(remaining.ScanSubmittedUtc);
        }
    }

    [Theory]
    [InlineData("plex", false)]
    [InlineData("jellyfin", false)]
    [InlineData("navidrome", false)]
    [InlineData("plex", true)]
    [InlineData("jellyfin", true)]
    [InlineData("navidrome", true)]
    public async Task PagingHandlesShortPagesAndRejectsNonAdvancingPages(string target, bool repeat)
    {
        await SeedRefreshLibraryAsync();
        await _repository.UpsertMediaServerTrackMetadataAsync([new(2,target,"old-id","/music/Other.flac",DateTimeOffset.UtcNow)]);
        using var handler = new IdentityListingHandler(false) { ShortPages = true, RepeatPage = repeat };
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(null!,new PlexApiClient(NullLogger<PlexApiClient>.Instance,http),
            new JellyfinApiClient(http),new NavidromeApiClient(http),_repository,NullLogger<MediaServerLibraryRefreshService>.Instance);
        var method = typeof(MediaServerLibraryRefreshService).GetMethod("Update" + (target == "plex" ? "Plex" : target == "jellyfin" ? "Jellyfin" : "Navidrome") + "TrackMetadataIndexAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        object?[] args = target switch
        {
            "plex" => [new PlexAuth {Url="http://server",Token="token"},new List<PlexLibrarySection>{new(){Key="1",Title="Music",Type="artist"}},7L,null,CancellationToken.None],
            "jellyfin" => [new JellyfinAuth {Url="http://server",ApiKey="token",UserId="user"},7L,null,CancellationToken.None],
            _ => [new NavidromeAuth {Url="http://server",Username="user",Password="secret"},7L,null,CancellationToken.None]
        };
        if (repeat) await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)method.Invoke(service,args)!);
        else await (Task)method.Invoke(service,args)!;
        var mapped = await _repository.GetMediaServerItemIdsByTrackIdsAsync(target,[1,2]);
        Assert.Equal("new-id",mapped[1]);
        Assert.Equal(repeat ? "old-id" : "other-id",mapped[2]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task UnseenIdsArePrunedOnlyWhenTheServerScanIsIdle(bool? scanning)
    {
        await SeedRefreshLibraryAsync();
        await _repository.UpsertMediaServerTrackMetadataAsync([new(2,"jellyfin","old-id","/music/Other.flac",DateTimeOffset.UtcNow)]);
        using var handler = new IdentityListingHandler(false) {Scanning = scanning};
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(null!,null!,new JellyfinApiClient(http),null!,_repository,
            NullLogger<MediaServerLibraryRefreshService>.Instance);
        var method = typeof(MediaServerLibraryRefreshService).GetMethod("UpdateJellyfinTrackMetadataIndexAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)method.Invoke(service,[new JellyfinAuth {Url="http://server",ApiKey="token",UserId="user"},7L,null,CancellationToken.None])!;
        var mapped = await _repository.GetMediaServerItemIdsByTrackIdsAsync("jellyfin",[1,2]);
        Assert.Equal("new-id",mapped[1]);
        Assert.Equal(scanning != false,mapped.ContainsKey(2));
    }

    [Fact]
    public async Task ResetWaitsForActiveFetchBeforeDeletingIdsAndCanBeCancelledWhileWaiting()
    {
        await SeedRefreshLibraryAsync();
        await _repository.UpsertMediaServerTrackMetadataAsync([new(1,"jellyfin","old-id","/music/Song.flac",DateTimeOffset.UtcNow)]);
        var auth = await CreateJellyfinAuthAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new IdentityListingHandler(false) {BeforeTracks = async () => {entered.TrySetResult();await release.Task;} };
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(auth,null!,new JellyfinApiClient(http),null!,_repository,NullLogger<MediaServerLibraryRefreshService>.Instance);
        var active = service.FetchTargetIdentitiesAsync("jellyfin",7,false,CancellationToken.None,[1]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        var reset = service.FetchTargetIdentitiesAsync("jellyfin",7,true,cancelled.Token);
        try
        {
            Assert.False(reset.IsCompleted);
            Assert.Equal("old-id",(await _repository.GetMediaServerItemIdsByTrackIdsAsync("jellyfin",[1]))[1]);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
        }
        finally {release.TrySetResult();}
        Assert.True((await active).Success);
    }

    [Fact]
    public async Task StoredButUnverifiedIdsDoNotCompleteRefreshWork()
    {
        await SeedRefreshLibraryAsync();
        await _repository.UpsertMediaServerTrackMetadataAsync([new(2,"jellyfin","old-id","/music/Other.flac",DateTimeOffset.UtcNow)]);
        var auth = await CreateJellyfinAuthAsync();
        using var handler = new IdentityListingHandler(false) {Scanning = null};
        using var http = new HttpClient(handler);
        var service = new MediaServerLibraryRefreshService(auth,null!,new JellyfinApiClient(http),null!,_repository,NullLogger<MediaServerLibraryRefreshService>.Instance);
        var outbox = new MediaServerRefreshOutboxService(_repository,service,NullLogger<MediaServerRefreshOutboxService>.Instance);
        await _repository.EnqueueMediaServerRefreshAsync(7,"jellyfin",["/music/Other.flac"],[2],TimeSpan.Zero);
        var job = Assert.Single(await _repository.ClaimDueMediaServerRefreshesAsync(1,TimeSpan.FromMinutes(5),"worker"));
        var verify = typeof(MediaServerRefreshOutboxService).GetMethod("RefreshAndVerifyRequestedIdentitiesAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var work = (Task)verify.Invoke(outbox,[job,CancellationToken.None])!;
        await work;
        var result = work.GetType().GetProperty("Result")!.GetValue(work)!;
        Assert.False((bool)result.GetType().GetProperty("IsComplete")!.GetValue(result)!);
        Assert.Equal("old-id",(await _repository.GetMediaServerItemIdsByTrackIdsAsync("jellyfin",[2]))[2]);
    }

    private async Task SeedRefreshLibraryAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO folder(id,root_path,display_name,enabled) VALUES(7,'/music','Music',1);
            INSERT INTO artist(id,name) VALUES(1,'Artist');
            INSERT INTO album(id,artist_id,title) VALUES(1,1,'Album');
            INSERT INTO track(id,album_id,title,duration_ms) VALUES(1,1,'Song',120000),(2,1,'Other',120000);
            INSERT INTO audio_file(id,path,relative_path,folder_id,size,duration_ms) VALUES
                (1,'/music/Song.flac','Song.flac',7,1000,120000),(2,'/music/Other.flac','Other.flac',7,1000,120000);
            INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1),(2,2);
            """;
        await seed.ExecuteNonQueryAsync();
    }

    private async Task<PlatformAuthService> CreateJellyfinAuthAsync()
    {
        var auth = new PlatformAuthService(new TestEnvironment(_tempRoot),NullLogger<PlatformAuthService>.Instance,
            new EphemeralDataProtectionProvider());
        await auth.SaveAsync(new PlatformAuthState { Jellyfin = new() {Url="http://server",ApiKey="token",UserId="user"} });
        return auth;
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment,IAppDataRootOverride
    {
        public string ApplicationName {get;set;} = "DeezSpoTag.Tests";
        public string EnvironmentName {get;set;} = "Development";
        public string ContentRootPath {get;set;} = root;
        public string WebRootPath {get;set;} = root;
        public IFileProvider ContentRootFileProvider {get;set;} = new NullFileProvider();
        public IFileProvider WebRootFileProvider {get;set;} = new NullFileProvider();
        public string? AppDataRoot => root;
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
