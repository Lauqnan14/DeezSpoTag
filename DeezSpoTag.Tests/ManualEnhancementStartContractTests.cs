using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ManualEnhancementStartContractTests
{
    private static readonly MethodInfo NormalizeRunTriggerMethod =
        typeof(AutoTagService).GetMethod(
            "NormalizeRunTrigger",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutoTagService.NormalizeRunTrigger not found.");

    [Fact]
    public void NormalizeRunTrigger_DoesNotTreatExplicitInvalidTriggerAsManual()
    {
        var normalized = Assert.IsType<string>(NormalizeRunTriggerMethod.Invoke(null, new object?[] { "unexpected-trigger" }));

        Assert.Equal("invalid", normalized);
    }

    [Fact]
    public void NormalizeRunTrigger_DefaultsMissingTriggerToManual()
    {
        var normalized = Assert.IsType<string>(NormalizeRunTriggerMethod.Invoke(null, new object?[] { null }));

        Assert.Equal("manual", normalized);
    }

    [Fact]
    public void LibraryManualEnhancementClient_RequiresRunningStatusFromStartResponse()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library.js"));
        var startFunction = ExtractFunction(source, "async function startFolderEnhancement");

        Assert.Contains("status !== 'running'", startFunction, StringComparison.Ordinal);
        Assert.Contains("response?.error", startFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("gapFillTags", startFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryManualEnhancementClient_KeepsButtonInRunningStateUntilJobStops()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library.js"));
        var monitorFunction = ExtractFunction(source, "async function monitorFolderEnhancementJob");
        var bindFunction = ExtractFunction(source, "function bindFolderEnhanceAction");

        Assert.Contains("setFolderEnhancementButtonState(button, 'running')", monitorFunction, StringComparison.Ordinal);
        Assert.Contains("/api/autotag/jobs/", monitorFunction, StringComparison.Ordinal);
        Assert.Contains("status === 'running'", monitorFunction, StringComparison.Ordinal);
        Assert.Contains("setFolderEnhancementButtonState(button, 'idle')", monitorFunction, StringComparison.Ordinal);
        Assert.Contains("await monitorFolderEnhancementJob(enhanceButton, folder, started)", bindFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationPendingPipeline_CanInterruptManualOrScheduledEnhancement()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"));

        Assert.Contains("IsAutomationInterruptibleEnhancementTrigger", source, StringComparison.Ordinal);
        Assert.Contains("return IsInterruptibleEnhancementTrigger(job.Trigger);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("? IsAutomationInterruptibleEnhancementTrigger(job.Trigger)", source, StringComparison.Ordinal);
        Assert.Contains("StopJobAsync(jobId, \"automation\")", source, StringComparison.Ordinal);
        Assert.Contains("StopJobAsync(runningEnhancementJobId, \"automation\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStopReason_DoesNotLabelAutomationInterruptionsAsUserInterruptions()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("StopJobAsync(string id, string? stopReason = null)", source, StringComparison.Ordinal);
        Assert.Contains("Interrupted by automation. Resume is available.", source, StringComparison.Ordinal);
        Assert.Contains("autotag interrupted by {actor}", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "DeezSpoTag.Web")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ExtractFunction(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"{marker} was not found.");
        }

        var nextFunction = source.IndexOf("\nasync function ", start + marker.Length, StringComparison.Ordinal);
        return nextFunction > start
            ? source[start..nextFunction]
            : source[start..];
    }
}
