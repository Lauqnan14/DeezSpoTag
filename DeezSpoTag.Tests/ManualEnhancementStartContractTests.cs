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
    public void EnhancementRunClient_FailsWhenTheStartResponseIsNotOk()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var startFunction = ExtractFunction(source, "async function startCentralEnhancementFeature");

        Assert.Contains("if (!response.ok)", startFunction, StringComparison.Ordinal);
        Assert.Contains("payload?.error", startFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementRunClient_PollsUntilTheJobStopsAndReleasesTheButton()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var pollFunction = ExtractFunction(source, "async function pollCentralEnhancementJob");
        var runFunction = ExtractFunction(source, "async function runEnhancementSections");

        Assert.Contains("/api/autotag/jobs/", pollFunction, StringComparison.Ordinal);
        Assert.Contains("\"queued\", \"running\", \"tagging\"", pollFunction, StringComparison.Ordinal);
        Assert.Contains("button.disabled = true;", runFunction, StringComparison.Ordinal);
        Assert.Contains("finally", runFunction, StringComparison.Ordinal);
        Assert.Contains("button.disabled = false;", runFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementRunClient_DoesNotWaitOnASingleWorkflowWhenSeveralSectionsRun()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var startFunction = ExtractFunction(source, "async function startCentralEnhancementFeature");
        var pollFunction = ExtractFunction(source, "async function pollCentralEnhancementJob");

        Assert.Contains("features.length === 1 ? features[0] : null", startFunction, StringComparison.Ordinal);
        Assert.Contains("if (!expectedName && ![\"queued\", \"running\", \"tagging\"].includes(status))", pollFunction, StringComparison.Ordinal);
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
    public void AutoTagStopReason_LabelsUserEnhancementStopsAsStopped()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("? AutoTagLiterals.CanceledStatus", source, StringComparison.Ordinal);
        Assert.Contains("_ => \"Stopped by user.\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ => \"Interrupted by user. Resume is available.\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStopReason_NormalizesLegacyManualEnhancementStops()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("NormalizeLegacyUserStoppedEnhancement(job);", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeLegacyUserStoppedEnhancement(summary);", source, StringComparison.Ordinal);
        Assert.Contains("IsLegacyUserInterruptedStopMessage", source, StringComparison.Ordinal);
        Assert.Contains("run.Status = AutoTagLiterals.CanceledStatus;", source, StringComparison.Ordinal);
        Assert.Contains("run.Error = \"Stopped by user.\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStart_DoesNotResumeCanceledEnhancementRuns()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var resumeCandidate = ExtractSourceSpan(
            source,
            "private bool IsResumeCandidate",
            "private static AutoTagResumeCheckpoint? CloneResumeCheckpoint");
        var preserveRuntimeConfig = ExtractSourceSpan(
            source,
            "private static bool ShouldPreserveRuntimeConfigFilesForResume",
            "private static HashSet<string> InitializeRuntimeConfigPaths");

        Assert.DoesNotContain("AutoTagLiterals.CanceledStatus", resumeCandidate, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoTagLiterals.CanceledStatus", preserveRuntimeConfig, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.InterruptedStatus", resumeCandidate, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.PausedStatus", resumeCandidate, StringComparison.Ordinal);
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

    private static string ExtractSourceSpan(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"{startMarker} was not found.");
        }

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        return end > start ? source[start..end] : source[start..];
    }
}
