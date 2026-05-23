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
    public void AutoTagStatusScript_FallsBackToLiveJobWhenArchivedRunHasNoStatusHistory()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("const archivedStatusHistory = Array.isArray(archive?.statusHistory) ? archive.statusHistory : [];", source, StringComparison.Ordinal);
        Assert.Contains("if (archivedStatusHistory.length === 0)", source, StringComparison.Ordinal);
        Assert.Contains("tryLoadLiveRunDetailsForSelection", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusScript_OnlyTreatsActiveRunsAsLiveHistorySource()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("return isHistoryTabActive() && !state.manualHistorySelection && hasActiveLiveRun();", source, StringComparison.Ordinal);
        Assert.Contains("function canUseLiveRunSelection(runId)", source, StringComparison.Ordinal);
        Assert.Contains("if (!canUseLiveRunSelection(runId))", source, StringComparison.Ordinal);
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
    public void AutoTagService_MovesOnlyEnhancementRunsAcrossHistoryDays()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains("public DateTimeOffset? HistoryDate { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("HistoryDate = ResolveRunHistoryDate(job)", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsEnhancementRunIntent(job.RunIntent))", source, StringComparison.Ordinal);
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
    public void AutoTagStatusScript_DoesNotOverwriteArchivedHistoryFromCompletedLatestPoll()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("if (hasActiveLiveRun() && hasDetails) {", source, StringComparison.Ordinal);
        Assert.Contains("syncSelectedRunWithLiveJob(job, logs);", source, StringComparison.Ordinal);
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
    public void AutoTagService_PrunesArchivedRunsAfterSixtyOneDays()
    {
        var repoRoot = ResolveRepoRoot();
        var servicePath = Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.True(File.Exists(servicePath), $"Missing AutoTag service source: {servicePath}");

        var source = File.ReadAllText(servicePath);
        Assert.Contains("ArchivedRunRetentionPeriod = TimeSpan.FromDays(61)", source, StringComparison.Ordinal);
        Assert.Contains("PruneExpiredArchivedRuns(force: true)", source, StringComparison.Ordinal);
        Assert.Contains("GetRunHistoryTimestamp(summary).ToUniversalTime() < cutoffUtc", source, StringComparison.Ordinal);
        Assert.Contains("DeleteArchivedRunFiles(summary.Id)", source, StringComparison.Ordinal);
        Assert.Contains("TryDeleteFile(Path.Join(_jobsDir, normalizedJobId + \".json\"))", source, StringComparison.Ordinal);
        Assert.Contains("public AutoTagRunArchive? GetArchivedRun(string id)", source, StringComparison.Ordinal);
        Assert.Contains("if (IsExpiredArchivedRun(summary, DateTimeOffset.UtcNow.Subtract(ArchivedRunRetentionPeriod)))", source, StringComparison.Ordinal);
        Assert.Contains("public void WarmRunIndexIfMissing()", source, StringComparison.Ordinal);
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
