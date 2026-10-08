using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackAnalysisBackgroundServiceGuardrailTest
{
    [Fact]
    public void VibeAnalyzerWorker_UsesOneAnalyzerAndFlushesOrderedJsonLines()
    {
        var repoRoot = ResolveRepoRoot();
        var analyzer = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Tools", "vibe_analyzer.py"));

        Assert.Contains("def run_worker(models_dir: str)", analyzer, StringComparison.Ordinal);
        Assert.Contains("analyzer = AudioAnalyzer(models_dir)", analyzer, StringComparison.Ordinal);
        Assert.Contains("for request_line in sys.stdin", analyzer, StringComparison.Ordinal);
        Assert.Contains("request_id = request.get(\"requestId\")", analyzer, StringComparison.Ordinal);
        Assert.Contains("file_path = request.get(\"filePath\")", analyzer, StringComparison.Ordinal);
        Assert.Contains("sys.stdout.flush()", analyzer, StringComparison.Ordinal);
        Assert.Contains("parser.add_argument(\"--worker\"", analyzer, StringComparison.Ordinal);
        Assert.Contains("\"errorCode\": \"VIBE_ANALYZER_NOT_INITIALIZED\"", analyzer, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalyzerHealth_StaysInRuntimeAndOnlyABooleanBadgeIsRendered()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var worker = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "Vibe", "VibeAnalyzerWorker.cs"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        Assert.Contains("VibeAnalyzerWorkerSnapshot Analyzer", service, StringComparison.Ordinal);
        Assert.Contains("_analyzerWorker?.GetSnapshot()", service, StringComparison.Ordinal);

        // The snapshot exposes a derived boolean so the view never has to
        // interpret the failure reason itself.
        Assert.Contains("public bool Degraded =>", worker, StringComparison.Ordinal);

        // A discreet degraded badge is rendered so a silent standard-mode
        // fallback is visible to the user.
        Assert.Contains("analysisDegradedPill", view, StringComparison.Ordinal);
        Assert.Contains("analyzer?.degraded === true", view, StringComparison.Ordinal);

        // The original intent is preserved and strengthened: the raw analyzer
        // failure reason must never reach the Activities view.
        Assert.DoesNotContain("analysisAnalyzerState", view, StringComparison.Ordinal);
        Assert.DoesNotContain("analysisAnalyzerFailure", view, StringComparison.Ordinal);
        Assert.DoesNotContain("lastFailureReason", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EssentiaProbeTimeout_IsConfigurableAndNotAHardcodedFifteenSeconds()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        // The probe loads every model graph including the ~332 MB Discogs519 MAEST
        // graph, so a 15s budget forced enhanced mode into standard fallback.
        Assert.DoesNotContain("process.WaitForExit(15000)", service, StringComparison.Ordinal);
        Assert.Contains(
            "VibeAnalyzerProbeTimeoutSecondsEnvironmentVariable = \"VIBE_ANALYZER_PROBE_TIMEOUT_SECONDS\"",
            service,
            StringComparison.Ordinal);
        Assert.Contains("DefaultVibeAnalyzerProbeTimeoutSeconds = 180", service, StringComparison.Ordinal);
        Assert.Contains(
            "RunProcessCapturingOutput(startInfo, ResolveProbeTimeout())",
            service,
            StringComparison.Ordinal);
        Assert.Contains("Essentia probe timed out after", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessRunner_DrainsPipesAsynchronouslyToAvoidDeadlockOnLargeOutput()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        // Reading the pipes only after the process exits deadlocks once the child
        // fills the OS pipe buffer, which pip reliably does during a large install
        // and which a noisy Essentia/TensorFlow load can also do.
        var beginStdout = service.IndexOf("process.BeginOutputReadLine();", StringComparison.Ordinal);
        var beginStderr = service.IndexOf("process.BeginErrorReadLine();", StringComparison.Ordinal);
        var timedWait = service.IndexOf(
            "if (!process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds",
            StringComparison.Ordinal);
        var flushWait = service.IndexOf("process.WaitForExit();", StringComparison.Ordinal);

        Assert.True(beginStdout > 0, "The capture helper must arm the asynchronous stdout reader.");
        Assert.True(beginStderr > 0, "The capture helper must arm the asynchronous stderr reader.");
        Assert.True(timedWait > 0, "The capture helper must still enforce a timeout.");
        Assert.True(beginStdout < timedWait, "Readers must be armed before the blocking wait.");
        Assert.True(beginStderr < timedWait, "Readers must be armed before the blocking wait.");
        Assert.True(flushWait > timedWait, "The parameterless wait must flush the async readers.");

        // Both the bootstrap helper and the Essentia probe must go through it, so
        // neither can regress into a post-exit pipe read.
        Assert.Equal(2, CountOccurrences(service, "RunProcessCapturingOutput(startInfo"));
        Assert.DoesNotContain("var stdout = process.StandardOutput.ReadToEnd();", service, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [Fact]
    public void VibeModelManifests_MatchTheChecksumVerifiedShellManifest()
    {
        var repoRoot = ResolveRepoRoot();
        var fetchScript = File.ReadAllText(Path.Join(repoRoot, "scripts", "fetch-vibe-models.sh"));
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        var shellFiles = fetchScript
            .Split('\n')
            .Where(line => line.StartsWith("download \"", StringComparison.Ordinal))
            .Select(line => line.Split('"')[1])
            .ToHashSet(StringComparer.Ordinal);

        var appFiles = Regex.Matches(
                service,
                "^\\s*\\(\"([^\"]+\\.(?:pb|json))\", \"https://[^\"]+\", \"[0-9a-f]{64}\"\\),?$",
                RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(shellFiles);
        Assert.NotEmpty(appFiles);

        // Both provisioning paths must fetch exactly the same models. Otherwise the
        // in-app bootstrap can never produce the Discogs519 branch and the analyzer
        // silently downgrades to Discogs400.
        Assert.Equal(shellFiles, appFiles);

        Assert.Contains("discogs-maest-30s-pw-519l-2.pb", appFiles);
        Assert.Contains("genre_discogs519-discogs-maest-30s-pw-519l-1.pb", appFiles);
        Assert.Contains("genre_discogs519-discogs-maest-30s-pw-519l-1.json", appFiles);
    }

    [Fact]
    public void VibeAnalyzerFallbackReason_IsSanitizedWithoutFailingStandardResult()
    {
        var sanitized = TrackAnalysisBackgroundService.SanitizeAnalyzerFailure("failure\r\nsecret\t" + new string('x', 500));

        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\t', sanitized);
        Assert.True(sanitized.Length <= 259);

        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        Assert.Contains("LogAnalyzerFallback", service, StringComparison.Ordinal);
        Assert.Contains("CreateCompletedAnalysisResult", service, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerParitySmoke_RequiresTwoEnhancedPersistentWorkerResponses()
    {
        var repoRoot = ResolveRepoRoot();
        var smoke = File.ReadAllText(Path.Join(repoRoot, "scripts", "docker-parity-smoke.sh"));

        Assert.Contains("vibe_analyzer.py --worker --models", smoke, StringComparison.Ordinal);
        Assert.Contains("requestId", smoke, StringComparison.Ordinal);
        Assert.Contains("AnalysisMode", smoke, StringComparison.Ordinal);
        Assert.Contains("enhanced", smoke, StringComparison.Ordinal);
        Assert.Contains("len(responses) != 2", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void PerTrackAnalyzerFailures_DoNotDisableEnhancedCapabilityGlobally()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        Assert.Contains("IsMlCapabilityFailure(analyzerFailure.ErrorCode)", source, StringComparison.Ordinal);
        Assert.Contains("\"ESSENTIA_MISSING_REQUIRED\" or", source, StringComparison.Ordinal);
        Assert.Contains("\"VIBE_ANALYZER_NOT_INITIALIZED\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetMlCapabilityUnavailable(analyzerFailure.Reason);\r\n                LogMlUnavailable(analyzerFailure.Reason);\r\n                failureReason = analyzerFailure.Reason", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticAndManualAnalysisPasses_AreSerialized()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        Assert.Contains("await _analysisLock.WaitAsync(stoppingToken);", source, StringComparison.Ordinal);
        Assert.Contains("await _analysisLock.WaitAsync(cancellationToken);", source, StringComparison.Ordinal);
        Assert.Contains("DefaultVibeAnalyzerTimeoutSeconds = 180", source, StringComparison.Ordinal);
        Assert.Contains("process.WaitForExit(5000);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysisDisable_PausesEveryEntryPointAndStopsTheWorker()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        Assert.Contains("public async Task<bool> TrySignalBackgroundAnalysisAsync(int batchSize)", source, StringComparison.Ordinal);
        Assert.Contains("public async Task<bool> TryStartManualAnalysisAsync(int batchSize)", source, StringComparison.Ordinal);
        Assert.Contains("await IsAnalysisEnabledAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await StopAnalyzerWorkerAsync()", source, StringComparison.Ordinal);
        Assert.Contains("ClearPendingRunSignal();", source, StringComparison.Ordinal);
        Assert.Contains("WakeAnalysisLoop();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("forceWhenDisabled", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysisRuntime_IsCancellableAndDoesNotReadStaleProcessingRowsForCurrentTrack()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var controller = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryAnalysisStatusApiController.cs"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Contains("PauseActiveRun();", service, StringComparison.Ordinal);
        Assert.Contains("WaitForExitAsync(linked.Token)", service, StringComparison.Ordinal);
        Assert.Contains("ResetInterruptedProcessingRowsAsync", service, StringComparison.Ordinal);
        Assert.Contains("GetRuntimeSnapshot()", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProcessingTrackAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProcessingTrackAsync", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysisPass_ResolvesAllLibrariesInOneQueryPerPass()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        var passStart = service.IndexOf("private async Task AnalyzeStablePassesAsync(", StringComparison.Ordinal);
        var passEnd = service.IndexOf("internal static IReadOnlyList<long> ResolveAnalysisFolderOrder(", StringComparison.Ordinal);
        Assert.True(passStart > 0 && passEnd > passStart, "AnalyzeStablePassesAsync must exist.");
        var pass = service[passStart..passEnd];

        // Querying one folder at a time rescanned every table once per folder on
        // every pass, for no ordering benefit. The pass must scope to the whole
        // ordered folder list and stop as soon as a pass finds nothing new.
        Assert.DoesNotContain("orderedLibraryIds: [folderId]", pass, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var folderId in orderedFolderIds)", pass, StringComparison.Ordinal);
        Assert.Contains("orderedLibraryIds: orderedFolderIds", pass, StringComparison.Ordinal);
        Assert.Contains("if (snapshot.Count == 0)\n            {\n                return;\n            }", pass, StringComparison.Ordinal);

        // Each pass must strictly grow the exclusion set, otherwise the new loop
        // could spin forever on a snapshot it never consumes.
        Assert.Contains("excludedTrackIds: attemptedTrackIds", pass, StringComparison.Ordinal);
        Assert.Contains("attemptedTrackIds.Add(track.TrackId)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysisFolderOrder_ReordersButNeverExcludesALibrary()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var store = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "VibeAnalysisSettingsStore.cs"));
        var api = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "VibeAnalysisSettingsApiController.cs"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        // Regression guard, still the point of this test: a stored libraryOrder used to
        // be the entire scope, so a stale list containing a single library silently
        // restricted analysis to it and new files elsewhere were never analysed. A
        // custom order now exists again, so the concept can no longer be removed as the
        // only defence -- it must instead reorder and never exclude.
        Assert.Contains("VibeAnalysisSettingsDto? settings = null", service, StringComparison.Ordinal);

        // Anything the stored order omits is appended, never dropped. This is the line
        // that keeps the original bug dead.
        Assert.Contains("ordered.AddRange(remaining", service, StringComparison.Ordinal);

        // An id in the stored list that no longer resolves is ignored rather than
        // trusted as a scope, so a removed library cannot be passed downstream.
        Assert.Contains("if (remaining.Remove(folderId))", service, StringComparison.Ordinal);

        // Alphabetical is still the default, and still the fallback for an empty order.
        Assert.Contains("OrderBy(static folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase)", service, StringComparison.Ordinal);

        // The settings round-trip the order instead of dropping it, and an omitted
        // field means "unchanged" so an older client cannot erase a stored order.
        Assert.Contains("UseLibraryOrder", store, StringComparison.Ordinal);
        Assert.Contains("LibraryOrder", store, StringComparison.Ordinal);
        Assert.Contains("request.UseLibraryOrder ?? existing.UseLibraryOrder", api, StringComparison.Ordinal);
        Assert.Contains("request.LibraryOrder ?? existing.LibraryOrder", api, StringComparison.Ordinal);

        // The UI offers the order again, and says out loud that it is a priority order
        // and not a filter, so it cannot be read as "unlisted libraries are skipped".
        Assert.Contains("analysis-use-library-order", view, StringComparison.Ordinal);
        Assert.Contains("analysis-folder-order-edit-toggle", view, StringComparison.Ordinal);
        Assert.Contains("analysis-reset-folder-order", view, StringComparison.Ordinal);
        Assert.Contains("analysis-folder-enabled", view, StringComparison.Ordinal);
        // The old claim that libraries themselves are analysed alphabetically is gone;
        // the album-level claim replaces it, and is what the analyser now does.
        Assert.DoesNotContain("audio libraries analysed alphabetically", view, StringComparison.Ordinal);
        Assert.Contains("Albums inside a library are always alphabetical", view, StringComparison.Ordinal);
        Assert.DoesNotContain("selected from", view, StringComparison.Ordinal);
        Assert.Contains("never skipped", view, StringComparison.Ordinal);

        // The folder list shown in the UI must match what the analyser will run.
        Assert.Contains("(folder?.libraryId ?? folder?.LibraryId) != null", view, StringComparison.Ordinal);
        Assert.Contains("folder.LibraryId.HasValue", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysisStatus_ScopesExplicitLibraryAndPreservesUnfilteredApi()
    {
        var repoRoot = ResolveRepoRoot();
        var status = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryAnalysisStatusApiController.cs"));
        Assert.Contains("[FromQuery] long? libraryId = null", status, StringComparison.Ordinal);
        Assert.Contains("libraryId is <= 0", status, StringComparison.Ordinal);
        Assert.Contains("libraryId.HasValue ? new[] { libraryId.Value } : null", status, StringComparison.Ordinal);
        Assert.Contains("GetAnalysisStatusAsync(libraryIds, cancellationToken)", status, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysisStatus_ViewKeepsCountsWithDisplayedLibrary()
    {
        var view = File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
        var stateStart = view.IndexOf("const analysisState =", StringComparison.Ordinal);
        var stateEnd = view.IndexOf("const analysisSettingsState", stateStart, StringComparison.Ordinal);
        string Function(string name)
        {
            var match = Regex.Match(view, @"^(?:async )?function " + name + @"\(", RegexOptions.Multiline);
            Assert.True(match.Success, "Missing function: " + name);
            var next = Regex.Match(view[(match.Index + match.Length)..], @"^(?:async )?function ", RegexOptions.Multiline);
            Assert.True(next.Success);
            return view.Substring(match.Index, match.Length + next.Index);
        }
        var script = """
const assert = require('node:assert/strict');
const elements = new Map();
const document = { getElementById(id) {
    if (!elements.has(id)) elements.set(id, { textContent: '', classList: { toggle() {} } });
    return elements.get(id);
}};
const window = {};
let lastAnalysisLogKey = null;
const requests = [];
function fetchJson(url) { return new Promise((resolve, reject) => requests.push({url, resolve, reject})); }
function applyAnalysisDegradedPill() {}
function renderAnalysisRecent() {}
function updateAnalysisStatusPill() {}
function formatTimestamp(value) { return value || 'Never'; }
function formatNumber(value) { return value ?? '--'; }
function formatList(value) { return value?.join(', ') || 'None'; }
const tick = () => new Promise(resolve => setImmediate(resolve));
const item = id => ({ track: { title: 'Track', artistName: 'Artist' }, analysis: { libraryId: id } });
const counts = (total, analyzed, pending, lastRunUtc) => ({totalTracks: total, analyzedTracks: analyzed, pendingTracks: pending, lastRunUtc});
const text = id => document.getElementById(id).textContent;
""" + view[stateStart..stateEnd]
            + Function("applyAnalysisRuntime") + Function("applyLatestAnalysis")
            + Function("loadLatestAnalysis") + Function("setAnalysisStatusLibrary")
            + Function("clearAnalysisStatus") + Function("loadAnalysisStatus") + """
(async () => {
    applyAnalysisRuntime({current: item(1), latest: item(2)});
    assert.equal(analysisState.statusLibraryId, 1);
    const old = requests.shift();
    assert.equal(old.url, '/api/library/analysis/status?libraryId=1');
    applyAnalysisRuntime({current: item(2), latest: item(1)});
    assert.equal(text('analysisStatus'), '--');
    const current = requests.shift();
    assert.equal(current.url, '/api/library/analysis/status?libraryId=2');
    current.resolve(counts(7, 3, 4, 'library-two'));
    await tick();
    old.resolve(counts(99, 98, 1, 'library-one'));
    await tick();
    assert.equal(text('analysisStatus'), '3/7 analyzed');
    assert.equal(text('analysisPending'), '4');
    assert.equal(text('analysisLastRun'), 'library-two');

    const latestLoad = loadLatestAnalysis();
    requests.shift().resolve(item(1));
    await latestLoad;
    assert.equal(analysisState.statusLibraryId, 2);
    assert.equal(requests.length, 0);

    const first = loadAnalysisStatus();
    const firstRequest = requests.shift();
    const second = loadAnalysisStatus();
    requests.shift().resolve(counts(8, 5, 3, null));
    await second;
    firstRequest.reject(new Error('stale request failure'));
    await first;
    assert.equal(text('analysisStatus'), '5/8 analyzed');
    assert.equal(text('analysisLastRun'), 'Never');

    applyAnalysisRuntime({latest: item(1)});
    assert.equal(analysisState.statusLibraryId, 1);
    const beforeClear = requests.shift();
    applyAnalysisRuntime({current: item(null), latest: item(1)});
    await loadAnalysisStatus();
    assert.equal(requests.length, 0);
    beforeClear.resolve(counts(9, 9, 0, 'stale'));
    await tick();
    for (const id of ['analysisStatus', 'analysisPending', 'analysisLastRun']) assert.equal(text(id), '--');
    // A saved latest result can outlive the in-memory runtime after a restart.
    applyLatestAnalysis(item(3), false);
    applyAnalysisRuntime({current: null, latest: null});
    assert.equal(analysisState.statusLibraryId, 3);
    const persisted = requests.shift();
    assert.equal(persisted.url, '/api/library/analysis/status?libraryId=3');
    persisted.resolve(counts(10, 6, 4, 'saved-latest'));
    await tick();
    applyAnalysisRuntime({current: null, latest: null});
    assert.equal(text('analysisStatus'), '6/10 analyzed');
    assert.equal(requests.length, 0);

    const missingLatest = loadLatestAnalysis();
    requests.shift().reject(new Error('No saved analysis'));
    await missingLatest;
    applyAnalysisRuntime({});
    await loadAnalysisStatus();
    assert.equal(requests.length, 0);
    for (const id of ['analysisStatus', 'analysisPending', 'analysisLastRun']) assert.equal(text(id), '--');
    console.log('Library scope and stale response checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
""";
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        process!.StandardInput.Write(script);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stdout + stderr);
    }

    [Fact]
    public void VibeAnalysisSettings_SaveFailureIsReportedInsteadOfSilentlySwallowed()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var store = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "VibeAnalysisSettingsStore.cs"));
        var api = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "VibeAnalysisSettingsApiController.cs"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.DoesNotContain("Using in-memory settings for this runtime.", store, StringComparison.Ordinal);
        Assert.Contains("throw new IOException(", store, StringComparison.Ordinal);
        Assert.Contains("public sealed record VibeAnalysisSettingsDto(\n    bool Enabled,\n    int BatchSize,\n    int IntervalMinutes)", store, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackAnalysisCandidateSelection_PrefersEssentiaDecodableCopiesOverAtmosEac3()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Contains("lower(coalesce(af.codec, '')) LIKE '%eac3%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.codec, '')) LIKE '%dolby digital plus%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.audio_variant, '')) LIKE '%atmos%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.codec, '')) LIKE '%opus%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.extension, '')) = '.opus'", source, StringComparison.Ordinal);
        Assert.Contains("af.quality_rank DESC NULLS LAST", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticAndManualRunsShareStableLibraryPassesAndAttemptTracking()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Equal(2, service.Split("await AnalyzeStablePassesAsync(").Length - 1);
        Assert.Contains("var attemptedTrackIds = new HashSet<long>();", service, StringComparison.Ordinal);
        Assert.Contains("excludedTrackIds: attemptedTrackIds", service, StringComparison.Ordinal);
        Assert.Contains("BuildStablePassRanges(snapshot", service, StringComparison.Ordinal);
        Assert.Contains("attemptedTrackIds.Add(track.TrackId)", service, StringComparison.Ordinal);
        Assert.Contains("ArtistOrderKey.ResolveMainArtistKey", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY library_sort_order, id\n    LIMIT @limit", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualRunRequest_ReportsOutcomeInsteadOfCollapsingRejectionsToFalse()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var controller = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryAnalysisManagementApiController.cs"));

        // The public contract must stay boolean for existing callers and guardrails.
        Assert.Contains("public async Task<bool> TryStartManualAnalysisAsync(int batchSize)", service, StringComparison.Ordinal);

        // A declined manual run is not the same as a disabled feature: enabling
        // background analysis starts a pass immediately, so a run requested right
        // after the toggle is declined because a pass is already active.
        Assert.Contains("internal async Task<VibeAnalysisRunRequest> RequestManualAnalysisAsync(int batchSize)", service, StringComparison.Ordinal);
        Assert.Contains("VibeAnalysisRunOutcome.AlreadyRunning", service, StringComparison.Ordinal);
        Assert.Contains("VibeAnalysisRunOutcome.Disabled", service, StringComparison.Ordinal);
        Assert.Contains("VibeAnalysisRunOutcome.Queued", service, StringComparison.Ordinal);

        Assert.Contains("RequestManualAnalysisAsync(batchSize)", controller, StringComparison.Ordinal);
        Assert.Contains("alreadyRunning = request.Outcome == VibeAnalysisRunOutcome.AlreadyRunning", controller, StringComparison.Ordinal);
        Assert.Contains("enabled = request.Outcome != VibeAnalysisRunOutcome.Disabled", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAnalysis_OnlyWarnsAboutDisabledWhenAnalysisIsActuallyDisabled()
    {
        var repoRoot = ResolveRepoRoot();
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        // The "Enable background analysis" warning must be reserved for the
        // genuinely-disabled outcome; it must not be shown when a pass is running.
        var alreadyRunningIndex = view.IndexOf("result?.alreadyRunning === true", StringComparison.Ordinal);
        var warningIndex = view.IndexOf(
            "notifyActivity('Vibe analysis is paused. Enable background analysis to resume.', 'warning');",
            StringComparison.Ordinal);
        var alreadyRunningNotice = view.IndexOf(
            "notifyActivity('Vibe analysis is already running.');",
            StringComparison.Ordinal);

        Assert.True(alreadyRunningIndex > 0, "The run handler must inspect the alreadyRunning outcome.");
        Assert.True(warningIndex > 0, "The disabled warning must still exist.");
        Assert.True(alreadyRunningNotice > 0, "An already-running notice must exist.");
        Assert.True(
            alreadyRunningIndex < warningIndex,
            "The already-running branch must be evaluated before the disabled warning.");
    }

    [Fact]
    public void VibeAnalyzer_UsesFfmpegDecodeFallbackForEssentiaUnsupportedCodecs()
    {
        var repoRoot = ResolveRepoRoot();
        var analyzer = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Tools", "vibe_analyzer.py"));
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        Assert.Contains("DEEZSPOTAG_FFMPEG_PATH", analyzer, StringComparison.Ordinal);
        Assert.Contains("tempfile.NamedTemporaryFile", analyzer, StringComparison.Ordinal);
        Assert.Contains("subprocess.run", analyzer, StringComparison.Ordinal);
        Assert.Contains("\"-map\"", analyzer, StringComparison.Ordinal);
        Assert.Contains("\"0:a:0\"", analyzer, StringComparison.Ordinal);
        Assert.DoesNotContain("self.load_audio(file_path", analyzer, StringComparison.Ordinal);
        Assert.Contains("startInfo.Environment[\"DEEZSPOTAG_FFMPEG_PATH\"] = FfmpegExecutablePath;", service, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysis_UsesAudioLibrariesAndOneFfmpegDecodePath()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        Assert.Contains("isVibeAudioFolder(folder)", view, StringComparison.Ordinal);
        Assert.Contains("desiredQuality.includes('video') || desiredQuality.includes('podcast')", view, StringComparison.Ordinal);
        Assert.Contains("desired_quality_value, '')) NOT LIKE '%video%'", repository, StringComparison.Ordinal);
        Assert.Contains("desired_quality_value, '')) NOT LIKE '%podcast%'", repository, StringComparison.Ordinal);
        Assert.Contains("coalesce(af.size, 0) > 0", repository, StringComparison.Ordinal);
        Assert.Contains("coalesce(af.sample_rate_hz, 0) > 0", repository, StringComparison.Ordinal);

        Assert.Contains("TryReadWithFfmpeg(track.FilePath", service, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"0:a:0\")", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IsFfmpegHandledExtension", service, StringComparison.Ordinal);
        Assert.DoesNotContain("TryReadMp3Samples", service, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAudioStream", service, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeSampleDecoder_DecodesValidOpusAndRejectsEmptyOpus()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("deezspotag-vibe-opus-");
        try
        {
            var opusPath = Path.Join(tempDirectory.FullName, "tone.opus");
            GenerateOpusFixture(opusPath);

            var decodeMethod = typeof(TrackAnalysisBackgroundService).GetMethod(
                "TryLoadTrackSamples",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(decodeMethod);

            var decodeArguments = new object?[]
            {
                new TrackAnalysisInputDto(1, 1, opusPath, 1000),
                null,
                0,
                null
            };
            Assert.True(Assert.IsType<bool>(decodeMethod!.Invoke(null, decodeArguments)));
            Assert.NotEmpty(Assert.IsType<float[]>(decodeArguments[1]));
            Assert.Equal(44100, Assert.IsType<int>(decodeArguments[2]));
            Assert.Null(decodeArguments[3]);

            var emptyOpusPath = Path.Join(tempDirectory.FullName, "empty.opus");
            File.WriteAllBytes(emptyOpusPath, Array.Empty<byte>());
            var emptyArguments = new object?[]
            {
                new TrackAnalysisInputDto(2, 1, emptyOpusPath, null),
                null,
                0,
                null
            };
            Assert.False(Assert.IsType<bool>(decodeMethod.Invoke(null, emptyArguments)));
            var failure = Assert.IsType<TrackAnalysisResultDto>(emptyArguments[3]);
            Assert.Contains("empty", failure.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DockerParity_ProvesApplicationLevelEnhancedVibeAnalysis()
    {
        var repoRoot = ResolveRepoRoot();
        var parity = File.ReadAllText(Path.Join(repoRoot, "scripts", "docker-parity-smoke.sh"));

        Assert.Contains("Application-level enhanced Vibe analysis", parity, StringComparison.Ordinal);
        Assert.Contains("library/deezspotag.db", parity, StringComparison.Ordinal);
        Assert.Contains("track_analysis", parity, StringComparison.Ordinal);
        Assert.Contains("analysis_mode", parity, StringComparison.Ordinal);
        Assert.Contains("analysis_version", parity, StringComparison.Ordinal);
        Assert.Contains("essentia_genres", parity, StringComparison.Ordinal);
        Assert.Contains("mood_tags", parity, StringComparison.Ordinal);
        Assert.Contains("worker start count", parity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api/library/analysis/runtime", parity, StringComparison.Ordinal);
        Assert.Contains("Essentia analysis failed; standard analysis was used", parity, StringComparison.Ordinal);
    }

    private static void GenerateOpusFixture(string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                     "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                     "-c:a", "libopus", outputPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stderr = process!.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        Assert.True(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
