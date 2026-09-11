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

    private static void RecordMalformedHistoryEntry(ref int skippedMalformed)
        => skippedMalformed += 1;

    private HashSet<string> EnumerateHistoryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            try
            {
                var normalized = Path.GetFullPath(root);
                roots.Add(normalized);
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                // Ignore invalid paths.
            }
        }

        AddRoot(_historyDir);
        AddRoot(_workersHistoryDir);

        var configuredDataRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDataRoot))
        {
            AddRoot(Path.Join(configuredDataRoot, AutoTagFolderName, HistoryFolderName));
        }

        var configuredConfigRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configuredConfigRoot))
        {
            AddRoot(Path.Join(configuredConfigRoot, AutoTagFolderName, HistoryFolderName));
        }

        return roots;
    }

    private string? ResolveRunFilePath(string jobId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return EnumerateHistoryRoots()
            .Select(root => Path.Join(root, jobId, fileName))
            .FirstOrDefault(File.Exists);
    }

    private IEnumerable<string> EnumerateRunFileCandidates(string jobId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in EnumerateHistoryRoots()
            .Select(root => Path.Join(root, jobId, fileName))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Where(seen.Add))
        {
            yield return normalized;
        }
    }

    private void BackfillArchivedRuns()
    {
        try
        {
            if (!Directory.Exists(_jobsDir))
            {
                return;
            }

            foreach (var jobId in Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(jobId => !string.IsNullOrWhiteSpace(jobId)))
            {
                var currentJobId = jobId!;
                var archiveComplete = IsRunArchiveComplete(currentJobId);
                var needsRepair = archiveComplete && ShouldRepairRunArchive(currentJobId);
                if (archiveComplete && !needsRepair)
                {
                    continue;
                }

                var job = LoadJob(currentJobId);
                if (job == null)
                {
                    continue;
                }

                MaterializeRunArchive(job);
                if (needsRepair && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Repaired stale AutoTag archive for {JobId}.", jobId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to backfill archived AutoTag runs.");
        }
    }

    private static bool ShouldBackfillArchivedRunsOnStartup(IConfiguration configuration)
    {
        var configured = Environment.GetEnvironmentVariable("DEEZSPOTAG_AUTOTAG_BACKFILL_ON_STARTUP");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return string.Equals(configured, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configured, "yes", StringComparison.OrdinalIgnoreCase);
        }

        return configuration.GetValue("AutoTag:ArchiveBackfillOnStartup", false);
    }

    private bool ShouldRepairRunArchive(string jobId)
    {
        try
        {
            var summary = LoadRunSummary(jobId);
            if (summary == null)
            {
                return false;
            }

            var logPath = GetRunLogPath(jobId);
            if (summary.LogCount > 0 && File.Exists(logPath) && new FileInfo(logPath).Length == 0)
            {
                return true;
            }

            var statusPath = GetRunStatusHistoryPath(jobId);
            if (summary.StatusEntryCount > 0 && File.Exists(statusPath) && new FileInfo(statusPath).Length == 0)
            {
                return true;
            }

            var tagDiffsPath = GetRunTagDiffsPath(jobId);
            if (File.Exists(tagDiffsPath))
            {
                var content = File.ReadAllText(tagDiffsPath, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(content) || string.Equals(content, "{}", StringComparison.Ordinal))
                {
                    var job = LoadJob(jobId);
                    if (job?.TagDiffs != null && job.TagDiffs.Count > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private bool IsRunArchiveComplete(string jobId)
    {
        return File.Exists(GetRunSummaryPath(jobId))
            && File.Exists(GetRunLogPath(jobId))
            && File.Exists(GetRunStatusHistoryPath(jobId));
    }

    private void MaterializeRunArchive(AutoTagJob job)
    {
        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(job.Id, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(job.Id));
                var summary = BuildRunSummary(job);
                File.WriteAllText(
                    GetRunSummaryPath(job.Id),
                    JsonSerializer.Serialize(summary, _jsonOptions),
                    new UTF8Encoding(false));

                File.WriteAllLines(
                    GetRunLogPath(job.Id),
                    (job.Logs ?? new List<string>()).Where(line => !string.IsNullOrWhiteSpace(line)),
                    new UTF8Encoding(false));

                var statusLines = (job.StatusHistory ?? new List<TaggingStatusSnapshot>())
                    .Select(entry => JsonSerializer.Serialize(entry, _jsonOptions))
                    .ToList();
                File.WriteAllLines(GetRunStatusHistoryPath(job.Id), statusLines, new UTF8Encoding(false));
                SaveArchivedTagDiffs(job.Id, job.TagDiffs);
                UpdateRunIndex(summary, force: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to materialize AutoTag run archive for {JobId}", job.Id);
            }
        }
    }

    private void SaveJob(AutoTagJob job)
    {
        try
        {
            var path = Path.Join(_jobsDir, $"{job.Id}.json");
            var json = JsonSerializer.Serialize(CreateJobPersistenceSnapshot(job), _jsonOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            SaveRunSummary(job);
            _lastJobFullSaveUtc[job.Id] = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist AutoTag job {JobId}", job.Id);
            }
        }
    }

    /// <summary>
    /// Save for routine progress updates (per-file statuses, log lines). Rewriting the
    /// full job JSON per event is IO-amplifying on long runs, so updates inside the
    /// throttle window are skipped — the next event (or any forced save) persists the
    /// accumulated state. Terminal transitions, checkpoint updates, and other
    /// resume-critical callers must pass <paramref name="force"/> (or call SaveJob).
    /// </summary>
    private void SaveJobThrottled(AutoTagJob job, bool force = false)
    {
        if (force
            || !ShouldThrottleJobSave(
                _lastJobFullSaveUtc.TryGetValue(job.Id, out var lastSave) ? lastSave : null,
                DateTimeOffset.UtcNow))
        {
            SaveJob(job);
        }
    }

    internal static bool ShouldThrottleJobSave(DateTimeOffset? lastSaveUtc, DateTimeOffset now)
    {
        return lastSaveUtc.HasValue && now - lastSaveUtc.Value < JobSaveThrottleInterval;
    }

    private AutoTagJob? LoadJob(string id)
    {
        try
        {
            var path = Path.Join(_jobsDir, $"{id}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var utf8 = File.ReadAllBytes(path);
            if (utf8.Length >= 3
                && utf8[0] == 0xEF
                && utf8[1] == 0xBB
                && utf8[2] == 0xBF)
            {
                var noBom = new byte[utf8.Length - 3];
                Buffer.BlockCopy(utf8, 3, noBom, 0, noBom.Length);
                utf8 = noBom;
            }

            var job = JsonSerializer.Deserialize<AutoTagJob>(utf8, _jsonOptions);
            if (job != null)
            {
                foreach (var (pathKey, diff) in LoadPersistedTagDiffs(job.Id))
                {
                    job.TagDiffs[pathKey] = diff;
                }
            }
            return job;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load AutoTag job JobId");
            return null;
        }
    }

    private void NormalizeLoadedJobState(AutoTagJob job)
    {
        job.Trigger = NormalizeRunTrigger(job.Trigger);
        job.RunIntent = NormalizeRunIntent(job.RunIntent);
        if (job.LastActivityAt <= DateTimeOffset.MinValue)
        {
            job.LastActivityAt = ResolveLastActivityTimestamp(job);
        }

        if (!string.Equals(job.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_activeJobIds.ContainsKey(job.Id))
        {
            return;
        }

        job.Status = AutoTagLiterals.InterruptedStatus;
        job.ExitCode = 1;
        job.FinishedAt ??= DateTimeOffset.UtcNow;
        job.Error ??= "AutoTag job was interrupted by an application restart; resume is available.";
        SaveJob(job);
        // Archived summaries and job records are immutable history beyond this point:
        // restart-interrupted enhancement runs are queued for resume, never rewritten.
        if (IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent))
        {
            AppendLog(job, "stale recovery: job interrupted by application restart; resume queued for enhancement.");
            JobRecovered?.Invoke(job);
        }
    }

    private static DateTimeOffset ResolveLastActivityTimestamp(AutoTagJob job)
    {
        var timestamp = job.StartedAt;
        if (job.ResumeCheckpoint?.UpdatedAt > timestamp)
        {
            timestamp = job.ResumeCheckpoint.UpdatedAt;
        }

        var latestStatusTimestamp = job.StatusHistory
            .Select(static entry => entry.Timestamp)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (latestStatusTimestamp > timestamp)
        {
            timestamp = latestStatusTimestamp;
        }

        if (job.FinishedAt > timestamp)
        {
            timestamp = job.FinishedAt.Value;
        }

        return timestamp;
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

    private static bool IsRunningStatus(string? status)
        => string.Equals(status?.Trim(), AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset GetLastProgressTimestamp(AutoTagJob job, string? jobPath = null)
    {
        var timestamp = job.StartedAt;
        if (job.LastActivityAt > timestamp)
        {
            timestamp = job.LastActivityAt;
        }

        if (job.ResumeCheckpoint?.UpdatedAt > timestamp)
        {
            timestamp = job.ResumeCheckpoint.UpdatedAt;
        }

        var lastStatusTimestamp = job.StatusHistory
            .Select(entry => entry.Timestamp)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (lastStatusTimestamp > timestamp)
        {
            timestamp = lastStatusTimestamp;
        }

        if (!string.IsNullOrWhiteSpace(jobPath) && File.Exists(jobPath))
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(jobPath), TimeSpan.Zero);
            if (modified > timestamp)
            {
                timestamp = modified;
            }
        }

        return timestamp;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.0}h";
        }

        return $"{Math.Max(1, duration.TotalMinutes):0}m";
    }

    private string? TryFindRuntimeConfigPath(string jobId, string stage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(stage) || !Directory.Exists(_runtimeConfigDir))
            {
                return null;
            }

            var stageToken = NormalizeConfigKeyForRedaction(stage);
            var pattern = $"autotag-{jobId}-{stageToken}-*.json";
            return Directory
                .EnumerateFiles(_runtimeConfigDir, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to locate runtime config for stale recovery job {JobId}.", jobId);
            }
            return null;
        }
    }
}
