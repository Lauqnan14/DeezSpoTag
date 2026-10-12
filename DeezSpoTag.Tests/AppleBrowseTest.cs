using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleBrowseTest
{
    [Fact]
    public void HomeCategoriesKeepProviderRowsUniqueAndIncludeSpatialAudio()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
        const source=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        const ctx=vm.createContext({console,URLSearchParams,escapeHtml:String,renderTopGenresItem:i=>`<article>${i.title}</article>`});
        vm.runInContext("let spotifyHomeCategories=[],homeAppleCategories=[],homeYouTubeCategories=[],homeSpotifyStatus='ready';const homeCategoryStatus={apple:'ready',ytmusic:'ready'};",ctx);
        for(const name of ['homeCategoryKey','selectHomeCategoryRows','renderHomeCategoryRows','loadHomeCategories','refreshHomeCategories']){
          const match=source.match(new RegExp('(?:async )?function '+name+'\\('));assert(match, name+' must exist');
          const start=match.index,end=source.slice(start+match[0].length).search(/\n(?:async )?function /);
          vm.runInContext(source.slice(start,end<0?undefined:start+match[0].length+end),ctx);
        }
        vm.runInContext(`
          const item=(title,source)=>({title,source,type:'browse-category',target:'/category/'+title});
          const deezer=['Hip-Hop','New releases','Podcasts','Charts','For you','Chill'].map(t=>item(t,'deezer'));
          spotifyHomeCategories=['Hip Hop','New Releases','Made For You','Music','Live Events','Afro','Pop','Dance','Spatial Audio'].map(t=>item(t,'spotify'));
          homeAppleCategories=['Hip-Hop/Rap','Music Videos','Spatial Audio','African','Afrobeats','Jazz','Family','Classical','Rock'].map(t=>item(t,'apple'));
          homeYouTubeCategories=['Hip-hop','Pop','Jazz','Chill','Commute','Focus','Workout','Gaming'].map(t=>item(t,'ytmusic'));
          globalThis.rows=selectHomeCategoryRows(deezer);globalThis.markup=renderHomeCategoryRows(deezer);
        `,ctx);
        assert.deepEqual(Array.from(ctx.rows,r=>r.items.length),[5,5,5,4]);
        assert.deepEqual(Array.from(ctx.rows,r=>r.source),['deezer','spotify','apple','ytmusic']);
        const items=Array.from(ctx.rows).flatMap(r=>Array.from(r.items));
        assert.equal(new Set(items.map(ctx.homeCategoryKey)).size,19);
        assert(ctx.rows[2].items.some(i=>i.title==='Spatial Audio'));
        assert(!items.some(i=>/video/i.test(i.title)));
        assert.equal((ctx.markup.match(/data-category-provider=/g)||[]).length,4);
        assert.equal((ctx.markup.match(/href="\/Categories"/g)||[]).length,1);
        assert(ctx.markup.lastIndexOf('Show more')>ctx.markup.lastIndexOf('<article>'));
        vm.runInContext("homeAppleCategories=[];homeCategoryStatus.apple='loading';",ctx);
        assert(ctx.renderHomeCategoryRows([]).includes('Loading Apple Music'));
        let renders=0;
        const row={dataset:{deezerItems:encodeURIComponent('[]')},set innerHTML(value){renders++;assert(value.includes('data-category-provider="apple"'))}};
        ctx.document={querySelector:()=>row};
        ctx.fetch=async()=>({ok:true,json:async()=>({success:true,categories:[{title:'Spatial Audio',type:'browse-category'}]})});
        vm.runInContext('let homeLoadRequestSeq=1;',ctx);
        (async()=>{
          await ctx.loadHomeCategories('apple',0);assert.equal(renders,0,'Obsolete responses must not redraw categories');
          await ctx.loadHomeCategories('apple',1);assert.equal(renders,1);
          assert.equal(vm.runInContext('homeCategoryStatus.apple',ctx),'ready');
          ctx.fetch=async()=>{throw Error('unavailable')};
          await ctx.loadHomeCategories('ytmusic',1);assert.equal(renders,2);
          assert.equal(vm.runInContext('homeCategoryStatus.ytmusic',ctx),'error');
        })().catch(error=>{console.error(error);process.exitCode=1});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void ProviderCompletionPreservesBaseSectionsAndDefersOffscreenCards()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
        const source=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        let fullRenders=0,insertions=0,cards=0,observer;
        const container={querySelectorAll:()=>[],querySelector:()=>null,insertAdjacentHTML:()=>insertions++};
        const ctx=vm.createContext({console,Map,URLSearchParams,escapeHtml:String,
          document:{getElementById:()=>container},location:{search:''},
          IntersectionObserver:class{constructor(callback){observer=callback}observe(){}unobserve(){}},
          HomeViewHelpers:{renderAppleBrowseItem:()=>{cards++;return 'card'}},
          renderHomeItem:()=>{cards++;return 'card'},renderHomeSectionsWithLazyImages:()=>fullRenders++,
          observeLazyImages:()=>{},syncHomeTrendingPlaybackFromSession:()=>{},
          fetch:async()=>({ok:true,json:async()=>({success:true,sections:[{role:'new-this-week',title:'New this week',items:[{title:'Album'}]}]})})});
        vm.runInContext("let homeLoadRequestSeq=1,homeLatestSections=[{title:'Base'}],homeAppleSections=[],homeAppleStatus='loading',homeTrendingProvider='spotify';const homeDeferredRowRenders=new Map();let homeDeferredRowSeq=0;let homeDeferredRowObserver;",ctx);
        function load(name){const match=source.match(new RegExp('(?:async )?function '+name+'\\('));if(!match)return;const start=match.index,end=source.slice(start+match[0].length).search(/\n(?:async )?function /);vm.runInContext(source.slice(start,end<0?undefined:start+match[0].length+end),ctx)}
        for(const name of ['loadAppleHomeSections','refreshHomeProviderSections','renderAppleHomeRows','renderDeferredHomeRow','observeHomeDeferredRows','hydrateHomeDeferredRow','removeHomeProviderSections'])load(name);
        (async()=>{
          await ctx.loadAppleHomeSections(1);
          assert.equal(fullRenders,0,'Apple completion must not rebuild existing sections');
          assert(insertions>0,'Apple completion must insert its own sections');
          assert.equal(cards,0,'Cards outside the viewport must remain unrendered');
          const key=vm.runInContext('[...homeDeferredRowRenders.keys()][0]',ctx);
          const row={dataset:{homeDeferredRow:key},style:{},innerHTML:'',querySelectorAll:()=>[]};
          ctx.hydrateHomeDeferredRow(row);
          assert.equal(cards,1);assert.equal(row.innerHTML,'card');
          ctx.hydrateHomeDeferredRow(row);assert.equal(cards,1,'Rows must hydrate only once');
          await ctx.loadAppleHomeSections(0);assert.equal(fullRenders,0);assert.equal(insertions,1,'Obsolete responses must not update the page');
        })().catch(error=>{console.error(error);process.exitCode=1});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void HomeTrendingProvidersShareLoadingAnimationAndRestoreRedrawnButtons()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.Directory.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "Views"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
            const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
            const root = process.argv[1], home = fs.readFileSync(root + '/DeezSpoTag.Web/wwwroot/js/home-index.js', 'utf8');
            const classList = () => {
                const classes = new Set();
                return { toggle(name, value) { if (value) classes.add(name); else classes.delete(name); }, remove(...names) { names.forEach(name => classes.delete(name)); }, contains: name => classes.has(name) };
            };
            class Button {
                constructor(dataset) { this.dataset = dataset; this.classList = classList(); this.row = { classList: classList() }; this.isConnected = true; }
                closest() { return this.row; }
                matches() { return this.dataset.homeTrendingTrack === 'true'; }
                setAttribute() {}
                removeAttribute() {}
            }
            let buttons = [], request, releaseFetch;
            const ctx = vm.createContext({ console, URLSearchParams, HTMLElement: Button, escapeHtml: value => String(value || ''),
                document: { querySelectorAll: () => buttons }, localStorage: { getItem: () => null },
                fetch: () => new Promise(resolve => releaseFetch = resolve)
            });
            vm.runInContext(fs.readFileSync(root + '/DeezSpoTag.Web/wwwroot/js/deezer-playback-state.js', 'utf8'), ctx);
            vm.runInContext(fs.readFileSync(root + '/DeezSpoTag.Web/wwwroot/js/home-view-helpers.js', 'utf8'), ctx);
            ctx.setHomeTrendingPreviewButtonState = ctx.HomeViewHelpers.setHomeTrendingPreviewButtonState;
            vm.runInContext(home.slice(home.indexOf('function setHomeTrendingPlaybackState('), home.indexOf('function clearHomeTrendingPreviewButton(')), ctx);
            vm.runInContext(home.slice(home.indexOf('function getHomeTrendingPlaybackButtons('), home.indexOf('function syncHomeTrendingPlaybackFromSession(')), ctx);
            ctx.DeezerUnifiedPlayback = { play: async options => {
                request = options;
                options.onStateChange('requested');
                await options.resolve();
                options.onStateChange('playing');
            } };
            function state(button) {
                return [button.classList.contains('is-starting'), button.classList.contains('is-playing'), button.row.classList.contains('is-playing'), button.dataset.playbackState];
            }
            (async () => {
                const spotify = new Button({ homeTrendingTrack: 'true', spotifyUrl: 'https://open.spotify.com/track/test' });
                buttons = [spotify];
                ctx.setHomeTrendingPlaybackState(spotify, 'requested');
                const requested = state(spotify);
                ctx.setHomeTrendingPlaybackState(spotify, 'playing');
                const playing = state(spotify);
                for (const provider of ['apple', 'youtube']) {
                    const markup = provider === 'apple' ? ctx.HomeViewHelpers.renderAppleBrowseItem({ type: 'track', id: '123', title: 'Song' }) : '';
                    const dataset = provider === 'apple'
                        ? { appleId: '123', appleType: 'track', homeTrendingTrack: markup.includes('data-home-trending-track="true"') ? 'true' : '' }
                        : { youtubeId: 'dQw4w9WgXcQ', homeTrendingTrack: 'true' };
                    const button = new Button(dataset);
                    buttons = [button];
                    request = null;
                    const pending = provider === 'apple' ? ctx.HomeViewHelpers.playAppleBrowsePreview(button) : ctx.HomeViewHelpers.playYouTubeBrowse(button);
                    await new Promise(resolve => setImmediate(resolve));
                    assert.deepEqual(state(button), requested, provider + ' must animate before URL resolution finishes');
                    releaseFetch({ ok: true, json: async () => provider === 'apple' ? { success: true, previewUrl: 'https://preview.test/audio' } : { success: true, url: '/api/youtube/browse/stream?id=dQw4w9WgXcQ' } });
                    await pending;
                    assert.deepEqual(state(button), playing);
                    button.isConnected = false;
                    const redrawn = new Button({ ...dataset });
                    buttons = [redrawn];
                    const restored = ctx.findHomeTrendingPlaybackButtonFromSession({ button, sourceKey: request.sourceKey });
                    assert.equal(restored, redrawn, provider + ' must find its replacement after a redraw');
                    ctx.setHomeTrendingPlaybackState(restored, 'playing');
                    assert.deepEqual(state(redrawn), playing);
                    for (const terminal of ['paused', 'ended', 'error']) {
                        request.onStateChange(terminal);
                        assert.equal(button.classList.contains('is-starting'), false);
                        assert.equal(button.row.classList.contains('is-playing'), false);
                    }
                }
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static MethodInfo RequiredMethod(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!;
    }

    [Fact]
    public void TrendingTracklist_HydratesMetadataWithoutBlockingRenderingOrChangingIds()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(root.FullName, "DeezSpoTag.Web"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('fs'),vm=require('vm'),assert=require('assert');
        const view=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/Views/Tracklist/Index.cshtml','utf8');
        const part=view.slice(view.indexOf('function isHomeTrendingTracklistView()'),view.indexOf('async function loadTracklist()'));
        (async()=>{for(const source of ['apple','ytmusic'])for(const mismatch of [false,true]){
        let rendered,queries=[];const cells={'.track-album':{textContent:''},'.track-duration':{textContent:''},'.track-checkbox':{dataset:{}}};
        const id=source==='apple'?'123':'abcdefghijk';
        const row={dataset:{availabilityUrl:source==='apple'?'/song/song/123':`https://music.youtube.com/watch?v=${id}`},querySelector:s=>cells[s]};const ctx={document:{querySelector:()=>row},formatTrackDurationSeconds:d=>String(d),URLSearchParams,console:{warn(){}},tracklistSource:source,resetSpotifyState(){},loadIgnoredTracks:async()=>{},renderTracklist:d=>rendered=d,fetch:async url=>{
        queries.push(url);
        if(url.endsWith('/sections'))return{ok:true,json:async()=>({storefront:'ke',sections:[{role:'trending',items:[{id,title:'Song',artist:'Artist',appleUrl:'/song/song/123',coverUrl:'cover'}]}]})};
        assert(rendered,'The tracklist must render before individual metadata requests');return{ok:true,json:async()=>source==='apple'?{tracklist:{tracks:[{id:mismatch?'999':id,album:{title:'Album'},duration:201}]}}:{success:true,track:{videoId:mismatch?'xxxxxxxxxxx':id,album:'Album',durationSeconds:201}}};
        }};vm.createContext(ctx);vm.runInContext(part,ctx);await ctx.loadBrowseTrendingTracklist();
        assert.equal(queries.length,2);assert.equal(rendered.tracks[0].album.title,mismatch?'':'Album');assert.equal(rendered.tracks[0].duration,mismatch?0:201);
        assert.equal(cells['.track-album'].textContent,mismatch?'':'Album');assert.equal(cells['.track-duration'].textContent,mismatch?'':'201');if(!mismatch)assert.equal(cells['.track-checkbox'].dataset.trackDurationMs,'201000');if(source==='apple')assert(queries[1].includes('appleUrl=https%3A%2F%2Fmusic.apple.com%2Fke%2Fsong%2Fsong%2F123'));
        else assert.equal(rendered.tracks[0].id,'');
        }console.log('Apple/YouTube exact-ID hydration and Apple storefront routing passed');})().catch(e=>{console.error(e);process.exit(1)});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void BrowseSong_PreservesAlbumAndDuration()
    {
        using var document = JsonDocument.Parse("""
        {"contentDescriptor":{"kind":"song","url":"/ke/song/song/123","identifiers":{"storeAdamID":"123"}},"title":"Song","artistName":"Artist","albumName":"Album","durationInMillis":201000}
        """);
        var item = Assert.IsType<JsonObject>(RequiredMethod(typeof(AppleCatalogJsonHelper), "MapBrowseItem").Invoke(null, [document.RootElement, false]));
        Assert.Equal("Album", item["albumName"]!.GetValue<string>());
        Assert.Equal(201, item["durationSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void BrowseHtml_UsesStructuredDataAndRejectsMissingPayload()
    {
        var method = RequiredMethod(typeof(AppleMusicCatalogService), "ParseBrowsePage");
        using var result = Assert.IsType<JsonDocument>(method.Invoke(null, ["<html><script type='application/json' id='serialized-server-data'>{\"data\":[]}</script></html>"]));
        Assert.Equal(JsonValueKind.Array, result.RootElement.GetProperty("data").ValueKind);
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, ["<html><h2>New This Week</h2></html>"]));
        Assert.IsAssignableFrom<Exception>(error.InnerException);
    }

    [Theory]
    [InlineData("https://example.com/room/12")]
    [InlineData("//example.com/search")]
    [InlineData("../search")]
    [InlineData("room/%2e%2e/search")]
    [InlineData("search?url=https://example.com")]
    public async Task BrowseRequest_RejectsUntrustedPaths(string path)
    {
        using var handler = new BrowseHandler();
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);
        var method = RequiredMethod(typeof(AppleMusicCatalogService), "GetBrowsePageAsync");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            var task = Assert.IsAssignableFrom<Task<JsonDocument>>(method.Invoke(service, [path, "ke", "en-US", CancellationToken.None, false]));
            using var doc = await task;
        });
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BrowseCache_IsolatesStorefrontAndLanguageWithoutAuthorization()
    {
        using var handler = new BrowseHandler();
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);
        var method = RequiredMethod(typeof(AppleMusicCatalogService), "GetBrowsePageAsync");
        foreach (var (store, language) in new[] { ("ke", "en-US"), ("ke", "en-US"), ("us", "en-US"), ("us", "es-MX") })
        {
            using var doc = await Assert.IsAssignableFrom<Task<JsonDocument>>(method.Invoke(service, ["search", store, language, CancellationToken.None, false]));
        }
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/ke/search", handler.Requests[0]);
        Assert.Contains("/us/search", handler.Requests[1]);
        Assert.DoesNotContain(handler.AuthorizationPresent, value => value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentBrowseReadsShareOneProviderRequest(bool refresh)
    {
        using var handler = new BrowseHandler();
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);
        if (refresh)
        {
            using var initial = await service.GetBrowsePageAsync("new", "ke", "en-US", CancellationToken.None);
            handler.Requests.Clear();
            handler.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        handler.HoldResponse = true;
        var first = service.GetBrowsePageAsync("new", "ke", "en-US", CancellationToken.None, refresh);
        await handler.Started.Task;
        var second = service.GetBrowsePageAsync("new", "ke", "en-US", CancellationToken.None, refresh);
        handler.Release.TrySetResult();
        using var a = await first;
        using var b = await second;
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BrowseRefresh_ReplacesCacheBeforeNextForegroundRead()
    {
        using var handler = new BrowseHandler();
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);
        using var initial = await service.GetBrowsePageAsync("new", "ke", "en-US", CancellationToken.None);
        using var refreshed = await service.GetBrowsePageAsync("new", "ke", "en-US", CancellationToken.None, refresh: true);
        using var foreground = await service.GetBrowsePageAsync("new", "ke", "en-US", CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Storefront_UsesExistingAccountResolutionAndConfiguredStoreWhenSignedOut()
    {
        using var handler = new BrowseHandler { AccountResponse = "{\"meta\":{\"subscription\":{\"storefront\":\"ke\",\"active\":true}}}" };
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);
        Assert.Equal("us", await service.ResolveStorefrontAsync("us", null, CancellationToken.None));
        typeof(AppleMusicCatalogService).GetField("_cachedToken", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, "existing-token");
        typeof(AppleMusicCatalogService).GetField("_cachedTokenExpiresAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("ke", await service.ResolveStorefrontAsync("us", "account-token", CancellationToken.None));
    }

    [Fact]
    public void Categories_PreserveOrderArtworkAndCanonicalDestinationInsteadOfLegacyItunesLink()
    {
        using var doc = JsonDocument.Parse("""
        {"data":[{"data":{"sections":[{"id":"browse-category-bricks - dynamic","itemKind":"brickLockup","header":{"item":{"titleLink":{"title":"Browse Categories"}}},"items":[
        {"title":"Afrobeats","contentDescriptor":{"kind":"appleCurator","identifiers":{"storeAdamID":"123"},"url":"https://music.apple.com/ke/curator/afrobeats/123"},"artwork":{"dictionary":{"url":"https://images.test/{w}x{h}{c}.{f}","width":1000,"height":1000}}},
        {"title":"Charts","contentDescriptor":{"kind":"editorialItem","identifiers":{"storeAdamID":"456"},"url":"https://itunes.apple.com/old"},"segue":{"destination":{"intent":{"contentDescriptor":{"url":"/us/new/top-charts"}}}}}
        ]}]}}]}
        """);
        var sections = Map(doc.RootElement, true);
        Assert.Single(sections);
        var items = Assert.IsType<JsonArray>(sections[0]!["items"]);
        Assert.Equal("Afrobeats", items[0]!["title"]!.GetValue<string>());
        Assert.Equal("apple", items[0]!["source"]!.GetValue<string>());
        Assert.DoesNotContain("{w}", items[0]!["coverUrl"]!.GetValue<string>());
        Assert.Equal("/us/new/top-charts", items[1]!["target"]!.GetValue<string>());
    }

    [Fact]
    public void EditorialItems_UseNativeAppleIdentityAndIgnoreUnsupportedResources()
    {
        using var doc = JsonDocument.Parse("""
        {"data":[{"data":{"sections":[{"id":"copper-track-swoosh - 987","itemKind":"trackLockup","header":{"item":{"titleLink":{"title":"Trending Songs"}}},"items":[
        {"title":"Song","contentDescriptor":{"kind":"song","identifiers":{"storeAdamID":"789"},"url":"https://music.apple.com/us/album/a/123?i=789"}},
        {"title":"Unsupported","contentDescriptor":{"kind":"artistUploadedVideo","identifiers":{"storeAdamID":"999"},"url":"https://music.apple.com/us/post/999"}}
        ]}]}}]}
        """);
        var sections = Map(doc.RootElement, false);
        var items = Assert.IsType<JsonArray>(sections[0]!["items"]);
        Assert.Single(items);
        Assert.Equal("track", items[0]!["type"]!.GetValue<string>());
        Assert.Equal("789", items[0]!["appleId"]!.GetValue<string>());
        Assert.Equal("trending", sections[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void EditorialRoles_PreserveNativeAlbumsPlaylistsVideosAndSourceDisplayTitles()
    {
        var source = new JsonArray();
        foreach (var (kind, identity, resource, role) in new[] {
            ("squareLockup", "New This Week", "album", "new-this-week"),
            ("squareLockup", "Recent Releases", "album", "recent-releases"),
            ("squareLockup", "Updated Playlists", "playlist", "updated-playlists"),
            ("verticalVideoLockup", "New Music Videos", "musicVideo", "videos") })
        {
            source.Add(new JsonObject {
                ["id"] = kind + " - actual-editorial-id", ["itemKind"] = kind,
                ["header"] = new JsonObject { ["item"] = new JsonObject { ["titleLink"] = new JsonObject { ["title"] = "Source display title" } } },
                ["impressionMetrics"] = new JsonObject { ["fields"] = new JsonObject { ["name"] = identity } },
                ["items"] = new JsonArray(new JsonObject {
                    ["titleLinks"] = new JsonArray(new JsonObject { ["title"] = "Native item" }),
                    ["subtitleLinks"] = new JsonArray(new JsonObject { ["title"] = "Artist" }),
                    ["contentDescriptor"] = new JsonObject { ["kind"] = resource, ["identifiers"] = new JsonObject { ["storeAdamID"] = role }, ["url"] = "https://music.apple.com/us/" + resource + "/" + role }
                })
            });
        }
        using var document = JsonDocument.Parse(new JsonObject { ["data"] = new JsonArray(new JsonObject { ["data"] = new JsonObject { ["sections"] = source } }) }.ToJsonString());
        var mapped = Map(document.RootElement, false);
        Assert.Equal(4, mapped.Count);
        Assert.Equal("video", mapped[3]!["items"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("playlist", mapped[2]!["items"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("updated-playlists", mapped[2]!["role"]!.GetValue<string>());
        Assert.Equal("Source display title", mapped[0]!["title"]!.GetValue<string>());
        Assert.Equal("Artist", mapped[0]!["items"]![0]!["subtitle"]!.GetValue<string>());
    }

    [Fact]
    public void Categories_RejectExternalHostDestinations()
    {
        using var document = JsonDocument.Parse("""
        {"data":[{"data":{"sections":[{"id":"browse-category-bricks","items":[{"title":"Untrusted","contentDescriptor":{"kind":"appleCurator","url":"https://example.com/us/curator/untrusted/123"}}]}]}}]}
        """);
        Assert.Empty(Map(document.RootElement, true));
    }

    [Fact]
    public void BrowserFixtures_KeepAppleRoutingIndependentAndVideosLastAcrossProviderChanges()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !System.IO.File.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('fs'), vm=require('vm'), assert=require('assert');
        const root=process.argv[1], home=fs.readFileSync(root+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        const browseController=fs.readFileSync(root+'/DeezSpoTag.Web/Controllers/Api/AppleBrowseApiController.cs','utf8');assert(!browseController.includes('Parallel.ForEachAsync'), 'Browse must not wait for every preview');
        const homeView=fs.readFileSync(root+'/DeezSpoTag.Web/Views/Home/Index.cshtml','utf8');assert(homeView.includes('~/js/video-preview.js'));
        const ctx={URLSearchParams, console, location:{search:''}, escapeHtml:s=>String(s||'').replaceAll('"','&quot;'), globalThis:null}; ctx.globalThis=ctx;
        vm.createContext(ctx);
        vm.runInContext(fs.readFileSync(root+'/DeezSpoTag.Web/wwwroot/js/home-view-helpers.js','utf8'),ctx);
        const click=ctx.HomeViewHelpers.buildHomeClick({source:'apple',type:'track',id:'123',appleUrl:'https://music.apple.com/us/album/a/1?i=123'});
        assert(click.includes('source=apple')); assert(click.includes('type=track')); assert(!click.includes('source=deezer'));
        const video=ctx.HomeViewHelpers.renderAppleBrowseItem({source:'apple',type:'video',id:'456',title:'Video',previewUrl:'https://preview.test/video'});
        assert(video.includes('HomeViewHelpers.playAppleBrowsePreview'));assert(video.includes('data-apple-type="video"')); assert(video.includes('source=apple'));
        function load(name){const start=home.indexOf('function '+name+'(');assert(start>=0,name+' is missing'); let end=home.indexOf('\nfunction ',start+1);vm.runInContext(home.slice(start,end<0?undefined:end),ctx);}
        ctx.renderHomeItem=i=>i.title;ctx.renderHomeTrendingSongItem=i=>'spotify:'+i.title;ctx.renderHomeSectionCardItem=i=>i.title;
        vm.runInContext("let homeAppleSections=[], homeAppleStatus='loading', homeTrendingProvider='spotify', homeLatestSections=[];const homeDeferredRowRenders=new Map();let homeDeferredRowSeq=0;",ctx);
        load('renderDeferredHomeRow'); load('renderHomeTrendingProviders'); load('renderAppleHomeRows'); load('renderAppleHomeTrending'); load('selectHomeTrendingProvider'); load('renderHomeSectionEntry');
        vm.runInContext("homeAppleSections=[{role:'videos',title:'Videos',items:[{source:'apple',type:'video',title:'Video',id:'456'}]},{role:'updated-playlists',title:'Updated',items:[{title:'Playlist'}]},{role:'new-this-week',title:'New',items:[{title:'Album'}]},{role:'trending',title:'Trending Songs',items:[{source:'apple',type:'track',title:'Apple Song',id:'123'}]},{role:'recent-releases',title:'Recent',items:[{title:'Release'}]}];homeAppleStatus='ready';",ctx);
        const rows=ctx.renderAppleHomeRows(); assert(rows.indexOf('New')<rows.indexOf('Recent'));assert(rows.indexOf('Recent')<rows.indexOf('Updated'));assert(rows.indexOf('Updated')<rows.indexOf('Videos'));
        let rerenders=0;ctx.refreshHomeTrendingSection=()=>rerenders++;
        ctx.selectHomeTrendingProvider('apple');assert.equal(rerenders,1);assert(ctx.renderHomeTrendingProviders().includes('aria-pressed="true"'));
        assert(ctx.renderAppleHomeTrending().includes('Apple Song'));assert(!ctx.renderAppleHomeTrending().includes('data-spotify-url'));
        const previewMarkup=ctx.HomeViewHelpers.renderAppleBrowseItem({source:'apple',type:'track',id:'123',title:'Song',previewUrl:'https://preview.test/audio'});
        const thumbTag=previewMarkup.match(/<div class="home-top-song-item__thumb"[^>]*>/)[0];
        const thumbHandler=thumbTag.match(/onclick="([^"]*)"/);assert(thumbHandler,'Apple artwork must start the preview without requiring hover');
        let previewCalls=0,stopped=false;const savedPlay=ctx.HomeViewHelpers.playAppleBrowsePreview;ctx.HomeViewHelpers.playAppleBrowsePreview=button=>{assert.equal(button.dataset.preview,'https://preview.test/audio');previewCalls++};
        ctx.clickThumb={querySelector:()=>({dataset:{preview:'https://preview.test/audio'}})};ctx.event={preventDefault:()=>{},stopPropagation:()=>stopped=true};
        vm.runInContext('(function(){'+thumbHandler[1]+'}).call(clickThumb)',ctx);assert.equal(previewCalls,1);assert(stopped);ctx.HomeViewHelpers.playAppleBrowsePreview=savedPlay;

        const rerenderedRows=ctx.renderAppleHomeRows();assert(rerenderedRows.lastIndexOf('data-apple-role="videos"')>rerenderedRows.lastIndexOf('data-apple-role="updated-playlists"'));
        ctx.cacheHomeTrendingWatchMeta=()=>{throw Error('Apple must not enter Spotify watchlist')};
        const trendingEntry={section:{title:'Trending Songs'},meta:{isTrendingSongs:true,filteredItems:[{title:'Spotify Song'}],rowClass:'home-trending-grid'}};
        assert(ctx.renderHomeSectionEntry(trendingEntry,0,false).includes('Apple Song'));
        ctx.cacheHomeTrendingWatchMeta=()=> 'spotify-watch';ctx.selectHomeTrendingProvider('spotify');
        const spotifyRendered=ctx.renderHomeSectionEntry(trendingEntry,0,false);assert(spotifyRendered.includes('spotify:Spotify Song'));assert(!spotifyRendered.includes('Apple Song'));
        ctx.selectHomeTrendingProvider('apple');
        vm.runInContext("homeAppleStatus='error';homeAppleSections=[]",ctx);assert(ctx.renderAppleHomeTrending().includes('unavailable'));
        const categories=fs.readFileSync(root+'/DeezSpoTag.Web/Views/Categories/Index.cshtml','utf8');assert(categories.includes('categories-apple-tab'));
        assert(categories.includes('@AssetUrl.Versioned(ViewContext, Url, "~/js/home-view-helpers.js")'), 'Categories must resolve the shared helper URL with the existing asset helper');
        assert(categories.includes('@AssetUrl.Versioned(ViewContext, Url, "~/css/home-index.css")'), 'Categories must resolve the shared stylesheet URL');
        const inline=categories.slice(categories.indexOf('<script>')+8,categories.lastIndexOf('</script>'));
        const elements={};ctx.document={addEventListener:()=>{},getElementById:id=>elements[id]||(elements[id]={textContent:'',innerHTML:'',hidden:false}),createElement:()=>({})};
        vm.runInContext(inline,ctx);ctx.escapeHtml=s=>String(s||'');
        ctx.fetch=async url=>({ok:true,json:async()=>({success:true,storefront:'ke',categories:[{source:'apple',type:'browse-category',title:'Afrobeats',id:'9',target:'/curator/afrobeats/9'}],sections:[{title:'Albums',items:[{source:'apple',type:'album',id:'8',title:'Album'}]}]})});
        (async()=>{
            let previewFetches=0, audioPlays=0, videoPlays=0;
            ctx.localStorage={getItem:()=>null};ctx.setHomeTrendingPreviewButtonState=()=>{};
            ctx.DeezerUnifiedPlayback={play:async options=>{assert.equal((await options.resolve()).url,'https://preview.test/lazy');assert.equal(options.source,'apple');audioPlays++}};
            ctx.DeezSpoTagVideoPreview={play:async url=>{assert.equal(url,'https://preview.test/lazy');videoPlays++}};
            const categoryFetch=ctx.fetch;
            ctx.fetch=async url=>{assert(url.startsWith('/api/apple/browse/preview?'));previewFetches++;return {ok:true,json:async()=>({success:true,previewUrl:'https://preview.test/lazy'})}};
            const lazyMarkup=ctx.HomeViewHelpers.renderAppleBrowseItem({source:'apple',type:'track',id:'123',title:'Lazy song'});assert(lazyMarkup.includes('aria-label="Play preview"'));
            const button={dataset:{appleId:'123',appleType:'track'},setAttribute:()=>{},removeAttribute:()=>{}};
            await ctx.HomeViewHelpers.playAppleBrowsePreview(button);await ctx.HomeViewHelpers.playAppleBrowsePreview(button);
            assert.equal(previewFetches,1);assert.equal(audioPlays,2);assert.equal(button.disabled,false);
            const videoButton={dataset:{appleId:'456',appleType:'video'},setAttribute:()=>{},removeAttribute:()=>{}};
            await ctx.HomeViewHelpers.playAppleBrowsePreview(videoButton);assert.equal(previewFetches,2);assert.equal(videoPlays,1);
            ctx.fetch=categoryFetch;
            await ctx.loadAppleCategories();assert(elements['apple-categories'].innerHTML.includes('Afrobeats'));
            await ctx.openAppleCategory(encodeURIComponent('/curator/afrobeats/9'));assert(elements['apple-categories'].innerHTML.includes('Album'));assert.equal(elements['apple-back'].hidden,false);
            ctx.fetch=()=>{throw Error('Returning to categories must use its fresh cached response')};
            await ctx.loadAppleCategories();assert.equal(elements['apple-back'].hidden,true);
            let releasePage;ctx.fetch=()=>new Promise(resolve=>releasePage=resolve);
            const pending=ctx.openAppleCategory(encodeURIComponent('/room/888'));assert(elements['apple-categories'].innerHTML.includes('Afrobeats'));
            releasePage({ok:true,json:async()=>({success:true,storefront:'ke',sections:[]})});await pending;
            vm.runInContext('appleCategoryPages.clear()',ctx);
            vm.runInContext('appleCategoryPages.clear()',ctx);ctx.fetch=async()=>({ok:true,json:async()=>({success:true,storefront:'us',categories:[]})});await ctx.loadAppleCategories();assert(elements['apple-status'].textContent.includes('No Apple Music'));
            vm.runInContext('appleCategoryPages.clear()',ctx);ctx.fetch=async()=>({ok:true,json:async()=>({success:true,storefront:'us',categories:[{source:'apple',type:'browse-category',title:'US Category',id:'4',target:'/curator/us-category/4'}]})});await ctx.loadAppleCategories();assert(elements['apple-status'].textContent.includes('US'));assert(elements['apple-categories'].innerHTML.includes('US Category'));assert(!elements['apple-categories'].innerHTML.includes('Afrobeats'));
            vm.runInContext('appleCategoryPages.clear()',ctx);ctx.fetch=async()=>({ok:false,json:async()=>({error:'unavailable'})});await ctx.loadAppleCategories();assert(elements['apple-status'].textContent.includes('unavailable'));
            vm.runInContext(home.slice(home.indexOf('async function loadAppleHomeSections('),home.indexOf('function renderHomeTrendingProviders(')),ctx);
            vm.runInContext("let homeLoadRequestSeq=7;homeLatestSections=[{title:'Latest Spotify enhancement'}];homeAppleStatus='loading';",ctx);
            ctx.fetch=async()=>({ok:true,json:async()=>({success:true,sections:[{role:'trending',items:[{source:'apple',type:'track',title:'Late Apple Song',id:'12'}]},{role:'videos',title:'Videos',items:[{source:'apple',type:'video',title:'Late Video',id:'13'}]}]})});
            let latestBase;ctx.refreshHomeProviderSections=()=>latestBase=vm.runInContext('homeLatestSections',ctx);await ctx.loadAppleHomeSections(7);
            assert.equal(latestBase[0].title,'Latest Spotify enhancement');assert(ctx.renderAppleHomeTrending().includes('Late Apple Song'));assert(ctx.renderHomeTrendingProviders().includes('aria-pressed="true" onclick="selectHomeTrendingProvider(\'apple\')"'));
            assert(ctx.renderAppleHomeRows().includes('data-apple-role="videos"'));
        })().catch(e=>{console.error(e);process.exitCode=1});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static JsonArray Map(JsonElement root, bool categoriesOnly) =>
        Assert.IsType<JsonArray>(RequiredMethod(typeof(AppleCatalogJsonHelper), "MapBrowseSections").Invoke(null, [root, categoriesOnly]));

    private static AppleMusicCatalogService CreateService(HttpClient client, IMemoryCache cache) =>
        new(new ClientFactory(client), null!, NullLogger<AppleMusicCatalogService>.Instance, cache);

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class BrowseHandler : HttpMessageHandler
    {
        public bool HoldResponse;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Requests { get; } = [];
        public List<bool> AuthorizationPresent { get; } = [];
        public string? AccountResponse { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.ToString());
            AuthorizationPresent.Add(request.Headers.Authorization != null);
            Started.TrySetResult();
            if (HoldResponse) await Release.Task.WaitAsync(token);
            var response = request.RequestUri.AbsolutePath.Contains("/me/account")
                ? AccountResponse!
                : "<script id=\"serialized-server-data\" type=\"application/json\">{\"data\":[]}</script>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
