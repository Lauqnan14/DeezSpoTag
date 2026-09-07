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
    public void AutoTagStopReason_LabelsUserEnhancementStopsAsResumablePause()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        // Invariant: enhancement runs are never cancelled outright — a user stop is a
        // resumable pause so POST /jobs/{id}/resume can continue it.
        Assert.Contains("_ => \"Paused by user. Resume is available.\"", source, StringComparison.Ordinal);
        var stopStatus = ExtractSourceSpan(
            source,
            "private static string ResolveStopStatus",
            "private static string NormalizeStopReason");
        Assert.Contains("AutoTagLiterals.PausedStatus", stopStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("? AutoTagLiterals.CanceledStatus", stopStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStopReason_DoesNotRewriteArchivedEnhancementStops()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        // Guardrail: the legacy rewriter must stay removed. It reclassified
        // restart-interrupted manual enhancement runs as "Stopped by user." and destroyed
        // their resume eligibility.
        Assert.DoesNotContain("NormalizeLegacyUserStoppedEnhancement", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsLegacyUserInterruptedStopMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordStaleRecoveryPending", source, StringComparison.Ordinal);
        Assert.Contains("Archived run summaries are immutable history", source, StringComparison.Ordinal);
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
    public void AutoTagResume_ExplicitEndpointSeedsFromNamedJobAndMarksSourceResumed()
    {
        var repoRoot = FindRepoRoot();
        var controller = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("jobs/{id}/resume", controller, StringComparison.Ordinal);
        Assert.Contains("ResumeJobAsync(id, cancellationToken)", controller, StringComparison.Ordinal);
        Assert.Contains("public async Task<ResumeJobOutcome?> ResumeJobAsync", service, StringComparison.Ordinal);
        Assert.Contains("ResumeFromJobId: job.Id", service, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.ResumedStatus", service, StringComparison.Ordinal);
        // The resume endpoint must not depend on the passive scope lookup: it names the source job.
        Assert.Contains("if (!string.IsNullOrWhiteSpace(options.ResumeFromJobId))", service, StringComparison.Ordinal);
        // Checkpoint must be persisted with the job so resume survives runtime-config cleanup.
        Assert.Contains("public string? ResumeConfigJson { get; set; }", service, StringComparison.Ordinal);
        Assert.Contains("job.ResumeConfigJson = persistedConfigJson;", service, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagCheckpoint_FallsBackWhenNextIndexesMissing()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var update = ExtractSourceSpan(
            source,
            "private static bool TryUpdateResumeCheckpoint",
            "private static AutoTagResumeCursor? ResolveResumeCursor");

        // Invariant: every successfully processed file advances the checkpoint — no silent skips.
        Assert.Contains("Fallback: some terminal statuses", update, StringComparison.Ordinal);
        Assert.Contains("nextFileIndex = currentFile + 1;", update, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementResumeQueueing_SymmetryForBothEnhancementIntents()
    {
        var repoRoot = FindRepoRoot();
        var orchestration = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"));

        // Pauseable intents must be resumable: both queue paths handle recent-downloads runs.
        var queueFolders = ExtractSourceSpan(
            orchestration,
            "private void QueueResumeFoldersForPausedEnhancementJob",
            "private static bool PathScopesOverlap");
        Assert.DoesNotContain("AutoTagLiterals.RunIntentEnhancementRecentDownloads", queueFolders, StringComparison.Ordinal);

        var queueInterrupted = ExtractSourceSpan(
            orchestration,
            "private void QueueInterruptedEnhancementResume",
            "private static bool IsAutomationPausedEnhancementJob");
        Assert.Contains("AutoTagLiterals.RunIntentEnhancementRecentDownloads", queueInterrupted, StringComparison.Ordinal);

        // The pause handler must still cover both intents.
        var shouldPause = ExtractSourceSpan(
            orchestration,
            "private static bool ShouldPauseEnhancementJobForEnrichment",
            "private sealed record EnhancementExecutionResult");
        Assert.Contains("AutoTagLiterals.RunIntentEnhancementRecentDownloads", shouldPause, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementResumeState_HasOnePersistentRepresentation()
    {
        var repoRoot = FindRepoRoot();
        var orchestration = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"));
        var save = ExtractSourceSpan(
            orchestration,
            "private void SaveOrchestrationRuntimeState",
            "private void RestorePendingEnhancementResumeWork");

        // Root paths live in memory only; folder IDs are the single persisted representation.
        Assert.DoesNotContain("PendingEnhancementResumeRootPaths = _pendingEnhancementResumeRootPaths", save, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementClient_SurfacesResumeBannerAndStopsLoopOnInterrupt()
    {
        var repoRoot = FindRepoRoot();
        var script = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var resumeModule = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "enhancement-resume.js"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var sections = ExtractFunction(script, "async function runEnhancementSections");
        var poll = ExtractFunction(script, "async function pollJob");

        Assert.Contains("~/js/enhancement-resume.js", view, StringComparison.Ordinal);
        Assert.Contains("encodeURIComponent(id) + \"/resume\"", resumeModule, StringComparison.Ordinal);
        Assert.Contains("window.EnhancementResume?.offerResume", poll, StringComparison.Ordinal);
        Assert.Contains("window.EnhancementResume?.offerResume", sections, StringComparison.Ordinal);
        // Interrupted scope stops the section loop instead of silently continuing.
        Assert.Contains("break;", sections, StringComparison.Ordinal);
        Assert.Contains("group(s) not run.", sections, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementResumeActions_RenderInlineBesideTheRunJobId()
    {
        var repoRoot = FindRepoRoot();
        var resumeModule = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "enhancement-resume.js"));
        var statusScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js"));
        var activitiesView = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        // The "Runs on" list renders Resume/Cancel inline for paused/interrupted runs.
        Assert.Contains("data-run-action=\"resume\"", statusScript, StringComparison.Ordinal);
        Assert.Contains("data-run-action=\"cancel\"", statusScript, StringComparison.Ordinal);
        Assert.Contains("\"paused\", \"interrupted\"", statusScript, StringComparison.Ordinal);
        Assert.Contains("window.EnhancementResume?.resumeJob(jobId)", statusScript, StringComparison.Ordinal);
        Assert.Contains("window.EnhancementResume?.cancelJob(jobId)", statusScript, StringComparison.Ordinal);

        // The buttons sit ON the job-id line, inside the run entry itself: the entry
        // is a role=button div (a native <button> cannot contain buttons), the flex
        // job-id line renders the id span first and then interpolates the action
        // buttons — one button pair per run, never a detached row between entries.
        Assert.Contains("<div role=\"button\" tabindex=\"0\" class=\"autotag-run-item\"", statusScript, StringComparison.Ordinal);
        var runListStart = statusScript.IndexOf("list.innerHTML = runsForDisplay.map", StringComparison.Ordinal);
        Assert.True(runListStart >= 0, "Missing run list template.");
        var runListEnd = statusScript.IndexOf("highlightSelectedRun();", runListStart, StringComparison.Ordinal);
        var template = statusScript[runListStart..runListEnd];

        var jobIdIndex = template.IndexOf("job ${run.id}", StringComparison.Ordinal);
        var actionsOnJobIdLine = template.IndexOf("</span>${actionButtons}", StringComparison.Ordinal);
        Assert.True(jobIdIndex >= 0 && actionsOnJobIdLine > jobIdIndex,
            "The action buttons must interpolate on the job-id line, directly after the job id.");

        var resumeIndex = template.IndexOf("data-run-action=\"resume\"", StringComparison.Ordinal);
        var cancelIndex = template.IndexOf("data-run-action=\"cancel\"", StringComparison.Ordinal);
        Assert.True(resumeIndex >= 0 && cancelIndex > resumeIndex, "Resume must precede Cancel in the action pair.");

        // The resume module is the single action path and mounts no banner anywhere.
        Assert.Contains("resumeJob: resumeJob", resumeModule, StringComparison.Ordinal);
        Assert.Contains("cancelJob: cancelJob", resumeModule, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement-resume-banner", resumeModule, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement-resume-slot", resumeModule, StringComparison.Ordinal);
        Assert.DoesNotContain("document.body.insertBefore", resumeModule, StringComparison.Ordinal);
        Assert.DoesNotContain("document.getElementById(\"content\")", resumeModule, StringComparison.Ordinal);
        Assert.DoesNotContain("mainContent", resumeModule, StringComparison.Ordinal);

        // The Activities view is untouched: no reserved slot, script reference intact.
        Assert.DoesNotContain("enhancement-resume-slot", activitiesView, StringComparison.Ordinal);
        Assert.Contains("~/js/enhancement-resume.js", activitiesView, StringComparison.Ordinal);
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
