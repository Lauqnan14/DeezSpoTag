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
    public void AutomationPendingPipeline_CanPauseManualOrScheduledEnhancement()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"));

        Assert.Contains("IsAutomationInterruptibleEnhancementTrigger", source, StringComparison.Ordinal);
        Assert.Contains("return IsInterruptibleEnhancementTrigger(job.Trigger);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("? IsAutomationInterruptibleEnhancementTrigger(job.Trigger)", source, StringComparison.Ordinal);
        Assert.Contains("ShouldPauseEnhancementJobForEnrichment", source, StringComparison.Ordinal);
        Assert.Contains("StopJobAsync(jobId, \"automation\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStopReason_LabelsAutomationEnhancementStopsAsPaused()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("StopJobAsync(string id, string? stopReason = null)", source, StringComparison.Ordinal);
        Assert.Contains("Paused by automation. Resume is available after download finalization.", source, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.PausedStatus", source, StringComparison.Ordinal);
        Assert.Contains("autotag paused by {actor}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStart_BlocksIncompatibleConcurrentJobs()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("TryCreateBlockedJobForActiveJobPolicy", source, StringComparison.Ordinal);
        Assert.Contains("another AutoTag job is already running", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("TryCreateBlockedJobForActiveJobPolicy", StringComparison.Ordinal)
            < source.IndexOf("HasEligibleInputFiles(normalizedPath, configJson)", StringComparison.Ordinal));
        Assert.Contains("_activeJobIds.TryAdd(job.Id, 0)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualEnrichmentClient_UsesCentralEndpointWithDestinationAndReleaseChoice()
    {
        var repoRoot = FindRepoRoot();
        var script = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var startFunction = ExtractFunction(script, "async function startAutoTag");

        Assert.Contains("/api/autotag/enhancement/start", startFunction, StringComparison.Ordinal);
        Assert.Contains("features: [\"manual-enrichment\"]", startFunction, StringComparison.Ordinal);
        Assert.Contains("folderIds: [destination.id]", startFunction, StringComparison.Ordinal);
        Assert.Contains("releasePreference", startFunction, StringComparison.Ordinal);
        Assert.Contains("forceFingerprint", startFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/autotag/start", startFunction, StringComparison.Ordinal);
        Assert.Contains("id=\"autotag-move-success-library\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"manualReleasePreference\" value=\"album\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"manualReleasePreference\" value=\"single\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"manualRecognitionMethod\" value=\"id-first\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"manualRecognitionMethod\" value=\"fingerprint\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("autotag-move-failed", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualEnrichmentBackend_ClaimsUnownedStagingFilesAndUsesOneProfilePath()
    {
        var repoRoot = FindRepoRoot();
        var controller = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("Manual enrichment requires exactly one enabled music library destination.", controller, StringComparison.Ordinal);
        Assert.Contains("GetPipelineOwnedPayloadPathsAsync", controller, StringComparison.Ordinal);
        Assert.Contains("configNode[AutoTagLiterals.LibraryWideEnhancementBatchSizeKey] = 40", controller, StringComparison.Ordinal);
        Assert.Contains("RunIntent: AutoTagLiterals.RunIntentManualEnrichment", controller, StringComparison.Ordinal);
        Assert.Contains("ProfileId: selectedProfile.Id", controller, StringComparison.Ordinal);
        Assert.Contains("organizerOptions.BatchScopedFilesOnly = true", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RunManualEnrichmentArtworkMaintenanceAsync", service, StringComparison.Ordinal);
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
