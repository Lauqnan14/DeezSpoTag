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

    public sealed record StartJobOptions(
        string Trigger = AutoTagLiterals.ManualTrigger,
        TechnicalTagSettings? TechnicalOverride = null,
        string? ProfileId = null,
        string? ProfileName = null,
        string? RunIntent = null,
        FolderStructureSettings? FolderStructureOverride = null,
        string? EnhancementFeature = null,
        string? EnhancementGroupId = null,
        string? ResumeFromJobId = null);

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
            EnhancementGroupId = string.IsNullOrWhiteSpace(options.EnhancementGroupId) ? null : options.EnhancementGroupId.Trim(),
            ResumeCheckpoint = resumeSeed?.Checkpoint,
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

    private async Task PrepareRuntimeConfigAndRunJobAsync(
        AutoTagJob job,
        string normalizedPath,
        string configJson,
        StartJobOptions options)
    {
        try
        {
            AppendLog(job, "runtime config preparing");
            var runtimeConfigJson = SanitizeConfigJson(configJson);
            runtimeConfigJson = await InjectPlatformDefaultsAsync(runtimeConfigJson);
            runtimeConfigJson = await InjectPlatformAuthAsync(runtimeConfigJson);
            runtimeConfigJson = InjectRunTrigger(runtimeConfigJson, job.Trigger);
            runtimeConfigJson = InjectProfileRuntimeSettings(
                runtimeConfigJson,
                options.TechnicalOverride,
                options.FolderStructureOverride,
                job.ProfileId,
                job.ProfileName);
            var persistedConfigJson = RedactSensitiveConfigJson(runtimeConfigJson);
            var runtimeConfigPath = WriteRuntimeConfigFile(job.Id, "base", runtimeConfigJson);
            TrySaveLastConfig(persistedConfigJson);
            // Persist the redacted config on the job so POST /jobs/{id}/resume can restart
            // this exact scope even if runtime-config files were cleaned up meanwhile.
            if (!string.Equals(job.ResumeConfigJson, persistedConfigJson, StringComparison.Ordinal))
            {
                job.ResumeConfigJson = persistedConfigJson;
                SaveJob(job);
            }
            AppendLog(job, "runtime config ready");

            await RunJobAsync(job, normalizedPath, runtimeConfigPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AutoTag runtime config preparation failed for job {JobId}.", job.Id);
            job.Status = AutoTagLiterals.FailedStatus;
            job.Error = $"Runtime config preparation failed: {ex.Message}";
            job.ExitCode = 1;
            job.FinishedAt = DateTimeOffset.UtcNow;
            AppendLog(job, job.Error);
            SaveJob(job);
            AppendActivityLog(job.Id, "autotag failed: runtime config preparation");
            NotifyCompleted(job);
            _activeJobStages.TryRemove(job.Id, out _);
            _activeJobIds.TryRemove(job.Id, out _);
            Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(job));
            _jobs.TryRemove(job.Id, out _);
            _lastActivityLines.TryRemove(job.Id, out _);
            _archiveLocks.TryRemove(job.Id, out _);
            _lastRunIndexUpdateUtc.TryRemove(job.Id, out _);
        }
    }

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

    private async Task<bool> ShouldSkipForActiveDownloadsAsync()
    {
        return await _queueRepository.HasActiveDownloadsAsync();
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

    private static void HydrateResumeJob(AutoTagJob target, AutoTagJob source)
    {
        target.OkCount = source.OkCount;
        target.ErrorCount = source.ErrorCount;
        target.ReviewCount = source.ReviewCount;
        target.SkippedCount = source.SkippedCount;
        target.Progress = source.Progress;
        target.EnhancementFeature ??= source.EnhancementFeature;
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

    private async Task<string?> ValidateRunIntentScopeAsync(
        string normalizedPath,
        string runIntent,
        CancellationToken cancellationToken)
    {
        if (string.Equals(runIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            if (!ConfiguredDownloadRootResolver.TryResolve(
                    _settingsService,
                    "download location",
                    "download location is not configured.",
                    out var downloadRoot,
                    out var error))
            {
                return $"Download enrichment run blocked: {error}";
            }

            if (!IsPathUnderRoot(normalizedPath, downloadRoot))
            {
                return $"Download enrichment run blocked: path '{normalizedPath}' is outside configured download location '{downloadRoot}'.";
            }

            return null;
        }

        if (!IsEnhancementRunIntent(runIntent))
        {
            return null;
        }

        var libraryRoots = await ResolveAllowedLibraryRootsAsync(cancellationToken);
        if (libraryRoots.Count == 0)
        {
            return "Enhancement run blocked: no accessible library folders are configured.";
        }

        if (!libraryRoots.Any(root => IsPathUnderRoot(normalizedPath, root)))
        {
            return $"Enhancement run blocked: path '{normalizedPath}' is outside configured library roots.";
        }

        if (ConfiguredDownloadRootResolver.TryResolve(
                _settingsService,
                "download location",
                "download location is not configured.",
                out var configuredDownloadRoot,
                out _)
            && IsPathUnderRoot(normalizedPath, configuredDownloadRoot))
        {
            return $"Enhancement run blocked: path '{normalizedPath}' is inside download location '{configuredDownloadRoot}'.";
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> ResolveAllowedLibraryRootsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var folders = _libraryRepository.IsConfigured
                ? await _libraryRepository.GetFoldersAsync(cancellationToken)
                : await _activityLog.GetFoldersAsync();
            return LibraryFolderRootResolver.ResolveAccessibleRoots(folders);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LibraryFolderRootResolver.ResolveAccessibleRoots(await _activityLog.GetFoldersAsync());
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

        if (job.ResumeCheckpoint == null)
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

    private static string NormalizePathForJob(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Trim();
        }
    }

    private static bool HasEligibleInputFiles(string rootPath, string configJson)
    {
        var normalizedRoot = NormalizeRootPath(rootPath);
        if (normalizedRoot == null)
        {
            return false;
        }

        if (!TryParseAutoTagConfig(configJson, out var root))
        {
            // Avoid suppressing valid runs due to config parse issues.
            return true;
        }

        var includeSubfolders = ReadBool(root, AutoTagLiterals.IncludeSubfoldersKey) ?? true;
        var targetFiles = ReadStringList(root, AutoTagLiterals.TargetFilesKey);
        if (targetFiles.Count > 0)
        {
            return HasEligibleTargetFiles(targetFiles, normalizedRoot);
        }

        return HasEligibleFilesInDirectory(normalizedRoot, includeSubfolders);
    }

    private static string? NormalizeRootPath(string rootPath)
    {
        var normalizedRoot = NormalizePathForJob(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
        {
            return null;
        }

        return normalizedRoot;
    }

    private static bool TryParseAutoTagConfig(string configJson, out JsonObject root)
    {
        root = new JsonObject();
        try
        {
            root = JsonNode.Parse(configJson) as JsonObject ?? new JsonObject();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool HasEligibleTargetFiles(IEnumerable<string> targetFiles, string normalizedRoot)
    {
        foreach (var rawPath in targetFiles)
        {
            var candidate = NormalizePathForJob(rawPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsPathWithinScope(candidate, normalizedRoot)
                || !File.Exists(candidate))
            {
                continue;
            }

            var extension = Path.GetExtension(candidate);
            if (!string.IsNullOrWhiteSpace(extension) && EligibleAudioExtensions.Contains(extension))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEligibleFilesInDirectory(string normalizedRoot, bool includeSubfolders)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        return Directory.EnumerateFiles(normalizedRoot, "*", options)
            .Select(Path.GetExtension)
            .Any(extension => !string.IsNullOrWhiteSpace(extension) && EligibleAudioExtensions.Contains(extension));
    }

    private static bool IsPathWithinScope(string candidatePath, string scopePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(scopePath))
        {
            return false;
        }

        if (string.Equals(candidatePath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var scopeWithSeparator = scopePath.EndsWith(Path.DirectorySeparatorChar)
                                 || scopePath.EndsWith(Path.AltDirectorySeparatorChar)
            ? scopePath
            : scopePath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(scopeWithSeparator, StringComparison.OrdinalIgnoreCase);
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

    public AutoTagTagDiff? GetTagDiff(string jobId, string path, string? platform = null)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizeDiffPath(path);
        var job = GetJob(jobId) ?? LoadJob(jobId);
        if (job != null)
        {
            var diffFromJob = TryResolveTagDiff(job.TagDiffs, normalized, path, platform);
            if (diffFromJob != null)
            {
                return diffFromJob;
            }
        }

        var diffFromArchive = TryResolveTagDiff(ReadRunTagDiffs(jobId), normalized, path, platform);
        if (diffFromArchive != null)
        {
            return diffFromArchive;
        }

        if (job == null)
        {
            return null;
        }

        // Fallback for older jobs: if no diff snapshots were persisted, capture a current snapshot
        // so the UI can still display tag data for troubleshooting.
        try
        {
            var current = BuildTagSnapshot(normalized);
            var fallback = new AutoTagTagDiff
            {
                Path = normalized,
                LastPlatform = null,
                Before = null,
                After = current
            };
            lock (job.TagDiffs)
            {
                job.TagDiffs[normalized] = fallback;
            }
            SaveJob(job);
            return SelectRequestedPlatformDiff(fallback, platform);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AutoTag diff fallback snapshot failed for Path");
        }

        return null;
    }

    private static AutoTagTagDiff? TryResolveTagDiff(
        Dictionary<string, AutoTagTagDiff>? tagDiffs,
        string normalizedPath,
        string rawPath,
        string? platform)
    {
        if (tagDiffs == null || tagDiffs.Count == 0)
        {
            return null;
        }

        if (tagDiffs.TryGetValue(normalizedPath, out var normalized))
        {
            return SelectRequestedPlatformDiff(normalized, platform);
        }

        if (tagDiffs.TryGetValue(rawPath, out var raw))
        {
            return SelectRequestedPlatformDiff(raw, platform);
        }

        return null;
    }

    private static AutoTagTagDiff SelectRequestedPlatformDiff(AutoTagTagDiff stored, string? requestedPlatform)
    {
        if (string.IsNullOrWhiteSpace(requestedPlatform))
        {
            return CloneDiff(stored);
        }

        var completed = (stored.PlatformDiffs ?? new List<AutoTagPlatformDiffSnapshot>())
            .Where(step => step.After != null)
            .ToList();
        if (completed.Count == 0)
        {
            return CloneDiff(stored);
        }

        var targetIndex = completed.FindLastIndex(step =>
            string.Equals(step.Platform, requestedPlatform, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            return CloneDiff(stored);
        }

        var target = completed[targetIndex];
        var isFinal = targetIndex == completed.Count - 1;
        var baseSnapshot = stored.Before ?? completed[0].Before ?? target.Before;
        var cumulativeSteps = completed
            .Take(targetIndex + 1)
            .Select(ClonePlatformDiff)
            .ToList();

        var selected = new AutoTagTagDiff
        {
            Path = stored.Path,
            LastPlatform = target.Platform,
            TargetPlatform = target.Platform,
            IsFinalPlatformDiff = isFinal,
            BasePlatform = "original",
            Before = baseSnapshot,
            After = target.After,
            PlatformDiffs = cumulativeSteps
        };

        if (selected.After != null)
        {
            selected.RetainedSources = ComputeRetainedSources(
                selected.Before,
                selected.After,
                selected.PlatformDiffs);
        }

        return selected;
    }

    private static AutoTagTagDiff CloneDiff(AutoTagTagDiff source)
    {
        return new AutoTagTagDiff
        {
            Path = source.Path,
            LastPlatform = source.LastPlatform,
            BasePlatform = source.BasePlatform,
            TargetPlatform = source.TargetPlatform,
            IsFinalPlatformDiff = source.IsFinalPlatformDiff,
            Before = source.Before,
            After = source.After,
            RetainedSources = source.RetainedSources != null
                ? new Dictionary<string, string>(source.RetainedSources, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            PlatformDiffs = (source.PlatformDiffs ?? new List<AutoTagPlatformDiffSnapshot>())
                .Select(ClonePlatformDiff)
                .ToList()
        };
    }

    private static AutoTagPlatformDiffSnapshot ClonePlatformDiff(AutoTagPlatformDiffSnapshot source)
    {
        return new AutoTagPlatformDiffSnapshot
        {
            Platform = source.Platform,
            Status = source.Status,
            CapturedAt = source.CapturedAt,
            Before = source.Before,
            After = source.After
        };
    }

    private static Dictionary<string, string> ComputeRetainedSources(
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRetainedMetaSources(retained, baseline, finalSnapshot, completed);
        AddRetainedTagSources(retained, baseline, finalSnapshot, completed);
        return retained;
    }

    private static void AddRetainedMetaSources(
        Dictionary<string, string> retained,
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        foreach (var metaKey in DiffMetaKeys)
        {
            var source = ResolveValueSource(
                GetMetaFieldValue(finalSnapshot, metaKey),
                baseline is null ? null : GetMetaFieldValue(baseline, metaKey),
                completed,
                step => GetMetaFieldValue(step.After, metaKey),
                step => GetMetaFieldValue(step.Before, metaKey));
            if (!string.IsNullOrWhiteSpace(source))
            {
                retained[metaKey] = source;
            }
        }
    }

    private static void AddRetainedTagSources(
        Dictionary<string, string> retained,
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        var finalTags = finalSnapshot.Tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in finalTags.Keys)
        {
            finalTags.TryGetValue(key, out var finalTagValue);
            var baselineTagValue = baseline?.Tags != null && baseline.Tags.TryGetValue(key, out var value)
                ? value
                : null;
            var source = ResolveValueSource(
                finalTagValue,
                baselineTagValue,
                completed,
                step => step.After != null && step.After.Tags.TryGetValue(key, out var stepValue) ? stepValue : null,
                step => step.Before != null && step.Before.Tags.TryGetValue(key, out var stepValue) ? stepValue : null);
            if (!string.IsNullOrWhiteSpace(source))
            {
                retained[$"tag:{key.ToLowerInvariant()}"] = source;
            }
        }
    }

    private static string? ResolveValueSource<T>(
        T? finalValue,
        T? baselineValue,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        var mergedSources = ResolveMergedValueSources(
            finalValue,
            baselineValue,
            completed,
            afterSelector,
            beforeSelector);
        if (!string.IsNullOrWhiteSpace(mergedSources))
        {
            return mergedSources;
        }

        var normalizedFinal = NormalizeCompareValue(finalValue);
        if (string.IsNullOrEmpty(normalizedFinal))
        {
            return null;
        }

        var normalizedBaseline = NormalizeCompareValue(baselineValue);
        var (currentValue, currentSource) = ResolveCurrentValueAndSource(completed, normalizedBaseline, afterSelector);

        if (string.Equals(currentValue, normalizedFinal, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(currentSource))
            {
                return currentSource;
            }

            return string.Equals(normalizedBaseline, normalizedFinal, StringComparison.Ordinal)
                ? "original"
                : null;
        }

        return ResolveFallbackTransitionSource(
            normalizedFinal,
            normalizedBaseline,
            completed,
            afterSelector,
            beforeSelector);
    }

    private static (string CurrentValue, string? CurrentSource) ResolveCurrentValueAndSource<T>(
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        string normalizedBaseline,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector)
    {
        var currentValue = normalizedBaseline;
        string? currentSource = null;

        foreach (var step in completed)
        {
            var stepAfter = NormalizeCompareValue(afterSelector(step));
            if (string.Equals(stepAfter, currentValue, StringComparison.Ordinal))
            {
                continue;
            }

            currentValue = stepAfter;
            if (!string.IsNullOrWhiteSpace(step.Platform))
            {
                currentSource = step.Platform;
            }
        }

        return (currentValue, currentSource);
    }

    private static string? ResolveFallbackTransitionSource<T>(
        string normalizedFinal,
        string normalizedBaseline,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        foreach (var step in completed)
        {
            var after = NormalizeCompareValue(afterSelector(step));
            if (!string.Equals(after, normalizedFinal, StringComparison.Ordinal))
            {
                continue;
            }

            var before = NormalizeCompareValue(beforeSelector(step));
            var effectiveBefore = string.IsNullOrEmpty(before) ? normalizedBaseline : before;
            if (!string.Equals(effectiveBefore, normalizedFinal, StringComparison.Ordinal))
            {
                return step.Platform;
            }
        }

        return null;
    }

    private static string? ResolveMergedValueSources<T>(
        T? finalValue,
        T? baselineValue,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        var finalParts = NormalizeCompareParts(finalValue);
        if (finalParts.Count <= 1)
        {
            return null;
        }

        var baselineParts = NormalizeCompareParts(baselineValue);
        var sources = new List<string>();
        foreach (var step in completed)
        {
            if (string.IsNullOrWhiteSpace(step.Platform))
            {
                continue;
            }

            var beforeParts = NormalizeCompareParts(beforeSelector(step));
            if (beforeParts.Count == 0)
            {
                beforeParts = baselineParts;
            }

            var afterParts = NormalizeCompareParts(afterSelector(step));
            if (afterParts.Count == 0)
            {
                continue;
            }

            var stepChanged = !string.Equals(
                NormalizeCompareValue(beforeSelector(step)),
                NormalizeCompareValue(afterSelector(step)),
                StringComparison.Ordinal);
            var retainedContribution = afterParts.Intersect(finalParts, StringComparer.Ordinal).Any();
            var introducedContribution = afterParts
                .Except(beforeParts, StringComparer.Ordinal)
                .Intersect(finalParts, StringComparer.Ordinal)
                .Any();
            var changedToFinalValue = string.Equals(
                NormalizeCompareValue(afterSelector(step)),
                NormalizeCompareValue(finalValue),
                StringComparison.Ordinal)
                && !string.Equals(
                    NormalizeCompareValue(beforeSelector(step)),
                    NormalizeCompareValue(finalValue),
                    StringComparison.Ordinal);

            if ((introducedContribution || changedToFinalValue || retainedContribution)
                && stepChanged
                && !sources.Contains(step.Platform, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(step.Platform);
            }
        }

        return sources.Count > 1 ? string.Join(", ", sources) : null;
    }

    private static object? GetMetaFieldValue(AutoTagTagSnapshot? snapshot, string key)
    {
        if (snapshot?.Meta == null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var property = typeof(QuickTagDumpMeta).GetProperties()
            .FirstOrDefault(prop => string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase));
        return property?.GetValue(snapshot.Meta);
    }
}
