using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class RecommendationCacheServingTest : IAsyncLifetime
{
    [Theory]
    [InlineData(DailyRecommendationAiFilterMode.Allow, 20, false)]
    [InlineData(DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated, 20, false)]
    [InlineData(DailyRecommendationAiFilterMode.Strict, 20, false)]
    [InlineData(DailyRecommendationAiFilterMode.Allow, 20, true)]
    [InlineData(DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated, 20, true)]
    [InlineData(DailyRecommendationAiFilterMode.Strict, 20, true)]
    public async Task SavedDailySelectionRechecksLibraryProviderIdentitiesAndCurrentAiPreference(DailyRecommendationAiFilterMode mode, int expected, bool sameIsrc)
    {
        using var configScope = new TestConfigRootScope(_root);
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var value = settings.LoadSettings();
        value.DailyRecommendations = new() { TrackCount = 20, DeezerEnabled = false, DeezerTrackCount = 0,
            SpotifyEnabled = false, SpotifyTrackCount = 0, TidalEnabled = true, TidalTrackCount = 20, AiFilterMode = mode };
        settings.SaveSettings(value);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artist(id,name) VALUES(1,'Richie Spice');
            INSERT INTO album(id,artist_id,title) VALUES(1,1,'Local album');
            INSERT INTO track(id,album_id,title,tag_isrc,duration_ms) VALUES(1,1,'Grooving My Girl',@isrc,229493);
            INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/Grooving My Girl.flac',@folder);
            INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);
            INSERT INTO track_source(track_id,source,source_id) VALUES(1,'tidal',@tidalId);
            """;
        command.Parameters.AddWithValue("folder", Folder.Id);
        command.Parameters.AddWithValue("isrc", sameIsrc ? "USVPR0713125" : "OTHER-RELEASE");
        command.Parameters.AddWithValue("tidalId", sameIsrc ? "different-source-id" : "2487938");
        await command.ExecuteNonQueryAsync();
        var clock = new ArtworkClock();
        var day = DateOnly.FromDateTime(clock.GetUtcNow().DateTime);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var rows = Enumerable.Range(1,26).Select(n => new RecommendationTrackDto((10000+n).ToString(),
            n == 1 ? "Groovin My Girl" : "New song " + n, 229, n == 1 ? "USVPR0713125" : "NEW" + n, n,
            new("artist" + n, n == 1 ? "Richie Spice" : "New artist " + n), new("album", "New album", ""),
            RecommendationOrigin:"tidal", Provenance: [new("tidal", n == 1 ? "2487938" : "native" + n, n, "seed" + n, n)],
            AiEvidence: n is 2 or 3 ? [new(n == 2 ? RecommendationAiStatus.FullyAiTrack : RecommendationAiStatus.PartialAiInvolvement,
                "tidal", "disclosure", "verified_provider_disclosure", clock.GetUtcNow())] : null)).ToArray();
        var payload = JsonSerializer.Serialize(new { GeneratedAtUtc = clock.GetUtcNow(), Tracks = rows, SeedScopeKey = id,
            TrackCount = 20, Allocations = new Dictionary<string,int>{{"tidal",20}},
            AiFilterMode = DailyRecommendationAiFilterMode.Allow, Status = "ready",
            Diagnostics = new DailyRecommendationDiagnosticsDto(26, new Dictionary<string,int>(), new Dictionary<string,int>(),
                new Dictionary<string,int>(), new Dictionary<string,string>(), new Dictionary<string,int>(),20,6,
                InitialSelectionPublished:true, PublishedTrackIds:rows.Take(20).Select(t => t.Id).ToArray()) });
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", id,
            $"v1:{day:yyyyMMdd}", payload, 0, null, null, true);
        var result = await Service(clock, settings).GetRecommendationsAsync(Folder.LibraryId!.Value, id, Folder.Id);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Tracks.Count);
        Assert.DoesNotContain(result.Tracks, t => t.Title == "Groovin My Girl");
        Assert.Equal(mode, result.DailyAiFilterMode);
        Assert.Equal(20, result.Tracks.Count(t => t.RecommendationOrigin == "tidal"));
        if (mode == DailyRecommendationAiFilterMode.Allow) Assert.Contains(result.Tracks,t => t.Id == "10002");
        else Assert.DoesNotContain(result.Tracks,t => t.Id == "10002");
        if (mode == DailyRecommendationAiFilterMode.Strict) Assert.DoesNotContain(result.Tracks,t => t.Id == "10003");
        else Assert.Contains(result.Tracks,t => t.Id == "10003");
        Assert.All(result.Tracks, t => Assert.Null(LibraryRecommendationService.EvaluateDailyAiPolicy(t.AiEvidence, mode)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavedRecommendationsCannotBorrowAnotherFoldersSeedScope(bool weekly, bool unproven)
    {
        var id = weekly ? $"weekly-rotation:l{Folder.LibraryId}:f{Folder.Id}:{LibraryRecommendationService.WeeklyMissingType}"
            : $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var track = new RecommendationTrackDto("12345", "Foreign seeded song", 180, "", 1,
            new("artist", "Artist"), new("album", "Album", ""));
        var day = DateOnly.FromDateTime(DateTime.Now);
        var payload = weekly
            ? JsonSerializer.Serialize(new { Station = new RecommendationStationDto(id, "Music", "", LibraryRecommendationService.WeeklyMissingType, null, 1),
                Tracks = new[] { track }, GeneratedAtUtc = DateTimeOffset.UtcNow, SeedScopeKey = unproven ? null : "weekly-rotation:l999:f999:weekly-missing-favourites" })
            : JsonSerializer.Serialize(new { Tracks = new[] { track }, GeneratedAtUtc = DateTimeOffset.UtcNow, SeedScopeKey = unproven ? null : "daily-rotation:l999:f999" });
        await Repository.UpsertPlaylistTrackCandidateCacheAsync(weekly ? "recommendations-weekly-pool" : "recommendations-daily-pool",
            id, $"v1:{(weekly ? Monday : day):yyyyMMdd}", payload, 0, null, null, true);
        var result = await Service().GetRecommendationsAsync(Folder.LibraryId!.Value, id);
        Assert.NotNull(result);
        Assert.Empty(result.Tracks);
        Assert.NotEqual("ready", result.Status);
    }

    [Fact]
    public async Task CompletedUnprovenWeeklyPoolDoesNotSuppressFolderScopedRecovery()
    {
        var id = LibraryRecommendationService.BuildWeeklyStationId(Folder.LibraryId!.Value, Folder.Id, LibraryRecommendationService.WeeklyMissingType);
        var key = new RecommendationGenerationStateKey(Folder.LibraryId.Value, Folder.Id, id, Monday);
        var payload = JsonSerializer.Serialize(new { Station = new RecommendationStationDto(id, "Music", "", LibraryRecommendationService.WeeklyMissingType, null, 1),
            Tracks = new[] { new RecommendationTrackDto("12345", "Unproven", 180, "", 1, new("a", "Artist"), new("b", "Album", "")) }, GeneratedAtUtc = DateTimeOffset.UtcNow });
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key, "recommendations-weekly-pool", id, $"v1:{Monday:yyyyMMdd}", payload));
        var method = typeof(LibraryRecommendationService).GetMethod("GenerateWeeklyRecommendationsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(Service(), [Folder.LibraryId.Value, Folder.Id, LibraryRecommendationService.WeeklyMissingType, Monday, "recovery", System.Threading.CancellationToken.None])!;
        var state = await Repository.GetRecommendationGenerationStateAsync(key.LibraryId, key.FolderId, id, Monday);
        Assert.Equal("failed", state!.Status); // No provider auth wired: recovery was attempted instead of trusting old output.
        Assert.Equal("pathfinder_authentication_unavailable", state.ReasonCode);
        Assert.Equal(1, state.AttemptCount); // A forced rebuild resets the per-generation counter.
        Assert.Equal(payload, (await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id))!.CandidatesJson);
        Assert.Empty((await Service().GetRecommendationsAsync(key.LibraryId, id))!.Tracks);
    }

    [Fact]
    public async Task CompletedUnprovenDailyPoolDoesNotSuppressFolderScopedRecovery()
    {
        var clock = new ArtworkClock();
        var day = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var key = new RecommendationGenerationStateKey(Folder.LibraryId!.Value, Folder.Id, id, day);
        var payload = JsonSerializer.Serialize(new { GeneratedAtUtc = clock.GetUtcNow(),
            Tracks = new[] { new RecommendationTrackDto("12345", "Unproven", 180, "", 1, new("a", "Artist"), new("b", "Album", "")) } });
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key, "recommendations-daily-pool", id, $"v1:{day:yyyyMMdd}", payload));
        await Service(clock).RefreshDailyRecommendationsForDateAsync(day, "recovery", System.Threading.CancellationToken.None);
        var state = await Repository.GetRecommendationGenerationStateAsync(key.LibraryId, key.FolderId, id, day);
        Assert.NotEqual("completed", state!.Status); // This fixture has no eligible local seeds.
        Assert.Equal(1, state.AttemptCount); // A forced rebuild resets the per-generation counter.
        Assert.Empty((await Service(clock).GetRecommendationsAsync(key.LibraryId, id))!.Tracks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DailyGenerationPersistsAndServes60AfterTheFirst32SeedsYieldNoUsableSongs(bool recoveringSourceFailure)
    {
        using var configScope=new TestConfigRootScope(_root);
        var settings=new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var value=settings.LoadSettings();
        value.DailyRecommendations=new(){TrackCount=60,DeezerEnabled=true,DeezerTrackCount=60,SpotifyEnabled=false,SpotifyTrackCount=0,TidalEnabled=false,TidalTrackCount=0};
        settings.SaveSettings(value);
        await using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+DatabasePath);await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="""
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<80)
            INSERT INTO artist(id,name) SELECT n,'Local artist '||n FROM numbers;
            INSERT INTO album(id,artist_id,title) SELECT id,id,'Owned album' FROM artist;
            INSERT INTO track(id,album_id,title,deezer_id,duration_ms) SELECT id,id,'Owned song '||id,CAST(id AS TEXT),180000 FROM artist;
            INSERT INTO audio_file(id,path,folder_id) SELECT id,'/fixture/'||id||'.flac',@folder FROM artist;
            INSERT INTO track_local(track_id,audio_file_id) SELECT id,id FROM artist;
            """;
        command.Parameters.AddWithValue("folder",Folder.Id);await command.ExecuteNonQueryAsync();
        var httpState=new DailyHttpState();
        using var session=new DeezSpoTag.Integrations.Deezer.DeezerSessionManager(
            NullLogger<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>.Instance,()=>value,()=>new DailyHandler(httpState)){ApiToken="test"};
        using var gateway=new DeezSpoTag.Integrations.Deezer.DeezerGatewayService(NullLogger<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>.Instance);
        gateway.SetSessionManager(session);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:Queue","Data Source="+Path.Combine(_root,"queue.db")}}).Build();
        var queue=new DeezSpoTag.Services.Download.Queue.DownloadQueueRepository(config,NullLogger<DeezSpoTag.Services.Download.Queue.DownloadQueueRepository>.Instance);
        typeof(DeezSpoTag.Services.Download.Queue.DownloadQueueRepository).GetField("_connectionString",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .SetValue(queue,"Data Source="+Path.Combine(_root,"queue.db"));
        var dedupe=new DeezSpoTag.Services.Download.DownloadDedupeService(queue,Repository,NullLogger<DeezSpoTag.Services.Download.DownloadDedupeService>.Instance,new DailyAmbiguityResolver());
        using var client=new DeezSpoTag.Integrations.Deezer.DeezerClient(NullLogger<DeezSpoTag.Integrations.Deezer.DeezerClient>.Instance,session);
        var clock=new ArtworkClock();
        var service=new LibraryRecommendationService(new LibraryRecommendationService.LibraryRecommendationCollaborators
            {Repository=Repository,Settings=settings,DeezerClient=client,DeezerGatewayService=gateway,DedupeService=dedupe},new EnvironmentFixture(),NullLogger<LibraryRecommendationService>.Instance,timeProvider:clock);
        var day=new DateOnly(2026,10,9);
        var id=$"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        if (recoveringSourceFailure)
        {
            var diagnostics = new DailyRecommendationDiagnosticsDto(80, new Dictionary<string,int>(), new Dictionary<string,int>(),
                new Dictionary<string,int>(), new Dictionary<string,string>{{"deezer","provider_request_failed"}},
                new Dictionary<string,int>{{"deezer",60}}, 0, 0);
            await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", id+$":day:{day:yyyyMMdd}", $"v1:{day:yyyyMMdd}",
                JsonSerializer.Serialize(new { SeedScopeKey=id, GeneratedAtUtc=clock.GetUtcNow(), Tracks=Array.Empty<RecommendationTrackDto>(),
                    TrackCount=60, Allocations=new Dictionary<string,int>{{"deezer",60}}, Diagnostics=diagnostics, Status="draft" }),0,null,null,true);
        }
        await service.RefreshDailyRecommendationsForDateAsync(day,"scheduled",System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(45));
        var result=(await Service(clock).GetRecommendationsAsync(Folder.LibraryId!.Value,id))!;
        Assert.Equal("ready",result.Status);Assert.Equal(60,result.Tracks.Count);
        Assert.Equal(60,result.DailyTrackCount);Assert.Equal(60,result.Station.TrackCount);
        Assert.Equal(64,httpState.MixRequests);
        Assert.Equal(64,result.Diagnostics!.ProcessedSeedIds!["deezer"].Count);
        Assert.Equal(120,result.Diagnostics.OverflowCount);
        Assert.Equal("completed",(await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value,Folder.Id,id,day))!.Status);
        var reserve=(await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-daily-pool",id))!;
        using var saved=JsonDocument.Parse(reserve.CandidatesJson);
        Assert.Equal(180,saved.RootElement.GetProperty("Tracks").GetArrayLength());
    }

    private sealed class DailyAmbiguityResolver : ILocalTrackAmbiguityResolver
    {
        public Task<LibraryRepository.LocalTrackIdentityResult> ResolveAsync(LibraryRepository.LibraryExistenceInput input,
            LibraryRepository.LocalTrackIdentityResult initial,System.Threading.CancellationToken token)=>Task.FromResult(initial);
    }
    private sealed class DailyHttpState {public int MixRequests;}
    private sealed class DailyHandler(DailyHttpState state) : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,System.Threading.CancellationToken token)
        {
            if (request.Method == System.Net.Http.HttpMethod.Get)
            {
                var id = int.Parse(request.RequestUri!.Segments.Last());
                return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(
                    JsonSerializer.Serialize(new { id, title = "Owned song " + id, duration = 180, artist = new { name = "Local artist " + id } }),
                    System.Text.Encoding.UTF8, "application/json") };
            }
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            object[] rows;
            if(request.RequestUri!.Query.Contains("song.getContextualTrackMix",StringComparison.Ordinal))
            {
                var batch=++state.MixRequests;
                rows=batch<=32 ? [] : Enumerable.Range(1,10).Select(n=>DailyRow(batch*100+n)).ToArray();
            }
            else
            {
                Assert.Contains("song.getListData",request.RequestUri.Query);
                rows=body.RootElement.GetProperty("SNG_IDS").EnumerateArray().Select(n=>DailyRow(int.Parse(n.GetString()!))).ToArray();
            }
            return new(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(JsonSerializer.Serialize(new {results=new {data=rows},error=new {}}),System.Text.Encoding.UTF8,"application/json")};
        }
        private static object DailyRow(int id)=>new {SNG_ID=id,SNG_TITLE="New song "+id,DURATION=180,ART_ID=id,ART_NAME="New artist "+id,ALB_ID=id.ToString(),ALB_TITLE="New album "+id,ISRC="US000"+id.ToString("D7")};
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedPublicSelectionRecoversWhenProgressCacheStillContainsItsDraft(bool foreignProgressScope)
    {
        var clock=new ArtworkClock();var day=new DateOnly(2026,10,9);
        var id=$"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var rows=Enumerable.Range(1,60).Select(n=>new RecommendationTrackDto(n.ToString(),"Song "+n,180,"",n,
            new("a"+n,"Artist "+n),new("b"+n,"Album",""),RecommendationOrigin:"deezer")).ToArray();
        var key=new RecommendationGenerationStateKey(Folder.LibraryId!.Value,Folder.Id,id,day);
        var quotas=new Dictionary<string,int>{{"deezer",60}};
        var generated=clock.GetUtcNow();
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool",id+$":day:{day:yyyyMMdd}",$"v1:{day:yyyyMMdd}",
            JsonSerializer.Serialize(new {SeedScopeKey=foreignProgressScope ? "daily-rotation:l999:f999" : id,GeneratedAtUtc=generated,Tracks=rows,TrackCount=60,Allocations=quotas,Status="draft"}),0,null,null,true);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key,"test"));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key,"recommendations-daily-pool",id,$"v1:{day:yyyyMMdd}",
            JsonSerializer.Serialize(new {SeedScopeKey=id,GeneratedAtUtc=generated,Tracks=rows,TrackCount=60,Allocations=quotas,Status="ready"})));
        var result=(await Service(clock).GetRecommendationsAsync(key.LibraryId,id))!;
        Assert.Equal(60,result.Tracks.Count);Assert.Equal("ready",result.Status);
    }

    [Fact]
    public async Task ExpiredGenerationCannotCommitAPublicDailySnapshot()
    {
        var service=Service(new ArtworkClock());var day=new DateOnly(2026,10,8);
        var id=$"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var type=typeof(LibraryRecommendationService);
        var scope=Activator.CreateInstance(type.GetNestedType("RecommendationScope",System.Reflection.BindingFlags.NonPublic)!,
            [Folder.LibraryId!.Value,Folder.Id,"Music",id,id]);
        var detail=new RecommendationDetailDto(new(id,"Music","","daily","Music",1),
            [new("1","Expired",180,"",1,new("a","Artist"),new("b","Album",""))],DateTimeOffset.UtcNow, SeedScopeKey: id);
        var task=(Task)type.GetMethod("PersistDailyPoolAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service,[scope,day,detail,System.Threading.CancellationToken.None,false])!;
        await task;
        var result=task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.False((bool)result.GetType().GetProperty("Success")!.GetValue(result)!);
        Assert.Equal("daily_selection_expired",result.GetType().GetProperty("ReasonCode")!.GetValue(result));
        Assert.Null(await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-daily-pool",id));
    }

    [Fact]
    public async Task ExpandedDailyViewsKeepSavedCountsAndExpireOfflineWithoutRestoringLateResponses()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"DeezSpoTag.Web","Views","Tracklist","Index.cshtml"))) root=root.Parent;
        Assert.NotNull(root);
        var source=File.ReadAllText(Path.Combine(root.FullName,"DeezSpoTag.Web","Views","Tracklist","Index.cshtml"));
        source=source[source.IndexOf("let recommendationReloadTimer;",StringComparison.Ordinal)..source.IndexOf("function mapRecommendationsTracklist",StringComparison.Ordinal)];
        var script="""
            const assert=require('node:assert/strict');
            let now=0, render=null, requested='', ignore=async()=>{};
            Date.now=()=>now;
            const timers=new Map();let timerId=0;
            globalThis.setTimeout=(fn,delay)=>{timers.set(++timerId,{fn,due:now+delay});return timerId;};
            globalThis.clearTimeout=id=>timers.delete(id);
            Object.defineProperty(globalThis,'navigator',{value:{onLine:false},configurable:true});
            const tracklistSource='recommendations',tracklistLibraryId='1',tracklistId='daily-rotation:l1:f1',recommendationType='',recommendationValue='';
            const notice={textContent:'',classList:{remove(){},add(){}}};
            const document={getElementById:()=>notice};
            const mapRecommendationsTracklist=p=>p;
            const loadIgnoredTracks=()=>ignore();
            const renderTracklist=p=>{render=p;};
            const setTracksEmptyMessage=()=>{};
            let payload;
            const fetch=async(url)=>{requested=url;return {ok:true,json:async()=>payload};};
            """+source+"""
            (async()=>{
                for (const count of [60,100]) {
                    now=0;
                    payload={station:{cadence:'daily'},tracks:Array.from({length:count},(_,id)=>({id})),status:'ready',message:count+' tracks',serverNowUtc:'2026-10-24T20:59:59Z',expiresAtUtc:'2026-10-24T21:00:00Z'};
                    await loadRecommendationsTracklist();
                    assert.equal(render.tracks.length,count);
                    assert.equal(new URL(requested,'http://localhost').searchParams.has('limit'),false);
                    now=1000;
                    for(const timer of [...timers.values()]) if(timer.due<=now) timer.fn();
                    assert.equal(render.tracks.length,0,'Must clear at midnight with NAS offline');
                    assert.match(notice.textContent,/expired/);
                }
                now=0;
                let release;
                ignore=()=>new Promise(resolve=>{release=resolve;});
                const loading=loadRecommendationsTracklist();
                await new Promise(resolve=>setImmediate(resolve));
                now=1001;
                release();await loading;
                assert.equal(render.tracks.length,0,'A response expiring while ignored tracks load must never restore old rows');
                now=1001;ignore=async()=>{};
                await loadRecommendationsTracklist();
                assert.equal(render.tracks.length,100,'A fresh response carries a new server-relative deadline');
            })().catch(error=>{console.error(error);process.exitCode=1;});
            """;
        var info=new System.Diagnostics.ProcessStartInfo("node"){RedirectStandardError=true,UseShellExecute=false};
        info.ArgumentList.Add("-e");info.ArgumentList.Add(script);
        using var process=System.Diagnostics.Process.Start(info)!;
        var error=await process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        Assert.True(process.ExitCode==0,error);
    }

    [Fact]
    public async Task DailyCardsClearAtMidnightWhileWeeklyCardsArePreserved()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"DeezSpoTag.Web","wwwroot","js","auto-playlists.js"))) root=root.Parent;
        Assert.NotNull(root);
        var source=File.ReadAllText(Path.Combine(root.FullName,"DeezSpoTag.Web","wwwroot","js","auto-playlists.js"));
        var render=source[source.IndexOf("    const renderRecommendationCard =",StringComparison.Ordinal)..source.IndexOf("    const renderAutoPlaylistsEmptyIfNeeded",StringComparison.Ordinal)];
        var expiry=source[source.IndexOf("    var recommendationCardsRefreshTimer;",StringComparison.Ordinal)..source.IndexOf("    let recommendationFolderScopes",StringComparison.Ordinal)];
        var script="""
            const assert=require('node:assert/strict');
            let now=0;Date.now=()=>now;
            const timers=[];const setTimeout=(fn,delay)=>{timers.push({fn,delay});return timers.length;};const clearTimeout=()=>{};
            Object.defineProperty(globalThis,'navigator',{value:{onLine:false},configurable:true});
            const document={createElement:()=>({children:[],dataset:{},textContent:'',addEventListener(){},appendChild(c){this.children.push(c);},append(...c){this.children.push(...c);},replaceChildren(...c){this.children=c;}})};
            const openRecommendationStation=()=>{},normalizeRecommendationTitle=()=>'',normalizeRecommendationMode=()=>'';
            const recommendationsGrid={children:[]};
            const loadRecommendations=()=>{throw Error('Must not require NAS to clear cards');};
            """+render+expiry+"""
            const daily=renderRecommendationCard({cadence:'daily',trackCount:60,serverNowUtc:'2026-10-24T20:59:59Z',expiresAtUtc:'2026-10-24T21:00:00Z'},1,0);
            const weekly=renderRecommendationCard({cadence:'weekly',trackCount:50,distinctArtistCount:50},1,0);
            recommendationsGrid.children=[daily];
            armRecommendationCardsExpiry();
            assert.equal(timers[0].delay,1000);
            now=1000;timers[0].fn();
            assert.equal(daily.dataset.dailyExpired,'true');
            assert.equal(daily.children[1].children[2].children[0].textContent,'0 tracks');
            assert.equal(weekly.children[1].children[2].children[0].textContent,'50 tracks · 50 artists');
            assert.equal(recommendationLoadGeneration,1,'Expired cards invalidate outstanding requests');
            """;
        var info=new System.Diagnostics.ProcessStartInfo("node"){RedirectStandardError=true,UseShellExecute=false};
        info.ArgumentList.Add("-e");info.ArgumentList.Add(script);
        using var process=System.Diagnostics.Process.Start(info)!;
        var error=await process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        Assert.True(process.ExitCode==0,error);
    }

    [Fact]
    public async Task Initial59TrackDraftStaysEmptyUntilTheFull60ArePublished()
    {
        var clock=new ArtworkClock();var day=new DateOnly(2026,10,9);
        var id=$"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var rows=Enumerable.Range(1,60).Select(n=>new RecommendationTrackDto(n.ToString(),"Song "+n,180,"",n,
            new("a"+n,"Artist "+n),new("b"+n,"Album",""),RecommendationOrigin:n<=20?"deezer":n<=40?"spotify":"tidal")).ToArray();
        var quotas=new Dictionary<string,int>{{"deezer",20},{"spotify",20},{"tidal",20}};
        var key=new RecommendationGenerationStateKey(Folder.LibraryId!.Value,Folder.Id,id,day);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key,"test"));
        await Repository.FailRecommendationGenerationAsync(key,"tidal_allocation_incomplete","tidal_allocation_incomplete");
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool",id+$":day:{day:yyyyMMdd}",$"v1:{day:yyyyMMdd}",
            JsonSerializer.Serialize(new {SeedScopeKey=id,GeneratedAtUtc=clock.GetUtcNow(),Tracks=rows.Take(59),TrackCount=60,Allocations=quotas,Status="draft"}),0,null,null,true);
        var draft=(await Service(clock).GetRecommendationsAsync(key.LibraryId,id))!;
        Assert.Empty(draft.Tracks);Assert.Equal(0,draft.Station.TrackCount);
        Assert.Equal("failed",draft.Status);Assert.Contains("tidal: 19/20",draft.Message!);
        Assert.NotEqual("completed",(await Repository.GetRecommendationGenerationStateAsync(key.LibraryId,Folder.Id,id,day))!.Status);
        await Repository.RequestRecommendationGenerationAsync(key,"test",true);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key,"test"));
        var complete=JsonSerializer.Serialize(new {SeedScopeKey=id,GeneratedAtUtc=clock.GetUtcNow(),Tracks=rows,TrackCount=60,Allocations=quotas,Status="ready"});
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key,"recommendations-daily-pool",id,$"v1:{day:yyyyMMdd}",complete));
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool",id+$":day:{day:yyyyMMdd}",$"v1:{day:yyyyMMdd}",complete,0,null,null,true);
        var ready=(await Service(clock).GetRecommendationsAsync(key.LibraryId,id))!;
        Assert.Equal(60,ready.Tracks.Count);Assert.Equal("ready",ready.Status);
        Assert.All(quotas,p=>Assert.Equal(p.Value,ready.Tracks.Count(t=>t.RecommendationOrigin==p.Key)));
    }

    [Fact]
    public async Task DailyShortfallStaysPrivateAndPreservesSavedAiPolicy()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var quotas = new Dictionary<string, int> { ["deezer"] = 20 };
        var rows = Enumerable.Range(1, 20).Select(n => new RecommendationTrackDto(n.ToString(), "Track " + n, 180, "", n,
            new("artist" + n, "Artist " + n), new("album", "Album", ""), RecommendationOrigin: "deezer")).ToArray();
        var diagnostics = new DailyRecommendationDiagnosticsDto(1, new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), new Dictionary<string, string>(), new Dictionary<string, int> { ["deezer"] = 0 }, 20, 0);
        var key = new RecommendationGenerationStateKey(Folder.LibraryId!.Value, Folder.Id, id, day);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key, "recommendations-daily-pool", id,
            $"v1:{day:yyyyMMdd}", JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = rows, TrackCount = 20,
                Allocations = quotas, AiFilterMode = DailyRecommendationAiFilterMode.Strict, Diagnostics = diagnostics })));
        var result = (await Service().RejectRecommendationTrackAsync(new(key.LibraryId, Folder.Id, id, "1", null, "Track 1", "Artist 1")))!;
        Assert.Equal("refilling", result.Status);
        Assert.Empty(result.Tracks);
        Assert.Equal(1, result.Diagnostics!.MissingSourceCounts["deezer"]);
        Assert.Equal(0, result.Diagnostics.DestinationCount);
        Assert.Equal(DailyRecommendationAiFilterMode.Strict, result.DailyAiFilterMode);
    }

    [Fact]
    public async Task AllDisabledReturnsPausedStateAndDoesNotQueueDailyGeneration()
    {
        using var scope = new TestConfigRootScope(_root);
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var value = settings.LoadSettings();
        value.DailyRecommendations = new() { ShazamEnabled = false, ShazamTrackCount = 0, DeezerEnabled = false, DeezerTrackCount = 0,
            SpotifyEnabled = false, SpotifyTrackCount = 0, TidalEnabled = false, TidalTrackCount = 0 };
        settings.SaveSettings(value);
        var service = Service(settings: settings);
        var day = DateOnly.FromDateTime(DateTime.Now);
        await service.RefreshDailyRecommendationsForDateAsync(day, "test", System.Threading.CancellationToken.None);
        var result = (await service.GetRecommendationsAsync(Folder.LibraryId!.Value, folderId: Folder.Id))!;
        Assert.Equal("disabled", result.Status);
        Assert.Empty(result.Tracks);
        Assert.Contains("Enable a source", result.Message!);
        Assert.Null(await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value, Folder.Id, result.Station.Id, day));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task WeeklySavedPolicyControlsDefaultServingAndSmallerView(int count)
    {
        var id = $"weekly-rotation:l{Folder.LibraryId}:f{Folder.Id}:{LibraryRecommendationService.WeeklyMissingType}";
        var rows = Enumerable.Range(1, count).Select(n => new RecommendationTrackDto(n.ToString(), "Track " + n, 180, "", n,
            new("artist" + n, "Artist " + n), new("album", "Album", ""), "spotify", SeedArtistId: "artist" + n)).ToArray();
        var station = new RecommendationStationDto(id, "Weekly", "Weekly", "spotify", "Music", count);
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id, $"v1:{Monday:yyyyMMdd}",
            JsonSerializer.Serialize(new RecommendationDetailDto(station, rows, DateTimeOffset.UtcNow, WeeklyTrackCount: count, SeedScopeKey: id)), 0, null, null, true);
        var result = (await Service().GetRecommendationsAsync(Folder.LibraryId!.Value, id))!;
        Assert.Equal(count, result.Tracks.Count);
        Assert.Equal(count, result.WeeklyTrackCount);
        var smaller = (await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id, limit: 10))!;
        Assert.Equal(10, smaller.Tracks.Count);
        Assert.Equal(count, smaller.WeeklyTrackCount);
    }

    [Fact]
    public async Task InitialPartialSavedDailySelectionRemainsPrivateAfterRestart()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var rows = Enumerable.Range(1, 10).Select(n => new RecommendationTrackDto(n.ToString(), "Track " + n, 180, "", n,
            new("artist" + n, "Artist " + n), new("album", "Album", ""), RecommendationOrigin: "deezer")).ToArray();
        var key = new RecommendationGenerationStateKey(Folder.LibraryId!.Value, Folder.Id, id, day);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key, "recommendations-daily-pool", id,
            $"v1:{day:yyyyMMdd}", JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = rows, TrackCount = 20,
                Allocations = new Dictionary<string, int> { ["deezer"] = 10, ["tidal"] = 10 }, Status = "partial" })));
        var result = (await Service().GetRecommendationsAsync(key.LibraryId, id))!;
        Assert.Empty(result.Tracks);
        Assert.Equal("generating", result.Status);
        Assert.Contains("tidal", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, result.DailyTrackCount);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task SavedDailyCountAndAllocationsSurviveRestartAndRejection(int total)
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var quotas = new Dictionary<string, int> { ["shazam"] = total / 2, ["deezer"] = total / 2 };
        var tracks = quotas.SelectMany((pair, source) => Enumerable.Range(1, pair.Value * 3).Select(i =>
            new RecommendationTrackDto((source * 1000 + i).ToString(), "Track " + i, 180, "", i,
                new("artist" + i, "Artist " + i), new("album", "Album", ""), RecommendationOrigin: pair.Key))).ToArray();
        var key = new RecommendationGenerationStateKey(Folder.LibraryId!.Value, Folder.Id, id, day);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key, "recommendations-daily-pool", id,
            $"v1:{day:yyyyMMdd}", JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = tracks, TrackCount = total, Allocations = quotas })));
        var first = (await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id))!;
        Assert.Equal(total, first.Tracks.Count);
        Assert.Equal(total, first.DailyTrackCount);
        Assert.Equal(total / 2, first.Tracks.Count(t => t.RecommendationOrigin == "shazam"));
        var rejected = first.Tracks.First(t => t.RecommendationOrigin == "shazam");
        var after = (await Service().RejectRecommendationTrackAsync(new(Folder.LibraryId.Value, Folder.Id, id,
            rejected.Id, null, rejected.Title, rejected.Artist.Name)))!;
        Assert.Equal(total, after.Tracks.Count);
        Assert.DoesNotContain(after.Tracks, t => t.Id == rejected.Id);
        Assert.Equal(total / 2, after.Tracks.Count(t => t.RecommendationOrigin == "shazam"));
        Assert.Equal(total / 2, after.Tracks.Count(t => t.RecommendationOrigin == "deezer"));
        var smaller = (await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id, limit: 10))!;
        Assert.Equal(10, smaller.Tracks.Count);
        Assert.Equal(total, smaller.DailyTrackCount);
    }

    [Fact]
    public async Task SourceShortageIsReportedInsteadOfSuccessfulGeneration()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var key = new RecommendationGenerationStateKey(Folder.LibraryId!.Value, Folder.Id, id, day);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        await Repository.FailRecommendationGenerationAsync(key, "shazam_allocation_incomplete", "shazam_allocation_incomplete");
        var detail = (await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id))!;
        Assert.Equal("failed", detail.Status);
        Assert.Empty(detail.Tracks);
        Assert.Contains("shazam_allocation_incomplete", detail.ReasonCodes!);
        Assert.Contains("Shazam", detail.Message!);
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recommendation-cache-" + Guid.NewGuid().ToString("N"));
    internal LibraryRepository Repository { get; private set; } = null!;
    internal FolderDto Folder { get; private set; } = null!;
    internal string DatabasePath => Path.Combine(_root, "library.db");
    internal LibraryRecommendationService Service(TimeProvider? clock = null, DeezSpoTagSettingsService? settings = null) => new(
        new LibraryRecommendationService.LibraryRecommendationCollaborators { Repository = Repository, Settings = settings },
        new EnvironmentFixture(), NullLogger<LibraryRecommendationService>.Instance, timeProvider: clock);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_root, "library.db") }).Build();
        await new LibraryDbService(config, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        Repository = new(config, NullLogger<LibraryRepository>.Instance);
        Folder = await Repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            RootPath: _root, DisplayName: "Music", Enabled: true, LibraryName: "Music", DesiredQuality: "flac",
            ConvertEnabled: false, ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
    }
    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }
    internal async Task<string> SeedWeekly(DateOnly week, string type = LibraryRecommendationService.WeeklyMissingType)
    {
        var id = $"weekly-rotation:l{Folder.LibraryId}:f{Folder.Id}:{type}";
        var track = new RecommendationTrackDto("0123456789abcdefghijkl", "Saved track", 180, "", 1,
            new("artist", "Artist"), new("album", "Album", ""), "spotify", MappingStatus: "awaiting_isrc");
        var detail = new RecommendationDetailDto(new(id, "Weekly", "", type, null, 1, Cadence: "weekly"), [track], DateTimeOffset.UtcNow.AddDays(-7), SeedScopeKey: id);
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id,
            $"v1:{week:yyyyMMdd}", JsonSerializer.Serialize(detail), 0, null, null, true);
        return id;
    }
    internal static DateOnly Monday => DateOnly.FromDateTime(DateTime.Now).AddDays(-(((int)DateTime.Now.DayOfWeek + 6) % 7));

    [Fact]
    public async Task RefreshDailyArtworkReplacesRepeatedSavedImagesWithoutChangingTracks()
    {
        var webRoot = Path.Combine(_root, "web");
        foreach (var variant in new[] { "a", "b" })
        {
            var directory = Path.Combine(webRoot, "images", "recommendations", variant);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Friday.jpg"), "image fixture");
        }
        var second = await Repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            RootPath: Folder.RootPath + "-second", DisplayName: "Second", Enabled: true, LibraryName: "Music", DesiredQuality: "flac",
            ConvertEnabled: false, ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
        foreach (var folder in new[] { Folder, second })
        {
            var id = $"daily-rotation:l{folder.LibraryId}:f{folder.Id}";
            var track = new RecommendationTrackDto(folder.Id.ToString(), "Saved song", 180, "", 1, new("artist", "Artist"), new("album", "Album", ""));
            await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", id, "v1:20261009",
                JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track }, StationImageUrl = "/repeated.jpg" }), 0, null, null, true);
        }
        var service = new LibraryRecommendationService(new LibraryRecommendationService.LibraryRecommendationCollaborators { Repository = Repository },
            new EnvironmentFixture { WebRootPath = webRoot }, NullLogger<LibraryRecommendationService>.Instance,
            timeProvider: new ArtworkClock());
        var daily = (await service.GetStationsAsync(Folder.LibraryId!.Value)).Where(s => s.Type == LibraryRecommendationService.RecommendationSourceId).ToArray();
        Assert.Equal(2, daily.Length);
        Assert.Equal(2, daily.Select(s => s.ImageUrl).Distinct().Count());
        Assert.All(daily, s => Assert.StartsWith("/images/recommendations/", s.ImageUrl));
        foreach (var folder in new[] { Folder, second })
        {
            var response = await service.GetRecommendationsAsync(folder.LibraryId!.Value, $"daily-rotation:l{folder.LibraryId}:f{folder.Id}");
            Assert.Equal(folder.Id.ToString(), Assert.Single(response!.Tracks).Id);
        }
    }

    private sealed class ArtworkClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task RenamedLibraryWidePoolIsNotServedAsAFolderSelection()
    {
        var id = await SeedWeekly(Monday);
        var saved = (await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id))!;
        var detail = JsonSerializer.Deserialize<RecommendationDetailDto>(saved.CandidatesJson)!;
        detail = detail with { Station = detail.Station with { Id = $"library:{Folder.LibraryId}:{LibraryRecommendationService.WeeklyMissingType}" } };
        await Repository.UpsertPlaylistTrackCandidateCacheAsync(saved.Source, id, saved.SnapshotId,
            JsonSerializer.Serialize(detail), 0, null, null, true);
        var response = await Service().GetRecommendationsAsync(Folder.LibraryId!.Value, id);
        Assert.Empty(response!.Tracks);
        Assert.Equal("waiting", response.Status);
    }

    [Fact]
    public async Task CorruptWeeklyCacheDoesNotPreventDailyCardsLoading()
    {
        var id = await SeedWeekly(Monday);
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id,
            $"v1:{Monday:yyyyMMdd}", "invalid json", 0, null, null, true);
        var stations = await Service().GetStationsAsync(Folder.LibraryId!.Value);
        Assert.Single(stations.Where(s => s.Type == LibraryRecommendationService.RecommendationSourceId));
        Assert.Contains(stations, s => s.Id == id && s.Status == "failed");
    }

    [Fact]
    public async Task CachedDailyCardsFollowFolderRenameWithoutReplacingTheirTracks()
    {
        var service = Service();
        var day = DateOnly.FromDateTime(DateTime.Now);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var track = new RecommendationTrackDto("12345", "Original daily song", 180, "", 1, new("artist", "Artist"), new("album", "Album", ""));
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", id, $"v1:{day:yyyyMMdd}",
            JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track } }), 0, null, null, true);
        Assert.Equal("Recommendations - Music", (await service.GetRecommendationsAsync(Folder.LibraryId!.Value, id))!.Station.Name);
        await Repository.UpdateFolderAsync(Folder.Id, new LibraryRepository.FolderUpsertInput(
            RootPath: Folder.RootPath, DisplayName: "Renamed music", Enabled: true, LibraryName: Folder.LibraryName,
            DesiredQuality: "flac", ConvertEnabled: false, ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
        var renamed = (await service.GetRecommendationsAsync(Folder.LibraryId.Value, id))!;
        Assert.Equal("Recommendations - Renamed music", renamed.Station.Name);
        Assert.Equal("Renamed music", renamed.Station.Value);
        Assert.Equal(track.Id, Assert.Single(renamed.Tracks).Id);
    }

    [Fact]
    public async Task CardsUseConfiguredFolderNamesEvenWhenLibrariesHaveOtherNames()
    {
        Folder = (await Repository.UpdateFolderAsync(Folder.Id, new LibraryRepository.FolderUpsertInput(
            RootPath: Folder.RootPath, DisplayName: "My renamed songs", Enabled: true,
            LibraryName: "Ignored rename", DesiredQuality: "flac", ConvertEnabled: false,
            ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile")))!;
        var second = await Repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            RootPath: Folder.RootPath + "-second", DisplayName: "Other songs", Enabled: true,
            LibraryName: "Music", DesiredQuality: "flac", ConvertEnabled: false,
            ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
        Assert.Equal(Folder.LibraryId, second.LibraryId);
        var stations = await Service().GetStationsAsync(Folder.LibraryId!.Value);
        Assert.Equal(6, stations.Count);
        Assert.All(stations, s => Assert.Contains(s.Value, new[] { Folder.DisplayName, second.DisplayName }));
        Assert.All(stations, s => Assert.Equal("Recommendations - " + s.Value, s.Name));
        var id = await SeedWeekly(Monday);
        Assert.Null(await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id, second.Id));
    }

    [Fact]
    public async Task PreviousWeeklySnapshotIsVisibleWithoutStartingGeneration()
    {
        var id = await SeedWeekly(Monday.AddDays(-7));
        var before = (await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id))!.CandidatesJson;
        var detail = await Service().GetRecommendationsAsync(Folder.LibraryId!.Value, id);
        Assert.Single(detail!.Tracks);
        Assert.Equal("refreshing", detail.Status);
        Assert.Contains("previous", detail.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value, Folder.Id, $"weekly-rotation:l{Folder.LibraryId}:f{Folder.Id}:{LibraryRecommendationService.WeeklyMissingType}", Monday));
        Assert.Equal(before, (await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id))!.CandidatesJson);
    }

    [Fact]
    public async Task CurrentCachedReadsDoNotUseUnconfiguredProviderOrDedupeDependencies()
    {
        var id = await SeedWeekly(Monday);
        var result = await Service().GetStationsAsync(Folder.LibraryId!.Value);
        Assert.Contains(result, s => s.Id == id && s.TrackCount == 1);
        Assert.Single((await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id))!.Tracks);
        Assert.Null(await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value, Folder.Id, $"weekly-rotation:l{Folder.LibraryId}:f{Folder.Id}:{LibraryRecommendationService.WeeklyMissingType}", Monday));
    }

    [Fact]
    public async Task StoredRejectionsRemoveCachedTracksWithoutChangingSnapshot()
    {
        var id = await SeedWeekly(Monday);
        await Repository.AddRecommendationRejectionAsync(new(Folder.LibraryId!.Value, Folder.Id, id, "spotify:track:0123456789abcdefghijkl", null, "Saved track", "Artist"));
        Assert.Empty((await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id))!.Tracks);
        Assert.Single(JsonSerializer.Deserialize<RecommendationDetailDto>((await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id))!.CandidatesJson)!.Tracks);
    }

    [Fact]
    public async Task CardsRenderBeforeSlowLibraryCompletes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js"));
        var start = source.IndexOf("    var recommendationCardsRefreshTimer", StringComparison.Ordinal);
        var end = source.IndexOf("    async function resolveRecommendationLibraryIds()", start, StringComparison.Ordinal);
        var script = """
            const assert = require('node:assert/strict');
            function grid() { return { children: [], appendChild(card) { if (card.children) this.children.push(...card.children); else this.children.push(card); card.parent=this; }, replaceChildren() { this.children=[]; }, set innerHTML(v) { this.children=[]; } }; }
            const recommendationsGrid=grid(), missingGrid=grid(), similarGrid=grid(), recommendationsEmpty={hidden:true};
            const document={getElementById(id){return id==='weeklyMissingRecommendationsGrid'?missingGrid:similarGrid;},createDocumentFragment:grid};
            function renderRecommendationCard(station,libraryId) { return { id:station.id,dataset:{},remove(){this.parent.children=this.parent.children.filter(c=>c!==this);} }; }
            let configuredLibraries=[1,2];
            async function resolveRecommendationLibraryIds(){return configuredLibraries;}
            let slowResolve;
            const slow=new Promise(resolve=>slowResolve=resolve);
            let round=0;
            const response=id=>({ok:true,json:async()=>[{id,type:'daily',status:'ready'}]});
            let fetch=url=>url.endsWith('=1')?Promise.resolve(response('fast')):slow;
            """ + source[start..end] + """
            (async()=>{
                const load=loadRecommendations();
                await new Promise(resolve=>setImmediate(resolve));
                assert.equal(recommendationsGrid.children.length,1,'Fast cached library must render before slow request completes');
                slowResolve(response('slow'));
                await load;
                assert.deepEqual(recommendationsGrid.children.map(c=>c.id),['fast','slow']);
                fetch=async url=>{if(url.endsWith('=1'))await new Promise(r=>setTimeout(r,10));return response(url.endsWith('=1')?'fast':'slow');};
                await loadRecommendations();
                assert.deepEqual(recommendationsGrid.children.map(c=>c.id),['fast','slow'],'Daily order must survive reverse completion');
                fetch=async url=>{if(url.endsWith('=2'))throw new Error('offline');return response('new-fast');};
                await loadRecommendations();
                assert.deepEqual(recommendationsGrid.children.map(c=>c.id).sort(),['new-fast','slow'],'Failed library keeps its cached cards');
                clearTimeout(recommendationCardsRefreshTimer);
                let oldResolve;
                fetch=()=>new Promise(resolve=>{oldResolve=resolve;});
                // One shared pending response lets both old requests finish after the new load.
                const pending=new Promise(resolve=>{oldResolve=resolve;});
                fetch=()=>pending;
                const older=loadRecommendations();
                await new Promise(resolve=>setImmediate(resolve));
                fetch=async()=>response('newest');
                await loadRecommendations();
                oldResolve(response('outdated'));
                await older;
                assert.ok(recommendationsGrid.children.every(c=>c.id==='newest'),'Old responses must not replace newer cards');
                configuredLibraries=[1];
                await loadRecommendations();
                assert.equal(recommendationsGrid.children.length,1,'Removed scopes must disappear');
                configuredLibraries=null;
                await loadRecommendations();
                assert.equal(recommendationsGrid.children.length,1,'Discovery failure must preserve cached cards');
                configuredLibraries=[1];
                recommendationFolderScopes=new Set(['1:1']);
                recommendationsGrid.children[0].dataset.recommendationFolder='2';
                fetch=async()=>{throw new Error('offline');};
                await loadRecommendations();
                assert.equal(recommendationsGrid.children.length,0,'Disabled folder must disappear even when its shared library request fails');
                clearTimeout(recommendationCardsRefreshTimer);
            })().catch(error=>{clearTimeout(recommendationCardsRefreshTimer);console.error(error);process.exitCode=1;});
            """;
        var info = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(script);
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task RecommendationBootstrapRunsAfterStateIsDeclared()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js"));

        // The whole file is executed, including the real `if (hasRecommendationSection)
        // loadRecommendations();` bootstrap. CardsRenderBeforeSlowLibraryCompletes cannot cover
        // this: its slice starts at `var recommendationCardsRefreshTimer`, which is AFTER that
        // call site, so a bootstrap that runs before `let recommendationLoadGeneration = 0`
        // is initialized would reject with a ReferenceError there and leave the tab blank.
        const string harness = """
            const assert = require('node:assert/strict');
            function element(tag) {
                return {
                    tag, className: "", textContent: "", title: "", type: "", dataset: {},
                    children: [], hidden: false, addEventListener() {},
                    appendChild(child) {
                        if (child.parent) child.remove();
                        child.parent = this;
                        this.children.push(child);
                        return child;
                    },
                    remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); },
                    append(...items) { for (const item of items) this.appendChild(item); },
                    replaceChildren() { this.children = []; },
                    set innerHTML(value) { this.children = []; }
                };
            }
            const recommendationsGrid = element("div");
            const recommendationsEmpty = element("div");
            const weeklyMissingGrid = element("div");
            const weeklySimilarGrid = element("div");
            const document = {
                getElementById(id) {
                    if (id === "recommendationsGrid") return recommendationsGrid;
                    if (id === "recommendationsEmpty") return recommendationsEmpty;
                    if (id === "weeklyMissingRecommendationsGrid") return weeklyMissingGrid;
                    if (id === "weeklySimilarRecommendationsGrid") return weeklySimilarGrid;
                    return null; // playlist sections stay disabled
                },
                createElement: (tag) => element(tag)
            };
            const requests = [];
            async function fetch(url) {
                requests.push(url);
                if (url.includes("/api/library/folders")) return { ok: true, json: async () => [{ libraryId: 1 }] };
                if (url.includes("/api/library/recommendations/stations")) {
                    return { ok: true, json: async () => [{ id: "daily-rotation:l1:f1", type: "daily-rotation", name: "Recommendations - Music", status: "ready" }] };
                }
                return { ok: false, status: 404, json: async () => [] };
            }
            """;
        const string assertions = """
            (async () => {
                for (let i = 0; i < 50; i++) await new Promise(resolve => setImmediate(resolve));
                assert.ok(requests.some(url => url.includes("/api/library/folders")), 'The bootstrap must request the recommendation folder scope');
                assert.ok(requests.some(url => url.includes("/api/library/recommendations/stations")), 'The bootstrap must request station cards');
                assert.equal(recommendationsGrid.children.length, 1, 'The daily grid must render a station card');
                assert.equal(recommendationsEmpty.hidden, true, 'The empty state must hide once a card rendered');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;
        var script = harness + source + assertions;

        var info = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(script);
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task RefreshNoticeDoesNotEraseSavedTrackRows()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "DeezSpoTag.Web", "Views", "Tracklist"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));
        var begin = source.IndexOf("let recommendationReloadTimer;", StringComparison.Ordinal);
        var finish = source.IndexOf("function mapRecommendationsTracklist", begin, StringComparison.Ordinal);
        source = source[begin..finish] + "function mapRecommendationsTracklist";
        var script = """
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=SOURCE;
const start=source.indexOf('let recommendationReloadTimer;');
const end=source.indexOf('function mapRecommendationsTracklist',start);
let emptied=0,notice='';
const context={URLSearchParams,clearTimeout,setTimeout,tracklistSource:'recommendations',tracklistId:'library:1:weekly-missing-favourites',recommendationType:'',recommendationValue:'',tracklistLibraryId:1,
 fetch:async()=>({ok:true,json:async()=>({station:{cadence:'weekly'},tracks:[{id:'saved'}],generatedAtUtc:'2026-10-05T00:00:00Z',status:'refreshing',message:'Previous selection'})}),
 mapRecommendationsTracklist:p=>p,loadIgnoredTracks:async()=>{},renderTracklist:()=>{},setTracksEmptyMessage:()=>emptied++,
 document:{getElementById:()=>({set textContent(v){notice=v;},classList:{remove(){}}})}};
vm.createContext(context);vm.runInContext(source.slice(start,end),context);
(async()=>{await context.loadRecommendationsTracklist();vm.runInContext('clearTimeout(recommendationReloadTimer)',context);assert.equal(emptied,0,'A refresh message must not erase saved tracks');assert.match(notice,/Previous selection/);})().catch(e=>{console.error(e);process.exitCode=1;});
""".Replace("SOURCE", JsonSerializer.Serialize(source), StringComparison.Ordinal);
        var info = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(script);
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task SavedDailyReadNeedsNoProviderOrDedupeAndDoesNotStartMissingWeeklyJobs()
    {
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var today = DateOnly.FromDateTime(DateTime.Now);
        var track = new RecommendationTrackDto("12345", "Saved", 180, "", 1, new("1", "Artist"), new("1", "Album", ""));
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", id, $"v1:{today:yyyyMMdd}",
            JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track } }), 0, null, null, true);
        Assert.Single((await Service().GetRecommendationsAsync(Folder.LibraryId!.Value, id))!.Tracks);
        var stations = await Service().GetStationsAsync(Folder.LibraryId.Value);
        Assert.Contains(stations, s => s.Id == id && s.TrackCount == 1);
        Assert.Null(await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value, Folder.Id, $"weekly-rotation:l{Folder.LibraryId}:f{Folder.Id}:{LibraryRecommendationService.WeeklyMissingType}", Monday));
        Assert.Null(await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value, Folder.Id, $"weekly-rotation:l{Folder.LibraryId}:f{Folder.Id}:{LibraryRecommendationService.WeeklySimilarType}", Monday));
    }

    [Fact]
    public async Task FailedDailyRefreshKeepsSavedTracksAndReportsFailure()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var track = new RecommendationTrackDto("12345", "Saved", 180, "", 1, new("1", "Artist"), new("1", "Album", ""));
        await Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", id, $"v1:{day:yyyyMMdd}",
            JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track } }), 0, null, null, true);
        var key = new RecommendationGenerationStateKey(Folder.LibraryId!.Value, Folder.Id, id, day);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "manual-rebuild"));
        await Repository.FailRecommendationGenerationAsync(key, "provider_failed", "unavailable");
        var result = await Service().GetRecommendationsAsync(key.LibraryId, id);
        Assert.Single(result!.Tracks);
        Assert.Equal("refresh_failed", result.Status);
        Assert.Contains("failed", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WeeklyCacheReadDoesNotWaitForGenerationOrMappingGates()
    {
        var id = await SeedWeekly(Monday);
        var service = Service();
        var gates = new[] { "_weeklyGenerationGate", "_weeklyMappingGate" }.Select(name =>
            (System.Threading.SemaphoreSlim)typeof(LibraryRecommendationService).GetField(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!).ToList();
        foreach (var gate in gates) await gate.WaitAsync();
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Single((await service.GetRecommendationsAsync(Folder.LibraryId!.Value, id, cancellationToken: timeout.Token))!.Tracks);
        }
        finally { foreach (var gate in gates) gate.Release(); }
    }

    [Fact]
    public async Task NativeWeeklySelectionPublishesBeforeOptionalMetadataIsAvailable()
    {
        var id = LibraryRecommendationService.BuildWeeklyStationId(Folder.LibraryId!.Value, Folder.Id, LibraryRecommendationService.WeeklySimilarType);
        var track = new RecommendationTrackDto("spotify-native", "Song", 180, "GB0000000001", 1,
            new("artist", "Artist"), new("album", "Album", ""), "spotify");
        var detail = new RecommendationDetailDto(new(id, "Weekly", "", LibraryRecommendationService.WeeklySimilarType, "Music", 1),
            [track], DateTimeOffset.UtcNow, ReserveTracks: [track with { Id = "reserve-native" }], WeeklyTrackCount: 1, SeedScopeKey: id);
        var key = new RecommendationGenerationStateKey(Folder.LibraryId.Value, Folder.Id, id, Monday);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        var method = typeof(LibraryRecommendationService).GetMethod("PublishWeeklyRecommendationResultAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        // No Spotify/Deezer network clients are wired. Saving the confirmed Spotify selection must not need them.
        await (Task)method.Invoke(Service(), [key, detail, System.Threading.CancellationToken.None])!;
        var state = await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value, Folder.Id, id, Monday);
        Assert.Equal("completed", state!.Status);
        var result = (await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id))!;
        Assert.Equal("spotify-native", Assert.Single(result.Tracks).Id);
        Assert.Equal("spotify", result.Tracks[0].Source);
        Assert.Contains("weekly_mapping_incomplete", result.ReasonCodes!);
        Assert.Contains("pending", result.Message!);
    }

    [Fact]
    public async Task DeferredReserveMetadataDoesNotPreventWeeklyPublicationOrLoseSpotifyIdentity()
    {
        var id = LibraryRecommendationService.BuildWeeklyStationId(Folder.LibraryId!.Value, Folder.Id, LibraryRecommendationService.WeeklySimilarType);
        var selected = new RecommendationTrackDto("selected-spotify", "Song", 180, "", 1,
            new("artist", "Artist"), new("album", "Album", ""), "spotify", DeezerId: "123", MappingStatus: "matched");
        var reserve = selected with { Id = "reserve-spotify", DeezerId = null, MappingStatus = null };
        var detail = new RecommendationDetailDto(new(id, "Weekly", "", LibraryRecommendationService.WeeklySimilarType, "Music", 1),
            [selected], DateTimeOffset.UtcNow, ReserveTracks: [reserve], WeeklyTrackCount: 1, SeedScopeKey: id);
        var method = typeof(LibraryRecommendationService).GetMethod("MapWeeklyTracksAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var mapped = await (Task<RecommendationDetailDto>)method.Invoke(Service(), [detail, System.Threading.CancellationToken.None])!;
        var key = new RecommendationGenerationStateKey(Folder.LibraryId.Value, Folder.Id, id, Monday);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key, "recommendations-weekly-pool", id,
            $"v1:{Monday:yyyyMMdd}", JsonSerializer.Serialize(mapped)));
        var result = (await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id))!;
        Assert.Equal("selected-spotify", Assert.Single(result.Tracks).Id);
        Assert.Equal("spotify", result.Tracks[0].Source);
        Assert.Equal("ready", result.Status);
        Assert.Contains("weekly_mapping_incomplete", result.ReasonCodes!);
        Assert.Contains("reserve", result.Message!);
        var saved = await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id);
        Assert.Contains("reserve-spotify", saved!.CandidatesJson);
    }

    [Fact]
    public async Task WeeklyGenerationPreservesSafeStageAndFailureReasonForApiConsumers()
    {
        var id = LibraryRecommendationService.BuildWeeklyStationId(Folder.LibraryId!.Value, Folder.Id, LibraryRecommendationService.WeeklySimilarType);
        var generate = typeof(LibraryRecommendationService).GetMethod("GenerateWeeklyRecommendationsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)generate.Invoke(Service(), [Folder.LibraryId.Value, Folder.Id, LibraryRecommendationService.WeeklySimilarType,
            Monday, "scheduled", System.Threading.CancellationToken.None])!;
        var state = await Repository.GetRecommendationGenerationStateAsync(Folder.LibraryId.Value, Folder.Id, id, Monday);
        Assert.Equal("pathfinder_authentication_unavailable", state!.ReasonCode);
        Assert.Contains("spotify_candidates", state.LastError!);
        Assert.Contains("InvalidOperationException", state.LastError!);
        var result = (await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id))!;
        Assert.Contains("pathfinder_authentication_unavailable", result.ReasonCodes!);
        Assert.Equal(result.ReasonCodes, result.Station.ReasonCodes);
        Assert.Contains("authentication", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedWeeklyProviderGenerationPreservesPreviousSelection()
    {
        var id = await SeedWeekly(Monday.AddDays(-7));
        var before = await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id);
        var method = typeof(LibraryRecommendationService).GetMethod("GenerateWeeklyRecommendationsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(Service(), [Folder.LibraryId!.Value, Folder.Id, LibraryRecommendationService.WeeklyMissingType, Monday, "scheduled", System.Threading.CancellationToken.None])!;
        var after = await Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id);
        Assert.Equal(before!.CandidatesJson, after!.CandidatesJson);
        Assert.Equal(before.SnapshotId, after.SnapshotId);
        var response = await Service().GetRecommendationsAsync(Folder.LibraryId.Value, id);
        Assert.Single(response!.Tracks);
        Assert.Equal("refresh_failed", response.Status);
        Assert.Contains("failed", response.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pathfinder_authentication_unavailable", response.ReasonCodes!);
        Assert.Equal(response.ReasonCodes, response.Station.ReasonCodes);
    }

    internal sealed class EnvironmentFixture : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    [Fact]
    public async Task YesterdayDailyPoolIsNeverReturnedAsTodaysSelection()
    {
        var clock = new ArtworkClock();
        var yesterday = new DateOnly(2026, 10, 8);
        var id = $"daily-rotation:l{Folder.LibraryId}:f{Folder.Id}";
        var key = new RecommendationGenerationStateKey(Folder.LibraryId!.Value, Folder.Id, id, yesterday);
        Assert.True(await Repository.TryStartRecommendationGenerationAsync(key, "test"));
        var row = new RecommendationTrackDto("1", "Yesterday", 180, "", 1, new("a", "Artist"), new("b", "Album", ""));
        Assert.True(await Repository.PublishRecommendationSnapshotAsync(key, "recommendations-daily-pool", id,
            "v1:20261008", JsonSerializer.Serialize(new { SeedScopeKey = id, GeneratedAtUtc = clock.GetUtcNow().AddDays(-1), Tracks = new[] { row }, TrackCount = 60 })));
        var result = (await Service(clock).GetRecommendationsAsync(key.LibraryId, id))!;
        Assert.Empty(result.Tracks);
        Assert.Equal(0, result.Station.TrackCount);
        Assert.Equal("Friday's recommendation for Music.", result.Station.Description);
        Assert.Equal(new DateOnly(2026, 10, 9), result.GetType().GetProperty("DailyDateLocal")!.GetValue(result));
        Assert.Equal(new DateTimeOffset(2026,10,10,0,0,0,TimeSpan.Zero), result.GetType().GetProperty("ExpiresAtUtc")!.GetValue(result));
    }

}
