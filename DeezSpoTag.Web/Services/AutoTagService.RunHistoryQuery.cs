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

    public string? TryGetLastJobId()
    {
        try
        {
            if (!File.Exists(_lastJobPath))
            {
                return null;
            }

            var json = File.ReadAllText(_lastJobPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var node = JsonNode.Parse(json);
            return node?["jobId"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load last AutoTag job id.");
            return null;
        }
    }

    public string? TryGetLastConfigJson()
    {
        try
        {
            if (!File.Exists(_lastConfigPath))
            {
                return null;
            }

            var json = File.ReadAllText(_lastConfigPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return RedactSensitiveConfigJson(SanitizeConfigJson(json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load last AutoTag config.");
            return null;
        }
    }

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
}
