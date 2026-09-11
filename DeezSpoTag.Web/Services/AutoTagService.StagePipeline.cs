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

    private async Task<AutoMoveExecutionResult> RunFinalAutoMoveAsync(
        AutoTagJob job,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        if (!IsManualEnrichmentRunIntent(job.RunIntent)
            && ConfiguredDownloadRootResolver.TryResolve(
                _settingsService,
                "download location",
                "download location is not configured.",
                out var configuredDownloadRoot,
                out _)
            && IsPathUnderRoot(path, configuredDownloadRoot))
        {
            AppendLog(job, "auto-move skipped: download-root finalization is owned by download orchestration");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for download-root run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        if (IsEnhancementRunIntent(job.RunIntent))
        {
            AppendLog(job, "auto-move skipped: enhancement run uses configured enhancement workflows only");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for enhancement run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog(job, "auto-move skipped: download enrichment finalization is owned by download orchestration");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for download enrichment run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        var (taggedFiles, failedFiles) = BuildMoveFileSets(fileOutcomes);
        if (IsManualEnrichmentRunIntent(job.RunIntent))
        {
            failedFiles = Array.Empty<string>();
        }
        AppendLog(job, "tagging completed, auto-move starting");
        var result = await MoveAfterAutoTagAsync(job, path, configPath, taggedFiles, failedFiles, cancellationToken);
        if (!IsManualEnrichmentRunIntent(job.RunIntent) || !result.Completed)
        {
            return result;
        }

        var remainingTaggedFiles = taggedFiles
            .Where(file => !string.IsNullOrWhiteSpace(file) && File.Exists(file))
            .ToList();
        if (remainingTaggedFiles.Count == 0)
        {
            return result;
        }

        result.Summary.Error = $"Manual enrichment finalization left {remainingTaggedFiles.Count} enriched file(s) in staging.";
        ApplyAutoMoveSummary(job, result.Summary);
        AppendLog(job, result.Summary.Error);
        return new AutoMoveExecutionResult(false, result.Summary);
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

    private async Task IngestKnownFilesAfterAutoMoveAsync(
        AutoTagJob job,
        AutoTagMoveSummary autoMoveSummary,
        CancellationToken cancellationToken)
    {
        if (autoMoveSummary.MovedCount <= 0)
        {
            return;
        }

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            AppendLog(job, "post auto-move direct library ingestion skipped (downloads active).");
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Post auto-move direct library ingestion skipped because downloads became active."));
            return;
        }

        var changedFolderIds = await ResolveChangedLibraryFolderIdsAsync(autoMoveSummary, cancellationToken);
        if (changedFolderIds.Count == 0)
        {
            AppendLog(job, "post auto-move direct library ingestion skipped (no moved library folders).");
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Post auto-move direct library ingestion skipped because no changed library folders were detected."));
            return;
        }

        if (autoMoveSummary.ChangedFilePaths.Count > 0)
        {
            var changedFilesByFolder = await ResolveChangedLibraryFilesByFolderAsync(
                autoMoveSummary,
                changedFolderIds,
                cancellationToken);
            if (changedFilesByFolder.Count == 0)
            {
                changedFilesByFolder = changedFolderIds.ToDictionary(
                    folderId => folderId,
                    _ => autoMoveSummary.ChangedFilePaths.ToList());
            }

            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Post auto-move direct library ingestion starting for {autoMoveSummary.ChangedFilePaths.Count} file(s) in folder(s): {string.Join(", ", changedFolderIds)}."));
            AppendLog(job, $"post auto-move direct library ingestion starting for {autoMoveSummary.ChangedFilePaths.Count} file(s) in folder(s): {string.Join(", ", changedFolderIds)}");
            var ingestion = await _knownFileIngestionService.IngestAndVerifyAsync(
                changedFilesByFolder,
                cancellationToken);
            if (!ingestion.IsComplete)
            {
                _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "error",
                    $"Post auto-move direct library ingestion incomplete; {ingestion.MissingFilePaths.Count} moved audio file(s) are missing from the library DB."));
                AppendLog(job, $"post auto-move direct library ingestion incomplete; {ingestion.MissingFilePaths.Count} moved audio file(s) missing from DB");
            }
            return;
        }

        _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Post auto-move direct library ingestion skipped because no changed file paths were reported (folders={string.Join(", ", changedFolderIds)}, moved={autoMoveSummary.MovedCount}, skipped={autoMoveSummary.SkippedCount}, failed={autoMoveSummary.FailedCount})."));
        AppendLog(job, $"post auto-move direct library ingestion skipped (no changed file paths; folders={string.Join(", ", changedFolderIds)}, moved={autoMoveSummary.MovedCount}, skipped={autoMoveSummary.SkippedCount}, failed={autoMoveSummary.FailedCount})");
    }

    private async Task<List<long>> ResolveChangedLibraryFolderIdsAsync(
        AutoTagMoveSummary autoMoveSummary,
        CancellationToken cancellationToken)
    {
        var changed = autoMoveSummary.ChangedFolderIds
            .Where(folderId => folderId > 0)
            .ToHashSet();
        if (autoMoveSummary.MovedCount <= 0 || autoMoveSummary.DestinationRoots.Count == 0 || !_libraryRepository.IsConfigured)
        {
            return changed.OrderBy(folderId => folderId).ToList();
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        foreach (var destinationRoot in autoMoveSummary.DestinationRoots)
        {
            AddMatchingLibraryFolders(destinationRoot, folders, changed);
        }

        return changed.OrderBy(folderId => folderId).ToList();
    }

    private async Task<Dictionary<long, List<string>>> ResolveChangedLibraryFilesByFolderAsync(
        AutoTagMoveSummary autoMoveSummary,
        List<long> changedFolderIds,
        CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<long, List<string>>();
        if (autoMoveSummary.ChangedFilePaths.Count == 0
            || changedFolderIds.Count == 0
            || !_libraryRepository.IsConfigured)
        {
            return grouped;
        }

        var changedFolderIdSet = changedFolderIds.ToHashSet();
        var folders = (await _libraryRepository.GetFoldersAsync(cancellationToken))
            .Where(folder => changedFolderIdSet.Contains(folder.Id))
            .ToList();

        foreach (var path in autoMoveSummary.ChangedFilePaths)
        {
            foreach (var folder in folders)
            {
                TryAddPathToFolderGroup(grouped, path, folder);
            }
        }

        return grouped;
    }

    private static void TryAddPathToFolderGroup(
        Dictionary<long, List<string>> grouped,
        string path,
        FolderDto folder)
    {
        try
        {
            if (!IsPathUnderRoot(path, folder.RootPath))
            {
                return;
            }

            if (!grouped.TryGetValue(folder.Id, out var paths))
            {
                paths = new List<string>();
                grouped[folder.Id] = paths;
            }

            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore paths the runtime cannot normalize; folder-level fallback remains available.
        }
    }

    private static void AddMatchingLibraryFolders(
        string destinationRoot,
        IReadOnlyCollection<FolderDto> folders,
        HashSet<long> changed)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return;
        }

        foreach (var folder in folders.Where(static folder => folder.Id > 0 && !string.IsNullOrWhiteSpace(folder.RootPath)))
        {
            try
            {
                if (IsPathUnderRoot(destinationRoot, folder.RootPath))
                {
                    changed.Add(folder.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ignore paths the runtime cannot normalize; explicit changed folder ids remain authoritative.
            }
        }
    }

    private static List<long> ParseFolderIds(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is not JsonArray values)
        {
            return new List<long>();
        }

        var parsed = new List<long>();
        foreach (var item in values)
        {
            if (item is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var longValue) && longValue > 0)
            {
                parsed.Add(longValue);
                continue;
            }

            if (item is JsonValue stringValue
                && stringValue.TryGetValue<string>(out var raw)
                && long.TryParse(raw, out var parsedValue)
                && parsedValue > 0)
            {
                parsed.Add(parsedValue);
            }
        }

        return parsed
            .Distinct()
            .ToList();
    }

    private static bool IsMusicCapableFolder(FolderDto folder)
    {
        var normalized = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        return !normalized.Contains("video", StringComparison.Ordinal)
            && !normalized.Contains("podcast", StringComparison.Ordinal);
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftPrefix = normalizedLeft + Path.DirectorySeparatorChar;
        var rightPrefix = normalizedRight + Path.DirectorySeparatorChar;
        return normalizedLeft.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedRight.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadBoundedInt(JsonObject node, string propertyName, int fallback, int min, int max)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return Math.Clamp(intValue, min, max);
        }

        if (value.TryGetValue<string>(out var raw) && int.TryParse(raw, out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return fallback;
    }

    private static int? ReadOptionalInt(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        if (valueNode is JsonValue intNode && intNode.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (valueNode is JsonValue stringNode
            && stringNode.TryGetValue<string>(out var raw)
            && int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private JsonObject? LoadConfigRoot(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag config could not be read.");
            return null;
        }
    }

    private static AutoTagOrganizerOptions BuildFolderUniformityOptions(
        JsonObject folderUniformity)
    {
        var options = new AutoTagOrganizerOptions
        {
            IncludeSubfolders = ReadBool(folderUniformity, AutoTagLiterals.IncludeSubfoldersKey) ?? true,
            MoveMisplacedFiles = ReadBool(folderUniformity, "moveMisplacedFiles") ?? true,
            MergeIntoExistingDestinationFolders = ReadBool(folderUniformity, "mergeIntoExistingDestinationFolders") != false,
            RenameFilesToTemplate = ReadBool(folderUniformity, "renameFilesToTemplate") != false,
            RemoveEmptyFolders = ReadBool(folderUniformity, "removeEmptyFolders") != false,
            MergeNoAudioArtistFolders = ReadBool(folderUniformity, "mergeNoAudioArtistFolders") != false,
            ReconcileOrphanArtistFolders = ReadBool(folderUniformity, "reconcileOrphanArtistFolders") != false,
            QuarantineNoAudioDirectories = ReadBool(folderUniformity, "quarantineNoAudioDirectories") == true,
            ResolveSameTrackQualityConflicts = ReadBool(folderUniformity, "resolveSameTrackQualityConflicts") != false,
            KeepBothOnUnresolvedConflicts = ReadBool(folderUniformity, "keepBothOnUnresolvedConflicts") != false,
            OnlyMoveWhenTagged = ReadBool(folderUniformity, "onlyMoveWhenTagged") == true,
            OnlyReorganizeAlbumsWithFullTrackSets = ReadBool(folderUniformity, "onlyReorganizeAlbumsWithFullTrackSets") == true,
            SkipCompilationFolders = ReadBool(folderUniformity, "skipCompilationFolders") == true,
            SkipVariousArtistsFolders = ReadBool(folderUniformity, "skipVariousArtistsFolders") == true,
            GenerateReconciliationReport = ReadBool(folderUniformity, "generateReconciliationReport") == true,
            UseShazamForUntaggedFiles = ReadBool(folderUniformity, "useShazamForUntaggedFiles") == true,
            DuplicateConflictPolicy = folderUniformity["duplicateConflictPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.DuplicateConflictKeepBest,
            DuplicatesFolderName = folderUniformity["duplicatesFolderName"]?.GetValue<string>() ?? DuplicateCleanerService.DuplicatesFolderName
        };

        return options;
    }

    private static List<string> ResolveEnhancementRequestedTags(JsonObject baseRoot)
        => AutoTagPlatformTagContract.ResolveRequestedTags(baseRoot);

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
        }
        else if (string.Equals(context.RunIntent, AutoTagLiterals.RunIntentEnhancementOnly, StringComparison.OrdinalIgnoreCase))
        {
            stageRoot[AutoTagLiterals.LibraryWideEnhancementBatchSizeKey] = 40;
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

    private static JsonObject CloneRoot(JsonObject root)
    {
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return (JsonNode.Parse(json) as JsonObject) ?? new JsonObject();
    }

    private static string ComputeConfigHash(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(configJson);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static List<string> ReadStringList(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(item => item?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
    }

    private static void WriteStringList(JsonObject root, string propertyName, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            array.Add(value);
        }

        root[propertyName] = array;
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

    private async Task<Dictionary<string, PlatformTagCapabilities>> LoadPlatformCapabilitiesAsync()
    {
        var result = new Dictionary<string, PlatformTagCapabilities>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await _metadataService.GetPlatformsJsonAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            if (JsonNode.Parse(json) is not JsonArray array)
            {
                return result;
            }

            foreach (var node in array.OfType<JsonObject>())
            {
                var id = GetPlatformId(node);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var supportedTags = ReadPlatformList(node, "supportedTags");
                var downloadTags = ReadPlatformList(node, AutoTagLiterals.DownloadTagsKey);
                var requiresAuth = ReadPlatformRequiresAuth(node);

                var normalizedSupported = supportedTags
                    .Select(NormalizeSupportedTagKey)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in downloadTags
                             .Select(NormalizeSupportedTagKey)
                             .Where(tag => !string.IsNullOrWhiteSpace(tag))
                             .Select(tag => tag!))
                {
                    normalizedSupported.Add(tag);
                }

                result[id.Trim()] = new PlatformTagCapabilities(normalizedSupported, requiresAuth);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load AutoTag platform metadata.");
        }

        return result;
    }

    private static string? GetPlatformId(JsonObject node)
    {
        return node["id"]?.GetValue<string>()
            ?? node[AutoTagLiterals.PlatformKey]?["id"]?.GetValue<string>();
    }

    private static List<string> ReadPlatformList(JsonObject node, string key)
    {
        var values = ReadStringList(node, key);
        if (values.Count == 0 && node[AutoTagLiterals.PlatformKey] is JsonObject platformNode)
        {
            values = ReadStringList(platformNode, key);
        }

        return values;
    }

    private static bool ReadPlatformRequiresAuth(JsonObject node)
    {
        return ReadBool(node, "requiresAuth")
            ?? (node[AutoTagLiterals.PlatformKey] is JsonObject platformNode ? ReadBool(platformNode, "requiresAuth") : null)
            ?? false;
    }

    private static List<string> FilterSupportedTags(
        IEnumerable<string> requested,
        IEnumerable<string> platforms,
        Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        var supported = AutoTagPlatformTagContract.ToSupportedTagMap(
            platformCaps,
            static caps => caps.SupportedTags);
        return AutoTagPlatformTagContract.FilterOfferedTags(
            requested,
            platforms,
            supported,
            NormalizeSupportedTagKey);
    }

    private async Task<List<string>> ResolveEligiblePlatformsAsync(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        AutoTagJob job)
    {
        var configured = ReadStringList(baseRoot, AutoTagLiterals.PlatformsKey)
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Select(platform => platform.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configured.Count == 0)
        {
            return new List<string>();
        }

        var candidates = configured;

        if (candidates.Count == 0)
        {
            return candidates;
        }

        PlatformAuthState? authState = null;
        try
        {
            authState = await _platformAuthService.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load platform auth state while filtering AutoTag platforms.");
        }

        var removedUnauthenticated = new List<string>();
        var eligible = new List<string>();

        foreach (var platform in candidates)
        {
            if (!RequiresPlatformAuth(platform, platformCaps))
            {
                eligible.Add(platform);
                continue;
            }

            if (IsPlatformAuthenticated(platform, authState))
            {
                eligible.Add(platform);
                continue;
            }

            removedUnauthenticated.Add(platform);
        }

        if (removedUnauthenticated.Count > 0)
        {
            AppendLog(job, $"platform filter: excluded unauthenticated platforms ({string.Join(", ", removedUnauthenticated)})");
        }

        return eligible;
    }

    private static bool RequiresPlatformAuth(string platformId, Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return false;
        }

        if (platformCaps.TryGetValue(platformId.Trim(), out var caps))
        {
            return caps.RequiresAuth;
        }

        return platformId.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.SpotifySource => true,
            AutoTagLiterals.DiscogsPlatform => true,
            AutoTagLiterals.LastFmPlatform => true,
            AutoTagLiterals.BpmSupremePlatform => true,
            AutoTagLiterals.AppleMusicPlatform => true,
            AutoTagLiterals.PlexPlatform => true,
            AutoTagLiterals.JellyfinPlatform => true,
            _ => false
        };
    }

    private static bool IsPlatformAuthenticated(string platformId, PlatformAuthState? state)
    {
        var key = platformId.Trim().ToLowerInvariant();
        return key switch
        {
            AutoTagLiterals.SpotifySource => IsSpotifyAuthenticated(state?.Spotify),
            AutoTagLiterals.DiscogsPlatform => !string.IsNullOrWhiteSpace(state?.Discogs?.Token),
            AutoTagLiterals.LastFmPlatform => !string.IsNullOrWhiteSpace(state?.LastFm?.ApiKey),
            AutoTagLiterals.BpmSupremePlatform => HasBpmSupremeCredentials(state?.BpmSupreme),
            AutoTagLiterals.AppleMusicPlatform => state?.AppleMusic?.WrapperReady == true,
            AutoTagLiterals.ITunesPlatform => state?.AppleMusic?.WrapperReady == true,
            AutoTagLiterals.PlexPlatform => IsPlexAuthenticated(state?.Plex),
            AutoTagLiterals.JellyfinPlatform => IsJellyfinAuthenticated(state?.Jellyfin),
            _ => false
        };
    }

    private static bool IsSpotifyAuthenticated(SpotifyConfig? spotify)
    {
        if (spotify == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(spotify.ActiveAccount))
        {
            var active = spotify.Accounts.FirstOrDefault(account =>
                account.Name.Equals(spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase));
            if (active != null && !string.IsNullOrWhiteSpace(active.BlobPath) && File.Exists(active.BlobPath))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(spotify.WebPlayerSpDc))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(spotify.ClientId) &&
            !string.IsNullOrWhiteSpace(spotify.ClientSecret);
    }

    private static bool HasBpmSupremeCredentials(BpmSupremeAuth? bpmSupreme)
    {
        var email = bpmSupreme?.Email;
        var password = bpmSupreme?.Password;
        return !string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password);
    }

    private static bool IsPlexAuthenticated(PlexAuth? plex)
    {
        return plex is not null &&
            !string.IsNullOrWhiteSpace(plex.Url) &&
            !string.IsNullOrWhiteSpace(plex.Token);
    }

    private static bool IsJellyfinAuthenticated(JellyfinAuth? jellyfin)
    {
        return jellyfin is not null &&
            !string.IsNullOrWhiteSpace(jellyfin.Url) &&
            (!string.IsNullOrWhiteSpace(jellyfin.ApiKey) ||
             !string.IsNullOrWhiteSpace(jellyfin.Username));
    }

    private static bool? ReadBool(JsonObject? node, string propertyName)
    {
        if (node == null)
        {
            return null;
        }

        if (!node.TryGetPropertyValue(propertyName, out var value) || value is not JsonValue jsonValue)
        {
            return null;
        }

        return jsonValue.TryGetValue<bool>(out var parsed) ? parsed : null;
    }

    private static string NormalizeDownloadTagSource(string? downloadTagSource)
    {
        return DownloadTagSourceHelper.NormalizeStoredSource(downloadTagSource, AutoTagLiterals.DeezerSource);
    }

    private static string? ResolveDownloadSourcePlatform(JsonObject root)
    {
        if (!root.TryGetPropertyValue(AutoTagLiterals.DownloadTagSourceKey, out var sourceNode) || sourceNode is null)
        {
            return null;
        }

        if (sourceNode is not JsonValue sourceValue || !sourceValue.TryGetValue<string>(out var rawSource))
        {
            return null;
        }

        return NormalizeDownloadTagSource(rawSource) switch
        {
            AutoTagLiterals.DeezerSource => AutoTagLiterals.DeezerSource,
            AutoTagLiterals.SpotifySource => AutoTagLiterals.SpotifySource,
            _ => null
        };
    }

    private static string? NormalizeSupportedTagKey(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var normalized = tag.Trim();
        return SupportedTagKeyMap.TryGetValue(normalized, out var mapped) ? mapped : null;
    }

    private void NotifyCompleted(AutoTagJob job)
    {
        _activeJobStages.TryRemove(job.Id, out _);
        _activeJobIds.TryRemove(job.Id, out _);
        _jobCancellationSources.TryRemove(job.Id, out _);

        try
        {
            JobCompleted?.Invoke(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "AutoTag job {JobId}: completion handler failed.", job.Id);
            }
        }
    }

    private static AutoTagOrganizerOptions LoadOrganizerOptions(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return new AutoTagOrganizerOptions();
            }

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AutoTagOrganizerOptions();
            }

            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null)
            {
                return new AutoTagOrganizerOptions();
            }

            var enhancementNode = node[AutoTagLiterals.EnhancementStage] as JsonObject;
            var folderUniformityNode = enhancementNode?["folderUniformity"] as JsonObject;
            var tagsNode = node["tags"] as JsonObject;
            var options = new AutoTagOrganizerOptions
            {
                OnlyMoveWhenTagged = ReadBool(folderUniformityNode, "onlyMoveWhenTagged") == true,
                MoveTaggedPath = node["moveSuccess"]?.GetValue<bool>() == true
                    ? node["moveSuccessPath"]?.GetValue<string>()
                    : null,
                MoveUntaggedPath = node["moveFailed"]?.GetValue<bool>() == true
                    ? node["moveFailedPath"]?.GetValue<string>()
                    : null,
                IncludeSubfolders = node[AutoTagLiterals.IncludeSubfoldersKey]?.GetValue<bool>() ?? true,
                MoveMisplacedFiles = ReadBool(folderUniformityNode, "moveMisplacedFiles") ?? true,
                RenameFilesToTemplate = ReadBool(folderUniformityNode, "renameFilesToTemplate") != false,
                RemoveEmptyFolders = ReadBool(folderUniformityNode, "removeEmptyFolders") != false,
                UsePrimaryArtistFoldersOverride =
                    tagsNode?["singleAlbumArtist"]?.GetValue<bool?>(),
                MultiArtistSeparatorOverride =
                    tagsNode?[AutoTagLiterals.MultiArtistSeparatorKey]?.GetValue<string>()
            };

            return options;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoTagOrganizerOptions();
        }
    }

    private async Task<AutoTagOrganizerOptions> LoadOrganizerOptionsAsync(AutoTagJob job, string configPath)
    {
        var options = LoadOrganizerOptions(configPath);
        await ApplyJobProfileOrganizerOverridesAsync(job, options, CancellationToken.None);
        return options;
    }

    private async Task ApplyJobProfileOrganizerOverridesAsync(
        AutoTagJob job,
        AutoTagOrganizerOptions options,
        CancellationToken cancellationToken)
    {
        var profile = await ResolveJobProfileAsync(job, cancellationToken);
        if (profile != null)
        {
            AutoTagOrganizerProfileOverlay.ApplyTaggingProfileOverrides(options, profile);
            return;
        }
        throw new InvalidOperationException("AutoTag organization requires a valid profile.");
    }

    private async Task<TaggingProfile?> ResolveJobProfileAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.ProfileId) && string.IsNullOrWhiteSpace(job.ProfileName))
        {
            return null;
        }

        try
        {
            var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: false, cancellationToken);
            return AutoTagProfileResolutionService.ResolveProfileReference(state.Profiles, job.ProfileId)
                ?? AutoTagProfileResolutionService.ResolveProfileReference(state.Profiles, job.ProfileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve AutoTag profile for organizer overrides.");
            return null;
        }
    }

    private async Task<AutoMoveExecutionResult> MoveAfterAutoTagAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        IReadOnlyCollection<string> taggedFiles,
        IReadOnlyCollection<string> failedFiles,
        CancellationToken cancellationToken)
    {
        if (_disableAutoMove)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("AutoTag job {JobId}: auto-move skipped (disabled).", job.Id);
            }
            AppendLog(job, "auto-move skipped: disabled");
            var disabledSummary = new AutoTagMoveSummary
            {
                Error = "auto-move disabled by configuration."
            };
            ApplyAutoMoveSummary(job, disabledSummary);
            return new AutoMoveExecutionResult(false, disabledSummary);
        }

        try
        {
            _logger.LogInformation("AutoTag job JobId: auto-move started for RootPath");
            AppendLog(job, "auto-move started");
            var organizerOptions = await LoadOrganizerOptionsAsync(job, configPath);
            if (IsManualEnrichmentRunIntent(job.RunIntent))
            {
                organizerOptions.BatchScopedFilesOnly = true;
                organizerOptions.MoveUntaggedPath = null;
                organizerOptions.OnlyMoveWhenTagged = true;
            }
            var summary = await _downloadMoveService.MoveForRootWithSummaryAsync(
                rootPath,
                organizerOptions,
                taggedFiles,
                failedFiles,
                cancellationToken);
            _logger.LogInformation("AutoTag job JobId: auto-move finished for RootPath");
            AppendLog(job, "auto-move finished");
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(true, summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: auto-move failed.", job.Id);
            AppendLog(job, $"auto-move failed: {ex.Message}");
            var failedSummary = new AutoTagMoveSummary
            {
                FailedCount = 1,
                Error = ex.Message
            };
            ApplyAutoMoveSummary(job, failedSummary);
            return new AutoMoveExecutionResult(false, failedSummary);
        }
    }

    private void ApplyAutoMoveSummary(AutoTagJob job, AutoTagMoveSummary summary)
    {
        job.AutoMoveSummary = summary.Clone();
        var destinations = summary.DestinationRoots.Count > 0
            ? string.Join(", ", summary.DestinationRoots)
            : "<none>";
        const string label = "auto-move summary";
        AppendLog(
            job,
            $"{label}: moved={summary.MovedCount}, skipped={summary.SkippedCount}, failed={summary.FailedCount}, destinations=[{destinations}]");
        if (!string.IsNullOrWhiteSpace(summary.Error))
        {
            AppendLog(job, $"{label}: error={summary.Error}");
        }

        SaveJob(job);
    }

    private async Task<bool> TriggerPlexScanAfterMoveAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        var plex = await LoadConfiguredPlexForScanAsync(job);
        if (plex == null)
        {
            return false;
        }

        return await TriggerPlexScanAsync(job, plex, "after auto-move", cancellationToken);
    }

    private async Task TriggerConfiguredMediaServerRefreshAfterEnhancementAsync(
        AutoTagJob job,
        bool includesEnhancementStage,
        CancellationToken cancellationToken)
    {
        if (!includesEnhancementStage
            || (!ShouldRunEnhancementForIntent(job.RunIntent)
                && !IsManualEnrichmentRunIntent(job.RunIntent)))
        {
            return;
        }

        try
        {
            AppendLog(job, "media server metadata refresh starting after enhancement (request only; not waiting for a full library reindex).");
            using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            refreshTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var refresh = await _mediaServerRefreshService.RefreshConfiguredServersAsync(
                refreshTimeout.Token,
                updateTrackIndex: false);
            AppendLog(
                job,
                $"media server metadata refresh requested after enhancement: configured={refresh.ConfiguredServerCount}, refreshed={refresh.RefreshedServerCount}, failed=[{string.Join(", ", refresh.FailedServers)}]");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppendLog(job, "media server metadata refresh after enhancement timed out; enhancement run will finish without waiting.");
        }
        catch (OperationCanceledException)
        {
            AppendLog(job, "media server metadata refresh after enhancement was canceled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: configured media server refresh after enhancement failed.", job.Id);
            AppendLog(job, $"media server metadata refresh after enhancement failed: {ex.Message}");
        }
    }

    private async Task<PlexAuth?> LoadConfiguredPlexForScanAsync(AutoTagJob job)
    {
        try
        {
            var authState = await _platformAuthService.LoadAsync();
            var plex = authState.Plex;
            if (!IsPlexAuthenticated(plex))
            {
                return null;
            }

            return plex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: failed loading Plex auth state for scan.", job.Id);
            return null;
        }
    }

    private async Task<bool> TriggerPlexScanAsync(
        AutoTagJob job,
        PlexAuth plex,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var plexUrl = plex.Url;
            var plexToken = plex.Token;
            if (string.IsNullOrWhiteSpace(plexUrl) || string.IsNullOrWhiteSpace(plexToken))
            {
                return false;
            }

            AppendLog(job, $"plex scan starting {reason}");

            var sections = await _plexApiClient.GetLibrarySectionsAsync(plexUrl, plexToken, cancellationToken);
            var musicSections = sections
                .Where(section => string.Equals(section.Type, AutoTagLiterals.ArtistTag, StringComparison.OrdinalIgnoreCase))
                .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (musicSections.Count == 0)
            {
                AppendLog(job, "plex scan skipped: no music libraries found");
                return false;
            }

            var refreshed = 0;
            foreach (var section in musicSections)
            {
                refreshed += await _plexApiClient.RefreshLibraryAsync(plexUrl, plexToken, section.Key, cancellationToken) ? 1 : 0;
            }

            AppendLog(job, $"plex scan requested: {musicSections.Count} libraries (refreshed={refreshed})");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: Plex scan {Reason} failed.", job.Id, reason);
            AppendLog(job, $"plex scan failed: {ex.Message}");
            return false;
        }
    }
}
