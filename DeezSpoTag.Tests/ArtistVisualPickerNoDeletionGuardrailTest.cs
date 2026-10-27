using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistVisualPickerNoDeletionGuardrailTest
{
    [Fact]
    public void PickerPreservesExistingArtworkAndUsesArtworkOnlyRefresh()
    {
        var root = FindSourceRoot();
        var script = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "library.js"));
        var controller = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryArtistArtworkApiController.cs"));
        var loader = ExtractBetween(
            script,
            "async function loadExternalArtistVisuals",
            "function renderArtistVisualPicker");

        Assert.DoesNotContain("cachedPickerImages = []", loader, StringComparison.Ordinal);
        Assert.Contains("mergeArtistVisualPickerResult(visuals, cached)", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("/artwork/refresh", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("force=true", loader, StringComparison.Ordinal);
        Assert.Contains("/artwork", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("ArtistMetadataCacheRefreshService", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshArtistAsync", controller, StringComparison.Ordinal);
        Assert.Contains("_coordinator.EnqueueArtistCacheRefreshAsync", controller, StringComparison.Ordinal);
        Assert.Contains("ArtistCacheRefreshScope.Artwork", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void PanelResponsesCannotOverwriteAnotherArtistAfterNavigation()
        => RunJavaScript("""
            import fs from 'node:fs';
            import vm from 'node:vm';
            import assert from 'node:assert/strict';
            const script=source;
            for (const name of ['loadArtistBiographiesFromCache','loadSpotifyArtist']) {
             const start=script.indexOf('async function '+name+'(');
             const end=script.indexOf('\n}\n',start)+2;
             let current=1, release, mutations=0;
             const context={console, String, Array, encodeURIComponent,
               libraryState:{cachedBiographies:{sentinel:true}},
               getCurrentLibraryArtistId:()=>current,
               fetchJsonOptional:()=>new Promise(r=>release=r),
               getLibrarySpotifyBrowserCacheState:()=>({hasBrowserCached:false}),
               ensureLocalArtistIndex:()=>{},setSpotifyCacheStatus:()=>{},
               buildSpotifyArtistRequestQuery:()=>'',queueDiscographyBackgroundRefresh:()=>{},
               isSpotifyPayloadUnavailable:()=>{mutations++;return true;},
               handleUnavailableSpotifyArtistResponse:()=>{mutations++;},
               document:{getElementById:()=>{mutations++;throw Error('stale DOM write');}},
             };
             vm.createContext(context);vm.runInContext(script.slice(start,end),context);
             const pending=context[name](1,false,false,true);current=2;release({sources:[]});await pending;
             assert.equal(mutations,0,name+' must ignore the old artist response');
             assert.equal(context.libraryState.cachedBiographies.sentinel,true,name+' must preserve current artist state');
            }
            console.log('PASS: both pending panel responses ignore navigation to another artist');
            """);

    [Fact]
    public void SettledRefreshMessageSurvivesLateSpotifyRenders()
        => RunJavaScript("""
            import fs from 'node:fs';import vm from 'node:vm';import assert from 'node:assert/strict';
            const s=source;const i=s.indexOf('function setSpotifyCacheStatus(');const end=s.indexOf('\n}\n',i)+2;
            const el={textContent:''};const context={libraryState:{artistCacheStatus:{artistId:'904',runId:'known-run',message:'Source cache: retained 2 unfinished sources'}},String,getCurrentLibraryArtistId:()=>904,getSpotifyCacheStatusElement:()=>el};
            vm.createContext(context);vm.runInContext(s.slice(i,end),context);context.setSpotifyCacheStatus('Spotify refresh: loaded');assert.equal(el.textContent,context.libraryState.artistCacheStatus.message);
            context.getCurrentLibraryArtistId=()=>1084;context.setSpotifyCacheStatus('Spotify refresh: loaded');assert.equal(el.textContent,'Spotify refresh: loaded');console.log('PASS: settled cache outcome survives late Spotify renders and stays artist-scoped');
            """);

    private static void RunJavaScript(string script)
    {
        var source=File.ReadAllText(Path.Join(FindSourceRoot(),"DeezSpoTag.Web","wwwroot","js","library.js"));
        var info=new System.Diagnostics.ProcessStartInfo("node") {
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        info.ArgumentList.Add("--input-type=module");
        using var process=System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Write("const source="+System.Text.Json.JsonSerializer.Serialize(source)+";\n"+script);
        process.StandardInput.Close();
        var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000),"Browser-state regression timed out.");
        Assert.True(process.ExitCode==0,error+output);
    }

    private static string ExtractBetween(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Services")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("DeezSpoTag source root was not found.");
    }
}
