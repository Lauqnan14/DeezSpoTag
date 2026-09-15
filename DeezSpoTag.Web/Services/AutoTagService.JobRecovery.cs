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

    public async Task<bool> StopJobAsync(string id, string? stopReason = null)
    {
        var outcome = await StopJobInternalAsync(id, stopReason);
        return outcome.Stopped;
    }

    /// <summary>
    /// Stop a job and report the status actually applied (canceled / paused /
    /// interrupted), so callers can surface the truth instead of assuming "paused".
    /// </summary>
    public async Task<StopJobOutcome> StopJobWithStatusAsync(string id, string? stopReason = null)
    {
        return await StopJobInternalAsync(id, stopReason);
    }

    private async Task<StopJobOutcome> StopJobInternalAsync(string id, string? stopReason)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            var loaded = LoadJob(id);
            if (loaded == null)
            {
                return new StopJobOutcome(false, null);
            }
            NormalizeLoadedJobState(loaded);
            if (!IsActiveJobStatus(loaded.Status) && !IsPausedOrInterruptedRunStatus(loaded.Status))
            {
                return new StopJobOutcome(false, null);
            }
            job = loaded;
            _jobs[id] = job;
        }

        // A paused/interrupted run holds no live execution: a stop request cancels it
        // outright. Re-applying the resumable-pause status here would leave the run
        // stuck as "paused" and report the cancel as a miss to clients.
        if (IsPausedOrInterruptedRunStatus(job.Status))
        {
            job.Status = AutoTagLiterals.CanceledStatus;
            job.Error = "Canceled by user.";
            SaveJob(job);
            AppendActivityLog(job.Id, "autotag canceled: resumable run canceled by user");
            return new StopJobOutcome(true, AutoTagLiterals.CanceledStatus);
        }

        var normalizedStopReason = NormalizeStopReason(stopReason);
        var stopStatus = ResolveStopStatus(job, normalizedStopReason);
        var previousStatus = job.Status;
        var previousError = job.Error;
        job.Status = stopStatus;
        job.Error = BuildStopError(job, normalizedStopReason);
        SaveJob(job);

        var stopped = await _autoTagRunner.StopAsync(id, CancellationToken.None);
        if (_jobCancellationSources.TryGetValue(id, out var cancellation))
        {
            await cancellation.CancelAsync();
            stopped = true;
        }

        if (stopped)
        {
            AppendActivityLog(
                job.Id,
                BuildStopActivityLog(stopStatus, normalizedStopReason));
            NotifyRunStopped(job, stopStatus, normalizedStopReason);
            return new StopJobOutcome(true, job.Status);
        }

        if (string.Equals(job.Status, stopStatus, StringComparison.OrdinalIgnoreCase))
        {
            job.Status = previousStatus;
            job.Error = previousError;
            SaveJob(job);
        }

        return new StopJobOutcome(false, null);
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
        NotifyRunStopped(job, job.Status, job.Error);
    }

    private async Task HandleRunJobFailureAsync(
        AutoTagJob job,
        Exception ex,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes)
    {
        _logger.LogError(ex, "AutoTag job {JobId} failed", job.Id);
        job.Status = AutoTagLiterals.FailedStatus;
        job.Error = ex.Message;
        job.FinishedAt = DateTimeOffset.UtcNow;
        AppendPlatformSummary(job);
        SaveJob(job);
        AppendActivityLog(job.Id, $"autotag failed: {job.Error ?? "unknown error"}");
        NotifyRunFinished(job);

        if (IsManualEnrichmentRunIntent(job.RunIntent) && job.AutoMoveSummary != null)
        {
            NotifyCompleted(job);
            return;
        }

        AppendLog(job, "tagging failed, evaluating post-failure auto-move");
        var autoMove = await RunFinalAutoMoveAsync(job, path, configPath, fileOutcomes, CancellationToken.None);
        if (autoMove.Completed)
        {
            await TriggerPlexScanAfterMoveAsync(job, CancellationToken.None);
            await IngestKnownFilesAfterAutoMoveAsync(
                job,
                autoMove.Summary,
                CancellationToken.None);
        }

        NotifyCompleted(job);
    }

    public async Task RecoverStuckJobsAsync(
        TimeSpan staleWindow,
        bool restartStalePersistedJobs,
        CancellationToken cancellationToken)
    {
        if (staleWindow <= TimeSpan.Zero)
        {
            staleWindow = TimeSpan.FromMinutes(30);
        }

        await StopActiveJobsWithoutProgressAsync(staleWindow, cancellationToken);
        await RecoverPersistedRunningJobsAsync(staleWindow, restartStalePersistedJobs, cancellationToken);
    }

    private async Task StopActiveJobsWithoutProgressAsync(TimeSpan staleWindow, CancellationToken cancellationToken)
    {
        foreach (var jobId in _activeJobIds.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_jobs.TryGetValue(jobId, out var job) || !IsRunningStatus(job.Status))
            {
                continue;
            }

            var idleFor = DateTimeOffset.UtcNow - GetLastProgressTimestamp(job);
            if (idleFor < staleWindow)
            {
                continue;
            }

            if (!_stuckRecoveryJobs.TryAdd(job.Id, 0))
            {
                continue;
            }

            AppendLog(
                job,
                $"stuck watchdog: no AutoTag progress for {FormatDuration(idleFor)}; canceling active run so it can resume.");

            try
            {
                if (await StopJobAsync(job.Id, "recovery")
                    && await WaitForJobToLeaveActiveSetAsync(job.Id, TimeSpan.FromSeconds(30), cancellationToken))
                {
                    await TryAutoResumeRecoveredJobAsync(job, cancellationToken);
                }
            }
            finally
            {
                _stuckRecoveryJobs.TryRemove(job.Id, out _);
            }
        }
    }

    private async Task RecoverPersistedRunningJobsAsync(
        TimeSpan staleWindow,
        bool restartStalePersistedJobs,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_jobsDir))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern).OrderByDescending(File.GetLastWriteTimeUtc).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activeJobIds.IsEmpty)
            {
                return;
            }

            var jobId = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(jobId)
                || _activeJobIds.ContainsKey(jobId)
                || !_stuckRecoveryJobs.TryAdd(jobId, 0))
            {
                continue;
            }

            try
            {
                var job = _jobs.TryGetValue(jobId, out var cachedJob) ? cachedJob : LoadJob(jobId);
                if (job == null || !IsRunningStatus(job.Status) || _activeJobIds.ContainsKey(job.Id))
                {
                    continue;
                }

                var idleFor = DateTimeOffset.UtcNow - GetLastProgressTimestamp(job, path);
                if (idleFor < staleWindow)
                {
                    continue;
                }

                await RecoverPersistedRunningJobAsync(job, idleFor, restartStalePersistedJobs, cancellationToken);
            }
            finally
            {
                _stuckRecoveryJobs.TryRemove(jobId, out _);
            }
        }
    }

    private async Task RecoverPersistedRunningJobAsync(
        AutoTagJob job,
        TimeSpan idleFor,
        bool restartStalePersistedJobs,
        CancellationToken cancellationToken)
    {
        job.Trigger = NormalizeRunTrigger(job.Trigger);
        job.RunIntent = NormalizeRunIntent(job.RunIntent);
        job.Status = AutoTagLiterals.InterruptedStatus;
        job.ExitCode = 1;
        job.FinishedAt ??= DateTimeOffset.UtcNow;
        job.Error = $"AutoTag job had no progress for {FormatDuration(idleFor)} and was recovered as interrupted.";
        SaveJob(job);
        AppendLog(job, "stuck watchdog: recovered stale running job; resume checkpoint preserved.");
        AppendLog(job, "stale recovery: auto-move disabled; file finalization remains owned by its authoritative pipeline");
        AppendActivityLog(job.Id, "autotag interrupted by stuck watchdog");

        if (!restartStalePersistedJobs)
        {
            return;
        }

        await TryAutoResumeRecoveredJobAsync(job, cancellationToken);
    }

    private async Task<bool> WaitForJobToLeaveActiveSetAsync(
        string jobId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activeJobIds.ContainsKey(jobId))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        if (_jobs.TryGetValue(jobId, out var job))
        {
            AppendLog(job, "stuck watchdog: auto-resume deferred because the canceled run is still active.");
        }

        return false;
    }
}
