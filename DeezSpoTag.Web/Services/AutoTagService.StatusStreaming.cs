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

    private AutoTagJob? TryCreateBlockedJobForTriggerPolicy(
        string normalizedPath,
        string normalizedTrigger,
        string normalizedRunIntent,
        string? profileId,
        string? profileName)
    {
        if (!IsEnhancementRunIntent(normalizedRunIntent)
            || IsAllowedEnhancementTrigger(normalizedTrigger))
        {
            return null;
        }

        var blockedJob = CreateBlockedJob(
            $"Enhancement run blocked: trigger '{normalizedTrigger}' is not allowed. Enhancement runs must be started manually or by schedule.",
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        AppendActivityLog(blockedJob.Id, "autotag blocked: invalid enhancement trigger");
        _logger.LogWarning(
            "AutoTag enhancement run blocked by trigger policy. intent={Intent}, trigger={Trigger}, path={Path}",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedRunIntent),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedTrigger),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedPath));
        return blockedJob;
    }

    private AutoTagJob? TryCreateBlockedJobForActiveJobPolicy(
        string normalizedPath,
        string normalizedTrigger,
        string normalizedRunIntent,
        string? profileId,
        string? profileName)
    {
        var activeJobId = _activeJobIds.Keys.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(activeJobId))
        {
            return null;
        }

        var blockedJob = CreateBlockedJob(
            $"AutoTag run blocked: another AutoTag job is already running ({activeJobId}).",
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        AppendActivityLog(blockedJob.Id, "autotag blocked: another job is already running");
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "AutoTag run blocked because another job is already running. activeJobId={ActiveJobId}, intent={Intent}, trigger={Trigger}, path={Path}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(activeJobId),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedRunIntent),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedTrigger),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedPath));
        }
        return blockedJob;
    }

    private async Task<AutoTagJob?> TryCreateBlockedJobForScopePolicyAsync(
        string normalizedPath,
        string normalizedTrigger,
        string normalizedRunIntent,
        string? profileId,
        string? profileName)
    {
        var runIntentScopeError = await ValidateRunIntentScopeAsync(normalizedPath, normalizedRunIntent, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(runIntentScopeError))
        {
            return null;
        }

        var blockedJob = CreateBlockedJob(
            runIntentScopeError,
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        AppendActivityLog(blockedJob.Id, $"autotag blocked: {runIntentScopeError}");
        _logger.LogWarning(
            "AutoTag blocked by scope policy. intent={Intent}, trigger={Trigger}, path={Path}, reason={Reason}",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedRunIntent),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedTrigger),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedPath),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(runIntentScopeError));
        return blockedJob;
    }

    private AutoTagJob CreateBlockedJob(
        string error,
        string rootPath,
        string trigger,
        string runIntent,
        string? profileId,
        string? profileName)
    {
        var blockedJob = new AutoTagJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = AutoTagLiterals.BlockedStatus,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Error = error,
            RootPath = rootPath,
            Trigger = trigger,
            RunIntent = runIntent,
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim()
        };

        SaveJob(blockedJob);
        TrySaveLastJobId(blockedJob.Id);
        Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(blockedJob));
        return blockedJob;
    }

    private AutoTagJob CreateSkippedJob(
        string message,
        string rootPath,
        string trigger,
        string runIntent,
        string? profileId,
        string? profileName)
    {
        var skippedJob = new AutoTagJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = AutoTagLiterals.SkippedStatus,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Error = message,
            RootPath = rootPath,
            Trigger = trigger,
            RunIntent = runIntent,
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim()
        };

        SaveJob(skippedJob);
        TrySaveLastJobId(skippedJob.Id);
        Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(skippedJob));
        AppendActivityLog(skippedJob.Id, $"autotag skipped: {message}");
        return skippedJob;
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

    private void AppendStatusHistory(AutoTagJob job, TaggingStatusWrap status)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new TaggingStatusSnapshot
        {
            Timestamp = timestamp,
            Status = status
        };
        job.LastActivityAt = timestamp;
        lock (job.StatusHistory)
        {
            job.StatusHistory.Add(snapshot);
            if (job.StatusHistory.Count > 300)
            {
                job.StatusHistory.RemoveRange(0, job.StatusHistory.Count - 300);
            }
        }
        AppendArchivedStatus(job.Id, snapshot);
    }

    private void AppendLog(AutoTagJob job, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var cleaned = AnsiRegex.Replace(line, string.Empty);
        job.LastActivityAt = DateTimeOffset.UtcNow;
        TrackStartedPlatform(job, cleaned);
        lock (job.Logs)
        {
            job.Logs.Add(cleaned);
            if (job.Logs.Count > 200)
            {
                job.Logs.RemoveRange(0, job.Logs.Count - 200);
            }
        }
        AppendActivityLog(job.Id, cleaned);
        AppendArchivedLog(job.Id, cleaned);
        SaveJobThrottled(job);
    }

    private void AppendActivityLog(string jobId, string line)
    {
        try
        {
            var level = ResolveLogLevel(line);
            var cleaned = AnsiRegex.Replace(line, string.Empty).Trim();
            cleaned = StripLinePrefix(cleaned);
            if (string.IsNullOrEmpty(cleaned))
            {
                return;
            }
            if (_lastActivityLines.TryGetValue(jobId, out var lastLine) &&
                string.Equals(lastLine, cleaned, StringComparison.Ordinal))
            {
                return;
            }
            _lastActivityLines[jobId] = cleaned;
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                level,
                $"[autotag] {cleaned}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to add AutoTag line to activity logs.");
        }
    }

    private void AppendPlatformSummary(AutoTagJob job)
    {
        if (job.StartedPlatforms.Count == 0)
        {
            return;
        }

        var summary = $"{AutoTagProtocol.LogMarker} platforms started: {string.Join(", ", job.StartedPlatforms)}";
        AppendActivityLog(job.Id, summary);
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

    private string GetRunStatusHistoryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "status-history.ndjson");

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
