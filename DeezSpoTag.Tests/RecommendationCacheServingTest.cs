using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class RecommendationCacheServingTest : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recommendation-cache-" + Guid.NewGuid().ToString("N"));
    internal LibraryRepository Repository { get; private set; } = null!;
    internal FolderDto Folder { get; private set; } = null!;
    internal string DatabasePath => Path.Combine(_root, "library.db");
    internal LibraryRecommendationService Service(TimeProvider? clock = null) => new(
        new LibraryRecommendationService.LibraryRecommendationCollaborators { Repository = Repository },
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
        var detail = new RecommendationDetailDto(new(id, "Weekly", "", type, null, 1, Cadence: "weekly"), [track], DateTimeOffset.UtcNow.AddDays(-7));
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
                JsonSerializer.Serialize(new { GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track }, StationImageUrl = "/repeated.jpg" }), 0, null, null, true);
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
            JsonSerializer.Serialize(new { GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track } }), 0, null, null, true);
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
            JsonSerializer.Serialize(new { GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track } }), 0, null, null, true);
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
            JsonSerializer.Serialize(new { GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { track } }), 0, null, null, true);
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
}
