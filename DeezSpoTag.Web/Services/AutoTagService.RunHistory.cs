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

    public bool TryGetRunningEnhancementJobId(out string? jobId)
    {
        var stage = _activeJobStages.FirstOrDefault(
            static entry => string.Equals(entry.Value, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(stage.Key))
        {
            jobId = stage.Key;
            return true;
        }

        var activeJobId = _activeJobIds.Keys.FirstOrDefault(activeJobId =>
            _jobs.TryGetValue(activeJobId, out var activeJob)
            && string.Equals(activeJob.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            && (IsEnhancementRunIntent(activeJob.RunIntent)
                || IsManualEnrichmentRunIntent(activeJob.RunIntent)));
        if (!string.IsNullOrWhiteSpace(activeJobId))
        {
            jobId = activeJobId;
            return true;
        }

        jobId = null;
        return false;
    }

    public bool TryGetAnyRunningJobId(out string? jobId)
    {
        var running = _activeJobIds.Keys.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(running))
        {
            jobId = running;
            return true;
        }

        jobId = null;
        return false;
    }

    public AutoTagJob? GetJob(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            return job;
        }

        var loaded = LoadJob(id);
        if (loaded != null)
        {
            NormalizeLoadedJobState(loaded);
            if (IsActiveJobStatus(loaded.Status))
            {
                _jobs[id] = loaded;
            }
        }

        return loaded;
    }

    public AutoTagJob? GetLatestJob()
    {
        var jobId = TryGetLastJobId();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        if (_jobs.TryGetValue(jobId, out var activeJob))
        {
            return activeJob;
        }

        var terminalJob = Volatile.Read(ref _latestTerminalJob);
        if (string.Equals(terminalJob?.Id, jobId, StringComparison.OrdinalIgnoreCase))
        {
            return terminalJob;
        }

        var loaded = LoadJob(jobId);
        if (loaded == null)
        {
            return null;
        }

        NormalizeLoadedJobState(loaded);
        if (IsActiveJobStatus(loaded.Status))
        {
            _jobs[jobId] = loaded;
            return loaded;
        }

        terminalJob = CreateCompactTerminalJob(loaded);
        Volatile.Write(ref _latestTerminalJob, terminalJob);
        return terminalJob;
    }

    internal static DateTimeOffset GetRunHistoryTimestamp(AutoTagRunSummary summary)
    {
        return summary.HistoryDate ?? summary.StartedAt;
    }

    internal static DateOnly GetRunDate(DateTimeOffset timestamp)
    {
        var localTimestamp = TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local);
        return DateOnly.FromDateTime(localTimestamp.DateTime);
    }

    internal static string GetRunDateToken(DateTimeOffset timestamp)
    {
        return GetRunDate(timestamp).ToString("yyyy-MM-dd");
    }

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

    private void TrySaveLastJobId(string jobId)
    {
        try
        {
            var payload = new JsonObject { ["jobId"] = jobId };
            File.WriteAllText(_lastJobPath, payload.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist last AutoTag job id.");
        }
    }

    private IReadOnlyList<AutoTagRunSummary> LoadRunIndexSummaries()
    {
        lock (_runIndexLock)
        {
            var indexed = TryLoadRunIndex();
            if (indexed.Count > 0 || (File.Exists(_runIndexPath) && new FileInfo(_runIndexPath).Length > 0))
            {
                return indexed;
            }

            var summaries = LoadArchivedRunSummaries();
            PersistRunIndex(summaries);
            return summaries;
        }
    }

    public void WarmRunIndexIfMissing()
    {
        PruneExpiredArchivedRuns();
        if (File.Exists(_runIndexPath) && new FileInfo(_runIndexPath).Length > 0)
        {
            return;
        }

        try
        {
            lock (_runIndexLock)
            {
                if (File.Exists(_runIndexPath) && new FileInfo(_runIndexPath).Length > 0)
                {
                    return;
                }

                PersistRunIndex(LoadArchivedRunSummaries());
            }
            PruneExpiredArchivedRuns(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to warm AutoTag run index.");
        }
    }

    private IReadOnlyList<AutoTagRunSummary> TryLoadRunIndex()
    {
        try
        {
            if (!File.Exists(_runIndexPath))
            {
                return Array.Empty<AutoTagRunSummary>();
            }

            var json = File.ReadAllText(_runIndexPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<AutoTagRunSummary>();
            }

            var document = JsonSerializer.Deserialize<AutoTagRunIndexDocument>(json, _jsonOptions);
            return NormalizeRunIndexSummaries(document?.Runs ?? new List<AutoTagRunSummary>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load AutoTag run index.");
            var summaries = LoadArchivedRunSummaries();
            PersistRunIndex(summaries);
            return summaries;
        }
    }

    private void UpdateRunIndex(AutoTagRunSummary summary, bool force = false)
    {
        if (!force && !ShouldUpdateRunIndex(summary))
        {
            return;
        }

        lock (_runIndexLock)
        {
            var summaries = TryLoadRunIndex()
                .Where(run => !string.Equals(run.Id, summary.Id, StringComparison.OrdinalIgnoreCase))
                .Append(summary)
                .ToList();
            PersistRunIndex(summaries);
        }

        InvalidateArchivedRunSummariesCache();
        _activitiesRealtime.PublishAutoTagRunChanged(summary);
        PruneExpiredArchivedRuns();
    }

    private void PersistRunIndex(IReadOnlyCollection<AutoTagRunSummary> summaries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_runIndexPath) ?? _historyDir);
            var document = new AutoTagRunIndexDocument
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Runs = NormalizeRunIndexSummaries(summaries).ToList()
            };
            File.WriteAllText(
                _runIndexPath,
                JsonSerializer.Serialize(document, _jsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist AutoTag run index.");
        }
    }

    private string GetRunIndexGroupKey(AutoTagRunSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.ResumeFromJobId))
        {
            return summary.Id;
        }

        var rootId = ResolveResumeRootJobId(summary.Id, summary.ResumeFromJobId);
        return string.IsNullOrWhiteSpace(rootId) ? summary.Id : rootId;
    }

    private static DateTimeOffset? ResolveRunHistoryDate(AutoTagJob job)
    {
        if (!IsEnhancementRunIntent(job.RunIntent)
            && !IsManualEnrichmentRunIntent(job.RunIntent))
        {
            return null;
        }

        return ResolveLastActivityTimestamp(job);
    }

    private List<string> ReadRunLogLines(string jobId)
    {
        try
        {
            var candidatePaths = EnumerateRunFileCandidates(jobId, "autotag.log").ToList();
            if (candidatePaths.Count == 0)
            {
                var fallbackPath = GetRunLogPath(jobId);
                var repairedMissingArchive = TryRepairArchivedLogsFromJob(jobId, fallbackPath);
                return repairedMissingArchive.Count > 0 ? repairedMissingArchive : new List<string>();
            }

            var archived = candidatePaths
                .Select(path => File.ReadAllLines(path, Encoding.UTF8)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList())
                .OrderByDescending(lines => lines.Count)
                .FirstOrDefault() ?? new List<string>();
            if (archived.Count > 0)
            {
                return archived;
            }

            var repaired = TryRepairArchivedLogsFromJob(jobId, GetRunLogPath(jobId));
            return repaired.Count > 0 ? repaired : archived;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag logs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<string>();
        }
    }

    private string GetRunHistoryDirectory(string jobId) => Path.Join(_historyDir, jobId);

    private string GetRunLogPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "autotag.log");

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
}
