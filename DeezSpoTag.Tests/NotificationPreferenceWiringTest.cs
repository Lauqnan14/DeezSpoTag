using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DeezSpoTag.Web.Services.Notifications;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NotificationPreferenceWiringTest
{
    [Fact]
    public void HomeLoadsSharedDownloadClientBeforeYouTubeActions()
    {
        var home = ReadSource("DeezSpoTag.Web/Views/Home/Index.cshtml");
        var logger = home.IndexOf("~/js/download-logger.js", StringComparison.Ordinal);
        var client = home.IndexOf("~/js/download-client.js", StringComparison.Ordinal);
        var actions = home.IndexOf("~/js/home-view-helpers.js", StringComparison.Ordinal);
        Assert.True(logger >= 0, "Home must load the existing download logger.");
        Assert.True(client > logger, "Home must load the shared download client after its logger.");
        Assert.True(actions > client, "YouTube actions require the shared download client before they run.");
    }

    [Fact]
    public async System.Threading.Tasks.Task UnresolvedYouTubeRecordingIsNotReportedAsABlocklistMatch()
    {
        const string script = """
            const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
            const context={document:{addEventListener(){}},console,URL,setTimeout,clearTimeout};
            context.globalThis=context;vm.createContext(context);
            vm.runInContext(fs.readFileSync('DeezSpoTag.Web/wwwroot/js/download-client.js','utf8'),context);
            const client=context.DeezSpoTagDownload;
            const message='No unambiguous destination recording was found for this YouTube song. Download blocked.';
            assert.equal(client.isSkipReason(['unresolved_engine_identity'],message),false);
            assert.notEqual(client.classifySkipReason(['unresolved_engine_identity'],message),'blocked');
            assert.equal(client.resolveSkipToastMessage(['unresolved_engine_identity'],message),message);
            assert.equal(client.isSkipReason(['blocklist_match'],'Track is blocked.'),true);
            assert.equal(client.classifySkipReason(['blocklist_match'],'Track is blocked.'),'blocked');
            assert.equal(client.classifySkipReason([],'Blocked by your Media Management blocklist.'),'blocked');
            """;
        await RunNotificationNodeAsync(script);
    }

    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public async System.Threading.Tasks.Task ActivitiesHtmlFailuresProduceCompactToastsWithoutClearingTheQueue()
    {
        const string script = """
            const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
            const source=fs.readFileSync('DeezSpoTag.Web/Views/Activities/Index.cshtml','utf8');
            const notifications=[],requests=[],originalTasks=[{taskId:'existing-download'}];
            const ctx={Headers,Response,Set,console:{error(){}},queuePollInFlight:false,queuePollRefreshPending:false,queueLoadRequestSequence:0,tasks:originalTasks,
                activityCsrfUnsafeMethods:new Set(['POST','PUT','PATCH','DELETE']),
                document:{querySelector:()=>({getAttribute:()=> 'csrf-token'})},
                activityNativeFetch:async(url,options)=>{requests.push({url,options});return ctx.response;},
                DeezSpoTag:{showNotification:(message,type)=>notifications.push({message,type})}};
            ctx.window=ctx;vm.createContext(ctx);
            for(const name of ['readActivityCsrfRequestToken','buildActivityFetchOptions','activityFetch','showDownloadActionError','loadQueueFromServer']){
                const start=source.indexOf('function '+name+'(');assert(start>=0);
                const begin=source.slice(start-6,start)==='async '?start-6:start;
                vm.runInContext(source.slice(begin,source.indexOf('\n}',start)+2),ctx);
            }
            (async()=>{
                for(const contentType of ['text/html','text/plain']){
                    ctx.response=new Response('<!doctype html><html><title>Cloudflare Tunnel error</title><script>var properties={errorCode:1033}</script>'+ 'private page markup '.repeat(1000)+'</html>',{status:530,headers:{'content-type':contentType}});
                    await ctx.loadQueueFromServer();
                    const toast=notifications.pop();assert.equal(toast.type,'error');assert.match(toast.message,/Cloudflare.*1033/i);
                    assert(toast.message.length<200);assert(!toast.message.includes('<'));assert(!toast.message.includes('private page markup'));
                    assert.equal(ctx.tasks,originalTasks);assert.equal(ctx.queuePollInFlight,false);
                }
                ctx.response=new Response('<html><body>Gateway error</body></html>',{status:502,headers:{'content-type':'text/html'}});
                await ctx.loadQueueFromServer();assert.match(notifications.pop().message,/HTTP 502/);
                for(const response of [new Response('{"error":"Permission denied"}',{status:403,headers:{'content-type':'application/json'}}),new Response('Provider unavailable',{status:503}),new Response('{"success":true}',{status:200,headers:{'content-type':'application/json'}})]){
                    ctx.response=response;const result=await ctx.activityFetch('/api/test',{method:'POST',headers:{'X-Custom':'kept'}});
                    assert.equal(result,response);assert.equal(response.bodyUsed,false);assert((await response.text()).length>0);
                    assert.equal(requests.at(-1).options.headers.get('X-CSRF-TOKEN'),'csrf-token');assert.equal(requests.at(-1).options.headers.get('X-Custom'),'kept');
                }
            })().catch(error=>{console.error(error);process.exitCode=1});
            """;
        await RunNotificationNodeAsync(script);
    }

    [Fact]
    public async System.Threading.Tasks.Task YouTubeQueueUsesVisibleIntentAndRequiresPersistedQueueIds()
    {
        const string script = """
            const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
            const notifications=[],requests=[];
            const context={document:{addEventListener(){},querySelector(){return null;}},console,URL,setTimeout,clearTimeout};
            context.globalThis=context;
            vm.createContext(context);
            vm.runInContext(fs.readFileSync('DeezSpoTag.Web/wwwroot/js/download-client.js','utf8'),context);
            const client=context.DeezSpoTagDownload;
            context.DeezSpoTag.ui={showToast:(message,options)=>notifications.push({message,type:options.type})};
            client.ensureSettingsLoaded=async()=>{};
            client.getPreferredEngineFromUi=()=> 'tidal';client.getPreferredQualityForEngine=()=> 'LOSSLESS';
            client.logDownloadEvent=()=>{};client.removePendingQueueItem=()=>{};
            client.handleDeezerDownload=()=>{throw Error('YouTube fell through to legacy Deezer background matching');};
            let response={success:true,queued:['persisted-id']};
            client.apiFetch=async(endpoint,options)=>{requests.push({endpoint,body:JSON.parse(options.body)});return {ok:true,json:async()=>response};};
            (async()=>{
             for(const url of ['https://music.youtube.com/watch?v=dQw4w9WgXcQ','https://www.youtube.com/watch?v=dQw4w9WgXcQ','https://youtu.be/dQw4w9WgXcQ']){
              const result=await client.addToQueue(url,0,17,{skipPending:true,metadata:{title:'Song',artist:'Artist',durationMs:180000}});
              assert.equal(result.success,true,'YouTube must use the visible intent endpoint');
              assert.equal(result.downloadId,'persisted-id');
              const sent=requests.at(-1);assert.equal(sent.endpoint,'/api/download/intent');assert.equal(sent.body.intents[0].sourceService,'ytmusic');
              assert.equal(sent.body.destinationFolderId,17);assert.equal(sent.body.intents[0].title,'Song');assert.equal(sent.body.intents[0].preferredEngine,'tidal');
              assert.equal(notifications.at(-1).type,'success');
             }
             notifications.length=0;response={success:true,queued:[],message:'Nothing queued.'};
             const empty=await client.addToQueue('https://music.youtube.com/watch?v=dQw4w9WgXcQ',0,17,{skipPending:true});
             assert.equal(empty.success,false);assert(!notifications.some(n=>n.type==='success'));
            })().catch(error=>{console.error(error);process.exitCode=1;});
            """;
        await RunNotificationNodeAsync(script);
    }

    [Fact]
    public async System.Threading.Tasks.Task YouTubeHomeNeverClaimsAnEmptyQueueResponseSucceeded()
    {
        const string script = """
            const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
            const notifications=[],requests=[];
            const context={document:{addEventListener(){},querySelector(){return null;}},console,URL,setTimeout,clearTimeout};
            context.globalThis=context;
            vm.createContext(context);
            vm.runInContext(fs.readFileSync('DeezSpoTag.Web/wwwroot/js/download-client.js','utf8'),context);
            const client=context.DeezSpoTagDownload;
            context.DeezSpoTag.ui={showToast:(message,options)=>notifications.push({message,type:options.type})};
            client.ensureSettingsLoaded=async()=>{};
            client.getPreferredEngineFromUi=()=> 'tidal';client.getPreferredQualityForEngine=()=> 'LOSSLESS';
            client.logDownloadEvent=()=>{};client.removePendingQueueItem=()=>{};
            client.handleDeezerDownload=()=>{throw Error('YouTube fell through to legacy Deezer background matching');};
            let response={success:true,queued:['persisted-id']};
            client.apiFetch=async(endpoint,options)=>{requests.push({endpoint,body:JSON.parse(options.body)});return {ok:true,json:async()=>response};};
            vm.runInContext(fs.readFileSync('DeezSpoTag.Web/wwwroot/js/home-view-helpers.js','utf8'),context);
            context.DeezSpoTag.showNotification=(message,type)=>notifications.push({message,type});
            client.getDestinationFolderId=()=>17;
            client.addPendingQueueIfNeeded=()=>{};
            const button={dataset:{youtubeId:'dQw4w9WgXcQ',trackTitle:'Song',trackArtist:'Artist'},disabled:false};
            (async()=>{
             await context.HomeViewHelpers.downloadYouTubeBrowse(button);
             assert.equal(requests.at(-1).endpoint,'/api/download/intent');
             assert.equal(requests.at(-1).body.intents[0].title,'Song');
             assert.equal(notifications.filter(n=>n.type==='success').length,1,'Shared client alone confirms the persisted entry');
             notifications.length=0;response={success:true,queued:[]};
             await context.HomeViewHelpers.downloadYouTubeBrowse(button);
             assert(!notifications.some(n=>n.type==='success'),'HTTP success without queue IDs is not a queued download');
             assert.equal(button.disabled,false);
             notifications.length=0;response={success:false,queued:[],reasonCodes:['unresolved_engine_identity'],message:'No unambiguous destination recording was found.'};
             await context.HomeViewHelpers.downloadYouTubeBrowse(button);
             assert(!notifications.some(n=>n.type==='success'));assert.match(notifications.at(-1).message,/unambiguous/);
             notifications.length=0;response={success:true,queued:[],deferred:true,deferredCount:1};
             await context.HomeViewHelpers.downloadYouTubeBrowse(button);
             assert.equal(notifications.at(-1).type,'warning');assert.match(notifications.at(-1).message,/not yet added/);
            })().catch(error=>{console.error(error);process.exitCode=1;});
            """;
        await RunNotificationNodeAsync(script);
    }

    private static async System.Threading.Tasks.Task RunNotificationNodeAsync(string script)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new("node") { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = RepoRoot, UseShellExecute = false }
        };
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await error + await output);
    }

    [Fact]
    public void SettingsEventRows_CoverEveryKnownNotificationKind()
    {
        var settings = ReadSource("DeezSpoTag.Web/Views/Settings/Index.cshtml");
        var labelsBlock = ExtractBetween(settings, "const NOTIFICATION_EVENT_LABELS = {", "};");

        foreach (var kind in NotificationKinds.All)
        {
            Assert.Contains($"{kind}:", labelsBlock, StringComparison.Ordinal);
        }

        var labeledKinds = Regex.Matches(labelsBlock, @"^\s*([a-z_]+):", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal(NotificationKinds.All.Count, labeledKinds.Length);
        Assert.All(labeledKinds, kind => Assert.True(NotificationKinds.IsKnown(kind), kind));
    }

    [Fact]
    public void SettingsNotificationSaves_SendCsrfAndPersistAppleDownloadMode()
    {
        var settings = ReadSource("DeezSpoTag.Web/Views/Settings/Index.cshtml");

        Assert.Contains("headers['X-CSRF-TOKEN'] = csrf;", settings, StringComparison.Ordinal);
        Assert.Contains("fetch('/api/notifications/preferences'", settings, StringComparison.Ordinal);
        Assert.Contains("fetch('/api/notifications/webhook-test'", settings, StringComparison.Ordinal);
        Assert.Contains("document.getElementById('appleDownloadNotificationMode')?.addEventListener('change', persistAppleNotificationPreference);", settings, StringComparison.Ordinal);
        Assert.Contains("UserPrefs.set('appleDownloadNotificationMode', mode);", settings, StringComparison.Ordinal);
        Assert.Contains("new StorageEvent('storage'", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownKinds_AreRaisedFromRealProductPaths()
    {
        var autoTagHistory = ReadSource("DeezSpoTag.Web/Services/AutoTagService.RunHistory.cs");
        var autoTagPipeline = ReadSource("DeezSpoTag.Web/Services/AutoTagService.StagePipeline.cs");
        var autoTagRecovery = ReadSource("DeezSpoTag.Web/Services/AutoTagService.JobRecovery.cs");
        var autoTagResume = ReadSource("DeezSpoTag.Web/Services/AutoTagService.Resume.cs");
        var watchlist = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var watchlistReadiness = ReadSource("DeezSpoTag.Web/Services/WatchlistPublicApiReadinessService.cs");
        var fallback = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");
        var orchestration = ReadSource("DeezSpoTag.Web/Services/DownloadOrchestrationService.cs");
        var tidal = ReadSource("DeezSpoTag.Web/Services/TidalPublicProviderRegistry.cs");
        var qobuz = ReadSource("DeezSpoTag.Web/Services/QobuzPublicProviderRegistry.cs");
        var amazon = ReadSource("DeezSpoTag.Web/Services/AmazonPublicProviderRegistry.cs");

        Assert.Contains("\"verification_required\"", watchlistReadiness, StringComparison.Ordinal);
        Assert.Contains("\"artist_new_release\"", watchlist, StringComparison.Ordinal);
        Assert.Contains("\"playlist_updated\"", watchlist, StringComparison.Ordinal);
        Assert.Contains("\"download_failed\"", fallback, StringComparison.Ordinal);
        Assert.Contains("\"download_blocked\"", orchestration, StringComparison.Ordinal);
        Assert.Contains("\"provider_unhealthy\"", tidal, StringComparison.Ordinal);
        Assert.Contains("\"provider_unhealthy\"", qobuz, StringComparison.Ordinal);
        Assert.Contains("\"provider_unhealthy\"", amazon, StringComparison.Ordinal);
        Assert.Contains("NotifyProviderRecovered", tidal, StringComparison.Ordinal);
        Assert.Contains("NotifyProviderRecovered", qobuz, StringComparison.Ordinal);
        Assert.Contains("NotifyProviderRecovered", amazon, StringComparison.Ordinal);
        Assert.Contains("\"run_paused\"", autoTagHistory, StringComparison.Ordinal);
        Assert.Contains("NotifyRunStopped(job, job.Status, job.Error ?? string.Empty);", autoTagPipeline, StringComparison.Ordinal);
        Assert.Contains("NotifyRunStopped(job, job.Status, job.Error);", autoTagRecovery, StringComparison.Ordinal);
        Assert.Contains("\"run_resumed\"", autoTagHistory, StringComparison.Ordinal);
        Assert.Contains("NotifyRunResumed(job, resumed.Id);", autoTagResume, StringComparison.Ordinal);
        Assert.Contains("\"run_completed\"", autoTagHistory, StringComparison.Ordinal);
        Assert.Contains("NotifyRunFinished(job);", autoTagPipeline, StringComparison.Ordinal);
        Assert.Contains("NotifyRunFinished(job);", autoTagRecovery, StringComparison.Ordinal);
        Assert.Contains("\"run_resumed\"", orchestration, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadClient_HonorsAppleDownloadNotificationMode()
    {
        var client = ReadSource("DeezSpoTag.Web/wwwroot/js/download-client.js");

        Assert.Contains("notifyAppleAware(kind, message, type = 'info', context = {})", client, StringComparison.Ordinal);
        Assert.Contains("this.refreshAppleNotificationMode();", client, StringComparison.Ordinal);
        Assert.Contains("createQueueNotifier(options = {}, url = '')", client, StringComparison.Ordinal);
        Assert.Contains("this.notifyAppleAware('deferred', resolvedMessage, 'warning', { url, linkType });", client, StringComparison.Ordinal);
        Assert.Contains("if (appleMode !== 'off')", client, StringComparison.Ordinal);
        Assert.Contains("this.getAppleNotificationMode() === 'detailed'", client, StringComparison.Ordinal);
        Assert.Contains("if (mode === 'off')", client, StringComparison.Ordinal);
        Assert.Contains("if (mode === 'merged')", client, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadBlocked_IsAKnownKind()
    {
        Assert.Contains(NotificationKinds.DownloadBlocked, NotificationKinds.All);
        Assert.True(NotificationKinds.IsKnown("download_blocked"));
        Assert.True(NotificationKinds.IsKnown("provider_recovered"));
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root.");
    }
    [Fact]
    public async System.Threading.Tasks.Task OnlyPersistedQueueEntriesUseTheOriginalGreenToast()
    {
        const string script = """
            const fs = require('fs'), vm = require('vm'), assert = require('node:assert/strict');
            const context = { document: { addEventListener() {} }, console, URL, setTimeout, clearTimeout };
            context.globalThis = context;
            vm.createContext(context);
            vm.runInContext(fs.readFileSync('DeezSpoTag.Web/wwwroot/js/download-client.js', 'utf8'), context);
            const client = context.DeezSpoTag.DownloadClient, seen = [];
            client.logDownloadEvent = () => {};
            client.clearPendingDownload = () => {};
            client.tryParseAbsoluteUrl = () => null;
            client.notifyAppleAware = (_kind, _message, type) => seen.push(type);
            client.handleDeferredResult({ deferred: true }, { url: 'https://open.spotify.com/track/id', options: {}, linkType: 'spotify' });
            assert.equal(seen.pop(), 'warning');
            client.tryParseAbsoluteUrl = () => new URL('https://music.youtube.com/watch?v=abc');
            client.showQueueToast = (_message, type) => seen.push(type);
            client.handleDeferredResult({ deferred: true }, { url: 'https://music.youtube.com/watch?v=abc', options: {}, linkType: 'ytmusic' });
            assert.equal(seen.pop(), 'warning');
            client.buildDeferredBatchQueueMessage = () => 'Queued for background processing';
            client.showBatchQueueSummary({ success: 0, deferred: 2, failed: 0 });
            assert.equal(seen.pop(), 'warning');
            assert.equal(client.getBatchQueueToastType({ failed: 0, alreadyQueued: 0, skipped: 0, deferred: 2 }), 'warning');
            assert.equal(client.getBatchQueueToastType({ failed: 1, alreadyQueued: 0, skipped: 0, deferred: 2 }), 'warning');
            client.showNotification = (_message, type) => seen.push(type);
            client.appleNotifications = { started: 2, completed: 0, failed: 0, timer: null };
            client.flushAppleNotificationSummary();
            assert.equal(seen.pop(), 'success');
            client.queueAppleNotification('deferred');
            client.flushAppleNotificationSummary();
            assert.equal(seen.pop(), 'warning');
            client.appleNotifications.failed = 1;
            client.flushAppleNotificationSummary();
            assert.equal(seen.pop(), 'error');
            """;
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new("node") { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = RepoRoot, UseShellExecute = false }
        };
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await error + await output);
    }

    [Fact]
    public void NotificationRefreshUsesTheExistingGlobalConnectionWithoutDuplicateActivitiesHandlers()
    {
        var site = ReadSource("DeezSpoTag.Web/wwwroot/js/site.js");
        var activities = ReadSource("DeezSpoTag.Web/Views/Activities/Index.cshtml");
        Assert.Contains("connection.on('notificationRaised'", site, StringComparison.Ordinal);
        Assert.Contains("connection.on('notificationsChanged'", site, StringComparison.Ordinal);
        Assert.DoesNotContain("connection.on('notificationRaised'", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("connection.on('toastNotification'", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("withUrl('/deezerQueueHub')", site, StringComparison.Ordinal);
        Assert.Contains("if (!response.ok) throw new Error(`Mark read failed:", site, StringComparison.Ordinal);
    }

}
