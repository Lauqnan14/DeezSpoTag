using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataAutomationResilienceTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedCancellationOrIncompleteResultRetainsRun(bool returnsFalse)
    {
        var root = Path.Join(Path.GetTempPath(), "artist-unexpected-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var coordinator = CreateCoordinatorForSweeps(root);
            Func<CancellationToken, Task<bool>> run = _ => returnsFalse
                ? Task.FromResult(false)
                : Task.FromException<bool>(new TaskCanceledException("Target request timed out", new TimeoutException()));
            Assert.True(await EnqueueCancellationScenario(coordinator, run));
            await WaitForSweepToSettle(coordinator);
            var state = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(await File.ReadAllTextAsync(
                Path.Join(root, "library-artist-images", "metadata-automation-state.json")))!;
            Assert.NotNull(state.ActiveRun);
            Assert.Null(state.LastTargetUpdateUtc);
            Assert.Equal(1, state.ActiveRun.ConsecutiveFailures);
            Assert.True(File.Exists(Path.Join(root, "library-artist-images", "metadata-automation-outcomes.ndjson")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RepeatedPreparationTimeoutRetainsRunForExplicitResume()
    {
        var root = Path.Join(Path.GetTempPath(), "artist-timeout-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var coordinator = CreateCoordinatorForSweeps(root);
            var retained = new ArtistMetadataActiveRun
            {
                Operation = "target-update", RunId = Guid.NewGuid().ToString("N"), StartedAtUtc = DateTimeOffset.UtcNow,
                ConsecutiveFailures = 100, TargetRequest = new MetadataUpdaterRunRequest()
            };
            Assert.True(await EnqueueCancellationScenario(coordinator,
                _ => Task.FromException<bool>(new TimeoutException("Plex request timed out")), retained));
            await WaitForSweepToSettle(coordinator);
            var state = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(await File.ReadAllTextAsync(
                Path.Join(root, "library-artist-images", "metadata-automation-state.json")))!;
            Assert.NotNull(state.ActiveRun);
            Assert.True(state.ActiveRun.Paused);
            Assert.Equal(ArtistMetadataRunDispositions.NeedsAttention, state.ActiveRun.Disposition);
            Assert.Null(state.LastTargetUpdateUtc);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<bool> EnqueueCancellationScenario(ArtistMetadataAutomationCoordinator coordinator,
        Func<CancellationToken, Task<bool>> run, ArtistMetadataActiveRun? resumed = null)
        => (Task<bool>)typeof(ArtistMetadataAutomationCoordinator).GetMethod("EnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object?[] { "target-update", run, CancellationToken.None, resumed, null,
                new MetadataUpdaterRunRequest { Targets = new() { "plex" }, Force = true }, false })!;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TargetUpdateReachesTheTargetWorkerWithoutRefreshingProviders(bool includeDiscography, bool automatic)
    {
        var root = Path.Join(Path.GetTempPath(), "metadata-cache-separation-" + Guid.NewGuid().ToString("N"));
        using var coordinator = CreateCoordinatorForSweeps(root);
        var updater = typeof(ArtistMetadataAutomationCoordinator)
            .GetField("_targetUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        using var targetGate = new SemaphoreSlim(1, 1);
        typeof(ArtistMetadataUpdaterService).GetField("_runGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(updater, targetGate);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // The cache dependency is deliberately absent. Any hidden provider-refresh call fails;
        // reaching the real target worker instead observes cancellation at its entry gate.
        var method = typeof(ArtistMetadataAutomationCoordinator)
            .GetMethod("RunTargetUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<bool>)method.Invoke(coordinator, new object?[]
        {
            new MetadataUpdaterRunRequest { IncludeDiscography = includeDiscography, IncludePopularSongs = true },
            automatic, null, cancellation.Token
        })!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, targetGate.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainRefreshCacheRetainsItsProviderAndDiscographyOptions(bool includeDiscography)
    {
        var preferences = new UserPreferencesDto
        {
            MetadataUpdaterSource = "apple", MetadataUpdaterFolderId = "7",
            MetadataUpdaterIncludePopularSongs = true,
            MetadataUpdaterIncludeDiscography = includeDiscography,
            MetadataUpdaterOcrTextArtBlocking = false
        };
        var method = typeof(ArtistMetadataAutomationCoordinator)
            .GetMethod("BuildCacheRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var request = (ArtistMetadataCacheRefreshRequest)method.Invoke(null, new object[] { preferences })!;
        Assert.Equal("apple", request.Source);
        Assert.Equal(7L, request.FolderId);
        Assert.True(request.IncludePopularSongs);
        Assert.Equal(includeDiscography, request.IncludeDiscography);
        Assert.False(request.ForceProviderRefresh);
        Assert.False(request.OcrTextArtBlockingEnabled);
    }

    [Fact]
    public void ArtistTrackingWritesStayOutsideThePerArtistLoopAndEnrichmentInsideItsDeadline()
    {
        var updater = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));
        var loop = updater.Substring(updater.IndexOf("foreach (var tracked in allCandidates)", StringComparison.Ordinal));
        loop = loop.Substring(0, loop.IndexOf("Phase = \"Metadata update completed\"", StringComparison.Ordinal));
        Assert.DoesNotContain("SaveStateAsync", loop, StringComparison.Ordinal);
        Assert.Contains("result.Targets", loop, StringComparison.Ordinal);
        Assert.Contains("await outcomeSink(record)", loop, StringComparison.Ordinal);
        var cache = ReadCacheRefresh();
        var enrich = cache.IndexOf("await RetainArtistEnrichmentAsync", StringComparison.Ordinal);
        Assert.True(enrich > cache.IndexOf("public async Task<bool> RefreshArtistAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("artistCancellation.CancelAfter", cache, StringComparison.Ordinal);
        Assert.Contains("retainEnrichment: true", cache, StringComparison.Ordinal);
        Assert.Contains("bool retainEnrichment = false", cache, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistPageRefresh_ReportsQueueAcceptanceAndRestoresButton()
    {
        var source = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "library.js"));
        var start = source.IndexOf("    refreshButton.addEventListener('click', async () => {", StringComparison.Ordinal);
        var end = source.IndexOf("    resetMatchButton.addEventListener", start, StringComparison.Ordinal);
        var script = """
            const assert = require('node:assert/strict');
            const artistId = 994;
            const label = {textContent: 'Refresh cache'}, status = {};
            const document = {getElementById: () => status};
            const cachePanel = {classList: {add() {}}};
            const refreshButton = {querySelector: () => label, setAttribute() {}, removeAttribute() {},
                addEventListener: (event, handler) => refreshButton.handler = handler};
            let queued = true, failing = false, messages = [], requests = [];
            const showToast = (message, warning) => messages.push({message, warning});
            const fetchJson = async (url, options) => {
                requests.push({url, options});
                if (failing) throw new Error('network');
                return {queued};
            };
            """ + source[start..end] + """
            (async () => {
                await refreshButton.handler();
                assert.equal(requests[0].url, '/api/library/artists/994/external-cache/refresh');
                assert.equal(requests[0].options.method, 'POST');
                assert.equal(status.textContent, 'Source cache: queued');
                assert.equal(messages.at(-1).warning, false);
                queued = false;
                await refreshButton.handler();
                assert.equal(status.textContent, 'Source cache: not queued');
                assert.equal(messages.at(-1).warning, true);
                failing = true;
                await refreshButton.handler();
                assert.equal(status.textContent, 'Source cache: failed');
                assert.equal(refreshButton.disabled, false);
                assert.equal(label.textContent, 'Refresh cache');
            })().catch(error => {console.error(error); process.exitCode = 1;});
            """;
        using var process = Process.Start(new ProcessStartInfo("node")
        { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public void ArtistPageCacheRefresh_ReusesUpdaterQueueAndSettingsForOnlyThisArtist()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("public async Task<bool> EnqueueArtistCacheRefreshAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("public Task<bool> EnqueueCacheRefreshAsync", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = source[start..end];
        Assert.Contains("await _preferences.LoadAsync()", method);
        Assert.Contains("BuildCacheRequest(preferences) with { ArtistId = artistId, FolderId = null }", method);
        Assert.Contains("await EnqueueCacheRefreshAsync(request, cancellationToken)", method);

        var controller = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryArtistExternalCacheApiController.cs"));
        Assert.Contains("_coordinator.EnqueueArtistCacheRefreshAsync(id, cancellationToken)", controller);
        Assert.Contains("queued", controller);
        Assert.DoesNotContain("RefreshArtistAsync(", controller);
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "Library", "Artist.cshtml"));
        Assert.Contains("<span>Refresh cache</span>", view);
        Assert.DoesNotContain("<span>Update Cache</span>", view);
    }

    [Fact]
    public void CacheWorker_PublishesTargetsBeforeProcessingAndCoordinatorCapturesThem()
    {
        var cache = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));
        Assert.Contains("targetSink", cache);
        Assert.True(cache.IndexOf("await targetSink(", StringComparison.Ordinal) < cache.IndexOf("foreach (var artist in artists)", StringComparison.Ordinal));
        Assert.Contains("targetSink: PersistRunTargetsAsync", ReadCoordinator());
        Assert.Contains("var total = targetArtistIds.Count;", cache);
        Assert.Contains("? resumedRun.TargetArtistIds", cache);
    }

    [Fact]
    public void CancelClick_SendsRequestAndReleasesControls()
    {
        var source = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
        var start = source.IndexOf("    if (cancelButton) {", source.IndexOf("function initMetadataUpdater()", StringComparison.Ordinal), StringComparison.Ordinal);
        var end = source.IndexOf("    if (targetContainer)", start, StringComparison.Ordinal);
        var helperStart = source.IndexOf("async function runActivityButtonAction(", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("function applyLibraryScanProgress", helperStart, StringComparison.Ordinal);
        var script = """
            const assert = require('node:assert/strict');
            let metadataActionPending = false, metadataStatusVersion = 0, requests = 0;
            const cancelButton = { disabled: false, addEventListener: (event, handler) => cancelButton.handler = handler };
            function applyMetadataRunControls() { cancelButton.disabled = metadataActionPending; }
            const activityFetch = async () => { requests++; return { ok: true, json: async () => ({ cancelled: true, status: {runState: 'idle'} }) }; };
            const applyMetadataAutomationStatus = () => {};
            const notifyActivity = () => {};
            const refreshLibraryScanAndMetadataStatus = async () => {};
            """ + source[helperStart..helperEnd] + source[start..end] + """
            (async () => { await cancelButton.handler(); assert.equal(requests, 1); assert.equal(metadataActionPending, false); })()
                .catch(error => { console.error(error); process.exitCode = 1; });
            """;
        using var process = Process.Start(new ProcessStartInfo("node")
        { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public void PauseResumeRoutes_ValidateRunIdentityAndKeepAuthentication()
    {
        var controller = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "ArtistMetadataAutomationApiController.cs"));
        foreach (var route in new[] { "cache/pause", "cache/resume", "targets/pause", "targets/resume" })
            Assert.Contains($"[HttpPost(\"{route}\")]", controller);
        Assert.Contains("request.RunId", controller);
        Assert.Contains("Conflict(", controller);
        Assert.Contains("[Authorize]", controller);
        Assert.Contains("[AutoValidateAntiforgeryToken]", controller);
        Assert.Contains("coordinator.CancelAsync", controller);
    }

    [Fact]
    public void MetadataControls_ExecuteLifecycleAndIgnoreStaleResponses()
    {
        var source = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
        var start = source.IndexOf("let metadataAutomationStatus =", StringComparison.Ordinal);
        Assert.True(start >= 0, "The metadata controls need an authoritative lifecycle state.");
        var end = source.IndexOf("function startLibraryScanStatusPolling()", start, StringComparison.Ordinal);
        var script = """
            const assert = require('node:assert/strict');
            const elements = new Map();
            for (const id of ['metadata-refresh-cache-button', 'metadata-update-button', 'metadata-cancel-button'])
                elements.set(id, { disabled: false, textContent: '', dataset: {} });
            const document = { getElementById: id => elements.get(id) || null };
            const applyMetadataUpdaterStatus = () => {};
            const applyMetadataTargetBreakdown = () => {};
            const pickMetadataStatus = status => status.cacheRefresh;
            const notifyActivity = () => {};
            """ + source[start..end] + """
            const cache = elements.get('metadata-refresh-cache-button');
            const target = elements.get('metadata-update-button');
            const cancel = elements.get('metadata-cancel-button');
            for (const [state, text, disabled] of [
                ['running', 'Pause', false], ['pausing', 'Pausing…', true],
                ['paused', 'Resume', false], ['resuming', 'Resuming…', true]]) {
                applyMetadataAutomationStatus({ runState: state, runOperation: 'cache-refresh', runId: 'same-run' });
                assert.equal(cache.textContent, text);
                assert.equal(cache.disabled, disabled);
                assert.equal(target.disabled, true);
                assert.equal(cancel.disabled, state === 'pausing' || state === 'resuming');
            }
            applyMetadataAutomationStatus({ runState: 'idle' });
            assert.equal(cache.textContent, 'Refresh Cache');
            assert.equal(target.textContent, 'Update Server');
            assert.equal(cache.disabled, false);
            assert.equal(cancel.disabled, true);
            applyMetadataAutomationStatus({ runState: 'paused', runOperation: 'target-update', runId: 'original' });
            assert.equal(target.textContent, 'Resume');
            assert.equal(cache.disabled, true);
            assert.equal(acceptMetadataPoll({ runState: 'idle' }, metadataStatusVersion - 1, 1), false);
            assert.equal(target.textContent, 'Resume');
            let requests = [];
            let resolveRequest;
            const activityFetch = (url, options) => {
                requests.push({ url, body: JSON.parse(options.body) });
                return new Promise(resolve => resolveRequest = resolve);
            };
            (async () => {
                let freshStarts = 0;
                const action = runMetadataControlAction('target-update', async () => freshStarts++);
                await runMetadataControlAction('target-update', async () => freshStarts++);
                assert.equal(requests.length, 1);
                assert.equal(requests[0].url, '/api/library/artist-metadata/targets/resume');
                assert.equal(requests[0].body.runId, 'original');
                resolveRequest({ ok: true, json: async () => ({ accepted: true, status: {
                    runState: 'running', runOperation: 'target-update', runId: 'original' } }) });
                await action;
                assert.equal(freshStarts, 0);
                assert.equal(target.textContent, 'Pause');
                assert.equal(target.disabled, false);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;
        using var process = Process.Start(new ProcessStartInfo("node")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10000), "JavaScript lifecycle checks timed out.");
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public void ShutdownKeepsInterruptedRunResumable_UserCancelClearsIt()
    {
        var source = ReadCoordinator();
        var methodStart = source.IndexOf("private async Task RunManualOperationAsync", StringComparison.Ordinal);
        var start = source.IndexOf("catch (OperationCanceledException)", methodStart, StringComparison.Ordinal);
        var body = source[start..source.IndexOf("catch (Exception ex)", start, StringComparison.Ordinal)];

        // Shutdown must keep the run so it resumes on the next start.
        Assert.Contains("_shutdownToken.IsCancellationRequested", body, StringComparison.Ordinal);
        Assert.Contains("keeping the run for resume", body, StringComparison.Ordinal);
        Assert.Contains("PersistCheckpointAsync", body, StringComparison.Ordinal);

        // Only the user-cancel branch clears the run.
        var clearIndex = body.IndexOf("ClearActiveRunWithoutStampingAsync", StringComparison.Ordinal);
        Assert.True(clearIndex > body.IndexOf("else", StringComparison.Ordinal),
            "The clear path must be the user-cancel branch, not the shutdown branch.");

        // Target update reports cancellation as a false result, so the same rule has
        // to hold before the catch. Otherwise shutdown wipes the checkpoint.
        var unfinished = source.IndexOf("if (!completed)", methodStart, StringComparison.Ordinal);
        Assert.True(unfinished > 0 && unfinished < start);
        var unfinishedBody = source[unfinished..start];
        Assert.Contains("_shutdownToken.IsCancellationRequested", unfinishedBody, StringComparison.Ordinal);
        Assert.Contains("keeping the run for resume", unfinishedBody, StringComparison.Ordinal);
        Assert.Contains("PersistCheckpointAsync", unfinishedBody, StringComparison.Ordinal);
        Assert.Contains("FlushCheckpointAsync", unfinishedBody, StringComparison.Ordinal);
        var unfinishedClear = unfinishedBody.IndexOf("ClearActiveRunWithoutStampingAsync", StringComparison.Ordinal);
        Assert.True(unfinishedClear > unfinishedBody.IndexOf("else", StringComparison.Ordinal),
            "A false result during shutdown must keep the run.");
    }

    [Fact]
    public void ManualRunsUseARealCancellationTokenNotNone()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private async Task RunManualOperationAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = source[start..(start + 2500)];

        Assert.Contains("await run(cts.Token)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("run(CancellationToken.None)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsLinkedToShutdownSoTheAppCanStopARun()
    {
        var source = ReadCoordinator();
        var updater = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));

        Assert.Contains("CreateLinkedTokenSource(_userCancelCts.Token, _shutdownToken)", source, StringComparison.Ordinal);
        Assert.Contains("public bool Cancel()", source, StringComparison.Ordinal);
        Assert.Contains("_targetUpdate.Cancel()", source, StringComparison.Ordinal);
        Assert.Contains("return requested || running;", source, StringComparison.Ordinal);
        Assert.Contains("public bool Cancel()", updater, StringComparison.Ordinal);
        Assert.Contains("CreateLinkedTokenSource(cancellationToken, runCts.Token)", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelEndpointExists()
    {
        var controller = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "ArtistMetadataAutomationApiController.cs"));

        Assert.Contains("[HttpPost(\"cancel\")]", controller, StringComparison.Ordinal);
        Assert.Contains("coordinator.CancelAsync(cancellationToken)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualRunFailuresAreObservedInsteadOfSwallowed()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private async Task RunManualOperationAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private void RecordOperationFailure", start, StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("catch (OperationCanceledException)", body, StringComparison.Ordinal);
        Assert.Contains("ExpectedExceptionPolicy.IsRecoverable(ex)", body, StringComparison.Ordinal);
        Assert.Contains("RecordOperationFailure", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizingACancelledCacheRefreshClearsItsRunningStatus()
    {
        var startedAt = new DateTimeOffset(2026, 9, 25, 19, 42, 22, TimeSpan.Zero);
        var completedAt = new DateTimeOffset(2026, 9, 28, 13, 0, 0, TimeSpan.Zero);
        var status = ArtistMetadataAutomationStatus.Idle() with
        {
            ActiveOperation = "cache-refresh",
            CacheRefresh = new ArtistMetadataCacheStatus(
                Running: true,
                Automatic: false,
                Phase: "Refreshing artist metadata cache",
                Message: null,
                ProcessedArtists: 1116,
                TotalArtists: 3768,
                CurrentArtist: "Frankie Paul",
                StartedAtUtc: startedAt,
                CompletedAtUtc: null,
                SuccessfulArtists: 1114,
                FailedArtists: 1)
        };

        var finalized = ArtistMetadataAutomationCoordinator.FinalizeOperationStatus(
            status,
            "cache-refresh",
            interruptedByShutdown: false,
            completedAt);

        Assert.Null(finalized.ActiveOperation);
        Assert.False(finalized.CacheRefresh.Running);
        Assert.Equal("Cache refresh cancelled", finalized.CacheRefresh.Phase);
        Assert.Equal("The cache refresh was cancelled.", finalized.CacheRefresh.Message);
        Assert.Null(finalized.CacheRefresh.CurrentArtist);
        Assert.Equal(completedAt, finalized.CacheRefresh.CompletedAtUtc);
        Assert.Equal(1116, finalized.CacheRefresh.ProcessedArtists);
        Assert.Equal(1114, finalized.CacheRefresh.SuccessfulArtists);
        Assert.Equal(1, finalized.CacheRefresh.FailedArtists);
    }

    [Fact]
    public void ScheduleLoopSurvivesAStrayCancellationException()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("protected override async Task ExecuteAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private static async Task<bool> DelayOrStopAsync", start, StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)", body, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException ex)", body, StringComparison.Ordinal);
        Assert.Contains("ExpectedExceptionPolicy.IsRecoverable(ex)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception ex) when (ex is not OperationCanceledException)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedRunsResumeOnStartup()
    {
        var source = ReadCoordinator();

        Assert.Contains("await ResumeInterruptedRunAsync(stoppingToken);", source, StringComparison.Ordinal);
        Assert.Contains("state.ActiveRun", source, StringComparison.Ordinal);
        Assert.Contains("run.CompletedArtistIds", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BothOperationsPersistCompletedArtistsAndSkipThemOnResume()
    {
        var coordinator = ReadCoordinator();
        var cache = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));
        var updater = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));

        // The per-artist durability barrier is the journal append chain, not a state-file rewrite.
        Assert.Contains("await FlushCheckpointAsync();", coordinator, StringComparison.Ordinal);
        Assert.Contains("await journal.FlushAsync(", coordinator, StringComparison.Ordinal);

        // The checkpoint is written from a per-artist outcome record, synchronously from the run loop,
        // so a shutdown cannot leave the durable record lagging behind work already performed.
        Assert.Contains("NoteArtistOutcome(ArtistRunOutcomeRecord record)", coordinator, StringComparison.Ordinal);
        Assert.Contains("outcomeSink: NoteArtistOutcome", coordinator, StringComparison.Ordinal);
        Assert.Contains("await outcomeSink(record)", updater, StringComparison.Ordinal);

        // Outcomes are appended to a journal rather than re-serialised into the state file after every
        // artist, which is what keeps the per-artist cost independent of the number already processed.
        Assert.Contains("ArtistMetadataOutcomeJournal.OpenAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("ArtistMetadataOutcomeJournal.Read", coordinator, StringComparison.Ordinal);
        Assert.Contains("journal.AppendAsync(record", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Outcomes = checkpoint.Outcomes.ToList()", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletedArtistIds = checkpoint.CompletedArtistIds.ToList()", coordinator, StringComparison.Ordinal);

        // Resume skips only terminal outcomes, so failures and missing metadata are retried.
        Assert.Contains("ArtistRunOutcomes.BuildResumeSkipSet", updater, StringComparison.Ordinal);
        Assert.Contains("if (skipSet.Contains(tracked.ArtistId))", updater, StringComparison.Ordinal);

        // Counters are rebuilt from the persisted outcomes, not inferred from a visited-id count.
        Assert.Contains("MetadataRunCounters.FromOutcomes(allCandidates.Count, resumedRun?.Outcomes)", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("counters.ProcessedArtists = allCandidates.Count(", updater, StringComparison.Ordinal);
        Assert.Contains("CountOutcomes(priorOutcomes)", cache, StringComparison.Ordinal);
        Assert.Contains("if (completed.Contains(artist.Id))", cache, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumedRunReusesTheArtistSetTheRunResolvedTo()
    {
        var coordinator = ReadCoordinator();
        var updater = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));

        // The frozen target set is only honoured if something actually publishes it. Asserting the
        // field merely round trips would miss a run that never populates it, which silently disables
        // the whole behaviour: the resume would re-probe the live servers and could select a
        // different set than the recorded outcomes belong to.
        Assert.Contains("await targetSink(allCandidates.Select(candidate => candidate.ArtistId).ToList())", updater, StringComparison.Ordinal);
        Assert.Contains("Func<IReadOnlyList<long>, Task>? targetSink", updater, StringComparison.Ordinal);
        Assert.Contains("private Task NoteRunTargets(IReadOnlyList<long> targetArtistIds)", coordinator, StringComparison.Ordinal);
        Assert.Contains("checkpoint.TargetArtistIds = targetArtistIds.ToList();", coordinator, StringComparison.Ordinal);
        Assert.Contains("NoteRunTargets,", coordinator, StringComparison.Ordinal);

        // And the resume path must actually branch on it.
        Assert.Contains("resumedRun is { TargetArtistIds.Count: > 0 }", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedRunIsFlushedOnShutdownInsteadOfOnlyOnNextStart()
    {
        var coordinator = ReadCoordinator();
        var queue = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "LibraryArtistMetadataQueueService.cs"));

        // BackgroundService only awaits ExecuteAsync, so shutdown must explicitly wait for the run
        // and flush the checkpoint before the host exits.
        Assert.Contains("public override async Task StopAsync(CancellationToken cancellationToken)", coordinator, StringComparison.Ordinal);
        Assert.Contains("ShutdownDrainTimeout", coordinator, StringComparison.Ordinal);
        Assert.Contains("await FlushCheckpointAsync();", coordinator, StringComparison.Ordinal);

        // An interrupted artist must stay in the durable queue: the cancellation path rethrows before
        // the item is completed, and completion is gated on the work having finished.
        Assert.Contains("catch (OperationCanceledException)", queue, StringComparison.Ordinal);
        Assert.Contains("leaving it queued for the next start", queue, StringComparison.Ordinal);
        Assert.Contains("if (finished)", queue, StringComparison.Ordinal);
        var cancellationIndex = queue.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal);
        var completeIndex = queue.IndexOf("PersistentArtistQueueStore.CompleteItem", StringComparison.Ordinal);
        Assert.True(cancellationIndex > 0 && completeIndex > cancellationIndex,
            "CompleteItem must not run for an artist that was interrupted by shutdown.");
    }

    [Fact]
    public void APoisonedRunIsAbandonedInsteadOfRetriedForever()
    {
        var coordinator = ReadCoordinator();
        Assert.Contains("MaxConsecutiveRunFailures", coordinator, StringComparison.Ordinal);
        Assert.Contains("poisoned.ConsecutiveFailures++", coordinator, StringComparison.Ordinal);
        Assert.Contains("await ClearActiveRunWithoutStampingAsync();", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheRefreshSchedulingRemainsIndependentOfTargetSweepCompletion()
    {
        var coordinator = ReadCoordinator();

        Assert.DoesNotContain("IsCacheRefreshComplete", coordinator, StringComparison.Ordinal);
        Assert.Contains("await RunCacheRefreshAsync(request, automatic: false, token);\n                return true;", coordinator, StringComparison.Ordinal);
        Assert.Contains("await RunCacheRefreshAsync(cacheRequest, automatic: true, token);\n                        return true;", coordinator, StringComparison.Ordinal);
        var cacheRun = coordinator.IndexOf("if (cacheDue)", StringComparison.Ordinal);
        var targetRun = coordinator.IndexOf("if (updateDue)", StringComparison.Ordinal);
        Assert.True(cacheRun >= 0 && targetRun > cacheRun);
    }

    [Fact]
    public async Task UnresolvedTargetSweepsDoNotAdvanceLastSuccessfulRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "deezspotag-artist-sweep-clock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "library-artist-images", "metadata-automation-state.json");

            // One failed target in an otherwise successful artist leaves required server work
            // outstanding, so the sweep must not stamp the success clock and must stay recoverable.
            var unresolved = CreateCoordinatorForSweeps(root);
            var pendingWork = new[]
            {
                new ArtistTargetPendingWork("plex", new[] { ArtistTargetFields.Background }, ArtistPendingWorkCategories.Transient)
            };
            Assert.True(await EnqueueSweep(unresolved, artistId: 41, pendingWork));
            await WaitForSweepToSettle(unresolved);
            var retained = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(
                await File.ReadAllTextAsync(statePath))!;
            Assert.Null(retained.LastTargetUpdateUtc);
            Assert.Equal(ArtistMetadataRunDispositions.PendingRetry, unresolved.GetStatus().RunDisposition);
            Assert.NotNull(retained.ActiveRun!.NextAttemptUtc);
            Assert.Equal(1, retained.ActiveRun.TargetAttempts[ArtistMetadataPendingWork.Key(41, "plex")]);
            Assert.True(File.Exists(Path.Combine(root, "library-artist-images", "metadata-automation-outcomes.ndjson")),
                "The retained run must keep the journal that holds its unresolved evidence.");

            // An all-failed sweep is treated the same way: the failure leaves required work behind, so
            // the clock cannot move and the work stays recoverable. A 401 pauses for user action
            // instead of burning attempts.
            var allFailedRoot = Path.Combine(root, "all-failed");
            var allFailed = CreateCoordinatorForSweeps(allFailedRoot);
            Assert.True(await EnqueueRecords(allFailed, new[]
            {
                new ArtistRunOutcomeRecord(42, ArtistRunOutcomes.Failed,
                    Targets: new[] { new ArtistTargetResult("plex", ArtistTargetOutcome.Failed, Error: "Plex update failed.") },
                    PendingWork: new[]
                    {
                        new ArtistTargetPendingWork("plex", new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background },
                            ArtistPendingWorkCategories.Transient, "Plex update failed.")
                    }),
                new ArtistRunOutcomeRecord(43, ArtistRunOutcomes.Failed,
                    Targets: new[] { new ArtistTargetResult("plex", ArtistTargetOutcome.Failed,
                        Error: "The request failed with status code 401 (Unauthorized).") },
                    PendingWork: new[]
                    {
                        new ArtistTargetPendingWork("plex", new[] { ArtistTargetFields.Avatar },
                            ArtistPendingWorkCategories.Authentication,
                            "The request failed with status code 401 (Unauthorized).")
                    })
            }));
            await WaitForSweepToSettle(allFailed);
            var failedSweep = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(await File.ReadAllTextAsync(
                Path.Combine(allFailedRoot, "library-artist-images", "metadata-automation-state.json")))!;
            Assert.Null(failedSweep.LastTargetUpdateUtc);
            Assert.True(failedSweep.ActiveRun!.Paused);
            Assert.Equal(ArtistMetadataRunDispositions.NeedsAttention, allFailed.GetStatus().RunDisposition);
            Assert.Null(failedSweep.ActiveRun.NextAttemptUtc);
            Assert.Equal(1, failedSweep.ActiveRun.TargetAttempts[ArtistMetadataPendingWork.Key(42, "plex")]);
            Assert.Contains("Resume", failedSweep.ActiveRun.PendingNotice!, StringComparison.Ordinal);

            // The same sweep with nothing outstanding is settled and does stamp the clock.
            var settledRoot = Path.Combine(root, "settled");
            Directory.CreateDirectory(settledRoot);
            var settled = CreateCoordinatorForSweeps(settledRoot);
            Assert.True(await EnqueueSweep(settled, artistId: 41, pendingWork: Array.Empty<ArtistTargetPendingWork>()));
            await WaitForSweepToSettle(settled);
            var stamped = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(await File.ReadAllTextAsync(
                Path.Combine(settledRoot, "library-artist-images", "metadata-automation-state.json")))!;
            Assert.NotNull(stamped.LastTargetUpdateUtc);
            Assert.Null(stamped.ActiveRun);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task<bool> EnqueueSweep(
        ArtistMetadataAutomationCoordinator coordinator,
        long artistId,
        IReadOnlyList<ArtistTargetPendingWork> pendingWork)
        => await EnqueueRecords(coordinator, new[]
        {
            new ArtistRunOutcomeRecord(
                artistId,
                pendingWork.Count == 0 ? ArtistRunOutcomes.Succeeded : ArtistRunOutcomes.Partial,
                Targets: new[] { new ArtistTargetResult("plex", ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }) },
                PendingWork: pendingWork)
        });

    private static Task<bool> EnqueueRecords(
        ArtistMetadataAutomationCoordinator coordinator,
        IReadOnlyList<ArtistRunOutcomeRecord> records)
        => (Task<bool>)typeof(ArtistMetadataAutomationCoordinator)
            .GetMethod("EnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object?[]
            {
                "target-update",
                (Func<CancellationToken, Task<bool>>)(async token =>
                {
                    foreach (var record in records)
                    {
                        typeof(ArtistMetadataAutomationCoordinator)
                            .GetMethod("NoteArtistOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(coordinator, new object[] { record });
                    }

                    await Task.Yield();
                    return true;
                }),
                CancellationToken.None,
                null,
                null,
                new MetadataUpdaterRunRequest { Targets = new List<string> { "plex" }, Force = true },
                false
            })!;

    private static async Task WaitForSweepToSettle(ArtistMetadataAutomationCoordinator coordinator)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (coordinator.GetStatus().RunState == "running")
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ArtistMetadataAutomationCoordinator CreateCoordinatorForSweeps(string root)
    {
        // Only status/state of the updater is used; provider dependencies are never invoked.
        var updater = (ArtistMetadataUpdaterService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMetadataUpdaterService));
        typeof(ArtistMetadataUpdaterService).GetField("_statusLock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater, new object());
        typeof(ArtistMetadataUpdaterService).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater, MetadataUpdaterStatusSnapshot.Idle());
        var env = new SweepClockEnvironment(root);
        return new ArtistMetadataAutomationCoordinator(null!, updater,
            new UserPreferencesStore(env, NullLogger<UserPreferencesStore>.Instance), env,
            NullLogger<ArtistMetadataAutomationCoordinator>.Instance);
    }

    private sealed class SweepClockEnvironment(string root) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string? AppDataRoot => root;
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void CancelClearsActiveRunWithoutStampingLastRun()
    {
        var coordinator = ReadCoordinator();
        var start = coordinator.IndexOf("private async Task RunManualOperationAsync", StringComparison.Ordinal);
        var end = coordinator.IndexOf("private void RecordOperationFailure", start, StringComparison.Ordinal);
        var body = coordinator[start..end];

        Assert.Contains("catch (OperationCanceledException)", body, StringComparison.Ordinal);
        Assert.Contains("await ClearActiveRunWithoutStampingAsync();", body, StringComparison.Ordinal);
        Assert.Contains("await StopCheckpointWritesAsync();", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("state.LastCacheRefreshUtc = DateTimeOffset.UtcNow;", body.Substring(body.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("state.ActiveRun = null;", coordinator, StringComparison.Ordinal);
        Assert.Contains("state.LastCacheRefreshUtc = DateTimeOffset.UtcNow;", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetUpdateDoesNotHoldTheGlobalHeavyLockForTheWholeSweep()
    {
        var updater = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));
        var runStart = updater.IndexOf("public async Task<bool> RunAndWaitAsync(", StringComparison.Ordinal);
        var runEnd = updater.IndexOf("private async Task RunInternalAsync(", runStart, StringComparison.Ordinal);
        var run = updater[runStart..runEnd];

        Assert.DoesNotContain("RunHeavyWorkAsync", updater, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException)", run, StringComparison.Ordinal);
        Assert.Contains("return false;", run, StringComparison.Ordinal);
        Assert.Contains("return !linkedCts.Token.IsCancellationRequested;", run, StringComparison.Ordinal);
        Assert.Contains("throw;", updater.Substring(updater.IndexOf("Phase = \"Metadata update cancelled\"", StringComparison.Ordinal), 500), StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(ArtistYield, cancellationToken);", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordOperationFailureCoversCacheAndTarget()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private void RecordOperationFailure", StringComparison.Ordinal);
        var body = source[start..(start + 1600)];

        Assert.Contains("operation == \"cache-refresh\"", body, StringComparison.Ordinal);
        Assert.Contains("Phase = \"Cache refresh failed\"", body, StringComparison.Ordinal);
        Assert.Contains("Phase = \"Metadata update failed\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveRunIsClearedWhenAnOperationFinishes()
    {
        var source = ReadCoordinator();

        Assert.Contains("state.ActiveRun = null;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledRunsShareTheManualEnqueuePathRatherThanDuplicatingIt()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private async Task RunScheduledOperationsAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task WaitForActiveOperationAsync", start, StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("await EnqueueAsync(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_operationGate.WaitAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistNameIsNotRenderedTwice()
    {
        var updater = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));

        Assert.Contains("Phase = \"Updating artists\"", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("Phase = $\"Updating {artistName}\"", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentArtistIsAlwaysRendered()
    {
        var view = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        Assert.Contains("`Current Artist: ${currentArtist}`", view, StringComparison.Ordinal);
        Assert.DoesNotContain("if (running && currentArtist)", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelButtonIsDrivenByStatusNotHardcodedDisabled()
    {
        var view = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        Assert.Contains("cancel.disabled = !retained || metadataActionPending || state === 'pausing' || state === 'resuming';", view, StringComparison.Ordinal);
        Assert.DoesNotContain("cancellation is not available yet", view, StringComparison.Ordinal);
        Assert.Contains("id=\"metadata-cancel-button\" class=\"action-btn action-btn-sm\" type=\"button\">Cancel<", view, StringComparison.Ordinal);
        Assert.Contains("const cancelled = payload?.cancelled === true || stillRunning;", view, StringComparison.Ordinal);
        Assert.Contains("{ restoreDisabled: false }", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressTicksDoNotRebuildTheWholeStatusSnapshot()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private void UpdateCacheProgress", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = source[start..(start + 700)];

        Assert.DoesNotContain("GetStatus()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void LateProgressCallbacksCannotResurrectAFinishedRun()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private void UpdateCacheProgress", StringComparison.Ordinal);
        var body = source[start..(start + 800)];

        Assert.Contains("if (!_status.CacheRefresh.Running)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveOperation = \"cache-refresh\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BiographyProvidersAreQueriedSequentiallyThroughTheProviderGate()
    {
        var cache = ReadCacheRefresh();
        var catalog = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistArtworkCatalogService.cs"));

        Assert.DoesNotContain("await Task.WhenAll(requestedProviders", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("await Task.WhenAll(providers)", catalog, StringComparison.Ordinal);
        Assert.Contains("foreach (var provider in requestedProviders)", cache, StringComparison.Ordinal);
        Assert.Contains("gate.RunAsync(", cache, StringComparison.Ordinal);
        Assert.Contains("gate.IsUnavailable(providerName)", cache, StringComparison.Ordinal);
        Assert.Contains("new ArtistMetadataProviderGate(_logger)", cache, StringComparison.Ordinal);
        Assert.Contains("await AddRemoteProviderAsync(", catalog, StringComparison.Ordinal);
        Assert.Contains("ArtistMetadataProviderGate.ThrowIfRateLimited(response)", cache, StringComparison.Ordinal);
        Assert.Contains("ArtistMetadataProviderGate.ThrowIfRateLimited(response)", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheRefreshBoundsEachArtistAndPreservesParentCancellation()
    {
        var cache = ReadCacheRefresh();

        Assert.Contains("ArtistRefreshTimeout = TimeSpan.FromMinutes(10)", cache, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)", cache, StringComparison.Ordinal);
        Assert.Contains("refreshTask.WaitAsync(ArtistRefreshTimeout, cancellationToken)", cache, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", cache, StringComparison.Ordinal);
        Assert.Contains("catch (TimeoutException)", cache, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();", cache, StringComparison.Ordinal);
        Assert.Contains("artistCancellation.Cancel();", cache, StringComparison.Ordinal);
        Assert.Contains("Artist metadata cache refresh timed out for artist {ArtistId} ({ArtistName})", cache, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticBiographyAndMediaExtrasProvidersAreOrderedAndIsolated()
    {
        var cache = ReadCacheRefresh();
        var spotify = cache.IndexOf("BiographyProvider.Spotify,", StringComparison.Ordinal);
        var apple = cache.IndexOf("BiographyProvider.Apple,", spotify, StringComparison.Ordinal);
        var tidal = cache.IndexOf("BiographyProvider.Tidal,", apple, StringComparison.Ordinal);
        var lastFm = cache.IndexOf("BiographyProvider.LastFm,", tidal, StringComparison.Ordinal);
        var audiomack = cache.IndexOf("BiographyProvider.Audiomack,", lastFm, StringComparison.Ordinal);
        var qobuz = cache.IndexOf("BiographyProvider.Qobuz", audiomack, StringComparison.Ordinal);

        Assert.True(spotify >= 0 && apple > spotify && tidal > apple);
        Assert.True(lastFm > tidal && audiomack > lastFm && qobuz > audiomack);
        // The engines are referenced through their canonical constants now rather than as repeated
        // literals, so this asserts the same thing against the new spelling: media extras are
        // refreshed for Apple and Tidal, and neither call dropped its provider argument.
        Assert.Contains("RefreshMediaExtrasAsync(AppleSource", cache, StringComparison.Ordinal);
        Assert.Contains("RefreshMediaExtrasAsync(TidalSource", cache, StringComparison.Ordinal);
        Assert.Contains("gate.RunAsync(\n                provider,", cache, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", cache, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (!cancellationToken.IsCancellationRequested)", cache, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderOrderStillDecidesTheSelectedBiography()
    {
        var cache = ReadCacheRefresh();

        Assert.Contains("biographies.Add((provider, biography!));", cache, StringComparison.Ordinal);
        Assert.Contains("SelectArtistBiographySourceAsync(", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshSelectedArtistBiographyAsync(artistId, cancellationToken)", cache, StringComparison.Ordinal);
    }

    private static string ReadCacheRefresh()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));

    [Fact]
    public void FinishedRunKeepsShowingItsOwnOutcomeInsteadOfTheIdleOperation()
    {
        var view = ReadActivitiesView();
        var start = view.IndexOf("function pickMetadataStatus", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = view[start..(start + 900)];

        Assert.Contains("cacheFinished > targetFinished", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "automationStatus?.activeOperation === 'cache-refresh'\n                ? automationStatus?.cacheRefresh",
            view,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedRunShowsSucceededAndFailedCounts()
    {
        var view = ReadActivitiesView();

        Assert.Contains("if (!running && message) {", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheRefreshReportsAnOutcomeMessage()
    {
        var coordinator = ReadCoordinator();

        Assert.Contains("result.Error is null ? \"Cache refresh completed\" : \"Cache refresh failed\"", coordinator, StringComparison.Ordinal);
        Assert.Contains("$\"{result.Succeeded} succeeded, {result.Failed} failed.\"", coordinator, StringComparison.Ordinal);
    }

    private static string ReadActivitiesView()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

    private static string ReadCoordinator()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataAutomationCoordinator.cs"));

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
