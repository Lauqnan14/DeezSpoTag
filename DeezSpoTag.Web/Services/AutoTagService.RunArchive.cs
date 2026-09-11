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

    private void InitializeRunArchive(AutoTagJob job)
    {
        try
        {
            Directory.CreateDirectory(GetRunHistoryDirectory(job.Id));
            SaveRunSummary(job);

            var logPath = GetRunLogPath(job.Id);
            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, string.Empty, new UTF8Encoding(false));
            }

            var statusPath = GetRunStatusHistoryPath(job.Id);
            if (!File.Exists(statusPath))
            {
                File.WriteAllText(statusPath, string.Empty, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to initialize AutoTag run archive for {JobId}", job.Id);
            }
        }
    }

    private IReadOnlyList<AutoTagRunSummary> GetArchivedRunSummaries()
    {
        PruneExpiredArchivedRuns();
        lock (_archivedRunSummariesCacheLock)
        {
            if (_archivedRunSummariesCache is not null && _archivedRunSummariesCacheExpiresUtc > DateTimeOffset.UtcNow)
            {
                return _archivedRunSummariesCache;
            }

            var summaries = LoadRunIndexSummaries();
            _archivedRunSummariesCache = summaries;
            _archivedRunSummariesCacheExpiresUtc = DateTimeOffset.UtcNow.Add(ArchivedRunSummariesCacheTtl);
            return summaries;
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

    private bool ShouldUpdateRunIndex(AutoTagRunSummary summary)
    {
        if (IsTerminalRunStatus(summary.Status))
        {
            _lastRunIndexUpdateUtc[summary.Id] = DateTimeOffset.UtcNow;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var lastUpdate = _lastRunIndexUpdateUtc.GetOrAdd(summary.Id, now);
        if (lastUpdate == now || now - lastUpdate < RunIndexUpdateInterval)
        {
            return lastUpdate == now;
        }

        _lastRunIndexUpdateUtc[summary.Id] = now;
        return true;
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

    private void PruneExpiredArchivedRuns(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastArchivedRunPruneUtc < ArchivedRunPruneInterval)
        {
            return;
        }

        lock (_archivedRunPruneLock)
        {
            now = DateTimeOffset.UtcNow;
            if (!force && now - _lastArchivedRunPruneUtc < ArchivedRunPruneInterval)
            {
                return;
            }

            _lastArchivedRunPruneUtc = now;
            var cutoffUtc = now.Subtract(ResolveArchivedRunRetentionPeriod());
            try
            {
                var summaries = TryLoadRunIndex().ToList();
                if (summaries.Count == 0)
                {
                    summaries = LoadArchivedRunSummaries().ToList();
                }

                var retained = new List<AutoTagRunSummary>();
                var expired = new List<AutoTagRunSummary>();
                foreach (var summary in summaries)
                {
                    if (IsExpiredArchivedRun(summary, cutoffUtc))
                    {
                        expired.Add(summary);
                    }
                    else
                    {
                        retained.Add(summary);
                    }
                }

                foreach (var summary in expired)
                {
                    DeleteArchivedRunFiles(summary.Id);
                }

                if (expired.Count > 0)
                {
                    lock (_runIndexLock)
                    {
                        PersistRunIndex(retained);
                    }
                }

                PruneOrphanedArchivedRunArtifacts(cutoffUtc);
                InvalidateArchivedRunSummariesCache();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to prune expired AutoTag run history.");
            }
        }
    }

    private TimeSpan ResolveArchivedRunRetentionPeriod()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var days = settings.AutoTagHistoryRetentionDays;
            if (days < 1 || days > 365)
            {
                days = new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings().AutoTagHistoryRetentionDays;
            }

            return TimeSpan.FromDays(days);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to resolve AutoTag history retention; using default.");
            }

            return TimeSpan.FromDays(new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings().AutoTagHistoryRetentionDays);
        }
    }

    private bool IsExpiredArchivedRun(AutoTagRunSummary summary, DateTimeOffset cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(summary.Id) || _activeJobIds.ContainsKey(summary.Id))
        {
            return false;
        }

        return GetRunHistoryTimestamp(summary).ToUniversalTime() < cutoffUtc;
    }

    private void DeleteArchivedRunFiles(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        var normalizedJobId = Path.GetFileName(jobId.Trim());
        if (string.IsNullOrWhiteSpace(normalizedJobId))
        {
            return;
        }

        foreach (var root in EnumerateHistoryRoots())
        {
            TryDeleteDirectory(Path.Join(root, normalizedJobId));
        }

        TryDeleteFile(Path.Join(_jobsDir, normalizedJobId + ".json"));
        _archiveLocks.TryRemove(normalizedJobId, out _);
        _lastRunIndexUpdateUtc.TryRemove(normalizedJobId, out _);
    }

    private void PruneOrphanedArchivedRunArtifacts(DateTimeOffset cutoffUtc)
    {
        var retainedIds = new HashSet<string>(
            TryLoadRunIndex()
                .Select(summary => summary.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateHistoryRoots())
        {
            PruneOrphanedHistoryDirectories(root, retainedIds, cutoffUtc);
        }

        PruneOrphanedJobSnapshots(retainedIds, cutoffUtc);
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

    private void PruneOrphanedJobSnapshots(HashSet<string> retainedIds, DateTimeOffset cutoffUtc)
    {
        try
        {
            if (!Directory.Exists(_jobsDir))
            {
                return;
            }

            foreach (var jobPath in Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern))
            {
                var jobId = Path.GetFileNameWithoutExtension(jobPath);
                if (string.IsNullOrWhiteSpace(jobId)
                    || _activeJobIds.ContainsKey(jobId)
                    || retainedIds.Contains(jobId))
                {
                    continue;
                }

                if (GetFileSystemTimestampUtc(jobPath) < cutoffUtc)
                {
                    TryDeleteFile(jobPath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to prune orphaned AutoTag job snapshots.");
            }
        }
    }

    private static DateTimeOffset GetFileSystemTimestampUtc(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.GetLastWriteTimeUtc(path);
            }

            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Use a current timestamp when the filesystem cannot provide one so pruning stays conservative.
        }

        return DateTimeOffset.UtcNow;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to delete expired AutoTag history directory {Path}.", path);
            }
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to delete expired AutoTag history file {Path}.", path);
            }
        }
    }

    private List<AutoTagRunSummary> NormalizeRunIndexSummaries(IEnumerable<AutoTagRunSummary> summaries)
    {
        return summaries
            .Where(static summary => !string.IsNullOrWhiteSpace(summary.Id))
            .GroupBy(GetRunIndexGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(summary => summary.StartedAt).First())
            .OrderByDescending(static summary => summary.StartedAt)
            .ToList();
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

    private IReadOnlyList<AutoTagRunSummary> LoadArchivedRunSummaries()
    {
        try
        {
            var summaries = new Dictionary<string, AutoTagRunSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in EnumerateHistoryRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var runDir in Directory.EnumerateDirectories(root))
                {
                    var jobId = Path.GetFileName(runDir);
                    if (string.IsNullOrWhiteSpace(jobId) || summaries.ContainsKey(jobId))
                    {
                        continue;
                    }

                    var summaryPath = Path.Join(runDir, "summary.json");
                    var summary = LoadRunSummaryFromPath(summaryPath);
                    if (summary != null)
                    {
                        summaries[jobId] = summary;
                    }
                }
            }

            return summaries.Values
                .OrderByDescending(summary => summary.StartedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to enumerate archived AutoTag runs.");
            return Array.Empty<AutoTagRunSummary>();
        }
    }

    private void InvalidateArchivedRunSummariesCache()
    {
        lock (_archivedRunSummariesCacheLock)
        {
            _archivedRunSummariesCache = null;
            _archivedRunSummariesCacheExpiresUtc = DateTimeOffset.MinValue;
        }
    }

    private void AppendArchivedLog(string jobId, string line)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(jobId, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(jobId));
                File.AppendAllText(GetRunLogPath(jobId), line + Environment.NewLine, new UTF8Encoding(false));
                _archivedLogLineCounts.AddOrUpdate(jobId, 1, static (_, count) => count + 1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to append archived AutoTag log for {JobId}", jobId);
            }
        }
    }

    private void AppendArchivedStatus(string jobId, TaggingStatusSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(jobId, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(jobId));
                var json = JsonSerializer.Serialize(snapshot, _jsonCompactOptions);
                File.AppendAllText(GetRunStatusHistoryPath(jobId), json + Environment.NewLine, new UTF8Encoding(false));
                _archivedStatusEntryCounts.AddOrUpdate(jobId, 1, static (_, count) => count + 1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to append archived AutoTag status for {JobId}", jobId);
            }
        }
    }

    private void SaveRunSummary(AutoTagJob job)
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
                UpdateRunIndex(summary);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to save AutoTag run summary for {JobId}", job.Id);
            }
        }
    }

    private AutoTagRunSummary BuildRunSummary(AutoTagJob job)
    {
        return new AutoTagRunSummary
        {
            Id = job.Id,
            Status = job.Status,
            StartedAt = job.StartedAt,
            FinishedAt = job.FinishedAt,
            ExitCode = job.ExitCode,
            Error = job.Error,
            Progress = job.Progress,
            OkCount = job.OkCount,
            ErrorCount = job.ErrorCount,
            ReviewCount = job.ReviewCount,
            SkippedCount = job.SkippedCount,
            RootPath = job.RootPath,
            Trigger = string.IsNullOrWhiteSpace(job.Trigger) ? AutoTagLiterals.ManualTrigger : job.Trigger,
            RunIntent = NormalizeRunIntent(job.RunIntent),
            ProfileId = job.ProfileId,
            ProfileName = job.ProfileName,
            EnhancementFeature = job.EnhancementFeature,
            EnhancementGroupId = job.EnhancementGroupId,
            CurrentPhase = job.CurrentPhase,
            CurrentBatch = job.CurrentBatch,
            BatchCount = job.BatchCount,
            BatchProcessed = job.BatchProcessed,
            BatchSize = job.BatchSize,
            ProcessedItems = job.ProcessedItems,
            TotalItems = job.TotalItems,
            TargetReason = job.TargetReason,
            TargetRequested = job.TargetRequested,
            TargetUsable = job.TargetUsable,
            EnhancementManifestPath = job.EnhancementManifestPath,
            AutoMoveSummary = job.AutoMoveSummary?.Clone(),
            ResumeFromJobId = string.IsNullOrWhiteSpace(job.ResumeFromJobId) ? null : job.ResumeFromJobId,
            HistoryDate = ResolveRunHistoryDate(job),
            LogCount = GetArchivedLogCount(job.Id, job.Logs.Count),
            StatusEntryCount = GetArchivedStatusCount(job.Id, job.StatusHistory.Count)
        };
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

    private AutoTagRunSummary? LoadRunSummary(string jobId)
    {
        try
        {
            var path = ResolveRunFilePath(jobId, "summary.json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return LoadRunSummaryFromPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load AutoTag run summary for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return null;
        }
    }

    private AutoTagRunSummary? LoadRunSummaryFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        var summary = JsonSerializer.Deserialize<AutoTagRunSummary>(json, _jsonOptions);
        if (summary == null)
        {
            return null;
        }

        // Archived run summaries are immutable history: statuses/errors are never rewritten on load.
        if (string.IsNullOrWhiteSpace(summary.ResumeFromJobId))
        {
            summary.ResumeFromJobId = TryReadJobResumeFromJobId(summary.Id);
        }

        return summary;
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

    private List<TaggingStatusSnapshot> ReadRunStatusHistory(string jobId)
    {
        try
        {
            var candidatePaths = EnumerateRunFileCandidates(jobId, "status-history.ndjson").ToList();
            if (candidatePaths.Count == 0)
            {
                var fallbackPath = GetRunStatusHistoryPath(jobId);
                var repairedMissingArchive = TryRepairArchivedStatusFromJob(jobId, fallbackPath);
                return repairedMissingArchive.Count > 0 ? repairedMissingArchive : new List<TaggingStatusSnapshot>();
            }

            List<TaggingStatusSnapshot> entries = new();
            var skippedMalformed = 0;
            foreach (var path in candidatePaths)
            {
                var (candidateEntries, candidateSkippedMalformed) = ParseStatusHistoryEntries(path);
                if (candidateEntries.Count > entries.Count)
                {
                    entries = candidateEntries;
                    skippedMalformed = candidateSkippedMalformed;
                }
            }
            if (entries.Count == 0)
            {
                var repaired = TryRepairArchivedStatusFromJob(jobId, GetRunStatusHistoryPath(jobId));
                if (repaired.Count > 0)
                {
                    return repaired;
                }
            }

            if (skippedMalformed > 0)
            {
                _logger.LogWarning(
                    "Skipped {SkippedMalformed} malformed AutoTag status entries for {JobId} while reading archive history.",
                    skippedMalformed,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag status history for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<TaggingStatusSnapshot>();
        }
    }

    private List<string> TryRepairArchivedLogsFromJob(string jobId, string archiveLogPath)
    {
        try
        {
            var job = LoadJob(jobId);
            var logs = (job?.Logs ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (logs.Count == 0)
            {
                return new List<string>();
            }

            var archiveDirectory = Path.GetDirectoryName(archiveLogPath);
            if (!string.IsNullOrWhiteSpace(archiveDirectory))
            {
                Directory.CreateDirectory(archiveDirectory);
            }
            File.WriteAllLines(archiveLogPath, logs, new UTF8Encoding(false));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recovered archived AutoTag logs for {JobId} from job snapshot ({Count} lines).",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId),
                    logs.Count);
            }
            return logs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag logs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<string>();
        }
    }

    private List<TaggingStatusSnapshot> TryRepairArchivedStatusFromJob(string jobId, string archiveStatusPath)
    {
        try
        {
            var job = LoadJob(jobId);
            var statusHistory = (job?.StatusHistory ?? new List<TaggingStatusSnapshot>()).ToList();
            if (statusHistory.Count == 0)
            {
                return new List<TaggingStatusSnapshot>();
            }

            var archiveDirectory = Path.GetDirectoryName(archiveStatusPath);
            if (!string.IsNullOrWhiteSpace(archiveDirectory))
            {
                Directory.CreateDirectory(archiveDirectory);
            }
            var statusLines = statusHistory
                .Select(entry => JsonSerializer.Serialize(entry, _jsonCompactOptions))
                .ToList();
            File.WriteAllLines(archiveStatusPath, statusLines, new UTF8Encoding(false));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recovered archived AutoTag status history for {JobId} from job snapshot ({Count} entries).",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId),
                    statusHistory.Count);
            }
            return statusHistory;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag status history for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<TaggingStatusSnapshot>();
        }
    }

    /// <summary>
    /// Archived log-line count. Seeded from the file once per job, then maintained
    /// incrementally by <see cref="AppendArchivedLog"/> — previously this re-read the
    /// entire log file on every per-file status and log line.
    /// </summary>
    private int GetArchivedLogCount(string jobId, int fallback)
    {
        try
        {
            return _archivedLogLineCounts.GetOrAdd(jobId, static (_, ctx) => CountArchiveFileLines(ctx.path) ?? ctx.fallback, (path: GetRunLogPath(jobId), fallback));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return fallback;
        }
    }

    /// <summary>
    /// Archived status-entry count. Seeded from the file once per job, then maintained
    /// incrementally by <see cref="AppendArchivedStatus"/>.
    /// </summary>
    private int GetArchivedStatusCount(string jobId, int fallback)
    {
        try
        {
            return _archivedStatusEntryCounts.GetOrAdd(jobId, static (_, ctx) => CountArchiveFileLines(ctx.path) ?? ctx.fallback, (path: GetRunStatusHistoryPath(jobId), fallback));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return fallback;
        }
    }

    private static int? CountArchiveFileLines(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadLines(path, Encoding.UTF8).Count();
    }

    private string GetRunHistoryDirectory(string jobId) => Path.Join(_historyDir, jobId);

    private string GetRunSummaryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "summary.json");

    private string GetRunLogPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "autotag.log");

    private string GetRunStatusHistoryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "status-history.ndjson");

    private string GetRunTagDiffsPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "tag-diffs.json");

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

    private void SaveArchivedTagDiffs(string jobId, Dictionary<string, AutoTagTagDiff>? tagDiffs)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(GetRunHistoryDirectory(jobId));
            var payload = (tagDiffs == null || tagDiffs.Count == 0)
                ? new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AutoTagTagDiff>(tagDiffs, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(
                GetRunTagDiffsPath(jobId),
                JsonSerializer.Serialize(payload, _jsonOptions),
                new UTF8Encoding(false));
            TryDeleteDirectory(GetRunTagDiffCheckpointDirectory(jobId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist archived AutoTag tag diffs for {JobId}", jobId);
            }
        }
    }

    private Dictionary<string, AutoTagTagDiff> LoadPersistedTagDiffs(string jobId)
    {
        var resolved = new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = ResolveRunFilePath(jobId, "tag-diffs.json");
            if (!string.IsNullOrWhiteSpace(path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(
                    File.ReadAllText(path, Encoding.UTF8),
                    _jsonOptions);
                if (parsed != null)
                {
                    foreach (var (pathKey, diff) in parsed)
                    {
                        resolved[pathKey] = diff;
                    }
                }
            }

            var checkpointDirectory = GetRunTagDiffCheckpointDirectory(jobId);
            if (Directory.Exists(checkpointDirectory))
            {
                foreach (var checkpointPath in Directory.EnumerateFiles(checkpointDirectory, "*.json"))
                {
                    var checkpoint = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(
                        File.ReadAllText(checkpointPath, Encoding.UTF8),
                        _jsonOptions);
                    if (checkpoint == null)
                    {
                        continue;
                    }
                    foreach (var (pathKey, diff) in checkpoint)
                    {
                        resolved[pathKey] = diff;
                    }
                }
            }
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load persisted AutoTag tag diffs for {JobId}", jobId);
            }
            return resolved;
        }
    }

    private Dictionary<string, AutoTagTagDiff> ReadRunTagDiffs(string jobId)
    {
        try
        {
            var path = ResolveRunFilePath(jobId, "tag-diffs.json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(json, _jsonOptions);
            var resolved = parsed != null
                ? new Dictionary<string, AutoTagTagDiff>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            if (resolved.Count > 0)
            {
                return resolved;
            }

            var repaired = TryRepairArchivedTagDiffsFromJob(jobId, path);
            return repaired.Count > 0 ? repaired : resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag tag diffs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Dictionary<string, AutoTagTagDiff> TryRepairArchivedTagDiffsFromJob(string jobId, string archiveTagDiffPath)
    {
        try
        {
            var job = LoadJob(jobId);
            if (job?.TagDiffs == null || job.TagDiffs.Count == 0)
            {
                return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            }

            var repaired = new Dictionary<string, AutoTagTagDiff>(job.TagDiffs, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(
                archiveTagDiffPath,
                JsonSerializer.Serialize(repaired, _jsonOptions),
                new UTF8Encoding(false));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recovered archived AutoTag tag diffs for {JobId} from job snapshot ({Count} entries).",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId),
                    repaired.Count);
            }
            return repaired;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag tag diffs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private (List<TaggingStatusSnapshot> Entries, int SkippedMalformed) ParseStatusHistoryEntries(string path)
    {
        var entries = new List<TaggingStatusSnapshot>();
        var skippedMalformed = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8)
            .Select(static rawLine => rawLine?.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<TaggingStatusSnapshot>(line!, _jsonOptions);
                if (entry != null)
                {
                    entries.Add(entry);
                }
                else
                {
                    skippedMalformed += 1;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                RecordMalformedHistoryEntry(ref skippedMalformed);
            }
        }

        return (entries, skippedMalformed);
    }
}
