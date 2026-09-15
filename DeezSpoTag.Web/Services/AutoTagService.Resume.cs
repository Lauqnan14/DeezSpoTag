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

    internal static bool ShouldRewindLegacyCombinedFolderUniformityJob(AutoTagJob job, JsonObject configRoot)
    {
        if (job.SelectedEnhancementFeatures.Count > 0
            || !string.IsNullOrWhiteSpace(job.FolderUniformityRunMode)
            || job.EnhancementBatchState.Pending != null
            || job.ResumeCheckpoint is not { } checkpoint
            || (checkpoint.PlatformIndex <= 0 && checkpoint.FileIndex <= 0)
            || configRoot[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot
            || !EnhancementWorkflowSelection.IsFolderUniformityRunnable(enhancementRoot))
        {
            return false;
        }

        var combined = EnhancementWorkflowSelection.IsGapFillRunnable(configRoot)
            || EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot);
        var hasFolderUniformityEvidence = job.EnhancementWorkflows.Any(workflow =>
                string.Equals(workflow.Name, EnhancementWorkflowSelection.FolderUniformity, StringComparison.OrdinalIgnoreCase))
            || job.StatusHistory.Any(entry => string.Equals(
                entry.Status?.Platform,
                EnhancementWorkflowSelection.FolderUniformity,
                StringComparison.OrdinalIgnoreCase));
        return combined && !hasFolderUniformityEvidence;
    }

    private static void HydrateResumeJob(AutoTagJob target, AutoTagJob source)
    {
        target.OkCount = source.OkCount;
        target.ErrorCount = source.ErrorCount;
        target.ReviewCount = source.ReviewCount;
        target.SkippedCount = source.SkippedCount;
        target.Progress = source.Progress;
        target.EnhancementFeature ??= source.EnhancementFeature;
        if (target.SelectedEnhancementFeatures.Count == 0)
        {
            target.SelectedEnhancementFeatures = source.SelectedEnhancementFeatures.ToList();
        }
        target.FolderUniformityRunMode ??= source.FolderUniformityRunMode;
        target.EnhancementBatchState = CloneEnhancementBatchState(source.EnhancementBatchState);
        target.EnhancementGroupId ??= source.EnhancementGroupId;
        target.CurrentPhase = source.CurrentPhase;
        target.CurrentBatch = source.CurrentBatch;
        target.BatchCount = source.BatchCount;
        target.BatchProcessed = source.BatchProcessed;
        target.BatchSize = source.BatchSize;
        target.ProcessedItems = source.ProcessedItems;
        target.TotalItems = source.TotalItems;
        target.TargetReason ??= source.TargetReason;
        target.TargetRequested = source.TargetRequested > 0 ? source.TargetRequested : target.TargetRequested;
        target.TargetUsable = source.TargetUsable > 0 ? source.TargetUsable : target.TargetUsable;
        target.EnhancementFoundCount = source.EnhancementFoundCount > 0 ? source.EnhancementFoundCount : target.EnhancementFoundCount;
        target.EnhancementGapFilledCount = source.EnhancementGapFilledCount > 0 ? source.EnhancementGapFilledCount : target.EnhancementGapFilledCount;
        target.EnhancementSidecarredCount = source.EnhancementSidecarredCount > 0 ? source.EnhancementSidecarredCount : target.EnhancementSidecarredCount;
        target.EnhancementTidiedCount = source.EnhancementTidiedCount > 0 ? source.EnhancementTidiedCount : target.EnhancementTidiedCount;
        target.EnhancementManifestPath ??= source.EnhancementManifestPath;
        target.ExitCode = null;
        target.Error = null;
        target.LastActivityAt = source.LastActivityAt > DateTimeOffset.MinValue
            ? source.LastActivityAt
            : source.StartedAt;
        if (source.Logs.Count > 0)
        {
            target.Logs.AddRange(source.Logs);
        }

        if (source.StatusHistory.Count > 0)
        {
            target.StatusHistory.AddRange(source.StatusHistory);
        }

        if (source.EnhancedFilePaths.Count > 0)
        {
            target.EnhancedFilePaths.AddRange(source.EnhancedFilePaths);
        }

        if (source.StartedPlatforms.Count > 0)
        {
            target.StartedPlatforms.AddRange(source.StartedPlatforms);
        }

        if (source.EnhancementWorkflows.Count > 0)
        {
            target.EnhancementWorkflows.AddRange(source.EnhancementWorkflows);
        }

        foreach (var (diffPath, diffValue) in source.TagDiffs)
        {
            target.TagDiffs[diffPath] = diffValue;
        }
    }

    private ResumeCheckpointSeed? TryResolveResumeCheckpointSeed(
        string normalizedPath,
        string normalizedRunIntent,
        string? profileId)
    {
        try
        {
            if (!Directory.Exists(_jobsDir))
            {
                return null;
            }

            var normalizedProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
            var latestMatchingJob = FindLatestResumeScopeJob(
                normalizedPath,
                normalizedRunIntent,
                normalizedProfileId);
            if (!IsEligibleResumeCandidate(latestMatchingJob, normalizedPath, normalizedRunIntent, normalizedProfileId))
            {
                return null;
            }

            return BuildResumeCheckpointSeed(latestMatchingJob!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed resolving AutoTag resume checkpoint seed.");
            return null;
        }
    }

    private AutoTagJob? FindLatestResumeScopeJob(
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        return Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern)
            .Select(TryLoadResumeScopeJob)
            .Where(job => job is not null
                && IsResumeScopeMatch(job, normalizedPath, normalizedRunIntent, normalizedProfileId))
            .Select(job => job!)
            .Aggregate<AutoTagJob, AutoTagJob?>(
                null,
                static (latestMatchingJob, job) => latestMatchingJob == null || job.StartedAt >= latestMatchingJob.StartedAt
                    ? job
                    : latestMatchingJob);
    }

    private AutoTagJob? TryLoadResumeScopeJob(string path)
    {
        var jobId = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        return _jobs.TryGetValue(jobId, out var cachedJob) ? cachedJob : LoadJob(jobId);
    }

    private bool IsEligibleResumeCandidate(
        AutoTagJob? latestMatchingJob,
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        return latestMatchingJob != null
            && IsResumeCandidate(latestMatchingJob, normalizedPath, normalizedRunIntent, normalizedProfileId);
    }

    private ResumeCheckpointSeed? BuildResumeCheckpointSeed(AutoTagJob job)
    {
        var checkpoint = CloneResumeCheckpoint(job.ResumeCheckpoint);
        if (checkpoint == null)
        {
            return null;
        }

        var rootJob = ResolveResumeRootJob(job);
        return new ResumeCheckpointSeed(job.Id, rootJob.Id, rootJob.StartedAt, checkpoint);
    }

    private AutoTagJob ResolveResumeRootJob(AutoTagJob job)
    {
        var current = job;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { job.Id };
        for (var depth = 0; depth < 20; depth += 1)
        {
            if (string.IsNullOrWhiteSpace(current.ResumeFromJobId) || !seen.Add(current.ResumeFromJobId))
            {
                return current;
            }

            var parent = GetJob(current.ResumeFromJobId) ?? LoadJob(current.ResumeFromJobId);
            if (parent == null)
            {
                return current;
            }

            current = parent;
        }

        return current;
    }

    private static bool IsResumeScopeMatch(
        AutoTagJob job,
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        if (!string.Equals(NormalizeRunIntent(job.RunIntent), normalizedRunIntent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(NormalizePathForJob(job.RootPath ?? string.Empty), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedProfileId)
            && !string.Equals(job.ProfileId?.Trim(), normalizedProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private bool IsResumeCandidate(
        AutoTagJob job,
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        if (!IsResumeScopeMatch(job, normalizedPath, normalizedRunIntent, normalizedProfileId))
        {
            return false;
        }

        if (job.ResumeCheckpoint == null
            && job.EnhancementBatchState.Pending == null
            && job.EnhancementBatchState.NextBatchIndex <= 0)
        {
            return false;
        }

        var status = job.Status?.Trim();
        var staleRunning = string.Equals(status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            && !_activeJobIds.ContainsKey(job.Id);
        if (!string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase)
            && !staleRunning)
        {
            return false;
        }

        return true;
    }

    private static AutoTagResumeCheckpoint? CloneResumeCheckpoint(AutoTagResumeCheckpoint? checkpoint)
    {
        if (checkpoint == null)
        {
            return null;
        }

        return new AutoTagResumeCheckpoint
        {
            StageName = checkpoint.StageName,
            StageConfigHash = checkpoint.StageConfigHash,
            PlatformIndex = checkpoint.PlatformIndex,
            FileIndex = checkpoint.FileIndex,
            PlatformCount = checkpoint.PlatformCount,
            FileCount = checkpoint.FileCount,
            LastPath = checkpoint.LastPath,
            UpdatedAt = checkpoint.UpdatedAt
        };
    }

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
                SelectedEnhancementFeatures: job.SelectedEnhancementFeatures,
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

        NotifyRunResumed(job, resumed.Id);
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

    private static bool TryUpdateResumeCheckpoint(
        AutoTagJob job,
        string stageName,
        string stageConfigHash,
        TaggingStatusWrap status)
    {
        if (!IsTerminalStatus(status.Status?.Status))
        {
            return false;
        }

        var nextPlatformIndex = status.NextPlatformIndex;
        var nextFileIndex = status.NextFileIndex;
        if (nextPlatformIndex is not int
            || nextFileIndex is not int
            || status.PlatformCount is not int platformCount
            || status.FileCount is not int fileCount
            || platformCount <= 0
            || fileCount <= 0)
        {
            // Fallback: some terminal statuses (workflow tails, batch boundaries) omit the
            // next-indexes. If the current indexes are known, advance by one so every
            // successfully processed file still advances the checkpoint (no silent skips).
            if (status.PlatformIndex is not int currentPlatform
                || status.FileIndex is not int currentFile)
            {
                return false;
            }

            platformCount = Math.Max(1, status.PlatformCount ?? 0);
            fileCount = Math.Max(1, status.FileCount ?? 0);
            nextPlatformIndex = currentPlatform;
            nextFileIndex = currentFile + 1;
            if (nextFileIndex >= fileCount)
            {
                nextFileIndex = 0;
                nextPlatformIndex = Math.Min(platformCount, currentPlatform + 1);
            }
        }

        job.ResumeCheckpoint = new AutoTagResumeCheckpoint
        {
            StageName = stageName,
            StageConfigHash = stageConfigHash,
            PlatformIndex = Math.Max(0, nextPlatformIndex ?? 0),
            FileIndex = Math.Max(0, nextFileIndex ?? 0),
            PlatformCount = platformCount,
            FileCount = fileCount,
            LastPath = status.Status?.Path,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return true;
    }

    private static AutoTagResumeCursor? ResolveResumeCursor(AutoTagJob job, AutoTagStageConfig stage)
    {
        if (!CanApplyResumeCheckpoint(job.ResumeCheckpoint, stage))
        {
            return null;
        }

        var checkpoint = job.ResumeCheckpoint!;
        return new AutoTagResumeCursor(
            Math.Max(0, checkpoint.PlatformIndex),
            Math.Max(0, checkpoint.FileIndex),
            checkpoint.PlatformCount,
            checkpoint.FileCount,
            checkpoint.LastPath);
    }

    private static bool CanApplyResumeCheckpoint(AutoTagResumeCheckpoint? checkpoint, AutoTagStageConfig stage)
    {
        if (checkpoint == null)
        {
            return false;
        }

        if (checkpoint.PlatformCount <= 0 || checkpoint.FileCount <= 0)
        {
            return false;
        }

        if (checkpoint.PlatformIndex < 0
            || checkpoint.FileIndex < 0
            || checkpoint.PlatformIndex >= checkpoint.PlatformCount
            || checkpoint.FileIndex > checkpoint.FileCount)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(checkpoint.StageName))
        {
            return false;
        }

        if (!string.Equals(checkpoint.StageName, stage.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Enhancement/gap-fill stage JSON is rebuilt on resume (platform auth,
        // eligibility, sanitization). A hash drift must not discard the cursor —
        // LastPath still identifies the file that finished.
        if (string.Equals(stage.Name, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(checkpoint.StageConfigHash))
        {
            return false;
        }

        return string.Equals(checkpoint.StageConfigHash, stage.ConfigHash, StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveResumeRootJobId(string jobId, string? resumeFromJobId)
    {
        var currentId = jobId;
        var parentId = resumeFromJobId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { jobId };
        for (var depth = 0; depth < 20; depth += 1)
        {
            if (string.IsNullOrWhiteSpace(parentId) || !seen.Add(parentId))
            {
                return currentId;
            }

            currentId = parentId;
            parentId = TryReadJobResumeFromJobId(currentId);
        }

        return currentId;
    }

    private string? TryReadJobResumeFromJobId(string jobId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            var path = Path.Join(_jobsDir, $"{jobId}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return document.RootElement.TryGetProperty(nameof(AutoTagJob.ResumeFromJobId), out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read AutoTag resume source for {JobId}.", jobId);
            }
            return null;
        }
    }

    private string GetRunTagDiffCheckpointDirectory(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "tag-diff-checkpoints");

    private void SaveTagDiffCheckpoint(string jobId, string normalizedPath, AutoTagTagDiff diff)
    {
        try
        {
            var directory = GetRunTagDiffCheckpointDirectory(jobId);
            Directory.CreateDirectory(directory);
            var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant();
            var path = Path.Join(directory, keyHash + ".json");
            var tempPath = path + ".tmp";
            var payload = new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase)
            {
                [normalizedPath] = diff
            };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(payload, _jsonOptions), new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist AutoTag tag-diff checkpoint for {JobId}", jobId);
            }
        }
    }

    private async Task TryAutoResumeRecoveredJobAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        if (job.ResumeCheckpoint == null)
        {
            AppendLog(job, "stuck watchdog: auto-resume skipped because no resume checkpoint is available.");
            return;
        }

        if (string.IsNullOrWhiteSpace(job.RootPath))
        {
            AppendLog(job, "stuck watchdog: auto-resume skipped because the job root path is missing.");
            return;
        }

        var runtimeConfigPath = TryFindRuntimeConfigPath(job.Id, "base");
        if (string.IsNullOrWhiteSpace(runtimeConfigPath) || !File.Exists(runtimeConfigPath))
        {
            AppendLog(job, "stuck watchdog: auto-resume skipped because the runtime config was not found.");
            return;
        }

        try
        {
            var configJson = await File.ReadAllTextAsync(runtimeConfigPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(configJson))
            {
                AppendLog(job, "stuck watchdog: auto-resume skipped because the runtime config is empty.");
                return;
            }

            AppendLog(job, "stuck watchdog: auto-resume starting from preserved checkpoint.");
            var resumed = await StartJob(
                job.RootPath!,
                configJson,
                new StartJobOptions(
                    Trigger: job.Trigger,
                    ProfileId: job.ProfileId,
                    ProfileName: job.ProfileName,
                    RunIntent: job.RunIntent));
            if (resumed == null)
            {
                AppendLog(job, "stuck watchdog: auto-resume skipped because downloads are active.");
                return;
            }

            AppendLog(job, $"stuck watchdog: auto-resume created job {resumed.Id} (status={resumed.Status}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag stuck watchdog failed to auto-resume job {JobId}.", job.Id);
            AppendLog(job, $"stuck watchdog: auto-resume failed: {ex.Message}");
        }
    }
}
