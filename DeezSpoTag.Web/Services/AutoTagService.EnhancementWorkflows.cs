using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.CoverPort;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{
    private const string EnabledField = "enabled";
    private const int EnhancementBatchSize = 40;
    private static readonly string[] MissingCoreMetadataTags =
    {
        "title", "artist", "album", "albumArtist", "trackNumber"
    };
    private readonly record struct EnhancementWorkflowOutcome(string Status, string Message)
    {
        public static EnhancementWorkflowOutcome Completed(string message) => new(AutoTagLiterals.CompletedStatus, message);
        public static EnhancementWorkflowOutcome Skipped(string message) => new("skipped", message);
    }

    private sealed record QualityCheckOptions(
        bool FlagMissingTags,
        bool FlagMismatchedMetadata,
        bool FlagDuplicates,
        bool UseDuplicatesFolder,
        bool UseShazamForDedupe,
        string? DuplicatesFolderName,
        bool QueueLyricsRefresh,
        bool QueueAtmosAlternatives,
        bool QueueTechnicalProfileUpgrades,
        bool RunQualityUpgradeStage,
        bool RunQualityScanner,
        IReadOnlyList<string> TechnicalProfiles)
    {
        public bool ShouldRunAnyWorkflow => FlagMissingTags
            || FlagMismatchedMetadata
            || RunQualityScanner
            || FlagDuplicates
            || QueueLyricsRefresh;
    }

    private async Task PrepareEnhancementRunAsync(
        AutoTagJob job,
        string configPath,
        CancellationToken cancellationToken)
    {
        if (!IsEnhancementRunIntent(job.RunIntent))
        {
            return;
        }

        var root = LoadConfigRoot(configPath);
        if (root?[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return;
        }

        if (!ShouldPrepareMissingCoreMetadataTargets(job, enhancementRoot)
            || ReadStringList(root, AutoTagLiterals.TargetFilesKey).Count > 0)
        {
            return;
        }

        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        var scopedFolders = ResolveEnhancementJobFolders(job, enhancementRoot, enabledFolders);
        if (scopedFolders.Count == 0)
        {
            throw new InvalidOperationException("Enhancement could not resolve an enabled music folder scope.");
        }

        SetEnhancementPhase(job, "missing-core-metadata-db-audit", 0, 1);
        AppendLog(job, $"enhancement missing core metadata DB audit starting for {scopedFolders.Count} indexed folder scope(s).");
        var missingFiles = await _libraryRepository.GetMissingCoreMetadataFilesAsync(
            scopedFolders.Select(folder => folder.Id).ToList(),
            cancellationToken);
        WriteStringList(root, "gapFillTags", MissingCoreMetadataTags);
        WriteStringList(
            root,
            AutoTagLiterals.TargetFilesKey,
            missingFiles
                .Select(file => file.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        File.WriteAllText(configPath, root.ToJsonString(_jsonOptions), new System.Text.UTF8Encoding(false));
        AppendLog(job, $"enhancement missing core metadata DB audit finished: {missingFiles.Count} indexed file(s)");
        SetEnhancementPhase(job, "missing-core-metadata-db-audit", 1, 1);
    }

    private static bool ShouldPrepareMissingCoreMetadataTargets(AutoTagJob job, JsonObject enhancementRoot)
    {
        return (string.IsNullOrWhiteSpace(job.EnhancementFeature)
                || string.Equals(job.EnhancementFeature, AutoTagLiterals.EnhancementFeatureQualityChecks, StringComparison.OrdinalIgnoreCase))
            && enhancementRoot["qualityChecks"] is JsonObject qualityChecks
            && ReadBool(qualityChecks, "flagMissingTags") == true;
    }

    private static List<FolderDto> ResolveEnhancementJobFolders(
        AutoTagJob job,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders)
    {
        JsonObject? section = job.EnhancementFeature switch
        {
            AutoTagLiterals.EnhancementFeatureGapFill => enhancementRoot["gapFilling"] as JsonObject,
            AutoTagLiterals.EnhancementFeatureFolderUniformity => enhancementRoot["folderUniformity"] as JsonObject,
            AutoTagLiterals.EnhancementFeatureQualityChecks => enhancementRoot["qualityChecks"] as JsonObject,
            AutoTagLiterals.EnhancementFeatureCoverMaintenance => enhancementRoot["coverMaintenance"] as JsonObject,
            _ => null
        };
        var requestedIds = section == null
            ? enhancementRoot
                .SelectMany(pair => pair.Value is JsonObject value ? ParseFolderIds(value, "folderIds") : [])
                .Distinct()
                .ToList()
            : ParseFolderIds(section, "folderIds");
        if (requestedIds.Count > 0)
        {
            var selected = requestedIds.ToHashSet();
            return enabledFolders.Where(folder => selected.Contains(folder.Id)).ToList();
        }

        return enabledFolders
            .Where(folder => !string.IsNullOrWhiteSpace(job.RootPath) && PathsOverlap(job.RootPath, folder.RootPath))
            .ToList();
    }

    private void SetEnhancementPhase(
        AutoTagJob job,
        string phase,
        int processed,
        int total,
        int currentBatch = 0,
        int batchCount = 0,
        int batchProcessed = 0,
        int batchSize = 0)
    {
        job.CurrentPhase = phase;
        job.ProcessedItems = Math.Max(0, processed);
        job.TotalItems = Math.Max(0, total);
        job.CurrentBatch = Math.Max(0, currentBatch);
        job.BatchCount = Math.Max(0, batchCount);
        job.BatchProcessed = Math.Max(0, batchProcessed);
        job.BatchSize = Math.Max(0, batchSize);
        if (total > 0)
        {
            job.Progress = Math.Clamp(processed / (double)total, 0d, 1d);
        }
        job.CurrentPlatform = string.IsNullOrWhiteSpace(job.EnhancementFeature)
            ? AutoTagLiterals.EnhancementStage
            : job.EnhancementFeature;
        SaveJob(job);
    }

    private void RecordEnhancementItemStatus(
        AutoTagJob job,
        string feature,
        string path,
        string status,
        string? message,
        int processed,
        int total,
        int currentBatch,
        int batchCount,
        int batchProcessed,
        int batchSize)
    {
        SetEnhancementPhase(job, feature, processed, total, currentBatch, batchCount, batchProcessed, batchSize);
        var update = new TaggingStatusWrap
        {
            Platform = feature,
            Progress = total > 0 ? Math.Clamp(processed / (double)total, 0d, 1d) : 0d,
            FileIndex = Math.Max(0, processed - 1),
            FileCount = total,
            Status = new TaggingStatus
            {
                Status = status,
                Path = path,
                Message = message
            }
        };
        job.LastStatus = update;
        AppendStatusHistory(job, update);
        switch (status)
        {
            case AutoTagLiterals.OkStatus:
            case AutoTagLiterals.TaggedStatus:
                job.OkCount++;
                break;
            case AutoTagLiterals.ErrorStatus:
                job.ErrorCount++;
                break;
            case AutoTagLiterals.ReviewStatus:
                job.ReviewCount++;
                break;
            case AutoTagLiterals.SkippedStatus:
                job.SkippedCount++;
                break;
        }
        SaveJob(job);
    }

    private async Task RunIntegratedEnhancementWorkflowsAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        bool includesEnhancementWorkflows,
        CancellationToken cancellationToken)
    {
        if (!includesEnhancementWorkflows
            || !ShouldRunEnhancementForIntent(job.RunIntent)
            || !IsEnhancementWorkflowTrigger(job.Trigger))
        {
            return;
        }

        var root = LoadConfigRoot(configPath);
        if (root == null || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return;
        }

        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        await RunEnhancementWorkflowAsync(
            job,
            "folder-uniformity",
            token => RunConfiguredFolderUniformityAsync(job, rootPath, enhancementRoot, enabledFolders, token),
            cancellationToken);
        await RunEnhancementWorkflowAsync(
            job,
            "cover-maintenance",
            token => RunConfiguredCoverMaintenanceAsync(job, rootPath, root, enhancementRoot, enabledFolders, configPath, token),
            cancellationToken);
        await RunEnhancementWorkflowAsync(
            job,
            "quality-checks",
            token => RunConfiguredQualityChecksAsync(job, rootPath, enhancementRoot, enabledFolders, configPath, token),
            cancellationToken);
    }

    private bool ShouldRunIntegratedEnhancementWorkflows(AutoTagJob job, string configPath)
    {
        if (!ShouldRunEnhancementForIntent(job.RunIntent) || !IsEnhancementWorkflowTrigger(job.Trigger))
        {
            return false;
        }

        var root = LoadConfigRoot(configPath);
        if (root == null || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return false;
        }

        return IsFolderUniformityWorkflowEnabled(enhancementRoot)
            || IsCoverMaintenanceWorkflowEnabled(enhancementRoot)
            || IsQualityChecksWorkflowEnabled(enhancementRoot);
    }

    private static bool HasConfiguredEnhancementWorkflows(JsonObject root)
    {
        return root[AutoTagLiterals.EnhancementStage] is JsonObject enhancementRoot
            && (IsFolderUniformityWorkflowEnabled(enhancementRoot)
                || IsCoverMaintenanceWorkflowEnabled(enhancementRoot)
                || IsQualityChecksWorkflowEnabled(enhancementRoot));
    }

    private async Task RunEnhancementWorkflowAsync(
        AutoTagJob job,
        string name,
        Func<CancellationToken, Task<EnhancementWorkflowOutcome>> run,
        CancellationToken cancellationToken)
    {
        var result = new EnhancementWorkflowResult
        {
            Name = name,
            Status = AutoTagLiterals.RunningStatus,
            StartedAt = DateTimeOffset.UtcNow
        };
        job.EnhancementWorkflows.Add(result);
        SaveJob(job);

        try
        {
            var outcome = await run(cancellationToken);
            result.Status = outcome.Status;
            result.Message = outcome.Message;
        }
        catch (OperationCanceledException)
        {
            result.Status = string.Equals(job.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
                ? AutoTagLiterals.PausedStatus
                : AutoTagLiterals.InterruptedStatus;
            result.Message = string.Equals(result.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
                ? "paused"
                : "interrupted";
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            result.Status = AutoTagLiterals.FailedStatus;
            result.Message = ex.Message;
            AppendLog(job, $"enhancement workflow: {name} failed ({ex.Message})");
            throw;
        }
        finally
        {
            result.FinishedAt = DateTimeOffset.UtcNow;
            SaveJob(job);
        }
    }

    private static bool IsEnhancementWorkflowTrigger(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return true;
        }

        return string.Equals(trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EnhancementWorkflowOutcome> RunConfiguredFolderUniformityAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (!TryGetFolderUniformityConfig(enhancementRoot, out var folderUniformity))
        {
            return EnhancementWorkflowOutcome.Skipped("folder uniformity is not configured.");
        }

        var scopedFolders = ResolveScopedFolders(rootPath, folderUniformity!, enabledFolders);
        var rootPaths = ResolveFolderUniformityRootPaths(rootPath, folderUniformity!, enabledFolders, scopedFolders);
        if (rootPaths.Count == 0)
        {
            AppendLog(job, "enhancement workflow: folder uniformity skipped (no eligible folders/paths).");
            return EnhancementWorkflowOutcome.Skipped("no eligible folders or paths.");
        }

        var profileState = scopedFolders.Count > 0
            ? await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken)
            : null;
        var scopedFoldersByPath = BuildScopedFoldersByPath(scopedFolders);

        AppendLog(job, $"enhancement workflow: folder uniformity starting ({rootPaths.Count} path(s)).");
        if (ReadBool(folderUniformity!, "enforceFolderStructure") != false)
        {
            await RunFolderUniformityForPathsAsync(job, folderUniformity!, rootPaths, profileState, scopedFoldersByPath, cancellationToken);
        }
        else
        {
            AppendLog(job, "enhancement workflow: folder structure skipped (enforceFolderStructure is disabled).");
        }

        await RunFolderUniformityDedupeAsync(job, folderUniformity!, scopedFolders, rootPaths, enabledFolders, cancellationToken);

        AppendLog(job, "enhancement workflow: folder uniformity completed.");
        return EnhancementWorkflowOutcome.Completed($"processed {rootPaths.Count} path(s).");
    }

    private async Task ApplyEnhancementBatchTemplatesAsync(
        AutoTagJob job,
        string configPath,
        IReadOnlyList<string> batchFiles,
        CancellationToken cancellationToken)
        => await ApplyProfileTemplatesToFilesAsync(
            job,
            configPath,
            batchFiles,
            requireSuccessfulEnhancement: true,
            cancellationToken);

    private async Task ApplyProfileTemplatesToFilesAsync(
        AutoTagJob job,
        string configPath,
        IReadOnlyList<string> batchFiles,
        bool requireSuccessfulEnhancement,
        CancellationToken cancellationToken)
    {
        var root = LoadConfigRoot(configPath);
        if (root?[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return;
        }
        var folderUniformity = enhancementRoot["folderUniformity"] as JsonObject;

        var successfulBatchFiles = batchFiles
            .Select(NormalizePathForJob)
            .Where(path => (!requireSuccessfulEnhancement
                    || job.EnhancedFilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (successfulBatchFiles.Count == 0)
        {
            AppendLog(job, "enhancement batch templates skipped: no eligible files remained in this batch.");
            return;
        }

        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        var profileState = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        var organizedCount = 0;
        foreach (var folder in enabledFolders)
        {
            var folderRoot = Path.GetFullPath(folder.RootPath);
            var folderFiles = successfulBatchFiles
                .Where(path => LibraryFolderPathSafety.IsSameOrDescendantPath(path, folderRoot))
                .ToList();
            if (folderFiles.Count == 0)
            {
                continue;
            }

            var profile = AutoTagProfileResolutionService.ResolveFolderProfile(
                profileState,
                folder.Id,
                folder.AutoTagProfileId);
            if (profile == null)
            {
                throw new InvalidOperationException($"Library folder '{folderRoot}' has no valid AutoTag profile for template application.");
            }

            var options = folderUniformity == null
                ? new AutoTagOrganizerOptions()
                : BuildFolderUniformityOptions(folderUniformity);
            options.MoveMisplacedFiles = true;
            options.RenameFilesToTemplate = true;
            options.BatchScopedFilesOnly = true;
            AutoTagOrganizerProfileOverlay.ApplyTaggingProfileOverrides(options, profile);
            if (options.RenameFilesToTemplate && string.IsNullOrWhiteSpace(options.TracknameTemplateOverride))
            {
                throw new InvalidOperationException($"Library folder '{folderRoot}' has no valid file template.");
            }

            await _libraryOrganizer.OrganizeFilesAsync(
                folderRoot,
                folderFiles,
                options,
                line => AppendLog(job, $"enhancement batch templates: {line}"),
                cancellationToken);
            organizedCount += folderFiles.Count;
        }

        if (organizedCount != successfulBatchFiles.Count)
        {
            throw new InvalidOperationException(
                $"Template application resolved {organizedCount} of {successfulBatchFiles.Count} successfully enhanced batch files to enabled library folders.");
        }

        AppendLog(job, $"enhancement batch templates completed: {organizedCount} file(s). Next batch unlocked.");
    }

    private static bool TryGetFolderUniformityConfig(JsonObject enhancementRoot, out JsonObject? folderUniformity)
    {
        if (enhancementRoot["folderUniformity"] is not JsonObject config
            || !IsFolderUniformityWorkflowEnabled(enhancementRoot))
        {
            folderUniformity = null;
            return false;
        }

        folderUniformity = config;
        return true;
    }

    private static bool IsFolderUniformityWorkflowEnabled(JsonObject enhancementRoot)
    {
        return enhancementRoot["folderUniformity"] is JsonObject config
            && ReadBool(config, EnabledField) == true
            && (ReadBool(config, "enforceFolderStructure") != false || ReadBool(config, "runDedupe") != false);
    }

    private static List<string> ResolveFolderUniformityRootPaths(
        string rootPath,
        JsonObject folderUniformity,
        IReadOnlyList<FolderDto> enabledFolders,
        List<FolderDto> scopedFolders)
    {
        return scopedFolders.Count > 0
            ? scopedFolders
                .Select(folder => folder.RootPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : ResolveRootPathsForWorkflow(rootPath, folderUniformity, enabledFolders);
    }

    private static Dictionary<string, FolderDto> BuildScopedFoldersByPath(IReadOnlyList<FolderDto> scopedFolders)
    {
        return scopedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.RootPath))
            .GroupBy(folder => Path.GetFullPath(folder.RootPath.Trim()), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task RunFolderUniformityForPathsAsync(
        AutoTagJob job,
        JsonObject folderUniformity,
        IReadOnlyList<string> rootPaths,
        AutoTagProfileResolutionService.ResolvedState? profileState,
        Dictionary<string, FolderDto> scopedFoldersByPath,
        CancellationToken cancellationToken)
    {
        var searchOption = ReadBool(folderUniformity, AutoTagLiterals.IncludeSubfoldersKey) != false
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        var totalFiles = rootPaths
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*", searchOption))
            .Count(file => EligibleAudioExtensions.Contains(Path.GetExtension(file)));
        var processedFiles = 0;
        var totalBatches = totalFiles == 0
            ? 0
            : (int)Math.Ceiling(totalFiles / (double)EnhancementBatchSize);

        foreach (var path in rootPaths)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var options = BuildFolderUniformityOptions(folderUniformity);
            if (!TryApplyFolderUniformityProfile(job, path, options, profileState, scopedFoldersByPath))
            {
                continue;
            }

            await _libraryOrganizer.OrganizePathInBatchesAsync(
                path,
                options,
                EnhancementBatchSize,
                line => AppendLog(job, $"folder uniformity: {line}"),
                cancellationToken,
                async (batch, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var summary = BuildFolderUniformityBatchSummary(batch.Report);
                    if (options.GenerateReconciliationReport)
                    {
                        foreach (var entry in batch.Report.Entries)
                        {
                            AppendLog(job, $"folder uniformity reconciliation: {entry}");
                        }
                    }
                    for (var index = 0; index < batch.Files.Count; index++)
                    {
                        processedFiles++;
                        RecordEnhancementItemStatus(
                            job,
                            AutoTagLiterals.EnhancementFeatureFolderUniformity,
                            batch.Files[index],
                            AutoTagLiterals.OkStatus,
                            summary,
                            processedFiles,
                            totalFiles,
                            Math.Max(1, (processedFiles - 1) / EnhancementBatchSize + 1),
                            totalBatches,
                            index + 1,
                            batch.Files.Count);
                    }

                    await Task.CompletedTask;
                });
        }
    }

    private static string BuildFolderUniformityBatchSummary(AutoTagLibraryOrganizer.AutoTagOrganizerReport report)
    {
        return $"moved folders {report.MovedFolders}; moved files {report.MovedFiles}; "
            + $"moved sidecars {report.MovedSidecars}; replaced duplicates {report.ReplacedDuplicates}; "
            + $"quarantined duplicates {report.QuarantinedDuplicates}; conflicts {report.SkippedConflicts}";
    }

    private bool TryApplyFolderUniformityProfile(
        AutoTagJob job,
        string path,
        AutoTagOrganizerOptions options,
        AutoTagProfileResolutionService.ResolvedState? profileState,
        Dictionary<string, FolderDto> scopedFoldersByPath)
    {
        if (profileState == null || !scopedFoldersByPath.TryGetValue(path, out var folder))
        {
            return true;
        }

        var profile = AutoTagProfileResolutionService.ResolveFolderProfile(
            profileState,
            folder.Id,
            folder.AutoTagProfileId);
        if (profile == null)
        {
            AppendLog(job, $"enhancement workflow: folder uniformity skipped for '{path}' (missing AutoTag profile).");
            return false;
        }

        AutoTagOrganizerProfileOverlay.ApplyTaggingProfileOverrides(options, profile);
        if (!options.RenameFilesToTemplate || !string.IsNullOrWhiteSpace(options.TracknameTemplateOverride))
        {
            return true;
        }

        AppendLog(job, $"enhancement workflow: folder uniformity skipped for '{path}' (profile tracknameTemplate is required when renameFilesToTemplate is enabled).");
        return false;
    }

    private async Task RunFolderUniformityDedupeAsync(
        AutoTagJob job,
        JsonObject folderUniformity,
        List<FolderDto> scopedFolders,
        IReadOnlyList<string> rootPaths,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (ReadBool(folderUniformity, "runDedupe") == false)
        {
            return;
        }

        var dedupeFolders = scopedFolders.Count > 0
            ? scopedFolders
            : enabledFolders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.RootPath)
                    && rootPaths.Any(path => PathsOverlap(path, folder.RootPath)))
                .ToList();
        if (dedupeFolders.Count == 0)
        {
            return;
        }

        var duplicateResult = await _duplicateCleanerService.ScanAsync(
            dedupeFolders,
            new DuplicateCleanerOptions
            {
                UseDuplicatesFolder = true,
                DuplicatesFolderName = folderUniformity["duplicatesFolderName"]?.GetValue<string>() ?? DuplicateCleanerService.DuplicatesFolderName,
                UseShazamForIdentity = ReadBool(folderUniformity, "useShazamForDedupe") == true,
                ConflictPolicy = folderUniformity["duplicateConflictPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.DuplicateConflictKeepBest
            },
            cancellationToken);
        AppendLog(job,
            $"enhancement workflow: folder-uniformity dedupe finished (found={duplicateResult.DuplicatesFound}, moved={duplicateResult.Deleted}, folder={duplicateResult.DuplicatesFolderName}).");
    }

    private async Task<EnhancementWorkflowOutcome> RunConfiguredCoverMaintenanceAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject configRoot,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        string configPath,
        CancellationToken cancellationToken)
    {
        if (enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance
            || ReadBool(coverMaintenance, EnabledField) != true)
        {
            return EnhancementWorkflowOutcome.Skipped("cover maintenance is not configured.");
        }

        var replaceMissingEmbedded = ReadBool(coverMaintenance, "replaceMissingEmbeddedCovers") == true;
        var syncExternalCovers = ReadBool(coverMaintenance, "syncExternalCovers") == true;
        var queueAnimatedArtwork = ReadBool(coverMaintenance, "queueAnimatedArtwork") == true;
        var upgradeLowResolution = ReadBool(coverMaintenance, "upgradeLowResolutionCovers") == true;
        if (!replaceMissingEmbedded && !syncExternalCovers && !queueAnimatedArtwork && !upgradeLowResolution)
        {
            return EnhancementWorkflowOutcome.Skipped("no cover maintenance actions are enabled.");
        }

        var rootPaths = ResolveRootPathsForWorkflow(rootPath, coverMaintenance, enabledFolders);
        if (rootPaths.Count == 0)
        {
            AppendLog(job, "enhancement workflow: cover maintenance skipped (no eligible folders/paths).");
            return EnhancementWorkflowOutcome.Skipped("no eligible folders or paths.");
        }

        var minResolution = ReadBoundedInt(coverMaintenance, "minResolution", 500, 100, 5000);
        var workerCount = ReadBoundedInt(coverMaintenance, "workerCount", 8, 1, 32);
        var settings = BuildEnhancementLyricsSettings(configRoot);
        ApplyProfileArtworkExtras(configRoot, settings);
        var enabledSources = ResolveProfileCoverSources(settings);
        if ((replaceMissingEmbedded || syncExternalCovers || upgradeLowResolution)
            && enabledSources.Count == 0)
        {
            throw new InvalidOperationException("The assigned profile has no compatible still-artwork source enabled for cover maintenance.");
        }
        var targetResolution = ResolveProfileCoverResolution(configRoot, settings, Math.Max(minResolution, 1200));
        var allFiles = rootPaths
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            .Where(path => EligibleAudioExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allFiles.Count == 0)
        {
            return EnhancementWorkflowOutcome.Skipped("no eligible audio files were found.");
        }

        var batchCount = (int)Math.Ceiling(allFiles.Count / (double)EnhancementBatchSize);
        var totalUpdated = 0;
        var totalSkipped = 0;
        var totalErrors = 0;
        AppendLog(job, $"enhancement workflow: cover maintenance starting ({allFiles.Count} file(s), {batchCount} batch(es)).");
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = allFiles
                .Skip(batchIndex * EnhancementBatchSize)
                .Take(EnhancementBatchSize)
                .ToList();
            SetEnhancementPhase(
                job,
                AutoTagLiterals.EnhancementFeatureCoverMaintenance,
                batchIndex * EnhancementBatchSize,
                allFiles.Count,
                batchIndex + 1,
                batchCount,
                0,
                batch.Count);
            var request = new CoverLibraryMaintenanceRequest(
                RootPaths: rootPaths,
                IncludeSubfolders: true,
                WorkerCount: workerCount,
                UpgradeLowResolutionCovers: upgradeLowResolution,
                MinResolution: minResolution,
                TargetResolution: targetResolution,
                SizeTolerancePercent: 25,
                PreserveSourceFormat: string.Equals(settings.LocalArtworkFormat, "png", StringComparison.OrdinalIgnoreCase),
                ReplaceMissingEmbeddedCovers: replaceMissingEmbedded,
                SyncExternalCovers: syncExternalCovers,
                QueueAnimatedArtwork: queueAnimatedArtwork,
                AppleStorefront: string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront,
                AnimatedArtworkMaxResolution: settings.Video?.AppleMusicVideoMaxResolution ?? 2160,
                AnimatedArtworkFormats: AppleQueueHelpers.ResolveAnimatedArtworkFormats(settings),
                EnabledSources: enabledSources,
                CoverImageTemplate: settings.CoverImageTemplate,
                TargetFiles: batch);
            var result = await _coverMaintenanceService.RunAsync(request, cancellationToken);
            totalUpdated += result.AlbumsUpdated;
            totalSkipped += result.AlbumsSkipped;
            totalErrors += result.Errors;
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            await ApplyProfileTemplatesToFilesAsync(
                job,
                configPath,
                batch,
                requireSuccessfulEnhancement: false,
                cancellationToken);
            for (var itemIndex = 0; itemIndex < batch.Count; itemIndex++)
            {
                var processed = Math.Min(allFiles.Count, batchIndex * EnhancementBatchSize + itemIndex + 1);
                var fileDirectory = Path.GetDirectoryName(batch[itemIndex]) ?? string.Empty;
                var errorPrefix = $"[error] {fileDirectory}:";
                var fileError = result.Logs.FirstOrDefault(log =>
                    log.StartsWith(errorPrefix, StringComparison.OrdinalIgnoreCase));
                RecordEnhancementItemStatus(
                    job,
                    AutoTagLiterals.EnhancementFeatureCoverMaintenance,
                    batch[itemIndex],
                    fileError == null ? AutoTagLiterals.OkStatus : AutoTagLiterals.ErrorStatus,
                    fileError ?? result.Message,
                    processed,
                    allFiles.Count,
                    batchIndex + 1,
                    batchCount,
                    itemIndex + 1,
                    batch.Count);
            }
        }

        var message = $"Cover maintenance finished: {totalUpdated} updated, {totalSkipped} skipped, {totalErrors} errors.";
        AppendLog(job, $"enhancement workflow: cover maintenance finished ({message})");
        return totalErrors > 0
            ? throw new InvalidOperationException(message)
            : EnhancementWorkflowOutcome.Completed(message);
    }

    private async Task<EnhancementWorkflowOutcome> RunConfiguredQualityChecksAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        string configPath,
        CancellationToken cancellationToken)
    {
        if (enhancementRoot["qualityChecks"] is not JsonObject qualityChecks
            || ReadBool(qualityChecks, EnabledField) != true)
        {
            return EnhancementWorkflowOutcome.Skipped("quality checks are not configured.");
        }

        var options = BuildQualityCheckOptions(qualityChecks);
        if (!options.ShouldRunAnyWorkflow)
        {
            return EnhancementWorkflowOutcome.Skipped("no quality check actions are enabled.");
        }

        var scopedFolders = ResolveScopedFolders(rootPath, qualityChecks, enabledFolders);
        if (scopedFolders.Count == 0)
        {
            AppendLog(job, "enhancement workflow: quality checks skipped (no eligible library folders in scope).");
            return EnhancementWorkflowOutcome.Skipped("no eligible library folders in scope.");
        }

        var scopedFolderIds = scopedFolders
            .Select(folder => folder.Id)
            .Distinct()
            .ToList();

        await ReportMissingCoreMetadataAuditIfRequestedAsync(job, options, scopedFolderIds, cancellationToken);
        await RunFolderTagAlignmentIfRequestedAsync(job, configPath, options, scopedFolders, cancellationToken);
        if (await RunQualityScannerIfRequestedAsync(job, qualityChecks, options, scopedFolderIds, cancellationToken))
        {
            return EnhancementWorkflowOutcome.Completed(
                $"staged {job.EnhancementDownloadItemCount} {job.EnhancementDownloadOperation} item(s); Enhancement stopped at the download batch boundary.");
        }
        await RunDuplicateCheckIfRequestedAsync(job, options, scopedFolders, cancellationToken);
        await RunLyricsRefreshIfRequestedAsync(job, options, scopedFolderIds, configPath, cancellationToken);
        return EnhancementWorkflowOutcome.Completed($"processed {scopedFolderIds.Count} folder scope(s).");
    }

    private static QualityCheckOptions BuildQualityCheckOptions(JsonObject qualityChecks)
    {
        var flagDuplicates = ReadBool(qualityChecks, "flagDuplicates") == true;
        var flagMissingTags = ReadBool(qualityChecks, "flagMissingTags") == true;
        var flagMismatchedMetadata = ReadBool(qualityChecks, "flagMismatchedMetadata") == true;
        var queueAtmosAlternatives = ReadBool(qualityChecks, "queueAtmosAlternatives") == true;
        var queueLyricsRefresh = ReadBool(qualityChecks, "queueLyricsRefresh") == true;
        var queueTechnicalProfileUpgrades = ReadBool(qualityChecks, "queueTechnicalProfileUpgrades") == true;
        var technicalProfiles = ReadStringList(qualityChecks, "technicalProfiles")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var runQualityUpgradeStage = queueTechnicalProfileUpgrades;
        var runQualityScanner = queueAtmosAlternatives || queueTechnicalProfileUpgrades;
        return new QualityCheckOptions(
            FlagMissingTags: flagMissingTags,
            FlagMismatchedMetadata: flagMismatchedMetadata,
            FlagDuplicates: flagDuplicates,
            UseDuplicatesFolder: ReadBool(qualityChecks, "useDuplicatesFolder") != false,
            UseShazamForDedupe: ReadBool(qualityChecks, "useShazamForDedupe") == true,
            DuplicatesFolderName: qualityChecks["duplicatesFolderName"]?.GetValue<string>(),
            QueueLyricsRefresh: queueLyricsRefresh,
            QueueAtmosAlternatives: queueAtmosAlternatives,
            QueueTechnicalProfileUpgrades: queueTechnicalProfileUpgrades,
            RunQualityUpgradeStage: runQualityUpgradeStage,
            RunQualityScanner: runQualityScanner,
            TechnicalProfiles: technicalProfiles);
    }

    private async Task ReportMissingCoreMetadataAuditIfRequestedAsync(
        AutoTagJob job,
        QualityCheckOptions options,
        List<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        if (!options.FlagMissingTags)
        {
            return;
        }

        var missingFiles = await _libraryRepository.GetMissingCoreMetadataFilesAsync(scopedFolderIds, cancellationToken);
        var missingFieldSummary = missingFiles
            .SelectMany(file => file.MissingFields)
            .GroupBy(field => field, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToList();
        var summary = missingFieldSummary.Count == 0
            ? "none"
            : string.Join(", ", missingFieldSummary);
        AppendLog(job,
            $"enhancement workflow: missing core metadata DB audit finished (files={missingFiles.Count}, fields={summary}).");
    }

    private static bool IsCoverMaintenanceWorkflowEnabled(JsonObject enhancementRoot)
    {
        if (enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance
            || ReadBool(coverMaintenance, EnabledField) != true)
        {
            return false;
        }

        return ReadBool(coverMaintenance, "replaceMissingEmbeddedCovers") == true
            || ReadBool(coverMaintenance, "syncExternalCovers") == true
            || ReadBool(coverMaintenance, "upgradeLowResolutionCovers") == true
            || ReadBool(coverMaintenance, "queueAnimatedArtwork") == true;
    }

    private static bool IsQualityChecksWorkflowEnabled(JsonObject enhancementRoot)
    {
        if (enhancementRoot["qualityChecks"] is not JsonObject qualityChecks
            || ReadBool(qualityChecks, EnabledField) != true)
        {
            return false;
        }

        return BuildQualityCheckOptions(qualityChecks).ShouldRunAnyWorkflow;
    }

    private async Task<bool> RunQualityScannerIfRequestedAsync(
        AutoTagJob job,
        JsonObject qualityChecks,
        QualityCheckOptions options,
        List<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        if (!options.RunQualityScanner)
        {
            return false;
        }

        if (options.QueueTechnicalProfileUpgrades)
        {
            if (await RunQualityScannerPassAsync(
                job,
                qualityChecks,
                scopedFolderIds,
                runQualityUpgradeStage: true,
                queueAtmosAlternatives: false,
                options.TechnicalProfiles,
                "technical-quality-upgrade",
                cancellationToken))
            {
                return true;
            }
        }

        if (options.QueueAtmosAlternatives)
        {
            if (await RunQualityScannerPassAsync(
                job,
                qualityChecks,
                scopedFolderIds,
                runQualityUpgradeStage: false,
                queueAtmosAlternatives: true,
                technicalProfiles: Array.Empty<string>(),
                "atmos-alternatives",
                cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> RunQualityScannerPassAsync(
        AutoTagJob job,
        JsonObject qualityChecks,
        List<long> scopedFolderIds,
        bool runQualityUpgradeStage,
        bool queueAtmosAlternatives,
        IReadOnlyList<string> technicalProfiles,
        string phase,
        CancellationToken cancellationToken)
    {
        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
            "all",
            scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);
        tracks = FilterTracksByScopedFolders(tracks, scopedFolderIds);
        if (technicalProfiles.Count > 0)
        {
            var selectedProfiles = technicalProfiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            tracks = tracks
                .Where(track => selectedProfiles.Contains(QualityScanTrackFormatter.FormatTechnicalProfile(track)))
                .ToList();
        }

        var orderedTracks = tracks
            .GroupBy(track => track.TrackId)
            .Select(group => group.First())
            .OrderBy(track => track.AlbumId)
            .ThenBy(track => track.DiscNumber ?? 1)
            .ThenBy(track => track.TrackNumber ?? int.MaxValue)
            .ThenBy(track => track.TrackId)
            .ToList();
        if (orderedTracks.Count == 0)
        {
            return false;
        }

        var batchId = Guid.NewGuid().ToString("N");
        job.EnhancementDownloadBatchId = batchId;
        job.EnhancementDownloadOperation = phase;
        job.EnhancementDownloadItemCount = 0;
        SetEnhancementPhase(job, phase, 0, orderedTracks.Count, 1, 1, 0, EnhancementBatchSize);
        var runTask = _qualityScannerService.StartAndWaitAsync(
            new QualityScannerStartRequest
            {
                Scope = "all",
                FolderId = scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
                RunQualityUpgradeStage = runQualityUpgradeStage,
                QueueAtmosAlternatives = queueAtmosAlternatives,
                CooldownMinutes = ReadOptionalInt(qualityChecks, "cooldownMinutes"),
                Trigger = "enhancement",
                MarkAutomationWindow = false,
                TechnicalProfiles = technicalProfiles,
                FolderIds = scopedFolderIds,
                TargetTrackIds = orderedTracks.Select(track => track.TrackId).ToList(),
                EnhancementBatchId = batchId,
                EnhancementOperation = phase,
                EnhancementAdmissionLimit = EnhancementBatchSize,
                EnhancementDuplicatesFolderName = qualityChecks["duplicatesFolderName"]?.GetValue<string>()
            },
            cancellationToken);
        while (!runTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = _qualityScannerService.GetState();
            SetEnhancementPhase(
                job,
                phase,
                Math.Clamp(state.Processed, 0, orderedTracks.Count),
                orderedTracks.Count,
                1,
                1,
                Math.Clamp(state.Processed, 0, orderedTracks.Count),
                EnhancementBatchSize);
            await Task.Delay(250, cancellationToken);
        }

        if (!await runTask)
        {
            throw new InvalidOperationException("Quality scanner is already running and could not execute this enhancement section.");
        }

        var finalState = _qualityScannerService.GetState();
        var queuedCount = runQualityUpgradeStage ? finalState.UpgradesQueued : finalState.AtmosQueued;
        if (queuedCount <= 0)
        {
            job.EnhancementDownloadBatchId = null;
            job.EnhancementDownloadOperation = null;
            AppendLog(job, $"enhancement workflow: {phase} finished without an admitted download.");
            return false;
        }

        job.EnhancementDownloadItemCount = queuedCount;
        AppendLog(job, $"enhancement workflow: staged {queuedCount} held item(s) for {phase} batch {batchId}.");
        return true;
    }

    private async Task RunFolderTagAlignmentIfRequestedAsync(
        AutoTagJob job,
        string configPath,
        QualityCheckOptions options,
        IReadOnlyList<FolderDto> scopedFolders,
        CancellationToken cancellationToken)
    {
        if (!options.FlagMismatchedMetadata)
        {
            return;
        }

        var files = scopedFolders
            .Where(folder => Directory.Exists(folder.RootPath))
            .SelectMany(folder => Directory.EnumerateFiles(folder.RootPath, "*.*", SearchOption.AllDirectories))
            .Where(path => EligibleAudioExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var batchCount = files.Count == 0 ? 0 : (int)Math.Ceiling(files.Count / (double)EnhancementBatchSize);
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var batch = files.Skip(batchIndex * EnhancementBatchSize).Take(EnhancementBatchSize).ToList();
            await ApplyProfileTemplatesToFilesAsync(
                job,
                configPath,
                batch,
                requireSuccessfulEnhancement: false,
                cancellationToken);
            for (var itemIndex = 0; itemIndex < batch.Count; itemIndex++)
            {
                var processed = batchIndex * EnhancementBatchSize + itemIndex + 1;
                RecordEnhancementItemStatus(
                    job,
                    "folder-tag-alignment",
                    batch[itemIndex],
                    AutoTagLiterals.OkStatus,
                    "Path checked and aligned with the assigned profile templates.",
                    processed,
                    files.Count,
                    batchIndex + 1,
                    batchCount,
                    itemIndex + 1,
                    batch.Count);
            }
        }
    }

    private async Task RunDuplicateCheckIfRequestedAsync(
        AutoTagJob job,
        QualityCheckOptions options,
        IReadOnlyList<FolderDto> scopedFolders,
        CancellationToken cancellationToken)
    {
        if (!options.FlagDuplicates)
        {
            return;
        }

        var duplicateOptions = new DuplicateCleanerOptions
        {
            UseDuplicatesFolder = options.UseDuplicatesFolder,
            DuplicatesFolderName = options.DuplicatesFolderName ?? DuplicateCleanerService.DuplicatesFolderName,
            UseShazamForIdentity = options.UseShazamForDedupe
        };
        var duplicateResult = await _duplicateCleanerService.ScanAsync(scopedFolders, duplicateOptions, cancellationToken);
        AppendLog(job,
            $"enhancement workflow: duplicate check finished (scanned={duplicateResult.FilesScanned}, found={duplicateResult.DuplicatesFound}, moved={duplicateResult.Deleted}, folder={duplicateResult.DuplicatesFolderName}).");
    }

    private async Task RunLyricsRefreshIfRequestedAsync(
        AutoTagJob job,
        QualityCheckOptions options,
        List<long> scopedFolderIds,
        string configPath,
        CancellationToken cancellationToken)
    {
        if (!options.QueueLyricsRefresh)
        {
            return;
        }

        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
            "all",
            scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);

        tracks = FilterTracksByScopedFolders(tracks, scopedFolderIds);
        var uniqueTracks = tracks
            .GroupBy(track => track.TrackId)
            .Select(group => group.First())
            .ToList();
        var batchCount = uniqueTracks.Count == 0
            ? 0
            : (int)Math.Ceiling(uniqueTracks.Count / (double)EnhancementBatchSize);
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var batch = uniqueTracks
                .Skip(batchIndex * EnhancementBatchSize)
                .Take(EnhancementBatchSize)
                .ToList();
            var completedPaths = new List<string>();
            for (var itemIndex = 0; itemIndex < batch.Count; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = batch[itemIndex];
                LyricsRefreshTrackResult result;
                try
                {
                    result = await _lyricsRefreshQueueService.RefreshTrackNowAsync(
                        track.TrackId,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = LyricsRefreshTrackResult.Skipped(track.TrackId, null, ex.Message);
                }

                if (result.Success && !string.IsNullOrWhiteSpace(result.FilePath))
                {
                    completedPaths.Add(result.FilePath);
                }
                var processed = batchIndex * EnhancementBatchSize + itemIndex + 1;
                RecordEnhancementItemStatus(
                    job,
                    "lyrics-refresh",
                    result.FilePath ?? $"{track.ArtistName} - {track.Title}",
                    result.Success ? AutoTagLiterals.OkStatus : AutoTagLiterals.SkippedStatus,
                    result.Message,
                    processed,
                    uniqueTracks.Count,
                    batchIndex + 1,
                    batchCount,
                    itemIndex + 1,
                    batch.Count);
            }

            await ApplyProfileTemplatesToFilesAsync(
                job,
                configPath,
                completedPaths,
                requireSuccessfulEnhancement: false,
                cancellationToken);
        }
        AppendLog(job, $"enhancement workflow: lyrics refresh completed ({uniqueTracks.Count} track(s)).");
    }

    private DeezSpoTagSettings BuildEnhancementLyricsSettings(JsonObject configRoot)
    {
        var settings = _settingsService.LoadSettings();
        var technical = TryReadTechnicalSettings(configRoot);
        if (technical != null)
        {
            TechnicalLyricsSettingsApplier.Apply(settings, technical);
        }

        return settings;
    }

    private static void ApplyProfileArtworkExtras(JsonObject configRoot, DeezSpoTagSettings settings)
    {
        var animatedFormats = configRoot["animatedArtworkFormats"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(animatedFormats))
        {
            settings.AnimatedArtworkFormats = animatedFormats.Trim();
        }

        var coverImageTemplate = configRoot["coverImageTemplate"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(coverImageTemplate))
        {
            settings.CoverImageTemplate = coverImageTemplate.Trim();
        }

        var localArtworkFormat = configRoot["localArtworkFormat"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(localArtworkFormat))
        {
            settings.LocalArtworkFormat = localArtworkFormat.Trim();
        }
    }

    private static IReadOnlyCollection<CoverSourceName> ResolveProfileCoverSources(DeezSpoTagSettings settings)
    {
        var sources = new List<CoverSourceName>();
        foreach (var raw in (settings.ArtworkFallbackOrder ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var source = raw.Trim().ToLowerInvariant() switch
            {
                "apple" or "applemusic" or "itunes" => (CoverSourceName?)CoverSourceName.Itunes,
                "deezer" => CoverSourceName.Deezer,
                "discogs" => CoverSourceName.Discogs,
                "lastfm" or "last.fm" => CoverSourceName.LastFm,
                "coverartarchive" => CoverSourceName.CoverArtArchive,
                _ => null
            };
            if (source.HasValue && !sources.Contains(source.Value))
            {
                sources.Add(source.Value);
            }
            if (!settings.ArtworkFallbackEnabled)
            {
                break;
            }
        }

        return sources;
    }

    private static int ResolveProfileCoverResolution(
        JsonObject configRoot,
        DeezSpoTagSettings settings,
        int fallback)
    {
        if (configRoot[AutoTagLiterals.CustomKey] is not JsonObject custom)
        {
            return fallback;
        }

        foreach (var raw in (settings.ArtworkFallbackOrder ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var aliases = raw.Trim().ToLowerInvariant() switch
            {
                "apple" or "applemusic" => new[] { "itunes", "applemusic" },
                _ => new[] { raw.Trim().ToLowerInvariant() }
            };
            foreach (var alias in aliases)
            {
                if (custom[alias] is not JsonObject platform
                    || platform["art_resolution"] is not JsonValue resolutionNode)
                {
                    continue;
                }

                if (resolutionNode.TryGetValue<int>(out var resolution))
                {
                    return Math.Clamp(resolution, 300, 5000);
                }
                if (resolutionNode.TryGetValue<string>(out var rawResolution)
                    && int.TryParse(rawResolution, out resolution))
                {
                    return Math.Clamp(resolution, 300, 5000);
                }
            }
        }

        return fallback;
    }

    private TechnicalTagSettings? TryReadTechnicalSettings(JsonObject? configRoot)
    {
        if (configRoot == null
            || configRoot["technical"] is not JsonObject technicalNode)
        {
            return null;
        }

        try
        {
            return technicalNode.Deserialize<TechnicalTagSettings>(_jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to parse technical settings from enhancement config root.");
            return null;
        }
    }

    private static IReadOnlyList<QualityScanTrackDto> FilterTracksByScopedFolders(
        IReadOnlyList<QualityScanTrackDto> tracks,
        List<long> scopedFolderIds)
    {
        if (scopedFolderIds.Count <= 1)
        {
            return tracks;
        }

        var allowed = scopedFolderIds.ToHashSet();
        return tracks
            .Where(track => track.DestinationFolderId.HasValue && allowed.Contains(track.DestinationFolderId.Value))
            .ToList();
    }

    private async Task<IReadOnlyList<FolderDto>> ResolveEnabledMusicFoldersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FolderDto> folders;
        try
        {
            folders = _libraryRepository.IsConfigured
                ? await _libraryRepository.GetFoldersAsync(cancellationToken)
                : await _activityLog.GetFoldersAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            folders = await _activityLog.GetFoldersAsync();
        }

        return folders
            .Where(folder => folder.Enabled
                && !string.IsNullOrWhiteSpace(folder.RootPath)
                && IsMusicCapableFolder(folder))
            .ToList();
    }

    private static List<FolderDto> ResolveScopedFolders(
        string rootPath,
        JsonObject workflowOptions,
        IReadOnlyList<FolderDto> enabledFolders)
    {
        var requestedIds = ParseFolderIds(workflowOptions, "folderIds");
        if (requestedIds.Count > 0)
        {
            var requested = requestedIds.ToHashSet();
            return enabledFolders
                .Where(folder => requested.Contains(folder.Id))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new List<FolderDto>();
        }

        return enabledFolders
            .Where(folder => PathsOverlap(rootPath, folder.RootPath))
            .ToList();
    }

    private static List<string> ResolveRootPathsForWorkflow(
        string rootPath,
        JsonObject workflowOptions,
        IReadOnlyList<FolderDto> enabledFolders)
    {
        var scopedFolders = ResolveScopedFolders(rootPath, workflowOptions, enabledFolders);
        if (scopedFolders.Count > 0)
        {
            return scopedFolders
                .Select(folder => folder.RootPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFullPath)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new List<string>();
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        return Directory.Exists(normalizedRoot)
            ? new List<string> { normalizedRoot }
            : new List<string>();
    }
}
