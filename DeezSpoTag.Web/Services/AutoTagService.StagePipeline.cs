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

    private static HashSet<string> BuildEnrichmentStageAllowedKeys()
    {
        var keys = BuildStageAllowedKeys(
            includeSkipTagged: true,
            includeConflictResolution: true,
            includeTargetFiles: true,
            includeLibraryWideEnhancementBatchSize: true);
        keys.Add(AutoTagLiterals.ManualReleasePreferenceKey);
        keys.Add(AutoTagLiterals.ManualDestinationFolderIdKey);
        keys.Add(AutoTagLiterals.ManualForceFingerprintKey);
        return keys;
    }

    private static HashSet<string> BuildEnhancementStageAllowedKeys()
    {
        var keys = BuildStageAllowedKeys(
            includeSkipTagged: true,
            includeConflictResolution: true,
            includeTargetFiles: true,
            includeLibraryWideEnhancementBatchSize: true);
        keys.Add(AutoTagLiterals.EnhancementStage);
        keys.Add(AutoTagLiterals.EnhancementForceFingerprintKey);
        keys.Add(AutoTagLiterals.ManualForceFingerprintKey);
        keys.Add(AutoTagLiterals.EnhancementUntrustedTargetsKey);
        return keys;
    }

    public bool TryGetRunningEnrichmentJobId(out string? jobId)
    {
        var stage = _activeJobStages.FirstOrDefault(
            static entry => string.Equals(entry.Value, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(stage.Key))
        {
            jobId = stage.Key;
            return true;
        }

        var manualJobId = _activeJobIds.Keys.FirstOrDefault(activeJobId =>
            _jobs.TryGetValue(activeJobId, out var activeJob)
            && string.Equals(activeJob.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            && IsManualEnrichmentRunIntent(activeJob.RunIntent));
        if (!string.IsNullOrWhiteSpace(manualJobId))
        {
            jobId = manualJobId;
            return true;
        }

        jobId = null;
        return false;
    }

    private void FinalizeStageExecution(AutoTagJob job, bool success)
    {
        if (!IsTerminalStopStatus(job.Status))
        {
            job.Status = success ? AutoTagLiterals.CompletedStatus : AutoTagLiterals.FailedStatus;
        }
        if (string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
        {
            job.Progress = 1d;
            job.CurrentPhase = "completed";
            job.ResumeCheckpoint = null;
            job.ResumeFromJobId = null;
        }
        job.ExitCode = success ? 0 : 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        AppendPlatformSummary(job);
        SaveJob(job);
        AppendActivityLog(job.Id, $"autotag finished: status={job.Status}");
        NotifyRunFinished(job);
        NotifyRunStopped(job, job.Status, job.Error ?? string.Empty);
    }

    private static void RegisterStageRuntimeConfigPaths(HashSet<string> runtimeConfigPaths, IReadOnlyList<AutoTagStageConfig> stages)
    {
        foreach (var stageConfigPath in stages
                     .Select(static stage => stage.ConfigPath)
                     .Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            runtimeConfigPaths.Add(stageConfigPath!);
        }
    }

    private bool TryMarkNoStagesConfigured(
        AutoTagJob job,
        IReadOnlyCollection<AutoTagStageConfig> stages,
        bool includesEnhancementWorkflows)
    {
        if (stages.Count > 0)
        {
            return false;
        }

        if (includesEnhancementWorkflows)
        {
            AppendLog(job, "gap-fill tagging skipped: no runnable gap-fill tagging stage was configured");
            return false;
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            job.Status = AutoTagLiterals.SkippedStatus;
            job.Error = "No runnable download enrichment stage was configured.";
            job.ExitCode = 0;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.ResumeCheckpoint = null;
            job.ResumeFromJobId = null;
            SaveJob(job);
            AppendActivityLog(job.Id, "autotag skipped: no runnable download enrichment stage configured");
            return true;
        }

        job.Status = AutoTagLiterals.FailedStatus;
        job.Error = "No AutoTag stages configured.";
        job.ExitCode = 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ResumeCheckpoint = null;
        job.ResumeFromJobId = null;
        SaveJob(job);
        AppendActivityLog(job.Id, "autotag failed: no stages configured");
        NotifyRunFinished(job);
        return true;
    }

    private async Task<StageRunResult> ExecuteStagesAsync(
        AutoTagJob job,
        IReadOnlyList<AutoTagStageConfig> stages,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var stageResult = await ExecuteSingleStageAsync(job, stage, index, stages.Count, path, fileOutcomes, cancellationToken);
            if (!stageResult.Success)
            {
                return new StageRunResult(false);
            }
        }

        return new StageRunResult(true);
    }

    private async Task<StageExecutionResult> ExecuteSingleStageAsync(
        AutoTagJob job,
        AutoTagStageConfig stage,
        int stageIndex,
        int totalStages,
        string path,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        AppendLog(job, BuildStageStartedLog(stage, stageIndex, totalStages));
        _activeJobStages[job.Id] = stage.Name;
        var resumeCursor = ResolveResumeCursor(job, stage);
        if (resumeCursor != null)
        {
            AppendLog(
                job,
                $"resume checkpoint active for stage '{stage.Name}': platformIndex={resumeCursor.PlatformIndex}, fileIndex={resumeCursor.FileIndex}");
        }
        else if (job.ResumeCheckpoint != null
            && !string.Equals(job.ResumeCheckpoint.StageName, stage.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Only drop the checkpoint when this stage is not the one it belongs to.
            // A drifted config hash must not rewind an enhancement run to file 0.
            job.ResumeCheckpoint = null;
            SaveJob(job);
        }
        EnsureInitialEnrichmentResumeCheckpoint(job, stage);

        try
        {
            await ReplayPendingEnhancementBatchAsync(job, stage.ConfigPath, cancellationToken);
            var result = await _autoTagRunner.RunAsync(
                job.Id,
                path,
                stage.ConfigPath,
                status => UpdateStatus(job, status, stage.Name, stage.ConfigHash, stageIndex, totalStages, fileOutcomes),
                line => AppendLog(job, line),
                IsEnhancementRunIntent(job.RunIntent)
                    ? (batch, token) => RunCoordinatedEnhancementBatchAsync(job, stage.ConfigPath, batch, token)
                    : null,
                resumeCursor,
                cancellationToken);
            if (result.Outcome == AutoTagRunOutcome.Stopped)
            {
                return HandleStoppedStage(job);
            }

            if (!result.Success)
            {
                if (result.Outcome == AutoTagRunOutcome.Paused
                    && TryHandlePausedStage(job, result.Error))
                {
                    return new StageExecutionResult(false);
                }

                job.Status = AutoTagLiterals.FailedStatus;
                job.Error = result.Error;
                return new StageExecutionResult(false);
            }

            AppendLog(job, BuildStageFinishedLog(stage, stageIndex, totalStages));
            if (CanApplyResumeCheckpoint(job.ResumeCheckpoint, stage))
            {
                job.ResumeCheckpoint = null;
                SaveJob(job);
            }

            return new StageExecutionResult(true);
        }
        finally
        {
            _activeJobStages.TryRemove(job.Id, out _);
        }
    }

    /// <summary>
    /// Applies a paused runner outcome (AutoTagRunOutcome.Paused) to the job. The
    /// outcome is typed; the error argument is the pause reason itself.
    /// </summary>
    private bool TryHandlePausedStage(AutoTagJob job, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var message = error.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "AutoTag paused.";
        }

        job.Status = AutoTagLiterals.PausedStatus;
        job.Error = message;
        AppendLog(job, $"autotag paused: {message}");
        NotifyDownloadToast(message, "warning");
        return true;
    }

    private static StageExecutionResult HandleStoppedStage(AutoTagJob job)
    {
        if (IsTerminalStopStatus(job.Status))
        {
            return new StageExecutionResult(false);
        }

        // The runner stopped without StopJobAsync having stamped a status (external kill).
        // Enhancement runs stay resumable: interrupted, never canceled.
        job.Status = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? AutoTagLiterals.InterruptedStatus
            : AutoTagLiterals.CanceledStatus;
        job.Error = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? "Interrupted. Resume is available."
            : "Stopped by user.";
        return new StageExecutionResult(false);
    }

    private void EnsureInitialEnrichmentResumeCheckpoint(AutoTagJob job, AutoTagStageConfig stage)
    {
        if (job.ResumeCheckpoint != null
            || !string.Equals(stage.Name, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        job.ResumeCheckpoint = new AutoTagResumeCheckpoint
        {
            StageName = stage.Name,
            StageConfigHash = stage.ConfigHash,
            PlatformIndex = 0,
            FileIndex = 0,
            PlatformCount = 1,
            FileCount = 1,
            LastPath = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        SaveJob(job);
    }

    private void EnsureInitialEnrichmentResumeCheckpoint(AutoTagJob job, IReadOnlyList<AutoTagStageConfig> stages)
    {
        if (job.ResumeCheckpoint != null)
        {
            return;
        }

        var stage = stages.FirstOrDefault(stage =>
            string.Equals(stage.Name, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase));
        if (stage == null)
        {
            return;
        }

        EnsureInitialEnrichmentResumeCheckpoint(job, stage);
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Stage planning intentionally evaluates enrichment and enhancement branches explicitly for deterministic run semantics.")]
    private async Task<List<AutoTagStageConfig>> BuildStageConfigsAsync(AutoTagJob job, string configPath)
    {
        var root = LoadConfigRoot(configPath);
        if (root == null)
        {
            return new List<AutoTagStageConfig>();
        }

        var platformCaps = await LoadPlatformCapabilitiesAsync();
        var eligiblePlatforms = await ResolveEligiblePlatformsAsync(root, platformCaps, job);
        var stages = new List<AutoTagStageConfig>();
        var runIntent = NormalizeRunIntent(job.RunIntent);

        var shouldRunEnrichment = ShouldRunEnrichmentForIntent(runIntent);
        var enrichmentSkipReason = string.Empty;
        if (shouldRunEnrichment
            && TryBuildEnrichmentStages(
                root,
                platformCaps,
                eligiblePlatforms,
                new EnrichmentBuildContext(runIntent, job.Id),
                out var enrichmentStages,
                out enrichmentSkipReason,
                out var enrichmentStrippedKeys))
        {
            stages.AddRange(enrichmentStages);
            AppendStageSchemaLog(job, AutoTagLiterals.EnrichmentStage, enrichmentStrippedKeys);
        }
        else
        {
            var reason = shouldRunEnrichment
                ? enrichmentSkipReason
                : $"disabled for run intent '{runIntent}'";
            AppendLog(job, $"enrichment skipped: {reason}");
        }

        var shouldRunEnhancement = ShouldRunEnhancementForIntent(runIntent);
        var enhancementSkipReason = "gap-fill tags not configured";
        if (shouldRunEnhancement
            && TryBuildEnhancementStage(
                root,
                platformCaps,
                eligiblePlatforms,
                new EnhancementBuildContext(runIntent, job.Id),
                out var enhancementStage,
                out enhancementSkipReason,
                out var enhancementStrippedKeys))
        {
            stages.Add(enhancementStage);
            AppendStageSchemaLog(job, AutoTagLiterals.EnhancementStage, enhancementStrippedKeys);
        }
        else
        {
            if (shouldRunEnhancement && HasConfiguredEnhancementWorkflows(root))
            {
                AppendLog(job, $"gap-fill tagging skipped: {enhancementSkipReason}");
            }
            else
            {
                var reason = shouldRunEnhancement
                    ? enhancementSkipReason
                    : $"disabled for run intent '{runIntent}'";
                AppendLog(job, $"enhancement skipped: {reason}");
            }
        }

        return stages;
    }

    private bool TryBuildEnhancementStage(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnhancementBuildContext context,
        out AutoTagStageConfig stage,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stage = null!;
        skipReason = "gap-fill tags not configured";
        strippedKeys = new List<string>();

        if (!EnhancementWorkflowSelection.IsGapFillRunnable(baseRoot))
        {
            return false;
        }

        var requested = ResolveEnhancementRequestedTags(baseRoot);
        var platforms = eligiblePlatforms
            .Where(platform => !IsLyricsProviderPlatform(platform))
            .ToList();
        if (platforms.Count == 0)
        {
            skipReason = "no eligible enhancement platforms enabled";
            return false;
        }

        var filtered = FilterSupportedTags(requested, platforms, platformCaps);
        if (filtered.Count == 0)
        {
            skipReason = "no supported enhancement tags for enabled platforms";
            return false;
        }

        var stageRoot = CloneRoot(baseRoot);
        WriteStringList(stageRoot, AutoTagLiterals.PlatformsKey, platforms);
        stageRoot[AutoTagLiterals.MultiPlatformKey] = platforms.Count > 1;
        WriteStringList(stageRoot, "tags", filtered);
        if (string.Equals(context.RunIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase))
        {
            var targetFiles = ReadStringList(baseRoot, AutoTagLiterals.TargetFilesKey);
            if (targetFiles.Count == 0)
            {
                skipReason = "no recent downloaded files were available for enhancement";
                return false;
            }

            WriteStringList(stageRoot, AutoTagLiterals.TargetFilesKey, targetFiles);
            stageRoot[AutoTagLiterals.LibraryWideEnhancementBatchSizeKey] = EnhancementBatchSize;
        }
        else if (string.Equals(context.RunIntent, AutoTagLiterals.RunIntentEnhancementOnly, StringComparison.OrdinalIgnoreCase))
        {
            stageRoot[AutoTagLiterals.LibraryWideEnhancementBatchSizeKey] = EnhancementBatchSize;
        }

        stageRoot["skipTagged"] = ReadBool(baseRoot, "enhancementSkipTagged")
            ?? ReadBool(baseRoot, "skipTagged")
            ?? false;
        var forceFingerprint = ReadBool(baseRoot, AutoTagLiterals.EnhancementForceFingerprintKey)
            ?? ReadBool(baseRoot, AutoTagLiterals.ManualForceFingerprintKey)
            ?? false;
        if (forceFingerprint)
        {
            stageRoot[AutoTagLiterals.EnhancementForceFingerprintKey] = true;
            if (platforms.Any(platform => string.Equals(platform, "shazam", StringComparison.OrdinalIgnoreCase)))
            {
                ConfigureShazamFingerprintBootstrap(stageRoot);
            }
        }

        if (ReadBool(baseRoot, AutoTagLiterals.EnhancementUntrustedTargetsKey) == true)
        {
            stageRoot[AutoTagLiterals.EnhancementUntrustedTargetsKey] = true;
        }

        strippedKeys = ApplyStageSchema(stageRoot, EnhancementStageAllowedKeys);

        var configJson = stageRoot.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var configPath = WriteRuntimeConfigFile(context.JobId, AutoTagLiterals.EnhancementStage, configJson);
        stage = new AutoTagStageConfig(
            AutoTagLiterals.EnhancementStage,
            configPath,
            filtered.Count,
            ComputeConfigHash(configJson));
        return true;
    }

    private static HashSet<string> BuildStageAllowedKeys(
        bool includeSkipTagged,
        bool includeConflictResolution,
        bool includeTargetFiles,
        bool includeLibraryWideEnhancementBatchSize = false)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AutoTagLiterals.PlatformsKey,
            "path",
            "tags",
            AutoTagLiterals.OverwriteTagsKey,
            "separators",
            AutoTagLiterals.OverwriteKey,
            "mergeGenres",
            "camelot",
            "shortTitle",
            "strictness",
            "matchDuration",
            "maxDurationDifference",
            "matchById",
            "enableShazam",
            "forceShazam",
            AutoTagLiterals.IncludeSubfoldersKey,
            AutoTagLiterals.MultiPlatformKey,
            "priorityTargetFiles",
            "editionConflictReview",
            "parseFilename",
            "id3v24",
            "trackNumberLeadingZeroes",
            "stylesOptions",
            "multipleMatches",
            "titleRegex",
            AutoTagLiterals.CustomKey,
            "stylesCustomTag",
            "id3CommLang",
            "capitalizeGenres",
            AutoTagLiterals.DownloadTagSourceKey,
            TracknameTemplateKey,
            "saveArtwork",
            "saveAnimatedArtwork",
            "saveSquareAnimatedArtwork",
            "saveTallAnimatedArtwork",
            "animatedArtworkFormats",
            "dlAlbumcoverForPlaylist",
            "saveArtworkArtist",
            "coverImageTemplate",
            "artistImageTemplate",
            "localArtworkFormat",
            "organizeSidecarsIntoTemplateFolders",
            "embedMaxQualityCover",
            "jpegImageQuality",
            "runTrigger",
            "technical",
            "folderStructure",
            "materializeToTemplatePath",
            "profileId",
            "profileName",
            "threads"
            // Playlist intake is intentionally disabled for AutoTag stage configs.
            // "isPlaylist"
        };

        if (includeSkipTagged)
        {
            keys.Add("skipTagged");
        }

        if (includeConflictResolution)
        {
            keys.Add("conflictResolution");
        }

        if (includeTargetFiles)
        {
            keys.Add(AutoTagLiterals.TargetFilesKey);
        }

        if (includeLibraryWideEnhancementBatchSize)
        {
            keys.Add(AutoTagLiterals.LibraryWideEnhancementBatchSizeKey);
        }

        return keys;
    }

    private static List<string> ApplyStageSchema(JsonObject root, HashSet<string> allowedKeys)
    {
        var stripped = root
            .Select(pair => pair.Key)
            .Where(key => !allowedKeys.Contains(key))
            .ToList();
        foreach (var key in stripped)
        {
            root.Remove(key);
        }
        stripped.Sort(StringComparer.OrdinalIgnoreCase);
        return stripped;
    }

    private void AppendStageSchemaLog(AutoTagJob job, string stageName, List<string> strippedKeys)
    {
        if (strippedKeys.Count == 0)
        {
            return;
        }

        AppendLog(job, $"{stageName} config: removed ignored keys ({string.Join(", ", strippedKeys)})");
    }

    private static bool IsManualEnrichmentRunIntent(string? runIntent)
        => string.Equals(
            NormalizeRunIntent(runIntent),
            AutoTagLiterals.RunIntentManualEnrichment,
            StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRunEnrichmentForIntent(string? runIntent)
    {
        return !IsEnhancementRunIntent(runIntent);
    }

    private static string BuildStageStartedLog(AutoTagStageConfig stage, int stageIndex, int stageCount)
    {
        _ = stageIndex;
        _ = stageCount;
        var name = FormatStageName(stage.Name);
        return $"{name} tagging started ({stage.TagCount} tags)";
    }

    private static string BuildStageFinishedLog(AutoTagStageConfig stage, int stageIndex, int stageCount)
    {
        _ = stageIndex;
        _ = stageCount;
        var name = FormatStageName(stage.Name);
        return $"{name} tagging finished";
    }

    private static string FormatStageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Stage";
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 1)
        {
            return trimmed.ToUpperInvariant();
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
