using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MonitoredPlaylistPresentationGuardrailTest
{
    [Fact]
    public void Incomplete_candidate_cache_is_refreshed_even_when_snapshot_is_unchanged()
    {
        var source = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));

        Assert.Contains("PlaylistCandidateContract.IsReusableCache", source, StringComparison.Ordinal);
        Assert.Contains("existingCandidateCache?.IsComplete == true", source, StringComparison.Ordinal);
        Assert.Contains("if (cachedCandidatesComplete)", source, StringComparison.Ordinal);
        Assert.Contains("cached candidates are incomplete. Refreshing candidates.", source, StringComparison.Ordinal);
        Assert.Contains("FetchPlaylistTrackPageAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_playlist_metadata_persists_owner_without_a_cached_tracklist_author_path()
    {
        var watchServiceSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));
        var controllerSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("OwnerName", watchServiceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("creator = new { name = playlist.Source },", controllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_tracklist_does_not_use_a_separate_cached_tracklist_renderer()
    {
        var viewSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Views",
            "Tracklist",
            "Index.cshtml"));

        Assert.DoesNotContain("loadMonitoredPlaylistGlobalTracklist", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/library/playlists/tracklist/", viewSource, StringComparison.Ordinal);
        Assert.Contains("preloadMonitoredPlaylistTrackStatuses", viewSource, StringComparison.Ordinal);
        Assert.Contains("shouldShowMonitoredStateColumn", viewSource, StringComparison.Ordinal);

        var controllerSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.DoesNotContain("[HttpGet(\"tracklist/{source}/{sourceId}\")]", controllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_playlist_state_column_accepts_every_backend_location_status()
    {
        var viewSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Views",
            "Tracklist",
            "Index.cshtml"));
        var controllerSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        string[] statuses =
        [
            "blocked",
            "queued",
            "downloading",
            "paused",
            "retrying",
            "failed",
            "cancelled",
            "review",
            "redirected",
            "synced",
            "mapping_retry",
            "downloaded",
            "library",
            "unavailable",
            "missing"
        ];

        foreach (var status in statuses)
        {
            Assert.Contains($"\"{status}\"", controllerSource, StringComparison.Ordinal);
            Assert.Contains($"'{status}'", viewSource, StringComparison.Ordinal);
        }

        string[] styledStatuses =
        [
            "synced",
            "redirected",
            "mapping_retry",
            "downloaded",
            "review"
        ];

        foreach (var status in styledStatuses)
        {
            Assert.Contains($"track-location-status-pill--{status}", viewSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Monitored_playlist_state_column_refreshes_from_the_authoritative_endpoint()
    {
        var viewSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Views",
            "Tracklist",
            "Index.cshtml"));

        Assert.Contains("monitoredPlaylistTrackStatusRefreshIntervalMs", viewSource, StringComparison.Ordinal);
        Assert.Contains("fetchMonitoredPlaylistTrackStatuses(source, sourceId)", viewSource, StringComparison.Ordinal);
        Assert.Contains("updateRenderedMonitoredPlaylistTrackStatuses()", viewSource, StringComparison.Ordinal);
        Assert.Contains("data-monitor-status-keys", viewSource, StringComparison.Ordinal);
        Assert.Contains("startMonitoredPlaylistTrackStatusRefresh();", viewSource, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('pagehide', stopMonitoredPlaylistTrackStatusRefresh)", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("window.setInterval", viewSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime health summary is the only worker-status surface the watchlist page shows.
    /// It must run in the real script: a failed or retrying worker is named as such with its
    /// failure stage and sanitized error, a stopped worker is stopped, and an ordinary pending
    /// trigger never reads as a dead or healthy-unknown worker.
    /// </summary>
    [Fact]
    public void Worker_health_renders_retrying_failed_and_stopped_states_distinctly()
    {
        RunHealthHarness("""
            const render = parts => `<div class="watchlist-runtime-health">${escapeHtml(parts.join(' • '))}</div>`;

            const failed = buildWatchlistRuntimeHealthSummary({
                runtime: {
                    workerState: 'failed',
                    workerFailureStage: 'startup-recovery',
                    workerLastError: 'Transient DB failure <script>alert(1)</script>',
                    workerFailureUtc: '2026-11-11T10:00:00Z',
                    triggerPending: false
                }
            });
            assert.ok(failed.some(p => p.includes('failed')), failed.join(' | '));
            assert.ok(failed.some(p => p.includes('startup-recovery')), failed.join(' | '));
            assert.ok(failed.some(p => p.includes('Transient DB failure')), failed.join(' | '));
            const failedHtml = render(failed);
            assert.ok(!failedHtml.includes('<script>'), failedHtml);
            assert.ok(failedHtml.includes('&lt;script&gt;'), failedHtml);
            // A dead worker must not be dressed up as an ordinary idle worker.
            assert.ok(!failed.some(p => p.includes('has not completed a cycle yet')), failed.join(' | '));
            assert.ok(!failed.some(p => /^Last cycle /.test(p)), failed.join(' | '));

            const retrying = buildWatchlistRuntimeHealthSummary({
                runtime: {
                    workerState: 'retrying',
                    workerFailureStage: 'scheduler-wait',
                    workerLastError: 'transient storage failure',
                    workerNextRetryUtc: '2026-11-11T10:05:00Z',
                    triggerPending: true
                }
            });
            assert.ok(retrying.some(p => p.includes('retrying')), retrying.join(' | '));
            assert.ok(retrying.some(p => p.includes('scheduler-wait')), retrying.join(' | '));
            assert.ok(retrying.some(p => p.includes('transient storage failure')), retrying.join(' | '));
            assert.ok(retrying.some(p => p.includes('retry')), retrying.join(' | '));
            assert.ok(retrying.some(p => p.includes('trigger queued')), retrying.join(' | '));

            const stopped = buildWatchlistRuntimeHealthSummary({
                runtime: { workerState: 'stopped', triggerPending: false }
            });
            assert.ok(stopped.some(p => p.includes('stopped')), stopped.join(' | '));

            const healthy = buildWatchlistRuntimeHealthSummary({
                runtime: { workerState: 'waiting', triggerPending: false, lastCycleCompletedUtc: '2026-11-11T09:00:00Z' }
            });
            assert.ok(healthy.some(p => /^Last cycle /.test(p)), healthy.join(' | '));
            assert.ok(!healthy.some(p => p.includes('failed') || p.includes('retrying')), healthy.join(' | '));

            // Legacy record without worker state: unknown, never healthy-claimed.
            const legacy = buildWatchlistRuntimeHealthSummary({ runtime: { triggerPending: false } });
            assert.ok(legacy.some(p => p.includes('has not completed a cycle yet')), legacy.join(' | '));
            """);
    }

    [Fact]
    public async System.Threading.Tasks.Task Watchlist_messages_label_historical_waits_and_platform_results_without_hiding_failures()
    {
        await RunMessageHarnessAsync("""
            const summary = buildWatchlistRuntimeHealthSummary({
                runtime: {workerState:'waiting', isRunning:false, lastCycleCompletedUtc:'2026-11-13T20:32:16Z',
                    lastAdmissionBlockReason:'Waiting for 1 watchlist download claim(s) to finish.'},
                claims:{pending:19, orphanedPending:19},
                finalization:{blockingAdmission:0},
                platforms:[{label:'Boomplay', completedUtc:'2026-11-13T20:26:51Z',
                    summary:'20/20 checked · 20 prepared · 18 failed'}],
                targetSyncJobs:{retryWaiting:6,lastError:'Playlist synced (append). Removals held.'},
                presentation:{mappingRetry:106,blocked:5,queued:0,downloading:0}
            });
            assert.ok(summary.includes('Last admission check: Waiting for 1 watchlist download claim(s) to finish.'), summary);
            assert.ok(!summary.some(line => line.startsWith('Waiting for 1')), summary);
            assert.ok(summary.includes('Boomplay (last check) · 20/20 checked · 20 snapshots prepared · 18 failed'), summary);
            assert.ok(summary.includes('Last target sync result: Playlist synced (append). Removals held.'), summary);
            assert.ok(summary.includes('106 unmapped') && summary.includes('5 blocked'), summary);
            assert.ok(summary.includes('19 orphaned pending claim(s)'), summary);
            assert.ok(summary.includes('6 target sync job(s)'), summary);
            const running = buildWatchlistRuntimeHealthSummary({runtime:{workerState:'running',isRunning:true},
                platforms:[{label:'Spotify',summary:'1/28 checked · 1 prepared'}]});
            assert.ok(running.includes('Spotify (current check) · 1/28 checked · 1 snapshots prepared'), running);
            """);
    }

    [Fact]
    public async System.Threading.Tasks.Task Watchlist_cards_use_current_sync_counts_and_preserve_last_check_and_elapsed_restrictions()
    {
        await RunMessageHarnessAsync("""
            assert.equal(formatPlaylistWatchlistSnapshotStatus({snapshotStatus:'complete',lastRunStatus:'incomplete',mappingRetryCount:5}), 'Snapshot: complete');
            assert.equal(formatPlaylistWatchlistSnapshotStatus({snapshotStatus:'changed'}), 'Snapshot changed; processing pending');
            assert.equal(formatPlaylistWatchlistSnapshotStatus({snapshotStatus:'incomplete'}), 'Snapshot: incomplete');
            assert.equal(formatPlaylistWatchlistMappingStatus({mappingRetryCount:5}), '5 unmapped tracks');
            assert.equal(formatPlaylistWatchlistMappingStatus({mappingRetryCount:0}), '');
            const synced = {trackCount:129,syncedTrackCount:129,incompleteTrackCount:0,lastRunStatus:'incomplete'};
            assert.equal(formatPlaylistWatchlistSyncStatus(synced), 'Target sync: 129/129 synced');
            assert.equal(formatPlaylistWatchlistRunStatus(synced), 'Last check: incomplete');
            const pending = {trackCount:74,syncedTrackCount:72,incompleteTrackCount:2,lastRunStatus:'completed'};
            assert.equal(formatPlaylistWatchlistSyncStatus(pending), 'Target sync: 72/74 synced · 2 not synced');
            assert.equal(formatPlaylistWatchlistRunStatus(pending), 'Last check: completed');
            assert.equal(formatPlaylistWatchlistRunStatus({...synced,lastRunStatus:'source_failure'}), 'Last check: source failure');
            assert.equal(formatPlaylistWatchlistSyncStatus({trackCount:null}), 'Target sync not checked');
            assert.equal(formatPlaylistWatchlistSyncStatus({trackCount:0,syncedTrackCount:0}), 'No tracked tracks');
            assert.equal(formatPlaylistWatchlistSyncStatus({trackCount:10,ignoredBlockedTrackCount:2,syncedTrackCount:8}), 'Target sync: 8/8 synced');
            const expired = getWatchlistSourceCircuitPresentation({isOpen:true,openUntilUtc:'2026-11-13T20:30:37Z',reason:'Empty source snapshot'}, '2026-11-13T22:29:42Z');
            assert.equal(expired.isActive, false);
            assert.equal(expired.label, 'Source cooldown elapsed; awaiting next check');
            assert.ok(expired.detail.includes('Last source restriction: Empty source snapshot'), expired);
            const active = getWatchlistSourceCircuitPresentation({isOpen:true,openUntilUtc:'2026-11-13T23:30:37Z',reason:'Rate limited'}, '2026-11-13T22:29:42Z');
            assert.equal(active.isActive, true);
            assert.equal(active.label, 'Source cooling down');
            assert.ok(active.detail.includes('Rate limited'), active);
            assert.equal(getWatchlistSourceCircuitPresentation({isOpen:true}, '2026-11-13T22:29:42Z').isActive, true);
            assert.equal(getWatchlistSourceCircuitPresentation({isOpen:false}, '2026-11-13T22:29:42Z').label, '');
            """);
    }

    [Fact]
    public async System.Threading.Tasks.Task Watchlist_page_renders_current_counts_and_historical_details_from_NAS_shaped_responses()
    {
        await RunMessageHarnessAsync("""
            const container = {dataset:{},innerHTML:'',querySelectorAll:()=>[]};
            globalThis.document = {getElementById:id=>id==='playlistWatchlistContainer'?container:null,addEventListener:()=>{}};
            globalThis.libraryState = {folders:[{id:1}]};
            globalThis.escapeHtml = value=>String(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
            globalThis.toSafeHttpUrl = () => '';
            globalThis.hydratePlaylistPreferences = async()=>[];
            globalThis.renderPlaylistWatchlistPresentationBadges = ()=>'';
            globalThis.renderPlaylistWatchlistPriorityBadge = ()=>'';
            globalThis.renderSharedPlaylistActions = ()=>'';
            globalThis.bindPlaylistWatchlistDragOrdering = ()=>{};
            globalThis.tryOpenPendingPlaylistSettings = ()=>{};
            const runtime = {utcNow:'2026-11-13T22:29:42Z',runtime:{workerState:'waiting',isRunning:false},
                scheduler:{activeSource:'boomplay',activeSourceId:'fixture'},
                circuits:[{source:'boomplay',isOpen:true,openUntilUtc:'2026-11-13T20:30:37Z',reason:'Empty snapshot'}]};
            const item = {source:'boomplay',sourceId:'fixture',name:'Fixture',trackCount:74,syncedTrackCount:72,
                incompleteTrackCount:2,snapshotStatus:'complete',mappingRetryCount:2,lastRunStatus:'completed',lastRunMessage:'Sync scheduled <unsafe>'};
            globalThis.fetchJson = async(url,options)=>{
                assert.equal(options,undefined,'Rendering must not mutate the queue or scheduler');
                if(url==='/api/library/playlists/watch-runtime') return runtime;
                if(url==='/api/library/playlists') return [item];
                if(url==='/api/library/playlists/manual-unavailable') return {tracks:[]};
                throw new Error('Unexpected request '+url);
            };
            (async()=>{
                await loadPlaylistWatchlist();
                assert.equal(container.dataset.loadState,'ready',container.innerHTML);
                assert.ok(container.innerHTML.includes('Target sync: 72/74 synced · 2 not synced'),container.innerHTML);
                assert.ok(container.innerHTML.includes('Snapshot: complete'),container.innerHTML);
                assert.ok(container.innerHTML.includes('2 unmapped tracks'),container.innerHTML);
                assert.ok(container.innerHTML.includes('Last check: completed'),container.innerHTML);
                assert.ok(container.innerHTML.includes('Last check details: Sync scheduled &lt;unsafe&gt;'),container.innerHTML);
                assert.ok(!container.innerHTML.includes('<unsafe>'),container.innerHTML);
                assert.ok(container.innerHTML.includes('Source cooldown elapsed; awaiting next check'),container.innerHTML);
                assert.ok(!container.innerHTML.includes('checking playlist'),container.innerHTML);
                runtime.runtime.isRunning=true;
                runtime.circuits[0].openUntilUtc='2026-11-13T23:30:37Z';
                item.syncedTrackCount=74; item.incompleteTrackCount=0; item.lastRunStatus='source_failure';
                await loadPlaylistWatchlist();
                assert.equal(container.dataset.loadState,'ready',container.innerHTML);
                assert.ok(container.innerHTML.includes('Target sync: 74/74 synced'),container.innerHTML);
                assert.ok(container.innerHTML.includes('Snapshot: complete'),container.innerHTML);
                assert.ok(container.innerHTML.includes('Last check: source failure'),container.innerHTML);
                assert.ok(container.innerHTML.includes('Source cooling down'),container.innerHTML);
                assert.ok(container.innerHTML.includes('checking playlist'),container.innerHTML);
            })().catch(error=>{console.error(error);process.exitCode=1;});
            """);
    }

    private static async System.Threading.Tasks.Task RunMessageHarnessAsync(string assertions)
    {
        var source = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));
        var healthStart = source.IndexOf("function buildWatchlistRuntimeHealthSummary(", StringComparison.Ordinal);
        var healthEnd = source.IndexOf("\nfunction ", healthStart + 10, StringComparison.Ordinal);
        var cardsStart = source.IndexOf("function toNonNegativeCount(", StringComparison.Ordinal);
        var cardsEnd = source.IndexOf("function renderPlaylistWatchlistPresentationBadges(", cardsStart, StringComparison.Ordinal);
        var loaderStart = source.IndexOf("async function loadPlaylistWatchlist(", StringComparison.Ordinal);
        var loaderEnd = source.IndexOf("\nglobalThis.loadPlaylistWatchlist", loaderStart, StringComparison.Ordinal);
        var harness = "const assert = require('node:assert/strict'); function formatRelativeTime(v){return String(v); }\n"
            + source[healthStart..healthEnd] + "\n" + source[cardsStart..cardsEnd]
            + "\n" + source[loaderStart..loaderEnd] + "\n" + assertions;
        var info = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardError = true,
            RedirectStandardOutput = true, UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        await process.StandardInput.WriteAsync(harness);
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private void RunHealthHarness(string assertions)
    {
        if (!NodeIsAvailable())
        {
            return;
        }

        var script = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));
        var start = script.IndexOf("function buildWatchlistRuntimeHealthSummary(", StringComparison.Ordinal);
        var formatStart = script.IndexOf("function formatRelativeTime(", StringComparison.Ordinal);
        Assert.True(start >= 0, "the runtime health summary must stay liftable for this harness.");
        var end = script.IndexOf("\nfunction ", start + 10, StringComparison.Ordinal);
        Assert.True(end > start, "the runtime health summary function must be closed by the next function.");
        var summary = script[start..end];
        string format;
        if (formatStart >= 0)
        {
            var formatEnd = script.IndexOf("\nfunction ", formatStart + 10, StringComparison.Ordinal);
            format = formatEnd > formatStart ? script[formatStart..formatEnd] : script[formatStart..];
        }
        else
        {
            format = "function formatRelativeTime(value){ return 'at ' + String(value); }";
        }

        var harness = $$"""
            const assert = require('node:assert/strict');
            function escapeHtml(text) {
                if (text === null || text === undefined) return '';
                return String(text).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'","&#39;");
            }
            {{format}}
            {{summary}}
            {{assertions}}
            """;
        var process = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var node = System.Diagnostics.Process.Start(process)!;
        node.StandardInput.Write(harness);
        node.StandardInput.Close();
        Assert.True(node.WaitForExit(30000), node.StandardError.ReadToEnd());
        var error = node.StandardError.ReadToEnd();
        Assert.True(node.ExitCode == 0, error);
    }

    private static bool NodeIsAvailable()
    {
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (probe is null)
            {
                return false;
            }

            return probe.WaitForExit(10000) && probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FindSourceFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate {Path.Combine(pathParts)}.");
    }
}
