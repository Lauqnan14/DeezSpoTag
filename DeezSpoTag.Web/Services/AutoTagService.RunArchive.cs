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

    public IReadOnlyList<AutoTagRunDaySummary> GetArchivedRunCalendar(int year, int month)
    {
        var summaries = GetArchivedRunSummaries()
            .Where(summary => GetRunDate(GetRunHistoryTimestamp(summary)).Year == year
                && GetRunDate(GetRunHistoryTimestamp(summary)).Month == month)
            .OrderBy(summary => summary.StartedAt)
            .ToList();

        return summaries
            .GroupBy(summary => GetRunDateToken(GetRunHistoryTimestamp(summary)))
            .Select(group => new AutoTagRunDaySummary
            {
                Date = group.Key,
                RunCount = group.Count(),
                Runs = group.OrderByDescending(run => run.StartedAt).ToList()
            })
            .OrderBy(day => day.Date, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<AutoTagRunSummary> GetArchivedRunsByDate(DateOnly date)
    {
        var token = date.ToString("yyyy-MM-dd");
        return GetArchivedRunSummaries()
            .Where(summary => string.Equals(GetRunDateToken(GetRunHistoryTimestamp(summary)), token, StringComparison.Ordinal))
            .OrderByDescending(summary => summary.StartedAt)
            .ToList();
    }

    public AutoTagRunArchive? GetArchivedRun(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var archiveLock = _archiveLocks.GetOrAdd(id, static _ => new object());
        lock (archiveLock)
        {
            var summary = LoadRunSummary(id);
            if (summary == null)
            {
                return null;
            }
            if (IsExpiredArchivedRun(summary, DateTimeOffset.UtcNow.Subtract(ResolveArchivedRunRetentionPeriod())))
            {
                DeleteArchivedRunFiles(summary.Id);
                PruneExpiredArchivedRuns(force: true);
                return null;
            }

            var logs = ReadRunLogLines(id);
            var statusHistory = ReadRunStatusHistory(id);
            var job = (logs.Count == 0 || statusHistory.Count == 0)
                ? GetJob(id) ?? LoadJob(id)
                : null;
            if (logs.Count == 0 && summary.LogCount > 0 && job?.Logs.Count > 0)
            {
                logs = job.Logs
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                if (logs.Count > 0)
                {
                    _ = TryRepairArchivedLogsFromJob(id, GetRunLogPath(id));
                }
            }
            if (statusHistory.Count == 0 && summary.StatusEntryCount > 0 && job?.StatusHistory.Count > 0)
            {
                statusHistory = job.StatusHistory.ToList();
                if (statusHistory.Count > 0)
                {
                    _ = TryRepairArchivedStatusFromJob(id, GetRunStatusHistoryPath(id));
                }
            }

            return new AutoTagRunArchive
            {
                Summary = summary,
                Logs = logs,
                StatusHistory = statusHistory
            };
        }
    }

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
}
