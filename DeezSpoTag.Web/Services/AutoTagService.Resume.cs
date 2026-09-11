using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services.CoverPort;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{

    /// <summary>
    /// Explicitly resumes a paused/interrupted/failed AutoTag job from its checkpoint.
    /// Bypasses the automation resume cooldown: the user is present and acting.
    /// </summary>
    public async Task<ResumeJobOutcome?> ResumeJobAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        AutoTagJob? job;
        if (_jobs.TryGetValue(id, out var cached) && cached != null)
        {
            job = cached;
        }
        else
        {
            job = LoadJob(id);
            if (job == null)
            {
                return null; // 404
            }
            NormalizeLoadedJobState(job);
        }

        var status = job.Status?.Trim();
        var resumeEligible = string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase);
        if (!resumeEligible)
        {
            return new ResumeJobOutcome(false, $"Job is '{status}' and cannot be resumed. Only paused, interrupted, or failed jobs can resume.", null);
        }

        if (job.ResumeCheckpoint == null)
        {
            return new ResumeJobOutcome(false, "Job has no resume checkpoint; start a new run over the same scope instead.", null);
        }

        if (string.IsNullOrWhiteSpace(job.RootPath))
        {
            return new ResumeJobOutcome(false, "Job root path is missing; resume is not possible.", null);
        }

        var configJson = job.ResumeConfigJson;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            var runtimeConfigPath = TryFindRuntimeConfigPath(job.Id, "base");
            if (!string.IsNullOrWhiteSpace(runtimeConfigPath) && File.Exists(runtimeConfigPath))
            {
                try
                {
                    configJson = await File.ReadAllTextAsync(runtimeConfigPath, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed reading runtime config for resume of job {JobId}.", id);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new ResumeJobOutcome(false, "No stored config is available for this run; start a new run over the same scope instead.", null);
        }

        if (HasRunningJobs())
        {
            return new ResumeJobOutcome(false, "Another AutoTag job is running; resume is unavailable until it settles.", null);
        }

        var resumed = await StartJob(
            job.RootPath!,
            configJson,
            new StartJobOptions(
                Trigger: AutoTagLiterals.RecoveryTrigger,
                ProfileId: job.ProfileId,
                ProfileName: job.ProfileName,
                RunIntent: job.RunIntent,
                EnhancementFeature: job.EnhancementFeature,
                ResumeFromJobId: job.Id));
        if (resumed == null)
        {
            return new ResumeJobOutcome(false, "Resume was blocked (downloads are active or another run holds the scope).", null);
        }

        // StartJob returns job objects as admission failures (blocked/skipped). Treat
        // those as resume failures: never stamp the source job as resumed and never
        // report a running successor that will not actually run.
        if (IsBlockedResumeSuccessor(resumed))
        {
            _logger.LogWarning(
                "Resume of job {JobId} produced a '{Status}' successor job {SuccessorJobId}: {Error}",
                id,
                resumed.Status,
                resumed.Id,
                resumed.Error);
            return new ResumeJobOutcome(
                false,
                resumed.Error ?? $"Resume successor job was '{resumed.Status}' and will not run.",
                null);
        }

        // Same-id resume (enhancement reuses the root job id): StartJob already
        // replaced the in-memory job with the running continuation. Stamping this
        // stale reference as "resumed" would overwrite that continuation on disk
        // and make the run look like it started over.
        if (!string.Equals(job.Id, resumed.Id, StringComparison.OrdinalIgnoreCase))
        {
            job.Status = AutoTagLiterals.ResumedStatus;
            job.Error = $"Resumed by successor job {resumed.Id}.";
            AppendLog(job, $"resume: successor job {resumed.Id} started from checkpoint {job.ResumeCheckpoint.StageName}.");
            AppendActivityLog(job.Id, $"autotag resumed by successor job {resumed.Id}");
            SaveJob(job);
        }

        return new ResumeJobOutcome(true, null, resumed.Id);
    }

    /// <summary>
    /// StartJob returns a job object for every admission failure. A resume successor
    /// that was admitted as blocked/skipped will never run, so the resume must be
    /// reported as failed instead of stamping the source job as resumed.
    /// </summary>
    internal static bool IsBlockedResumeSuccessor(AutoTagJob? successor)
    {
        if (successor == null)
        {
            return false;
        }

        var status = successor.Status?.Trim();
        return string.Equals(status, AutoTagLiterals.BlockedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.SkippedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyRunStopped(AutoTagJob job, string stopStatus, string stopReason)
    {
        if (!string.Equals(stopStatus, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stopStatus, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _notifications.Raise(
            "run_paused",
            $"{(IsEnhancementRunIntent(job.RunIntent) ? "Enhancement" : "AutoTag")} run {stopStatus}",
            job.Error ?? BuildStopError(job, stopReason),
            "Warning",
            $"run_paused:{job.Id}",
            "job",
            job.Id);
    }

    private static string ResolveStopStatus(AutoTagJob job, string stopReason)
    {
        if (!IsEnhancementRunIntent(job.RunIntent)
            && !IsManualEnrichmentRunIntent(job.RunIntent))
        {
            return AutoTagLiterals.CanceledStatus;
        }

        // Enhancement runs are never cancelled outright: every stop is a pause that the
        // explicit resume endpoint (POST /jobs/{id}/resume) can continue from.
        return string.Equals(stopReason, AutoTagLiterals.AutomationTrigger, StringComparison.OrdinalIgnoreCase)
            ? AutoTagLiterals.PausedStatus
            : string.Equals(stopReason, "user", StringComparison.OrdinalIgnoreCase)
                ? AutoTagLiterals.PausedStatus
                : AutoTagLiterals.InterruptedStatus;
    }

    private static string NormalizeStopReason(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason))
        {
            return "user";
        }

        return stopReason.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.AutomationTrigger => AutoTagLiterals.AutomationTrigger,
            AutoTagLiterals.ScheduleTrigger => AutoTagLiterals.ScheduleTrigger,
            AutoTagLiterals.RecoveryTrigger => AutoTagLiterals.RecoveryTrigger,
            _ => "user"
        };
    }

    private static string BuildStopError(AutoTagJob job, string stopReason)
    {
        if (!IsEnhancementRunIntent(job.RunIntent)
            && !IsManualEnrichmentRunIntent(job.RunIntent))
        {
            return stopReason switch
            {
                AutoTagLiterals.AutomationTrigger => "Stopped by automation.",
                AutoTagLiterals.ScheduleTrigger => "Stopped after schedule change.",
                AutoTagLiterals.RecoveryTrigger => "Stopped by stale recovery.",
                _ => "Stopped by user."
            };
        }

        return stopReason switch
        {
            AutoTagLiterals.AutomationTrigger => "Paused by automation. Resume is available after download finalization.",
            AutoTagLiterals.ScheduleTrigger => "Interrupted after schedule change. Resume is available.",
            AutoTagLiterals.RecoveryTrigger => "Interrupted by stale recovery. Resume is available.",
            _ => "Paused by user. Resume is available."
        };
    }

    private static string BuildStopActivityLog(string stopStatus, string stopReason)
    {
        var actor = stopReason switch
        {
            AutoTagLiterals.AutomationTrigger => "automation",
            AutoTagLiterals.ScheduleTrigger => "schedule change",
            AutoTagLiterals.RecoveryTrigger => "stale recovery",
            _ => "user"
        };

        if (string.Equals(stopStatus, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return $"autotag paused by {actor}";
        }

        return string.Equals(stopStatus, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            ? $"autotag interrupted by {actor}"
            : $"autotag canceled by {actor}";
    }

    private async Task RunJobAsync(
        AutoTagJob job,
        string path,
        string configPath)
    {
        var fileOutcomes = new Dictionary<string, FileTagOutcome>(StringComparer.OrdinalIgnoreCase);
        var runtimeConfigPaths = InitializeRuntimeConfigPaths(configPath);
        using var jobCancellation = new CancellationTokenSource();
        _jobCancellationSources[job.Id] = jobCancellation;

        try
        {
            await RunJobCoreAsync(job, path, configPath, fileOutcomes, runtimeConfigPaths, jobCancellation.Token);
            NotifyCompleted(job);
        }
        catch (OperationCanceledException)
        {
            HandleRunJobCanceled(job);
            NotifyCompleted(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleRunJobFailureAsync(job, ex, path, configPath, fileOutcomes);
        }
        finally
        {
            if (!ShouldPreserveRuntimeConfigFilesForResume(job))
            {
                CleanupRuntimeConfigFiles(runtimeConfigPaths);
            }
            _activeJobStages.TryRemove(job.Id, out _);
            _activeJobIds.TryRemove(job.Id, out _);
            _jobCancellationSources.TryRemove(job.Id, out _);
            if (!IsActiveJobStatus(job.Status))
            {
                SaveArchivedTagDiffs(job.Id, job.TagDiffs);
                Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(job));
            }
            _jobs.TryRemove(job.Id, out _);
            _lastActivityLines.TryRemove(job.Id, out _);
            _archiveLocks.TryRemove(job.Id, out _);
            _lastRunIndexUpdateUtc.TryRemove(job.Id, out _);
        }
    }

    private static bool IsActiveJobStatus(string? status)
        => string.Equals(status, AutoTagLiterals.QueuedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.TaggingStatus, StringComparison.OrdinalIgnoreCase);

    private static bool IsPausedOrInterruptedRunStatus(string? status)
        => string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase);

    private static AutoTagJob CreateCompactTerminalJob(AutoTagJob source)
    {
        var compact = new AutoTagJob
        {
            Id = source.Id,
            Status = source.Status,
            StartedAt = source.StartedAt,
            FinishedAt = source.FinishedAt,
            ExitCode = source.ExitCode,
            Error = source.Error,
            Progress = source.Progress,
            OkCount = source.OkCount,
            ErrorCount = source.ErrorCount,
            ReviewCount = source.ReviewCount,
            SkippedCount = source.SkippedCount,
            RootPath = source.RootPath,
            Trigger = source.Trigger,
            RunIntent = source.RunIntent,
            ProfileId = source.ProfileId,
            ProfileName = source.ProfileName,
            EnhancementFeature = source.EnhancementFeature,
            EnhancementGroupId = source.EnhancementGroupId,
            CurrentPhase = source.CurrentPhase,
            CurrentBatch = source.CurrentBatch,
            BatchCount = source.BatchCount,
            BatchProcessed = source.BatchProcessed,
            BatchSize = source.BatchSize,
            ProcessedItems = source.ProcessedItems,
            TotalItems = source.TotalItems,
            TargetReason = source.TargetReason,
            TargetRequested = source.TargetRequested,
            TargetUsable = source.TargetUsable,
            EnhancementManifestPath = source.EnhancementManifestPath,
            AutoMoveSummary = source.AutoMoveSummary,
            CurrentPlatform = source.CurrentPlatform,
            LastStatus = source.LastStatus,
            ResumeCheckpoint = source.ResumeCheckpoint,
            ResumeFromJobId = source.ResumeFromJobId,
            LastActivityAt = source.LastActivityAt
        };
        compact.Logs.AddRange(source.Logs);
        compact.StatusHistory.AddRange(source.StatusHistory);
        return compact;
    }

    private static AutoTagJob CreateJobPersistenceSnapshot(AutoTagJob source)
    {
        var snapshot = CreateCompactTerminalJob(source);
        snapshot.EnhancementWorkflows.AddRange(source.EnhancementWorkflows);
        snapshot.EnhancedFilePaths.AddRange(source.EnhancedFilePaths);
        snapshot.StartedPlatforms.AddRange(source.StartedPlatforms);
        return snapshot;
    }

    private bool HasOtherActiveJobs(string jobId)
    {
        return _activeJobIds.Keys.Any(activeJobId => !string.Equals(activeJobId, jobId, StringComparison.Ordinal));
    }

    private async Task RunJobCoreAsync(
        AutoTagJob job,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        HashSet<string> runtimeConfigPaths,
        CancellationToken cancellationToken)
    {
        await PrepareEnhancementRunAsync(job, configPath, cancellationToken);
        var stages = await BuildStageConfigsAsync(job, configPath);
        var includesEnrichmentStage = stages.Any(stage =>
            string.Equals(stage.Name, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase));
        var includesEnhancementStage = stages.Any(stage =>
            string.Equals(stage.Name, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase));
        var includesEnhancementWorkflows = ShouldRunIntegratedEnhancementWorkflows(job, configPath);
        RegisterStageRuntimeConfigPaths(runtimeConfigPaths, stages);
        if (TryMarkNoStagesConfigured(job, stages, includesEnhancementWorkflows))
        {
            return;
        }
        EnsureInitialEnrichmentResumeCheckpoint(job, stages);

        var execution = await ExecuteStagesAsync(job, stages, path, configPath, fileOutcomes, cancellationToken);
        if (!execution.Success || IsTerminalStopStatus(job.Status))
        {
            FinalizeStageExecution(job, execution.Success);
            return;
        }

        await RunSuccessPostProcessingAsync(
            job,
            path,
            new SuccessPostProcessingContext
            {
                ConfigPath = configPath,
                IncludesEnrichmentStage = includesEnrichmentStage,
                IncludesEnhancementStage = includesEnhancementStage,
                IncludesEnhancementWorkflows = includesEnhancementWorkflows,
                FileOutcomes = fileOutcomes
            },
            cancellationToken);
        FinalizeStageExecution(job, success: true);
    }

    private void FinalizeStageExecution(AutoTagJob job, bool success)
    {
        if (!IsTerminalStopStatus(job.Status))
        {
            job.Status = success ? AutoTagLiterals.CompletedStatus : AutoTagLiterals.FailedStatus;
        }
        if (string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
        {
            job.Progress = 1d;
            job.CurrentPhase = "completed";
            job.ResumeCheckpoint = null;
            job.ResumeFromJobId = null;
        }
        job.ExitCode = success ? 0 : 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        AppendPlatformSummary(job);
        SaveJob(job);
        AppendActivityLog(job.Id, $"autotag finished: status={job.Status}");
    }

    private void HandleRunJobCanceled(AutoTagJob job)
    {
        if (IsTerminalStopStatus(job.Status))
        {
            return;
        }

        job.Status = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? AutoTagLiterals.InterruptedStatus
            : AutoTagLiterals.CanceledStatus;
        job.Error = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? "Interrupted. Resume is available."
            : "Stopped.";
        job.ExitCode = 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        SaveJob(job);
    }

    private static bool IsTerminalStopStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPreserveRuntimeConfigFilesForResume(AutoTagJob job)
    {
        if (job.ResumeCheckpoint == null)
        {
            return false;
        }

        return string.Equals(job.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> InitializeRuntimeConfigPaths(string configPath)
    {
        var runtimeConfigPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            runtimeConfigPaths.Add(configPath);
        }

        return runtimeConfigPaths;
    }

    private static void RegisterStageRuntimeConfigPaths(HashSet<string> runtimeConfigPaths, IReadOnlyList<AutoTagStageConfig> stages)
    {
        foreach (var stageConfigPath in stages
                     .Select(static stage => stage.ConfigPath)
                     .Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            runtimeConfigPaths.Add(stageConfigPath!);
        }
    }

    private bool TryMarkNoStagesConfigured(
        AutoTagJob job,
        IReadOnlyCollection<AutoTagStageConfig> stages,
        bool includesEnhancementWorkflows)
    {
        if (stages.Count > 0)
        {
            return false;
        }

        if (includesEnhancementWorkflows)
        {
            AppendLog(job, "gap-fill tagging skipped: no runnable gap-fill tagging stage was configured");
            return false;
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            job.Status = AutoTagLiterals.SkippedStatus;
            job.Error = "No runnable download enrichment stage was configured.";
            job.ExitCode = 0;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.ResumeCheckpoint = null;
            job.ResumeFromJobId = null;
            SaveJob(job);
            AppendActivityLog(job.Id, "autotag skipped: no runnable download enrichment stage configured");
            return true;
        }

        job.Status = AutoTagLiterals.FailedStatus;
        job.Error = "No AutoTag stages configured.";
        job.ExitCode = 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ResumeCheckpoint = null;
        job.ResumeFromJobId = null;
        SaveJob(job);
        AppendActivityLog(job.Id, "autotag failed: no stages configured");
        return true;
    }

    private readonly record struct StageRunResult(bool Success);

    private async Task<StageRunResult> ExecuteStagesAsync(
        AutoTagJob job,
        IReadOnlyList<AutoTagStageConfig> stages,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var stageResult = await ExecuteSingleStageAsync(job, stage, index, stages.Count, path, fileOutcomes, cancellationToken);
            if (!stageResult.Success)
            {
                return new StageRunResult(false);
            }
        }

        return new StageRunResult(true);
    }

    private readonly record struct StageExecutionResult(bool Success);

    private async Task<StageExecutionResult> ExecuteSingleStageAsync(
        AutoTagJob job,
        AutoTagStageConfig stage,
        int stageIndex,
        int totalStages,
        string path,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        AppendLog(job, BuildStageStartedLog(stage, stageIndex, totalStages));
        _activeJobStages[job.Id] = stage.Name;
        var resumeCursor = ResolveResumeCursor(job, stage);
        if (resumeCursor != null)
        {
            AppendLog(
                job,
                $"resume checkpoint active for stage '{stage.Name}': platformIndex={resumeCursor.PlatformIndex}, fileIndex={resumeCursor.FileIndex}");
        }
        else if (job.ResumeCheckpoint != null
            && !string.Equals(job.ResumeCheckpoint.StageName, stage.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Only drop the checkpoint when this stage is not the one it belongs to.
            // A drifted config hash must not rewind an enhancement run to file 0.
            job.ResumeCheckpoint = null;
            SaveJob(job);
        }
        EnsureInitialEnrichmentResumeCheckpoint(job, stage);

        try
        {
            var result = await _autoTagRunner.RunAsync(
                job.Id,
                path,
                stage.ConfigPath,
                status => UpdateStatus(job, status, stage.Name, stage.ConfigHash, stageIndex, totalStages, fileOutcomes),
                line => AppendLog(job, line),
                IsEnhancementRunIntent(job.RunIntent)
                    ? (files, token) => ApplyCompletedGapFillBatchAsync(job, stage.ConfigPath, files, token)
                    : null,
                resumeCursor,
                cancellationToken);
            if (result.Outcome == AutoTagRunOutcome.Stopped)
            {
                return HandleStoppedStage(job);
            }

            if (!result.Success)
            {
                if (result.Outcome == AutoTagRunOutcome.Paused
                    && TryHandlePausedStage(job, result.Error))
                {
                    return new StageExecutionResult(false);
                }

                job.Status = AutoTagLiterals.FailedStatus;
                job.Error = result.Error;
                return new StageExecutionResult(false);
            }

            AppendLog(job, BuildStageFinishedLog(stage, stageIndex, totalStages));
            if (CanApplyResumeCheckpoint(job.ResumeCheckpoint, stage))
            {
                job.ResumeCheckpoint = null;
                SaveJob(job);
            }

            return new StageExecutionResult(true);
        }
        finally
        {
            _activeJobStages.TryRemove(job.Id, out _);
        }
    }

    /// <summary>
    /// Applies a paused runner outcome (AutoTagRunOutcome.Paused) to the job. The
    /// outcome is typed; the error argument is the pause reason itself.
    /// </summary>
    private bool TryHandlePausedStage(AutoTagJob job, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var message = error.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "AutoTag paused.";
        }

        job.Status = AutoTagLiterals.PausedStatus;
        job.Error = message;
        AppendLog(job, $"autotag paused: {message}");
        NotifyDownloadToast(message, "warning");
        return true;
    }
}
