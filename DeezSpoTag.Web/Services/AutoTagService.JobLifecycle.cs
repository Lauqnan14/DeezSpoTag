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

    public bool HasRunningJobs()
    {
        return !_activeJobIds.IsEmpty;
    }

    public async Task<AutoTagJob?> StartJob(
        string path,
        string configJson,
        StartJobOptions? options = null)
    {
        options ??= new StartJobOptions();
        var normalizedPath = NormalizePathForJob(path);
        var normalizedTrigger = NormalizeRunTrigger(options.Trigger);
        var normalizedRunIntent = NormalizeRunIntent(options.RunIntent);
        ResumeCheckpointSeed? resumeSeed;
        AutoTagJob? resumeSourceJob;
        if (!string.IsNullOrWhiteSpace(options.ResumeFromJobId))
        {
            // Explicit resume: seed from the named source job instead of the scope lookup,
            // so the exact requested job's checkpoint is used even if newer interrupted
            // jobs exist in the same scope.
            var resumeSource = GetJob(options.ResumeFromJobId) ?? LoadJob(options.ResumeFromJobId);
            if (resumeSource == null || resumeSource.ResumeCheckpoint == null)
            {
                _logger.LogWarning(
                    "Explicit resume seed failed: job {JobId} not found or has no resume checkpoint.",
                    options.ResumeFromJobId);
                resumeSeed = null;
                resumeSourceJob = null;
            }
            else
            {
                resumeSeed = BuildResumeCheckpointSeed(resumeSource);
                resumeSourceJob = resumeSource;
            }
        }
        else
        {
            resumeSeed = TryResolveResumeCheckpointSeed(normalizedPath, normalizedRunIntent, options.ProfileId);
            resumeSourceJob = resumeSeed == null ? null : GetJob(resumeSeed.SourceJobId) ?? LoadJob(resumeSeed.SourceJobId);
        }
        if (resumeSeed != null
            && resumeSourceJob != null
            && JsonNode.Parse(configJson) is JsonObject resumeConfigRoot
            && ShouldRewindLegacyCombinedFolderUniformityJob(resumeSourceJob, resumeConfigRoot))
        {
            resumeSeed.Checkpoint.PlatformIndex = 0;
            resumeSeed.Checkpoint.FileIndex = 0;
            resumeSeed.Checkpoint.LastPath = null;
            resumeSeed.Checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
            AppendLog(resumeSourceJob, "resume compatibility: rewound legacy combined enhancement checkpoint because Folder Uniformity had no recorded execution evidence.");
        }
        var resumedJobId = resumeSeed?.ResumeJobId ?? Guid.NewGuid().ToString("N");
        var resumedStartedAt = resumeSeed?.StartedAt ?? DateTimeOffset.UtcNow;

        var blockedByTriggerPolicy = TryCreateBlockedJobForTriggerPolicy(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            options.ProfileId,
            options.ProfileName);
        if (blockedByTriggerPolicy != null)
        {
            return blockedByTriggerPolicy;
        }

        if (await ShouldSkipForActiveDownloadsAsync())
        {
            _logger.LogInformation("AutoTag skipped: downloads active.");
            return null;
        }

        var blockedByScope = await TryCreateBlockedJobForScopePolicyAsync(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            options.ProfileId,
            options.ProfileName);
        if (blockedByScope != null)
        {
            return blockedByScope;
        }

        var blockedByActiveJob = TryCreateBlockedJobForActiveJobPolicy(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            options.ProfileId,
            options.ProfileName);
        if (blockedByActiveJob != null)
        {
            return blockedByActiveJob;
        }

        if (!HasEligibleInputFiles(normalizedPath, configJson))
        {
            return CreateSkippedJob(
                "No eligible audio files were found for this run.",
                normalizedPath,
                normalizedTrigger,
                normalizedRunIntent,
                options.ProfileId,
                options.ProfileName);
        }

        var selectedEnhancementFeatures = EnhancementWorkflowSelection
            .OrderSelectedFeatures(options.SelectedEnhancementFeatures)
            .ToList();
        var folderUniformityRunMode = EnhancementWorkflowSelection.ResolveFolderUniformityRunMode(selectedEnhancementFeatures);
        var isWorkflowOnlyRun = selectedEnhancementFeatures.Count > 0
            && !selectedEnhancementFeatures.Contains(EnhancementWorkflowSelection.GapFill, StringComparer.OrdinalIgnoreCase);
        var job = new AutoTagJob
        {
            Id = resumedJobId,
            Status = AutoTagLiterals.RunningStatus,
            StartedAt = resumedStartedAt,
            RootPath = normalizedPath,
            Trigger = normalizedTrigger,
            RunIntent = normalizedRunIntent,
            ProfileId = string.IsNullOrWhiteSpace(options.ProfileId) ? null : options.ProfileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(options.ProfileName) ? null : options.ProfileName.Trim(),
            EnhancementFeature = NormalizeEnhancementFeature(options.EnhancementFeature),
            SelectedEnhancementFeatures = selectedEnhancementFeatures,
            FolderUniformityRunMode = folderUniformityRunMode,
            EnhancementGroupId = string.IsNullOrWhiteSpace(options.EnhancementGroupId) ? null : options.EnhancementGroupId.Trim(),
            ResumeCheckpoint = resumeSeed?.Checkpoint ?? (isWorkflowOnlyRun
                ? new AutoTagResumeCheckpoint
                {
                    StageName = "enhancement-workflows",
                    StageConfigHash = string.Empty,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                : null),
            ResumeFromJobId = null,
            LastActivityAt = DateTimeOffset.UtcNow
        };
        if (resumeSourceJob != null)
        {
            HydrateResumeJob(job, resumeSourceJob);
        }

        _jobs[job.Id] = job;
        _activeJobIds.TryAdd(job.Id, 0);
        SaveJob(job);
        TrySaveLastJobId(job.Id);
        AppendActivityLog(job.Id, $"autotag started: {normalizedPath}");
        if (resumeSeed != null)
        {
            AppendLog(
                job,
                $"resume checkpoint loaded from job {resumeSeed.SourceJobId}: stage={resumeSeed.Checkpoint.StageName}, platformIndex={resumeSeed.Checkpoint.PlatformIndex}, fileIndex={resumeSeed.Checkpoint.FileIndex}");
        }

        InitializeRunArchive(job);
        _ = PrepareRuntimeConfigAndRunJobAsync(job, normalizedPath, configJson, options);

        return job;
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

    private async Task RunSuccessPostProcessingAsync(
        AutoTagJob job,
        string path,
        SuccessPostProcessingContext context,
        CancellationToken cancellationToken)
    {
        var isManualEnrichment = IsManualEnrichmentRunIntent(job.RunIntent);
        var autoMove = await RunFinalAutoMoveAsync(job, path, context.ConfigPath, context.FileOutcomes, cancellationToken);
        if (isManualEnrichment && !autoMove.Completed)
        {
            throw new InvalidOperationException(
                autoMove.Summary.Error ?? "Manual enrichment finalization did not move every fully enriched file.");
        }
        await RunIntegratedEnhancementWorkflowsAsync(
            job,
            path,
            context.ConfigPath,
            context.IncludesEnhancementWorkflows,
            cancellationToken,
            autoMove.Summary);
        var hasEnhancementWork = context.IncludesEnhancementStage
            || context.IncludesEnhancementWorkflows
            || isManualEnrichment;
        if (autoMove.Completed && !isManualEnrichment)
        {
            await TriggerPlexScanAfterMoveAsync(job, cancellationToken);
        }
        if (!isManualEnrichment)
        {
            // Manual enrichment already ingested the moved paths before its sidecar
            // lookup; the lookup resolves track identities at the moved paths.
            await IngestKnownFilesAfterAutoMoveAsync(
                job,
                autoMove.Summary,
                cancellationToken);
        }
        await TriggerConfiguredMediaServerRefreshAfterEnhancementAsync(
            job,
            hasEnhancementWork,
            cancellationToken);
    }

    private static bool IsTerminalRunStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.ResumedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.SkippedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private void PruneOrphanedHistoryDirectories(string root, HashSet<string> retainedIds, DateTimeOffset cutoffUtc)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var jobId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(jobId)
                    || _activeJobIds.ContainsKey(jobId)
                    || retainedIds.Contains(jobId))
                {
                    continue;
                }

                if (GetFileSystemTimestampUtc(directory) < cutoffUtc)
                {
                    DeleteArchivedRunFiles(jobId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to prune orphaned AutoTag history directories from {Root}.", root);
            }
        }
    }

    private static bool IsRunningStatus(string? status)
        => string.Equals(status?.Trim(), AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase);
}
