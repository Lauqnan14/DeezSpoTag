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
    public void AutoTagStatusScript_DoesNotOverwriteArchivedHistoryFromCompletedLatestPoll()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("if (hasActiveLiveRun()) {", source, StringComparison.Ordinal);
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
