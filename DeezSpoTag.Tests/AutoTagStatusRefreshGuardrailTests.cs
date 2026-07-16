using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagStatusRefreshGuardrailTests
{
    [Fact]
    public void GetJob_DisablesResponseCaching()
    {
        var method = typeof(AutoTagJobsController).GetMethod(nameof(AutoTagJobsController.GetJob));
        Assert.NotNull(method);

        var responseCache = method!.GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: true)
            .OfType<ResponseCacheAttribute>()
            .SingleOrDefault();

        Assert.NotNull(responseCache);
        Assert.True(responseCache!.NoStore);
        Assert.Equal(ResponseCacheLocation.None, responseCache.Location);
    }

    [Fact]
    public void GetLatestJob_DisablesResponseCaching()
    {
        var method = typeof(AutoTagJobsController).GetMethod(nameof(AutoTagJobsController.GetLatestJob));
        Assert.NotNull(method);

        var responseCache = method!.GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: true)
            .OfType<ResponseCacheAttribute>()
            .SingleOrDefault();

        Assert.NotNull(responseCache);
        Assert.True(responseCache!.NoStore);
        Assert.Equal(ResponseCacheLocation.None, responseCache.Location);
    }

    [Fact]
    public void AutoTagStatusScript_UsesNoStoreFetchAndResumeRefreshHooks()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("cache: \"no-store\"", source, StringComparison.Ordinal);
        Assert.Contains("bindPageResumeRefresh", source, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", source, StringComparison.Ordinal);
        Assert.Contains("pageshow", source, StringComparison.Ordinal);
        Assert.Contains("\"focus\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_UsesOneHistoryEndpointForActiveAndCompletedRuns()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        var loadMethod = ExtractFunction(source, "async function loadRunDetails");
        Assert.Contains("fetchRunHistorySnapshot(runId, requestId)", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/autotag/jobs/", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback", loadMethod, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tryLoadLiveRunDetailsForSelection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tryLoadArchiveThenLiveFallback", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_DoesNotClearSelectedHistoryDuringListRefresh()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        var renderMethod = ExtractFunction(source, "function renderRunList");
        var loadMethod = ExtractFunction(source, "async function loadRunDetails");
        Assert.DoesNotContain("resetRunSelection", renderMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("state.historyStatus = []", renderMethod, StringComparison.Ordinal);
        Assert.Contains("retaining the last successful snapshot", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("resetRunSelection", loadMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_OnlyClearsHistoryForAnExplicitEmptyDateSelection()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        var loadRunsMethod = ExtractFunction(source, "async function loadRunsForDate");
        Assert.Contains("if (options.manual === true) {\n                resetRunSelection", loadRunsMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("resetRunSelection", ExtractFunction(source, "async function loadCalendar"), StringComparison.Ordinal);
        Assert.DoesNotContain("resetRunSelection", ExtractFunction(source, "async function refreshAutoTagRunHistory"), StringComparison.Ordinal);
        Assert.DoesNotContain("resetRunSelection", ExtractFunction(source, "async function applyPolledJob"), StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesPage_VersionsLazyLoadedAutoTagStatusScript()
    {
        var repoRoot = ResolveRepoRoot();
        var viewPath = Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");
        Assert.True(File.Exists(viewPath), $"Missing Activities view: {viewPath}");

        var source = File.ReadAllText(viewPath);
        Assert.Contains("autoTagStatus: '@AssetUrl.Versioned(ViewContext, Url, \"~/js/autotag-status.js\")'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("autoTagStatus: '@Url.Content(\"~/js/autotag-status.js\")'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_PreservesManualHistoricalSelectionWhileLiveRunUpdates()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        var resetMethod = ExtractFunction(source, "function shouldResetManualHistorySelectionForLiveRun");
        var realtimeMethod = ExtractFunction(source, "async function refreshRealtimeRunDetails");

        Assert.Contains("return selectedRunId === liveRunId;", resetMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedRunId !== liveRunId && isTodayDateToken(state.selectedDate)", resetMethod, StringComparison.Ordinal);
        Assert.Contains("const shouldFollowRun = !state.manualHistorySelection", realtimeMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_TreatsPausedRunsAsTerminalWarningState()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("const STATUS_PAUSED = \"paused\";", source, StringComparison.Ordinal);
        Assert.Contains("normalized === STATUS_PAUSED", source, StringComparison.Ordinal);
        Assert.Contains("case STATUS_PAUSED:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_GuardsAgainstOutOfOrderHistoryResponses()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("calendarRequestId", source, StringComparison.Ordinal);
        Assert.Contains("runsRequestId", source, StringComparison.Ordinal);
        Assert.Contains("runDetailsRequestId", source, StringComparison.Ordinal);
        Assert.Contains("if (requestId !== state.calendarRequestId)", source, StringComparison.Ordinal);
        Assert.Contains("if (requestId !== state.runsRequestId)", source, StringComparison.Ordinal);
        Assert.Contains("isStaleRunDetailsRequest(requestId)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_UsesRealtimeRunChangeEvents()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("const signalRHubUrl = \"/activitiesHub\";", source, StringComparison.Ordinal);
        Assert.Contains(".withUrl(signalRHubUrl)", source, StringComparison.Ordinal);
        Assert.Contains("autotagRunChanged", source, StringComparison.Ordinal);
        Assert.Contains("scheduleAutoTagRunRefresh", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistHistoryScript_UsesRealtimeAndIncrementalRefresh()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "watchlist-history.js");
        Assert.True(File.Exists(scriptPath), $"Missing watchlist history script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("const SIGNALR_HUB_URL = \"/activitiesHub\";", source, StringComparison.Ordinal);
        Assert.Contains("watchlistHistoryChanged", source, StringComparison.Ordinal);
        Assert.Contains("sinceId", source, StringComparison.Ordinal);
        Assert.Contains("loadChangedHistory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagService_MaintainsCompactRunIndexForHistoryLists()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains("run-index.json", source, StringComparison.Ordinal);
        Assert.Contains("LoadRunIndexSummaries", source, StringComparison.Ordinal);
        Assert.Contains("UpdateRunIndex", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagService_ReusesRootJobIdForAllResumeIntents()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.DoesNotContain("isEnhancementResume", source, StringComparison.Ordinal);
        Assert.Contains("var resumedJobId = resumeSeed?.ResumeJobId ?? Guid.NewGuid().ToString(\"N\");", source, StringComparison.Ordinal);
        Assert.Contains("ResumeFromJobId = null", source, StringComparison.Ordinal);
        Assert.Contains("ResolveResumeRootJob", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagService_SeedsEnrichmentResumeCheckpointBeforeRunnerStarts()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains("EnsureInitialEnrichmentResumeCheckpoint(job, stage);", source, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(stage.Name, AutoTagLiterals.EnrichmentStage", source, StringComparison.Ordinal);
        Assert.Contains("PlatformIndex = 0", source, StringComparison.Ordinal);
        Assert.Contains("FileIndex = 0", source, StringComparison.Ordinal);

        var earlySeedIndex = source.IndexOf("EnsureInitialEnrichmentResumeCheckpoint(job, stages);", StringComparison.Ordinal);
        var executeStagesIndex = source.IndexOf("ExecuteStagesAsync(job, stages", StringComparison.Ordinal);
        var seedIndex = source.IndexOf("EnsureInitialEnrichmentResumeCheckpoint(job, stage);", StringComparison.Ordinal);
        var runnerIndex = source.IndexOf("_autoTagRunner.RunAsync(", StringComparison.Ordinal);
        Assert.True(earlySeedIndex >= 0 && executeStagesIndex >= 0 && earlySeedIndex < executeStagesIndex);
        Assert.True(seedIndex >= 0 && runnerIndex >= 0 && seedIndex < runnerIndex);
    }

    [Fact]
    public void AutoTagService_CollapsesResumeChainsInRunIndex()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains(".GroupBy(GetRunIndexGroupKey, StringComparer.OrdinalIgnoreCase)", source, StringComparison.Ordinal);
        Assert.Contains("ResolveResumeRootJobId(summary.Id, summary.ResumeFromJobId)", source, StringComparison.Ordinal);
        Assert.Contains("TryReadJobResumeFromJobId(summary.Id)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagHistory_UsesLocalRunDateForCalendarAndRealtimeEvents()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var realtimePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "ActivitiesRealtimeService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");
        Assert.True(File.Exists(realtimePath), $"Missing realtime service source: {realtimePath}");

        var serviceSource = File.ReadAllText(servicePath);
        var realtimeSource = File.ReadAllText(realtimePath);
        Assert.Contains("TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local)", serviceSource, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(summary => GetRunDateToken(GetRunHistoryTimestamp(summary)))", serviceSource, StringComparison.Ordinal);
        Assert.Contains("string.Equals(GetRunDateToken(GetRunHistoryTimestamp(summary)), token, StringComparison.Ordinal)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("date = AutoTagService.GetRunDateToken(AutoTagService.GetRunHistoryTimestamp(summary))", realtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("summary.StartedAt.ToString(\"yyyy-MM-dd\")", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("summary.StartedAt.ToString(\"yyyy-MM-dd\")", realtimeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagService_MovesEnhancementAndManualEnrichmentRunsAcrossHistoryDays()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains("public DateTimeOffset? HistoryDate { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("HistoryDate = ResolveRunHistoryDate(job)", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsEnhancementRunIntent(job.RunIntent)", source, StringComparison.Ordinal);
        Assert.Contains("&& !IsManualEnrichmentRunIntent(job.RunIntent))", source, StringComparison.Ordinal);
        Assert.Contains("return null;", source, StringComparison.Ordinal);
        Assert.Contains("public DateTimeOffset LastActivityAt { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("job.LastActivityAt = DateTimeOffset.UtcNow;", source, StringComparison.Ordinal);
        Assert.Contains("return ResolveLastActivityTimestamp(job);", source, StringComparison.Ordinal);
        Assert.Contains("private static DateTimeOffset ResolveLastActivityTimestamp(AutoTagJob job)", source, StringComparison.Ordinal);
        Assert.Contains("job.ResumeCheckpoint?.UpdatedAt", source, StringComparison.Ordinal);
        Assert.Contains("job.StatusHistory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_RefreshesHistoryWhenLocalDateChanges()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("currentTodayToken: toDateToken(new Date())", source, StringComparison.Ordinal);
        Assert.Contains("function bindDateRolloverRefresh()", source, StringComparison.Ordinal);
        Assert.Contains("state.selectedDate = todayToken;", source, StringComparison.Ordinal);
        Assert.Contains("bindDateRolloverRefresh();", source, StringComparison.Ordinal);
        Assert.Contains("return state.selectedDate === getLiveRunDateToken(state.liveJobSummary);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_RefreshesSelectedRunDetailsAfterRealtimeUpdate()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("const runId = normalizeRunId(payload?.runId);", source, StringComparison.Ordinal);
        Assert.Contains("await refreshRealtimeRunDetails(runId, runDate);", source, StringComparison.Ordinal);
        Assert.Contains("function refreshRealtimeRunDetails(runId, runDate)", source, StringComparison.Ordinal);
        Assert.Contains("await loadRunDetails(runId);", source, StringComparison.Ordinal);

        var refreshStart = source.IndexOf("function refreshRealtimeRunDetails(runId, runDate)", StringComparison.Ordinal);
        Assert.True(refreshStart >= 0);
        var refreshEnd = source.IndexOf("\n    function connectRealtimeHistory()", refreshStart, StringComparison.Ordinal);
        Assert.True(refreshEnd > refreshStart);
        var refreshBody = source.Substring(refreshStart, refreshEnd - refreshStart);
        Assert.DoesNotContain("runIntent", refreshBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enhancement", refreshBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enrichment", refreshBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoTagStatusScript_RefreshesSelectedHistoryOnceRunBecomesTerminal()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        var pollMethod = ExtractFunction(source, "async function applyPolledJob");
        Assert.Contains("isTerminalRunStatus(job.status)", pollMethod, StringComparison.Ordinal);
        Assert.Contains("normalizeRunId(state.selectedRunId) === normalizeRunId(job.id)", pollMethod, StringComparison.Ordinal);
        Assert.Contains("await loadRunDetails(job.id)", pollMethod, StringComparison.Ordinal);
        Assert.Contains("state.terminalHistorySyncedRunId", pollMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("syncSelectedRunWithLiveJob", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_CoalescesRealtimeCalendarAndRunListRefresh()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        var refreshMethod = ExtractFunction(source, "async function refreshAutoTagRunHistory");
        Assert.Contains("await loadCalendar({ preserveSelection: true });", refreshMethod, StringComparison.Ordinal);
        Assert.Contains("await refreshRealtimeRunDetails(runId, runDate);", refreshMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("loadRunsForDate", refreshMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagService_ReadsEachRunHistoryAsOneLockedSnapshot()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        var methodStart = source.IndexOf("public AutoTagRunArchive? GetArchivedRun(string id)", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    public AutoTagTagDiff? GetTagDiff", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source.Substring(methodStart, methodEnd - methodStart);
        Assert.Contains("_archiveLocks.GetOrAdd(id", method, StringComparison.Ordinal);
        Assert.Contains("lock (archiveLock)", method, StringComparison.Ordinal);
        Assert.True(method.IndexOf("lock (archiveLock)", StringComparison.Ordinal) < method.IndexOf("LoadRunSummary(id)", StringComparison.Ordinal));
        Assert.True(method.IndexOf("lock (archiveLock)", StringComparison.Ordinal) < method.IndexOf("ReadRunStatusHistory(id)", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoTagService_ReadsBestAvailableArchivedStatusAndLogsAcrossHistoryRoots()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains("EnumerateRunFileCandidates(jobId, \"autotag.log\")", source, StringComparison.Ordinal);
        Assert.Contains("EnumerateRunFileCandidates(jobId, \"status-history.ndjson\")", source, StringComparison.Ordinal);
        Assert.Contains("if (candidateEntries.Count > entries.Count)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagService_PrunesArchivedRunsUsingConfiguredRetention()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains("ResolveArchivedRunRetentionPeriod()", source, StringComparison.Ordinal);
        Assert.Contains("PruneExpiredArchivedRuns(force: true)", source, StringComparison.Ordinal);
        Assert.Contains("GetRunHistoryTimestamp(summary).ToUniversalTime() < cutoffUtc", source, StringComparison.Ordinal);
        Assert.Contains("DeleteArchivedRunFiles(summary.Id)", source, StringComparison.Ordinal);
        Assert.Contains("TryDeleteFile(Path.Join(_jobsDir, normalizedJobId + \".json\"))", source, StringComparison.Ordinal);
        Assert.Contains("PruneOrphanedArchivedRunArtifacts(cutoffUtc)", source, StringComparison.Ordinal);
        Assert.Contains("PruneOrphanedJobSnapshots(retainedIds, cutoffUtc)", source, StringComparison.Ordinal);
        Assert.Contains("public AutoTagRunArchive? GetArchivedRun(string id)", source, StringComparison.Ordinal);
        Assert.Contains("if (IsExpiredArchivedRun(summary, DateTimeOffset.UtcNow.Subtract(ResolveArchivedRunRetentionPeriod())))", source, StringComparison.Ordinal);
        Assert.Contains("public void WarmRunIndexIfMissing()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAutoTagRunner_BoundsProviderMatchingAndOptionalPostProcessing()
    {
        var repoRoot = ResolveRepoRoot();
        var runnerPath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        Assert.True(File.Exists(runnerPath), $"Missing AutoTag runner source: {runnerPath}");

        var source = File.ReadAllText(runnerPath);
        Assert.Contains("private static readonly TimeSpan PlatformMatchTimeout = TimeSpan.FromSeconds(45)", source, StringComparison.Ordinal);
        Assert.Contains("RunPlatformMatchWithTimeoutAsync", source, StringComparison.Ordinal);
        Assert.Contains("matchTask.WaitAsync(PlatformMatchTimeout, context.Token)", source, StringComparison.Ordinal);
        Assert.Contains("match timed out after", source, StringComparison.Ordinal);
        Assert.Contains("stepTask.WaitAsync(timeout, context.Token)", source, StringComparison.Ordinal);
        Assert.Contains("ObserveBackgroundTask(stepTask)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStuckRecovery_IsEnabledByDefaultAndPollsFrequently()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagStuckRecoveryHostedService.cs");
        var settingsPath = Path.Join(repoRoot, "DeezSpoTag.Web", "appsettings.json");
        Assert.True(File.Exists(servicePath), $"Missing stuck recovery service source: {servicePath}");
        Assert.True(File.Exists(settingsPath), $"Missing appsettings source: {settingsPath}");

        var serviceSource = File.ReadAllText(servicePath);
        var settingsSource = File.ReadAllText(settingsPath);
        Assert.Contains("PollInterval = TimeSpan.FromMinutes(1)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("DefaultStaleWindow = TimeSpan.FromMinutes(10)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("_configuration.GetValue(\"AutoTag:StuckRecovery:Enabled\", true)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Enabled\": true", settingsSource, StringComparison.Ordinal);
        Assert.Contains("\"TimeoutMinutes\": 10", settingsSource, StringComparison.Ordinal);
        Assert.Contains("\"AutoResume\": true", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStart_ReturnsRunningJobBeforeRuntimeConfigHydration()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        var startMethod = ExtractMethod(source, "public async Task<AutoTagJob?> StartJob");
        Assert.Contains("_jobs[job.Id] = job;", startMethod, StringComparison.Ordinal);
        Assert.Contains("_activeJobIds.TryAdd(job.Id, 0);", startMethod, StringComparison.Ordinal);
        Assert.Contains("InitializeRunArchive(job);", startMethod, StringComparison.Ordinal);
        Assert.Contains("_ = PrepareRuntimeConfigAndRunJobAsync(job, normalizedPath, configJson, options);", startMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("await InjectPlatformAuthAsync", startMethod, StringComparison.Ordinal);
        Assert.Contains("runtime config preparing", source, StringComparison.Ordinal);
        Assert.Contains("runtime config ready", source, StringComparison.Ordinal);
        Assert.Contains("Runtime config preparation failed", source, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string functionName)
    {
        var index = source.IndexOf(functionName, StringComparison.Ordinal);
        if (index < 0)
        {
            return string.Empty;
        }

        var nextFunction = source.IndexOf("\n    function ", index + functionName.Length, StringComparison.Ordinal);
        var nextAsyncFunction = source.IndexOf("\n    async function ", index + functionName.Length, StringComparison.Ordinal);
        var candidates = new[] { nextFunction, nextAsyncFunction }
            .Where(position => position >= 0)
            .ToArray();
        var end = candidates.Length == 0 ? source.Length : candidates.Min();
        return source[index..end];
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var index = source.IndexOf(methodName, StringComparison.Ordinal);
        if (index < 0)
        {
            return string.Empty;
        }

        var nextMethod = source.IndexOf("\n    private ", index + methodName.Length, StringComparison.Ordinal);
        var nextPublicMethod = source.IndexOf("\n    public ", index + methodName.Length, StringComparison.Ordinal);
        var candidates = new[] { nextMethod, nextPublicMethod }
            .Where(position => position >= 0)
            .ToArray();
        var end = candidates.Length == 0 ? source.Length : candidates.Min();
        return source[index..end];
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
