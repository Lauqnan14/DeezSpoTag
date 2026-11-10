using System;
using System.Collections.Generic;
using System.Linq;
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
    public void HomeOpeningUsesAvailableAppleCacheWhenUpstreamRefreshFails()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
        const source=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        const calls=[],pending=[];let search='';
        const ctx=vm.createContext({console:{warn:()=>{},log:()=>{},error:console.error},URLSearchParams,Date,
          location:{get search(){return search}},document:{hidden:false},setTimeout:()=>1,clearTimeout:()=>{},
          fetchSpotifyHomeEnhancements:()=>null,loadHomeCategories:()=>{},loadYouTubeHomeTrending:()=>{},loadYouTubeHomeSections:()=>{},
          fetchHomeSections:async()=>[{}],renderHomeSectionsWithLazyImages:()=>{},refreshHomeProviderSections:()=>{},mountAppleHomeSectionsWithoutBase:()=>{},
          fetch:async url=>{calls.push(url);return url.includes('refresh=1')
            ? {ok:false,status:502}
            : {ok:true,json:async()=>({success:true,sections:[{role:'trending',items:[{title:'Available cached song'}]}]})};}});
        vm.runInContext(source.slice(source.indexOf('const homeAppleRefresh ='),source.indexOf('let homeSpotifyStatus')),ctx);
        vm.runInContext("let homeLoadRequestSeq=0,homeLatestSections=[],homeAppleSections=[],homeAppleStatus='loading',homeSpotifyStatus,homeAppleCategories,homeYouTubeCategories,spotifyHomeCategories;const homeCategoryStatus={};",ctx);
        for(const name of ['loadHomeData','loadAppleHomeSections','scheduleAppleHomeRefresh','refreshAppleHomeSectionsIfDue','nextAppleHomeRefreshAt']){
          const m=source.match(new RegExp('(?:async )?function '+name+'\\('));assert(m,name);
          const start=m.index,end=source.slice(start+m[0].length).search(/\n(?:async )?function /);
          vm.runInContext(source.slice(start,end<0?undefined:start+m[0].length+end),ctx);
        }
        const load=ctx.loadAppleHomeSections;ctx.loadAppleHomeSections=(...args)=>{const p=load(...args);pending.push(p);return p};
        (async()=>{
          await ctx.loadHomeData();await Promise.all(pending);
          assert.equal(vm.runInContext('homeAppleStatus',ctx),'ready','A fresh page must display available server-cached Apple content');
          assert.equal(calls[0],'/api/apple/browse/sections');
          assert.equal(vm.runInContext('homeAppleSections[0].items[0].title',ctx),'Available cached song');
          search='?refresh=1';await ctx.loadHomeData();await Promise.all(pending);
          assert(calls.at(-1).includes('refresh=1'),'Explicit refresh must still bypass the server cache');
          assert.equal(vm.runInContext('homeAppleStatus',ctx),'ready','Failed refresh must preserve content already loaded');
        })().catch(e=>{console.error(e);process.exitCode=1});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void AppleHomeRefreshReplacesExpiredTracksWithoutDuplicateRequestsOrPageRedraws()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
        const source=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        let now=100000,calls=[],timers=[],renders=0,hold,fail=false;
        const ctx=vm.createContext({console,URLSearchParams,Date:{now:()=>now},location:{search:''},document:{hidden:false},
          setTimeout:(fn,delay)=>{timers.push({fn,delay});return timers.length},clearTimeout:()=>{},
          refreshHomeProviderSections:()=>renders++,fetch:async(url,options)=>{
            calls.push({url,options});if(hold)await hold;if(fail)throw Error('upstream unavailable');
            return{ok:true,json:async()=>({success:true,sections:[{role:'trending',items:[{title:'Song '+calls.length}]}]})};
          }});
        const state=source.slice(source.indexOf('const homeAppleRefresh ='),source.indexOf('let homeSpotifyStatus'));
        assert(state.includes('intervalMs'),'Refresh state must exist');vm.runInContext(state,ctx);
        vm.runInContext("let homeLoadRequestSeq=1,homeLatestSections=[{}],homeAppleSections=[],homeAppleStatus='loading';",ctx);
        for(const name of ['loadAppleHomeSections','scheduleAppleHomeRefresh','refreshAppleHomeSectionsIfDue','nextAppleHomeRefreshAt']){
          const m=source.match(new RegExp('(?:async )?function '+name+'\\('));assert(m,name);
          const start=m.index,end=source.slice(start+m[0].length).search(/\n(?:async )?function /);
          vm.runInContext(source.slice(start,end<0?undefined:start+m[0].length+end),ctx);
        }
        (async()=>{
          await ctx.loadAppleHomeSections(1);assert.equal(calls.length,1);assert.equal(calls[0].options.cache,'no-store');
          assert.equal(calls[0].url,'/api/apple/browse/sections','Normal loading must use the existing server cache');
          assert.equal(vm.runInContext('homeAppleSections[0].items[0].title',ctx),'Song 1');
          assert.equal(timers.at(-1).delay,4*60*60*1000);
          await ctx.refreshAppleHomeSectionsIfDue();assert.equal(calls.length,1,'A fresh feed must not be refetched');
          now+=4*60*60*1000;let release;hold=new Promise(r=>release=r);
          const first=ctx.refreshAppleHomeSectionsIfDue(),second=ctx.refreshAppleHomeSectionsIfDue();
          assert.equal(calls.length,2,'Concurrent refresh triggers share one request');release();await Promise.all([first,second]);hold=null;
          assert(calls[1].url.includes('refresh=1'));assert.equal(vm.runInContext('homeAppleSections[0].items[0].title',ctx),'Song 2');
          ctx.document.hidden=true;now+=4*60*60*1000;await ctx.refreshAppleHomeSectionsIfDue();assert.equal(calls.length,2);
          ctx.document.hidden=false;await ctx.refreshAppleHomeSectionsIfDue();assert.equal(calls.length,3);
          await ctx.loadAppleHomeSections(1,true);assert(calls.at(-1).url.includes('refresh=1'));
          const last=vm.runInContext('homeAppleSections[0].items[0].title',ctx);fail=true;now+=4*60*60*1000;
          await ctx.refreshAppleHomeSectionsIfDue();assert.equal(vm.runInContext('homeAppleSections[0].items[0].title',ctx),last);
          assert.equal(vm.runInContext('homeAppleStatus',ctx),'ready');assert(timers.at(-1).delay>=30000);
          const count=calls.length;await ctx.refreshAppleHomeSectionsIfDue();assert.equal(calls.length,count,'Do not spin after failure');
          assert(renders>=3);assert(source.includes('loadAppleHomeSections(requestId, refreshEnabled)'));
          assert(source.includes("addEventListener('visibilitychange'"));assert(source.includes("addEventListener?.('pageshow'"));
        })().catch(e=>{console.error(e);process.exitCode=1});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task HomeSectionsRefreshBypassesCacheAndReturnsChangedTrendingTracks()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-home-freshness-").FullName;
        try
        {
            var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
            foreach (var (field,value) in new[] { ("_configFolder",directory),("_settingsFilePath",System.IO.Path.Join(directory,"config.json")) })
                typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(settings,value);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(directory,"config.json"),"{\"appleMusic\":{\"storefront\":\"ke\"}}");
            string Page(string title) => "<script id=\"serialized-server-data\">" +
                "{\"data\":[{\"data\":{\"sections\":[{\"itemKind\":\"trackLockup\",\"header\":{\"item\":{\"titleLink\":{\"title\":\"Trending Songs\"}}},\"items\":[{\"title\":\""+title+"\",\"contentDescriptor\":{\"kind\":\"song\",\"url\":\"https://music.apple.com/ke/song/test/123\",\"identifiers\":{\"storeAdamID\":\"123\"}}}]}]}}]}</script>";
            using var handler = new BrowseHandler { BrowseResponse = Page("First song") };
            using var client = new HttpClient(handler);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new AppleMusicCatalogService(new ClientFactory(client),settings,NullLogger<AppleMusicCatalogService>.Instance,cache);
            var controller = new DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController(service,settings,NullLogger<DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController>.Instance)
            { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
            async Task<string> Title()
            {
                var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Sections(CancellationToken.None));
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
                return json.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("title").GetString()!;
            }
            Assert.Equal("First song",await Title());
            handler.BrowseResponse = Page("New song");
            Assert.Equal("First song",await Title());
            controller.HttpContext.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?refresh=1");
            Assert.Equal("New song",await Title());
            controller.HttpContext.Request.QueryString = default;
            Assert.Equal("New song",await Title());
            Assert.Equal(2,handler.Requests.Count);
        }
        finally { System.IO.Directory.Delete(directory,true); }
    }
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
        const ctx=vm.createContext({console,Map,URLSearchParams,escapeHtml:String,setTimeout:()=>1,clearTimeout:()=>{},
          document:{getElementById:()=>container},location:{search:''},
          IntersectionObserver:class{constructor(callback){observer=callback}observe(){}unobserve(){}},
          HomeViewHelpers:{renderAppleBrowseItem:()=>{cards++;return 'card'}},
          renderHomeItem:()=>{cards++;return 'card'},renderHomeSectionsWithLazyImages:()=>fullRenders++,
          observeLazyImages:()=>{},syncHomeTrendingPlaybackFromSession:()=>{},
          fetch:async()=>({ok:true,json:async()=>({success:true,sections:[{role:'new-this-week',title:'New this week',items:[{title:'Album'}]}]})})});
        vm.runInContext("let homeLoadRequestSeq=1,homeLatestSections=[{title:'Base'}],homeAppleSections=[],homeAppleStatus='loading',homeTrendingProvider='spotify';const homeAppleRefresh={intervalMs:14400000,loadedAt:0,retryAt:0,inFlight:null,timer:null};const homeDeferredRowRenders=new Map();let homeDeferredRowSeq=0;let homeDeferredRowObserver;",ctx);
        function load(name){const match=source.match(new RegExp('(?:async )?function '+name+'\\('));if(!match)return;const start=match.index,end=source.slice(start+match[0].length).search(/\n(?:async )?function /);vm.runInContext(source.slice(start,end<0?undefined:start+match[0].length+end),ctx)}
        for(const name of ['loadAppleHomeSections','scheduleAppleHomeRefresh','refreshAppleHomeSectionsIfDue','nextAppleHomeRefreshAt','refreshHomeProviderSections','renderAppleHomeRows','renderDeferredHomeRow','observeHomeDeferredRows','hydrateHomeDeferredRow','removeHomeProviderSections'])load(name);
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

    private static MethodInfo MethodWithParameters(Type type, string name, int parameterCount)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == parameterCount);
        Assert.True(method is not null, $"{type.Name}.{name} must accept {parameterCount} parameters");
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
            var task = Assert.IsAssignableFrom<Task<JsonDocument>>(method.Invoke(service, [path, "ke", "en-US", CancellationToken.None, false, null]));
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
            using var doc = await Assert.IsAssignableFrom<Task<JsonDocument>>(method.Invoke(service, ["search", store, language, CancellationToken.None, false, null]));
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
        let rerenders=0;ctx.refreshHomeTrendingSection=()=>rerenders++;ctx.refreshAppleHomeSectionsIfDue=()=>{};
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
            vm.runInContext('appleCategoryPages.clear()',ctx);
            let parsedHtml=false;
            ctx.fetch=async()=>({ok:false,status:502,headers:{get:()=> 'text/html'},json:async()=>{parsedHtml=true;throw new SyntaxError("Unexpected token '<'")}});
            await ctx.loadAppleCategories();
            assert.equal(parsedHtml,false,'HTML error pages must never be parsed as JSON');
            assert(elements['apple-status'].textContent.includes('502'));
            assert(elements['apple-categories'].innerHTML.includes('US Category'),'An error must preserve the currently rendered categories');
            ctx.fetch=async()=>({ok:true,status:200,redirected:true,headers:{get:()=> 'text/html'},json:async()=>{parsedHtml=true;throw Error('login HTML')}});
            await ctx.loadAppleCategories();
            assert.equal(parsedHtml,false);
            assert(elements['apple-status'].textContent.includes('Sign in'));
            ctx.setTimeout=()=>1;ctx.clearTimeout=()=>{};
            vm.runInContext("const homeAppleRefresh={intervalMs:14400000,loadedAt:0,retryAt:0,inFlight:null,timer:null};",ctx);
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

    [Theory]
    [InlineData("categories")]
    [InlineData("sections")]
    [InlineData("page")]
    public async Task BrowseTimeoutReturnsJsonAndTheNextRequestCanLoad(string route)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-browse-timeout-").FullName;
        try
        {
            var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
                NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
            foreach (var (field, value) in new[]
            {
                ("_configFolder", directory), ("_settingsFilePath", System.IO.Path.Join(directory, "config.json"))
            })
                typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService)
                    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, value);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(directory, "config.json"),
                "{\"appleMusic\":{\"storefront\":\"us\",\"mediaUserToken\":\"\"}}");
            using var handler = new BrowseHandler { NextFailure = new TaskCanceledException("upstream timeout") };
            using var client = new HttpClient(handler);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new AppleMusicCatalogService(new ClientFactory(client), settings,
                NullLogger<AppleMusicCatalogService>.Instance, cache);
            var controller = new DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController(service, settings,
                NullLogger<DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController>.Instance);
            Task<Microsoft.AspNetCore.Mvc.IActionResult> Request(CancellationToken token) => route switch
            {
                "sections" => controller.Sections(token),
                "page" => controller.Page("room/123", token),
                _ => controller.Categories(token)
            };
            var timeout = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await Request(CancellationToken.None));
            Assert.Equal(504, timeout.StatusCode);
            using var error = JsonDocument.Parse(JsonSerializer.Serialize(timeout.Value));
            Assert.False(error.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("timed out", error.RootElement.GetProperty("error").GetString());
            handler.BrowseResponse = route == "categories" ? CategoriesBrowsePage() : TrendingBrowsePage("Recovered song");
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await Request(CancellationToken.None));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Page("room/456", canceled.Token));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private static JsonArray Map(JsonElement root, bool categoriesOnly) =>
        Assert.IsType<JsonArray>(RequiredMethod(typeof(AppleCatalogJsonHelper), "MapBrowseSections").Invoke(null, [root, categoriesOnly]));

    private static string BrowsePageScript(string serializedJson) =>
        "<script id=\"serialized-server-data\" type=\"application/json\">" + serializedJson + "</script>";

    private static JsonObject BrowseSection(string itemKind, string headerTitle, string kind, string title, string id, string url) => new()
    {
        ["itemKind"] = itemKind,
        ["header"] = new JsonObject { ["item"] = new JsonObject { ["titleLink"] = new JsonObject { ["title"] = headerTitle } } },
        ["items"] = new JsonArray(new JsonObject
        {
            ["title"] = title,
            ["contentDescriptor"] = new JsonObject
            {
                ["kind"] = kind,
                ["url"] = url,
                ["identifiers"] = new JsonObject { ["storeAdamID"] = id }
            }
        })
    };

    private static string TrendingBrowsePage(string title) => BrowsePageScript(new JsonObject
    {
        ["data"] = new JsonArray(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["sections"] = new JsonArray(
                    BrowseSection("trackLockup", "Trending Songs", "song", title, "123", "https://music.apple.com/us/song/test/123"),
                    BrowseSection("squareLockup", "New This Week", "album", title + " album", "456", "https://music.apple.com/us/album/test/456"))
            }
        })
    }.ToJsonString());

    private static string UnmappedBrowsePage(string title) => BrowsePageScript(new JsonObject
    {
        ["data"] = new JsonArray(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["sections"] = new JsonArray(BrowseSection("squareLockup", "Mystery Section", "song", title, "123", "https://music.apple.com/us/song/test/123"))
            }
        })
    }.ToJsonString());

    private static string CategoriesBrowsePage() => BrowsePageScript(new JsonObject
    {
        ["data"] = new JsonArray(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["sections"] = new JsonArray(new JsonObject
                {
                    ["id"] = "browse-category-bricks - dynamic",
                    ["itemKind"] = "brickLockup",
                    ["header"] = new JsonObject { ["item"] = new JsonObject { ["titleLink"] = new JsonObject { ["title"] = "Browse Categories" } } },
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["title"] = "Charts",
                        ["contentDescriptor"] = new JsonObject
                        {
                            ["kind"] = "appleCurator",
                            ["identifiers"] = new JsonObject { ["storeAdamID"] = "123" },
                            ["url"] = "https://music.apple.com/us/curator/charts/123"
                        }
                    })
                })
            }
        })
    }.ToJsonString());

    [Fact]
    public async Task Browse_AccountLookupTimeoutFallsBackToConfiguredStorefrontAndCallerCancellationStillPropagates()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-browse-account-budget-").FullName;
        var configFolder = System.IO.Path.Join(directory, "config");
        System.IO.Directory.CreateDirectory(configFolder);
        try
        {
            var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
                NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
            foreach (var (field, value) in new[]
            {
                ("_configFolder", configFolder), ("_settingsFilePath", System.IO.Path.Join(configFolder, "config.json"))
            })
                typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService)
                    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, value);
            // Keep credentials in the per-test platform-auth overlay so the settings service
            // never reads or writes shared authentication storage outside this directory.
            System.IO.Directory.CreateDirectory(System.IO.Path.Join(directory, "autotag"));
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(directory, "autotag", "applemusic.json"),
                "{\"mediaUserToken\":\"test-media-user-token\",\"authorizationToken\":\"eyJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJBTVAtc2VjdXJlIn0.c2lnbmF0dXJl\"}");
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(configFolder, "config.json"),
                "{\"appleMusic\":{\"storefront\":\"us\",\"mediaUserToken\":\"\",\"authorizationToken\":\"\"}}");
            using var handler = new BrowseHandler
            {
                BrowseResponse = TrendingBrowsePage("Budget song"),
                AccountFailure = new TaskCanceledException("account lookup timed out"),
            };
            using var client = new HttpClient(handler);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new AppleMusicCatalogService(new ClientFactory(client), settings,
                NullLogger<AppleMusicCatalogService>.Instance, cache);
            var controller = new DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController(service, settings,
                NullLogger<DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController>.Instance)
            { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

            // An internally timed-out account lookup must not turn public browse into a 504.
            var result = await controller.Sections(CancellationToken.None);
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("us", json.RootElement.GetProperty("storefront").GetString());
            Assert.Equal("Budget song",
                json.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("title").GetString());
            Assert.Contains(handler.Requests, url => url.Contains("/us/new"));
            Assert.Equal(1, handler.AccountRequests);

            // Caller cancellation must still cancel instead of falling back.
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Sections(canceled.Token));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Browse_CachedContentAndVariableConfiguredStorefrontSurviveAccountTimeout()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-browse-cached-timeout-").FullName;
        var configFolder = System.IO.Path.Join(directory, "config");
        System.IO.Directory.CreateDirectory(configFolder);
        System.IO.Directory.CreateDirectory(System.IO.Path.Join(directory, "autotag"));
        try
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(directory, "autotag", "applemusic.json"),
                "{\"mediaUserToken\":\"test-media-user-token\",\"authorizationToken\":\"eyJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJBTVAtc2VjdXJlIn0.c2lnbmF0dXJl\"}");
            var page = TrendingBrowsePage("Cached song");
            var cachedJson = page[(page.IndexOf('>') + 1)..page.LastIndexOf('<')];

            async Task<(Microsoft.AspNetCore.Mvc.IActionResult Result, BrowseHandler Handler)> Load(string storefront, Action<MemoryCache>? seed)
            {
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(configFolder, "config.json"),
                    "{\"appleMusic\":{\"storefront\":\"" + storefront + "\",\"mediaUserToken\":\"\",\"authorizationToken\":\"\"}}");
                var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
                    NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
                foreach (var (field, value) in new[]
                {
                    ("_configFolder", configFolder), ("_settingsFilePath", System.IO.Path.Join(configFolder, "config.json"))
                })
                    typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService)
                        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, value);
                var handler = new BrowseHandler
                {
                    AccountFailure = new TaskCanceledException("account lookup timed out"),
                    BrowseResponse = TrendingBrowsePage("Fetched song"),
                };
                var cache = new MemoryCache(new MemoryCacheOptions());
                seed?.Invoke(cache);
                var service = new AppleMusicCatalogService(new ClientFactory(new HttpClient(handler)), settings,
                    NullLogger<AppleMusicCatalogService>.Instance, cache);
                var controller = new DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController(service, settings,
                    NullLogger<DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController>.Instance)
                { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
                return (await controller.Sections(CancellationToken.None), handler);
            }

            var cached = await Load("gb", cache => cache.Set("apple:browse:gb:en-US:new", cachedJson, TimeSpan.FromHours(4)));
            var cachedOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(cached.Result);
            using var cachedJsonDoc = JsonDocument.Parse(JsonSerializer.Serialize(cachedOk.Value));
            Assert.Equal("gb", cachedJsonDoc.RootElement.GetProperty("storefront").GetString());
            Assert.Equal("Cached song", cachedJsonDoc.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("title").GetString());
            Assert.DoesNotContain(cached.Handler.Requests, url => url.Contains("/new"));
            Assert.Equal(1, cached.Handler.AccountRequests);

            var fetched = await Load("de", null);
            var fetchedOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(fetched.Result);
            using var fetchedJson = JsonDocument.Parse(JsonSerializer.Serialize(fetchedOk.Value));
            Assert.Equal("de", fetchedJson.RootElement.GetProperty("storefront").GetString());
            Assert.Equal("Fetched song", fetchedJson.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("title").GetString());
            Assert.Contains(fetched.Handler.Requests, url => url.Contains("/de/new"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Homepage_UsesAppMarketLikeTrendingSongsAndFallsBackToUsWhenThatFeedIsUnusable()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-browse-market-").FullName;
        var configFolder = System.IO.Path.Join(directory, "config");
        System.IO.Directory.CreateDirectory(configFolder);
        System.IO.Directory.CreateDirectory(System.IO.Path.Join(directory, "autotag"));
        try
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(directory, "autotag", "applemusic.json"),
                "{\"mediaUserToken\":\"test-media-user-token\",\"authorizationToken\":\"eyJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJBTVAtc2VjdXJlIn0.c2lnbmF0dXJl\"}");

            async Task<(Microsoft.AspNetCore.Mvc.IActionResult Result, BrowseHandler Handler)> Load(
                string appleStorefront,
                string deezerCountry,
                string? mediaUserToken,
                BrowseHandler handler)
            {
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(configFolder, "config.json"),
                    "{\"deezerCountry\":\"" + deezerCountry + "\",\"appleMusic\":{\"storefront\":\"" + appleStorefront + "\",\"mediaUserToken\":\"\",\"authorizationToken\":\"\"}}");
                if (!string.IsNullOrEmpty(mediaUserToken))
                {
                    await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(directory, "autotag", "applemusic.json"),
                        "{\"mediaUserToken\":\"" + mediaUserToken + "\",\"authorizationToken\":\"eyJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJBTVAtc2VjdXJlIn0.c2lnbmF0dXJl\"}");
                }
                else
                {
                    System.IO.File.Delete(System.IO.Path.Join(directory, "autotag", "applemusic.json"));
                }
                var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
                    NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
                foreach (var (field, value) in new[]
                {
                    ("_configFolder", configFolder), ("_settingsFilePath", System.IO.Path.Join(configFolder, "config.json"))
                })
                    typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService)
                        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, value);
                var service = new AppleMusicCatalogService(new ClientFactory(new HttpClient(handler)), settings,
                    NullLogger<AppleMusicCatalogService>.Instance, new MemoryCache(new MemoryCacheOptions()));
                typeof(AppleMusicCatalogService).GetField("_cachedToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(service, "existing-token");
                typeof(AppleMusicCatalogService).GetField("_cachedTokenExpiresAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(service, DateTimeOffset.UtcNow.AddHours(1));
                var controller = new DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController(service, settings,
                    NullLogger<DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController>.Instance)
                { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
                return (await controller.Sections(CancellationToken.None), handler);
            }

            var market = await Load("us", "ke", null, new BrowseHandler { BrowseResponse = TrendingBrowsePage("Kenyan song") });
            var marketOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(market.Result);
            using var marketJson = JsonDocument.Parse(JsonSerializer.Serialize(marketOk.Value));
            Assert.Equal("ke", marketJson.RootElement.GetProperty("storefront").GetString());
            Assert.Equal("Kenyan song", marketJson.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("title").GetString());
            Assert.Contains(market.Handler.Requests, url => url.Contains("/ke/new"));
            Assert.DoesNotContain(market.Handler.Requests, url => url.Contains("/us/new"));

            var explicitStore = await Load("gb", "ke", null, new BrowseHandler { BrowseResponse = TrendingBrowsePage("British song") });
            var explicitOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(explicitStore.Result);
            using var explicitJson = JsonDocument.Parse(JsonSerializer.Serialize(explicitOk.Value));
            Assert.Equal("gb", explicitJson.RootElement.GetProperty("storefront").GetString());
            Assert.Contains(explicitStore.Handler.Requests, url => url.Contains("/gb/new"));

            var account = await Load("us", "ke", "account-token", new BrowseHandler
            {
                AccountResponse = "{\"meta\":{\"subscription\":{\"storefront\":\"jp\",\"active\":true}}}",
                BrowseResponse = TrendingBrowsePage("Japanese song"),
            });
            var accountOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(account.Result);
            using var accountJson = JsonDocument.Parse(JsonSerializer.Serialize(accountOk.Value));
            Assert.Equal("jp", accountJson.RootElement.GetProperty("storefront").GetString());
            Assert.Contains(account.Handler.Requests, url => url.Contains("/jp/new"));

            var fallback = await Load("us", "ke", null, new BrowseHandler
            {
                BrowseResponseFor = url => url.Contains("/ke/")
                    ? "<script id=\"serialized-server-data\" type=\"application/json\">{\"data\":[]}</script>"
                    : TrendingBrowsePage("US fallback song"),
            });
            var fallbackOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(fallback.Result);
            using var fallbackJson = JsonDocument.Parse(JsonSerializer.Serialize(fallbackOk.Value));
            Assert.Equal("us", fallbackJson.RootElement.GetProperty("storefront").GetString());
            Assert.Equal("US fallback song", fallbackJson.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("title").GetString());
            Assert.Contains(fallback.Handler.Requests, url => url.Contains("/ke/new"));
            Assert.Contains(fallback.Handler.Requests, url => url.Contains("/us/new"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ResolveStorefront_BoundedAccountBudgetFallsBackWhileSuccessfulAccountStillWins()
    {
        using var handler = new BrowseHandler
        {
            AccountResponse = "{\"meta\":{\"subscription\":{\"storefront\":\"ke\",\"active\":true}}}",
        };
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);
        typeof(AppleMusicCatalogService).GetField("_cachedToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, "existing-token");
        typeof(AppleMusicCatalogService).GetField("_cachedTokenExpiresAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, DateTimeOffset.UtcNow.AddHours(1));

        // A successful account resolution retains precedence even under a bounded budget...
        var resolve = MethodWithParameters(typeof(AppleMusicCatalogService), "ResolveStorefrontAsync", 4);
        static async Task<string> ResolveAsync(MethodInfo resolve, AppleMusicCatalogService service, string token, TimeSpan budget)
            => await (Task<string>)resolve.Invoke(service, ["us", token, CancellationToken.None, budget])!;
        Assert.Equal("ke", await ResolveAsync(resolve, service, "account-token", TimeSpan.FromSeconds(3)));

        // ...while an expiring budget falls back to the configured storefront, repeatedly.
        handler.HoldAccountResponse = true;
        Assert.Equal("us", await ResolveAsync(resolve, service, "waiting-token", TimeSpan.FromMilliseconds(250)));
        Assert.Equal("us", await ResolveAsync(resolve, service, "waiting-token", TimeSpan.FromMilliseconds(250)));
        handler.Release.TrySetResult();
        await Task.Delay(500); // let the abandoned lookups settle into the cache
        Assert.Equal("ke", await ResolveAsync(resolve, service, "waiting-token", TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public async Task BrowsePageGate_IsScopedPerPage()
    {
        using var handler = new BrowseHandler();
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);

        handler.HoldPredicate = url => url.Contains("/search");
        var categories = service.GetBrowsePageAsync("search", "ke", "en-US", CancellationToken.None);
        await handler.Started.Task;
        var home = service.GetBrowsePageAsync("new", "ke", "en-US", CancellationToken.None);
        var homeCompleted = await Task.WhenAny(home, Task.Delay(5000)) == home;
        handler.HoldPredicate = null;
        handler.Release.TrySetResult();
        using var categoriesDoc = await categories;
        using var homeDoc = await home;
        Assert.True(homeCompleted, "A held categories refresh must not block the home page");
        Assert.Contains(handler.Requests, url => url.Contains("/new"));
    }

    [Fact]
    public async Task BrowsePageGate_IsReleasedAfterFailureAndCancellation()
    {
        using var handler = new BrowseHandler { BrowseResponse = "<html>unexpected</html>" };
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);

        // A failed refresh releases its page gate for later callers.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetBrowsePageAsync("search", "ke", "en-US", CancellationToken.None, refresh: true));
        handler.BrowseResponse = CategoriesBrowsePage();
        using (var recovered = await service.GetBrowsePageAsync("search", "ke", "en-US", CancellationToken.None, refresh: true))
        {
            Assert.Equal("browse-category-bricks - dynamic",
                recovered.RootElement.GetProperty("data")[0].GetProperty("data").GetProperty("sections")[0].GetProperty("id").GetString());
        }

        // A cancelled caller releases the gate so the next caller can proceed.
        handler.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.HoldResponse = true;
        var heldCall = service.GetBrowsePageAsync("search", "ke", "en-US", CancellationToken.None, refresh: true);
        await handler.Started.Task;
        using var cts = new CancellationTokenSource();
        var canceledCall = service.GetBrowsePageAsync("search", "ke", "en-US", cts.Token, refresh: true);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        handler.HoldResponse = false;
        handler.Release.TrySetResult();
        using var afterCancellation = await heldCall;
        Assert.NotNull(afterCancellation);
    }

    [Fact]
    public async Task BrowseController_RejectsUnusableHomeAndCategoryResponsesInsteadOfSuccessWithNothing()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-browse-validation-").FullName;
        var configFolder = System.IO.Path.Join(directory, "config");
        System.IO.Directory.CreateDirectory(configFolder);
        try
        {
            var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
                NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
            foreach (var (field, value) in new[]
            {
                ("_configFolder", configFolder), ("_settingsFilePath", System.IO.Path.Join(configFolder, "config.json"))
            })
                typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService)
                    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, value);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(configFolder, "config.json"),
                "{\"appleMusic\":{\"storefront\":\"us\",\"mediaUserToken\":\"\"}}");
            using var handler = new BrowseHandler();
            using var client = new HttpClient(handler);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new AppleMusicCatalogService(new ClientFactory(client), settings,
                NullLogger<AppleMusicCatalogService>.Instance, cache);
            var controller = new DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController(service, settings,
                NullLogger<DeezSpoTag.Web.Controllers.Api.AppleBrowseApiController>.Instance)
            { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

            // A structurally valid but completely empty home feed is a failure, not success.
            var emptyHome = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.Sections(CancellationToken.None));
            Assert.Equal(502, emptyHome.StatusCode);
            using var homeError = JsonDocument.Parse(JsonSerializer.Serialize(emptyHome.Value));
            Assert.False(homeError.RootElement.GetProperty("success").GetBoolean());

            // Same for an empty categories feed.
            var emptyCategories = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.Categories(CancellationToken.None));
            Assert.Equal(502, emptyCategories.StatusCode);

            // A home feed whose sections map to no known role is unusable too.
            handler.BrowseResponse = UnmappedBrowsePage("Mystery item");
            var unmapped = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.Sections(CancellationToken.None));
            Assert.Equal(502, unmapped.StatusCode);

            // Legitimate partial feeds stay acceptable: one useful mapped row is enough.
            handler.BrowseResponse = TrendingBrowsePage("Partial song");
            var partial = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Sections(CancellationToken.None));
            using var partialJson = JsonDocument.Parse(JsonSerializer.Serialize(partial.Value));
            Assert.True(partialJson.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Partial song",
                partialJson.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("title").GetString());
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BrowseCache_ValidationEvictsInvalidEntriesAndPreservesValidPayloadOnFailedRefresh()
    {
        using var handler = new BrowseHandler();
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);
        static bool HasUsefulContent(JsonDocument document) =>
            AppleCatalogJsonHelper.TryGetDataArray(document.RootElement, out var data) && data.GetArrayLength() > 0;
        var browse = MethodWithParameters(typeof(AppleMusicCatalogService), "GetBrowsePageAsync", 6);

        // An old invalid cache entry must be evicted and replaced by a fresh fetch, not served.
        cache.Set("apple:browse:ke:en-US:new", "{\"data\":[]}", TimeSpan.FromHours(4));
        handler.BrowseResponse = TrendingBrowsePage("Fresh song");
        using var document = await (Task<JsonDocument>)browse.Invoke(
            service, ["new", "ke", "en-US", CancellationToken.None, false, (Func<JsonDocument, bool>)HasUsefulContent])!;
        Assert.Single(handler.Requests);

        // A failed refresh must not overwrite the previously valid payload.
        handler.Requests.Clear();
        handler.BrowseResponse = "<script id=\"serialized-server-data\" type=\"application/json\">{\"data\":[]}</script>";
        await Assert.ThrowsAnyAsync<Exception>(() => (Task<JsonDocument>)browse.Invoke(
            service, ["new", "ke", "en-US", CancellationToken.None, true, (Func<JsonDocument, bool>)HasUsefulContent])!);
        handler.Requests.Clear();
        using var preserved = await (Task<JsonDocument>)browse.Invoke(
            service, ["new", "ke", "en-US", CancellationToken.None, false, (Func<JsonDocument, bool>)HasUsefulContent])!;
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AppleHomeMountsWithoutBaseProviderAndRejectsEmptySuccessResponses()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
        const source=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        const matches=(part,sel)=>{
          if(sel.startsWith('#'))return part.includes(sel.slice(1));
          if(sel.startsWith('.'))return part.includes(sel.slice(1).split('.')[0].split(' ')[0]);
          if(sel.startsWith('[')){const attr=sel.slice(1,-1).replace(/"/g,'');return part.replace(/"/g,'').includes(attr);}
          return part.includes(sel);
        };
        const makeContainer=()=>{
          let parts=[];
          const node=(index)=>({dataset:{},style:{},remove:()=>{parts[index]=null;},after:()=>{},
            querySelector:()=>null,querySelectorAll:()=>[]});
          return{
            get innerHTML(){return parts.filter(Boolean).join('')},set innerHTML(v){parts=String(v).length?[String(v)]:[]},
            insertAdjacentHTML:(position,markup)=>{if(position==='afterbegin')parts.unshift(markup);else parts.push(markup);},
            querySelector:sel=>{const index=parts.findIndex(p=>p&&matches(p,sel));return index<0?null:node(index);},
            querySelectorAll:sel=>parts.map((p,i)=>({p,i})).filter(({p})=>p&&matches(p,sel)).map(({i})=>node(i))};
        };
        let now=100000,calls=[],timers=[],currentSearch='';
        const appleOk=()=>({ok:true,json:async()=>({success:true,sections:[
          {role:'trending',title:'Trending Songs',items:[{source:'apple',type:'track',id:'1',title:'Apple Song'}]},
          {role:'new-this-week',title:'New This Week',items:[{source:'apple',type:'album',id:'2',title:'Apple Album'}]}]})});
        const build=fetchImpl=>{
          const container=makeContainer();
          const ctx=vm.createContext({console,URLSearchParams,URL,Date:{now:()=>now},escapeHtml:String,
            location:{get search(){return currentSearch},set search(value){currentSearch=value},origin:'http://localhost'},
            document:{hidden:false,getElementById:id=>id==='home-sections'?container:null,querySelectorAll:()=>[]},
            setTimeout:(fn,delay)=>{timers.push({fn,delay});return timers.length},clearTimeout:()=>{},
            IntersectionObserver:class{observe(){}unobserve(){}},getComputedStyle:()=>({getPropertyValue:()=>''}),
            HomeViewHelpers:{renderAppleBrowseItem:item=>`<div>apple-${item.title}</div>`},
            renderHomeItem:item=>`<div>${item.title}</div>`,
            getBrowserTimeZone:()=>'UTC',
            renderYouTubeHomeTrending:()=>'yt-trending',
            renderHomeSectionsWithLazyImages:()=>{},
            fetchSpotifyHomeEnhancements:()=>null,loadHomeCategories:async()=>{},
            loadYouTubeHomeTrending:async()=>{},loadYouTubeHomeSections:async()=>{},
            syncHomeTrendingPlaybackFromSession:()=>{},observeLazyImages:()=>{},
            fetch:fetchImpl});
          return {ctx,container};
        };
        const load=(ctx,name)=>{const match=source.match(new RegExp('(?:async )?function '+name+'\\('));assert(match,name+' must exist');
          const start=match.index,end=source.slice(start+match[0].length).search(/\n(?:async )?function /);
          vm.runInContext(source.slice(start,end<0?undefined:start+match[0].length+end),ctx);};
        const seed=ctx=>vm.runInContext("let homeLoadRequestSeq=1,homeLatestSections=[],homeAppleSections=[],homeAppleStatus='loading',homeTrendingProvider='spotify',homeSpotifyStatus='loading';const homeAppleRefresh={intervalMs:14400000,loadedAt:0,retryAt:0,inFlight:null,timer:null};const homeDeferredRowRenders=new Map();let homeDeferredRowSeq=0;let homeDeferredRowObserver;const homeCategoryStatus={};",ctx);
        const names=['loadHomeData','buildHomeRequestUrl','fetchHomeSections','loadAppleHomeSections','scheduleAppleHomeRefresh',
          'refreshAppleHomeSectionsIfDue','nextAppleHomeRefreshAt','mountAppleHomeSectionsWithoutBase','renderHomeTrendingSectionContent','refreshHomeProviderSections',
          'renderAppleHomeRows','renderAppleHomeTrending','renderHomeTrendingProviders','renderDeferredHomeRow','removeHomeProviderSections','observeHomeDeferredRows'];
        const track=ctx=>{const pending=[];const loadApple=ctx.loadAppleHomeSections;
          ctx.loadAppleHomeSections=(...args)=>{const p=loadApple(...args);pending.push(p);return p;};
          ctx.pendingApple=async()=>await Promise.all(pending);};
        const count=(text,needle)=>text.split(needle).length-1;
        (async()=>{
          // Phase 1: Apple succeeds before the base provider fails (Apple provider selected).
          calls=[];timers=[];currentSearch='';
          const first=build(async url=>{calls.push(url);if(url.startsWith('/api/apple/'))return appleOk();throw Error('base provider down');});
          seed(first.ctx);names.forEach(name=>load(first.ctx,name));track(first.ctx);
          vm.runInContext("homeTrendingProvider='apple'",first.ctx);
          await first.ctx.loadAppleHomeSections(1);
          assert(first.container.innerHTML.includes('id="home-trending"'),'Apple must mount the trending section without the base feed');
          assert(first.container.innerHTML.includes('home-trending-providers'),'Apple must mount the trending provider controls');
          assert(first.container.innerHTML.includes('apple-Apple Song'),'Apple trending content must render');
          assert(first.container.innerHTML.includes('data-apple-role="new-this-week"'),'Apple rows must mount without the base feed');
          await first.ctx.loadHomeData();await first.ctx.pendingApple();
          assert(first.container.innerHTML.includes('data-apple-role="new-this-week"'),'A base-provider failure must not wipe mounted Apple rows');

          // Phase 2: the base provider fails before Apple succeeds.
          calls=[];timers=[];currentSearch='';
          let releaseBase;const baseHold=new Promise(resolve=>releaseBase=resolve);
          const second=build(async url=>{calls.push(url);if(url.startsWith('/api/apple/'))return appleOk();await baseHold;throw Error('base provider down');});
          seed(second.ctx);names.forEach(name=>load(second.ctx,name));track(second.ctx);
          const homeData=second.ctx.loadHomeData();
          await second.ctx.pendingApple();
          assert(second.container.innerHTML.includes('home-trending-providers'),'Apple must mount while the base provider is still pending');
          releaseBase();await homeData;
          assert(second.container.innerHTML.includes('data-apple-role="new-this-week"'),'A late base failure must not overwrite Apple rows');
          assert.equal(vm.runInContext('homeAppleStatus',second.ctx),'ready');

          // Phase 3: an empty success must not clear rows and must retry within 30 seconds.
          calls=[];timers=[];currentSearch='';
          const third=build(async url=>{calls.push(url);return{ok:true,json:async()=>({success:true,sections:[
            {role:'trending',title:'Trending Songs',items:[{source:'apple',type:'track',id:'9',title:'Kept Song'}]},
            {role:'new-this-week',title:'New This Week',items:[{source:'apple',type:'album',id:'10',title:'Kept Album'}]}]})};});
          seed(third.ctx);names.forEach(name=>load(third.ctx,name));track(third.ctx);
          await third.ctx.loadAppleHomeSections(1);
          assert.equal(vm.runInContext('homeAppleSections[0].items[0].title',third.ctx),'Kept Song');
          assert(third.container.innerHTML.includes('data-apple-role="new-this-week"'),'Apple rows must mount without the base feed');
          now+=60000; // far from the four-hour deadline
          third.ctx.fetch=async url=>{calls.push(url);return{ok:true,json:async()=>({success:true,sections:[]})}};
          await third.ctx.loadAppleHomeSections(1,true);
          assert.equal(vm.runInContext('homeAppleSections[0].items[0].title',third.ctx),'Kept Song','An empty success must not clear loaded rows');
          assert.equal(vm.runInContext('homeAppleStatus',third.ctx),'ready');
          assert(timers.at(-1).delay<=30000,'A rejected empty response must retry within 30 seconds even with the four-hour deadline far away');
          now+=31000;calls=[];
          third.ctx.fetch=async url=>{calls.push(url);return{ok:true,json:async()=>({success:true,sections:[
            {role:'trending',title:'Trending Songs',items:[{source:'apple',type:'track',id:'11',title:'Recovered Song'}]},
            {role:'new-this-week',title:'New This Week',items:[{source:'apple',type:'album',id:'12',title:'Recovered Album'}]}]})}};
          await third.ctx.refreshAppleHomeSectionsIfDue();
          assert(calls.some(url=>url.includes('refresh=1')),'The failure retry must run after its 30 second deadline');
          assert.equal(vm.runInContext('homeAppleSections[0].items[0].title',third.ctx),'Recovered Song','A successful retry must replace the retained rows');

          // Phase 4: stale completions and channel pages must not mount anything.
          calls=[];timers=[];currentSearch='';
          const fourth=build(async url=>{calls.push(url);return appleOk();});
          seed(fourth.ctx);names.forEach(name=>load(fourth.ctx,name));track(fourth.ctx);
          await fourth.ctx.loadAppleHomeSections(0);
          assert.equal(calls.length,0,'Stale Apple requests must be ignored');
          assert.equal(fourth.container.innerHTML,'','Stale Apple responses must not mount sections');
          vm.runInContext("location.search='?channel=explore'",fourth.ctx);
          vm.runInContext('mountAppleHomeSectionsWithoutBase()',fourth.ctx);
          assert.equal(fourth.container.innerHTML,'','Channel pages must not mount provider rows');

          // Phase 5: base recovery must not duplicate mounted sections.
          vm.runInContext('homeLatestSections=[{title:"Deezer base"}]',third.ctx);
          vm.runInContext("location.search=''",third.ctx);
          await third.ctx.loadAppleHomeSections(1,true);
          assert.equal(count(third.container.innerHTML,'id="home-trending"'),1,'Recovery must not duplicate the trending section');
          assert.equal(count(third.container.innerHTML,'data-apple-role="new-this-week"'),1,'Recovery must not duplicate Apple rows');
        })().catch(error=>{console.error(error);process.exitCode=1});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void EmptyBaseFeedMountsProvidersAndLaterRecoveryKeepsAppleRows()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Join(root.FullName, "DeezSpoTag.Web", "wwwroot", "js", "home-index.js"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
        const source=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        const matches=(part,sel)=>{
          if(sel.startsWith('#'))return part.includes(sel.slice(1));
          if(sel.startsWith('.'))return part.includes(sel.slice(1).split('.')[0].split(' ')[0]);
          if(sel.startsWith('[')){const attr=sel.slice(1,-1).replace(/"/g,'');return part.replace(/"/g,'').includes(attr);}
          return part.includes(sel);
        };
        const makeContainer=()=>{
          let parts=[],writes=0;
          const node=()=>({dataset:{},style:{},remove(){},after(){},querySelector:()=>null,querySelectorAll:()=>[]});
          return{
            writes:()=>writes,
            get innerHTML(){return parts.filter(Boolean).join('')},
            set innerHTML(v){writes++;parts=String(v).length?[String(v)]:[]},
            insertAdjacentHTML:(position,markup)=>{if(!markup)return;if(position==='afterbegin')parts.unshift(markup);else parts.push(markup);},
            querySelector:sel=>{const index=parts.findIndex(p=>p&&matches(p,sel));return index<0?null:node();},
            querySelectorAll:sel=>parts.filter(p=>p&&matches(p,sel)).map(()=>node())};
        };
        const container=makeContainer();
        const ctx=vm.createContext({console,URLSearchParams,URL,Map,Set,Intl,Date,escapeHtml:String,
          location:{search:'',origin:'http://localhost'},
          document:{hidden:false,addEventListener(){},body:{addEventListener(){}},
            getElementById:id=>id==='home-sections'?container:{addEventListener(){},placeholder:''},
            querySelector:()=>null,querySelectorAll:()=>[]},
          IntersectionObserver:class{observe(){}unobserve(){}disconnect(){}},
          getComputedStyle:()=>({getPropertyValue:()=>''}),
          localStorage:{getItem:()=>null,setItem(){}},setTimeout:()=>1,clearTimeout:()=>{},
          HomeViewHelpers:{showToast(){},setHomeTrendingPreviewButtonState(){},renderTopGenresItem:()=>'',
            buildSpotifyBrowseClick:()=>'',buildHomeClick:()=>'',openSpotifyArtistFallback(){},openSpotifyArtist(){},
            renderAppleBrowseItem:item=>`<div>apple-${item.title}</div>`,renderYouTubeBrowseItem:()=>'<div>yt</div>'}});
        ctx.globalThis=ctx;
        vm.runInContext(source,ctx);
        vm.runInContext("homeAppleSections=[{role:'trending',title:'Trending Songs',items:[{source:'apple',type:'track',id:'1',title:'Apple Song'}]},{role:'new-this-week',title:'New This Week',items:[{source:'apple',type:'album',id:'2',title:'Apple Album'}]}];homeAppleStatus='ready';homeTrendingProvider='apple';",ctx);
        ctx.renderHomeSections([]);
        assert(container.innerHTML.includes('id="home-trending"'),'An empty base feed must mount trending controls');
        assert(container.innerHTML.includes('home-trending-providers'));
        assert(container.innerHTML.includes('apple-Apple Song'));
        assert(container.innerHTML.includes('data-apple-role="new-this-week"'));
        assert(!container.innerHTML.includes('No home sections available'));
        const writes=container.writes();
        ctx.renderHomeSections([]);
        assert.equal(container.writes(),writes,'A later empty base success must not redraw mounted provider rows');
        assert(container.innerHTML.includes('data-apple-role="new-this-week"'));
        vm.runInContext("location.search='?channel=explore'",ctx);
        ctx.renderHomeSections([]);
        assert(container.innerHTML.includes('No home sections available'),'Channel pages keep the empty-feed message');
        assert(!container.innerHTML.includes('data-apple-role="new-this-week"'));
        vm.runInContext("location.search='';homeAppleSections=[{role:'new-this-week',title:'New This Week',items:[{source:'apple',type:'album',id:'2',title:'Apple Album'}]}];",ctx);
        ctx.renderHomeSections([{title:'Discover',items:[{title:'One'},{title:'Two'},{title:'Three'},{title:'Four'}]}]);
        const html=container.innerHTML;
        assert(html.includes('Discover'),'A recovered base feed must render');
        assert.equal(html.split('data-apple-role="new-this-week"').length-1,1,'Recovery must keep one Apple row, not a duplicate');
        assert(!html.includes('No home sections available'));
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static AppleMusicCatalogService CreateService(HttpClient client, IMemoryCache cache) =>
        new(new ClientFactory(client), null!, NullLogger<AppleMusicCatalogService>.Instance, cache);

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class BrowseHandler : HttpMessageHandler
    {
        public string? BrowseResponse;
        public Exception? NextFailure;
        public Exception? AccountFailure;
        public bool HoldResponse;
        public bool HoldAccountResponse;
        public Func<string, bool>? HoldPredicate;
        public int AccountRequests;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Requests { get; } = [];
        public List<bool> AuthorizationPresent { get; } = [];
        public string? AccountResponse { get; init; }
        public Func<string, string?>? BrowseResponseFor { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri.AbsolutePath.Contains("/me/account"))
            {
                AccountRequests++;
                if (AccountFailure is { } accountFailure) throw accountFailure;
                if (HoldAccountResponse) await Release.Task.WaitAsync(token);
            }
            if (NextFailure is { } failure) { NextFailure = null; throw failure; }
            Requests.Add(request.RequestUri!.ToString());
            AuthorizationPresent.Add(request.Headers.Authorization != null);
            Started.TrySetResult();
            if (HoldResponse || (HoldPredicate?.Invoke(request.RequestUri.ToString()) ?? false)) await Release.Task.WaitAsync(token);
            var response = request.RequestUri.AbsolutePath.Contains("/me/account")
                ? AccountResponse!
                : BrowseResponseFor?.Invoke(request.RequestUri.ToString())
                    ?? BrowseResponse
                    ?? "<script id=\"serialized-server-data\" type=\"application/json\">{\"data\":[]}</script>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
