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
