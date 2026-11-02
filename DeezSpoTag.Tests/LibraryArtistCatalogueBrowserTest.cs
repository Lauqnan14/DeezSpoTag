using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Browsing authority for the four cached Spotify catalogue sections. Page visits render
/// server-persisted content only: no page-triggered Spotify fetch, no browser-cache merging
/// over server responses, and no workaround for legitimately empty sections.
/// </summary>
public class LibraryArtistCatalogueBrowserTest
{
    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && Directory.GetParent(current.FullName) is not null
               && !File.Exists(Path.Combine(current.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? Directory.GetCurrentDirectory();
    }

    [Fact]
    public void Similar_artist_click_waits_for_library_lookup_and_otherwise_opens_spotify()
    {
        RunBrowserScript("""
            const source=fs.readFileSync(root+'/DeezSpoTag.Web/wwwroot/js/library.js','utf8');
            const start=source.indexOf('function renderSpotifyRelatedArtists(');
            const end=source.indexOf('\nfunction renderSpotifyTopTracks(',start);
            let handler, releaseLookup;
            const button={dataset:{artistName:'Example Artist',spotifyId:'spotify-id',deezerId:'123',spotifyUrl:''},
                addEventListener:(_,fn)=>handler=fn};
            const container={innerHTML:'',querySelectorAll:()=>[button]};
            const state={localArtistIndex:new Map()};
            const context={document:{getElementById:()=>container},libraryState:state,
                selectImage:()=>'',toSafeHttpUrl:x=>x,escapeHtml:x=>x,getInitials:()=>'',
                normalizeArtistName:x=>String(x||'').toLowerCase(),buildLibraryScopedUrl:x=>x+'?folderId=7',
                spotifyUrlHelpers:{parseSpotifyUrl:()=>({type:'artist',id:'url-id'})},
                showToast:()=>{},location:{href:''},
                ensureLocalArtistIndex:()=>new Promise(resolve=>releaseLookup=resolve)};
            vm.createContext(context);vm.runInContext(source.slice(start,end),context);
            context.renderSpotifyRelatedArtists([{id:'spotify-id',name:'Example Artist',deezerId:'123'}]);
            const clicking=handler();
            assert.equal(context.location.href,'','must wait for pending library lookup');
            assert.equal(typeof releaseLookup,'function','click must check library before leaving');
            state.localArtistIndex.set('example artist',42);releaseLookup();await clicking;
            assert.equal(context.location.href,'/Library/Artist/42?folderId=7');
            state.localArtistIndex.clear();context.ensureLocalArtistIndex=async()=>{};
            context.renderSpotifyRelatedArtists([{id:'spotify-id',name:'Example Artist',deezerId:'123'}]);
            await handler();
            assert.equal(context.location.href,'/Artist?id=spotify-id&source=spotify&refresh=1');
            button.dataset.spotifyId='';button.dataset.deezerId='123';
            await handler();
            assert.equal(context.location.href,'/Artist?id=url-id&source=spotify&refresh=1');
            state.localArtistIndex.set('example artist',42);
            context.renderSpotifyRelatedArtists([{id:'spotify-id',name:'Example Artist'}]);
            assert.ok(container.innerHTML.includes('/Library/Artist/42?folderId=7'));
            """);
    }

    [Fact]
    public void Spotify_artist_page_loads_pathfinder_without_requiring_a_refresh_flag()
    {
        RunBrowserScript("""
            const source=fs.readFileSync(root+'/DeezSpoTag.Web/Views/Artist/Index.cshtml','utf8');
            const start=source.indexOf('async function loadArtist(');
            const end=source.indexOf('\nfunction hasArtistExtras(',start);
            for(const [artistSource,search,expected] of [
                ['spotify','?refresh=1','&refresh=1'],['spotify','','&refresh=1'],['deezer','?refresh=1','']]) {
                const requests=[];
                const context={currentArtistSource:artistSource,performance:{now:()=>0},console,
                    window:{location:{search}},URLSearchParams,
                    document:{getElementById:()=>({innerHTML:''})},
                    loadArtistPayloadFromCache:()=>null,formatArtistData:x=>x,displayArtist:()=>{},
                    mergeArtistPayload:x=>x,saveArtistPayloadToCache:()=>{},hasArtistExtras:()=>true,
                    fetch:async url=>{requests.push(url);return {ok:true,json:async()=>({name:'Artist'})}}};
                vm.createContext(context);vm.runInContext(source.slice(start,end),context);
                await context.loadArtist('spotify-id');
                assert.equal(requests.length,1);
                assert.equal(requests[0],'/api/artist-page?id=spotify-id&source='+artistSource+expected);
            }
            """);
    }

    private static void RunBrowserScript(string script)
    {
        var info = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        info.ArgumentList.Add("--input-type=module");
        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Write("import fs from 'node:fs';import vm from 'node:vm';import assert from 'node:assert/strict';\nconst root="
            + System.Text.Json.JsonSerializer.Serialize(RepositoryRoot()) + ";\n" + script);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000), "Similar artist browser test timed out.");
        Assert.True(process.ExitCode == 0, error + output);
    }

    [Fact]
    public void Page_loads_never_trigger_spotify_provider_work()
    {
        var libraryJs = File.ReadAllText(Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot", "js", "library.js"));

        // The page-triggered background discography refresh is retired: the retained catalogue
        // is served as-is and only Refresh cache runs replace it.
        Assert.DoesNotContain("queueDiscographyBackgroundRefresh", libraryJs, StringComparison.Ordinal);
        Assert.DoesNotContain("discography/refresh", libraryJs, StringComparison.Ordinal);

        // An empty section from the server is authoritative content, not a "warmup" signal.
        Assert.DoesNotContain("cache warmup", libraryJs, StringComparison.Ordinal);
        Assert.Contains("selectSpotifyPayloadForRender(spotifyData, browserCached)", libraryJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_responses_are_never_merged_with_browser_cache()
    {
        var libraryJs = File.ReadAllText(Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot", "js", "library.js"));

        // Merging browser content back into server data resurrected older lists; the render
        // selection is server-authoritative and valid-empty replaces retained content.
        Assert.DoesNotContain("mergeSpotifyArtistPayload", libraryJs, StringComparison.Ordinal);
        Assert.DoesNotContain("mergeArtistPagePayload", libraryJs, StringComparison.Ordinal);
        Assert.DoesNotContain("mergeSpotifyArtistProfile", libraryJs, StringComparison.Ordinal);
        Assert.DoesNotContain("mergeSpotifyTopTracks", libraryJs, StringComparison.Ordinal);
        Assert.DoesNotContain("countArtistPageReleases", libraryJs, StringComparison.Ordinal);
        Assert.Contains("server content is authoritative", libraryJs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserveExistingAlbums: false", libraryJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Artist_page_cache_api_retains_the_spotify_catalogue()
    {
        var controller = File.ReadAllText(Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryArtistCacheApiController.cs"));

        // Clearing the artist page cache keeps the four Spotify sections readable: only a
        // Refresh cache run replaces or discards them.
        Assert.Contains("preserveSource: \"spotify\"", controller, StringComparison.Ordinal);
        Assert.Contains("retained = new[] { \"spotify\" }", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Discography_refresh_reroutes_through_the_shared_lifecycle()
    {
        var controller = File.ReadAllText(Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryArtistSourceMetadataApiController.cs"));

        // The direct-discography publication path is gone; the refresh executor is the only
        // writer of a validated catalogue revision.
        Assert.Contains("EnqueueArtistCacheRefreshAsync", controller, StringComparison.Ordinal);
        Assert.Contains("lifecycle = true", controller, StringComparison.Ordinal);
        Assert.Contains("cachedComposition: true", controller, StringComparison.Ordinal);
        Assert.Contains("retainedCatalogue = true", controller, StringComparison.Ordinal);
        Assert.Contains("complete = catalogue.IsComplete", controller, StringComparison.Ordinal);
        // Legacy client-visible keys are retired with the direct path.
        Assert.DoesNotContain("RefreshLatestDiscographyAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_executor_is_the_only_catalogue_publisher()
    {
        var root = RepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "SpotifyArtistService.cs"));
        var cacheRefresh = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));

        // Lazy librespot fallback and ISRC enrichment never rewrite the whole payload: they
        // conditionally update the currently stored envelope, so a delayed write cannot roll
        // back a newer revision.
        Assert.Contains("UpsertIfUnchangedAsync", service, StringComparison.Ordinal);
        Assert.Contains("TryReadArtistCatalogueStatusAsync", service, StringComparison.Ordinal);
        Assert.Contains("GetStoredEnvelopeAsync", service, StringComparison.Ordinal);
        Assert.Contains("catalogue preserved", service, StringComparison.Ordinal);
        Assert.Contains("catalogue revision published", service, StringComparison.Ordinal);

        // The executor reports the metadata stage complete only against the stored catalogue.
        Assert.Contains("ReasonCode: \"catalogue-unresolved\"", cacheRefresh, StringComparison.Ordinal);
        Assert.Contains("IncludeCatalogue = stage == \"metadata\"", cacheRefresh, StringComparison.Ordinal);
    }
}
