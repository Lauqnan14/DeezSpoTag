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
    private sealed record EnhancementBatchContext(
        IReadOnlyList<string> OriginalFiles,
        IReadOnlyList<string> CurrentFiles,
        IReadOnlyDictionary<long, List<string>> FilesByFolder);

    private sealed record QualityCheckOptions(
        bool FlagMissingTags,
        bool FlagMismatchedMetadata,
        bool FlagDuplicates,
        bool UseDuplicatesFolder,
        bool UseShazamForDedupe,
        string? DuplicatesFolderName,
        bool QueueAtmosAlternatives,
        bool QueueTechnicalProfileUpgrades,
        bool RunQualityUpgradeStage,
        bool RunQualityScanner,
        IReadOnlyList<string> TechnicalProfiles)
    {
        public bool ShouldRunAnyWorkflow => FlagMissingTags
            || FlagMismatchedMetadata
            || RunQualityScanner
            || FlagDuplicates;
    }

    private sealed record SidecarLyricsOptions(
        bool QueueLyricsRefresh,
        bool RemoveLineSyncedTtml,
        bool RewriteLineSyncedTtml)
    {
        public bool ShouldRun => QueueLyricsRefresh || RemoveLineSyncedTtml || RewriteLineSyncedTtml;
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

        var existingTargets = ReadStringList(root, AutoTagLiterals.TargetFilesKey);
        var requestedCount = existingTargets.Count;
        var reason = existingTargets.Count > 0
            ? (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase)
                ? EnhancementTargetReasons.RecentDownloads
                : EnhancementTargetReasons.ExplicitTarget)
            : EnhancementTargetReasons.FolderEnumeration;

        if (ShouldPrepareMissingCoreMetadataTargets(enhancementRoot) && existingTargets.Count == 0)
        {
            var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
            var scopedFolders = ResolveEnhancementJobFolders(
                job,
                enhancementRoot,
                enabledFolders,
                AutoTagLiterals.EnhancementFeatureQualityChecks);
            if (scopedFolders.Count == 0)
            {
                throw new InvalidOperationException("Enhancement could not resolve an enabled music folder scope.");
            }

            SetEnhancementPhase(job, "missing-core-metadata-db-audit", 0, 1);
            AppendLog(job, $"enhancement missing core metadata DB audit starting for {scopedFolders.Count} indexed folder scope(s).");
            var missingFiles = await _libraryRepository.GetMissingCoreMetadataFilesAsync(
                scopedFolders.Select(folder => folder.Id).ToList(),
                cancellationToken);
            var missingTargets = missingFiles
                .Select(file => file.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            AppendLog(job, $"enhancement missing core metadata DB audit finished: {missingFiles.Count} indexed file(s)");
            SetEnhancementPhase(job, "missing-core-metadata-db-audit", 1, 1);
            if (missingTargets.Count > 0)
            {
                requestedCount = missingFiles.Count;
                reason = EnhancementTargetReasons.MissingCoreMetadata;
                existingTargets = missingTargets;
                WriteStringList(root, AutoTagLiterals.TargetFilesKey, existingTargets);
                root[AutoTagLiterals.EnhancementUntrustedTargetsKey] = true;
                File.WriteAllText(configPath, root.ToJsonString(_jsonOptions), new System.Text.UTF8Encoding(false));
            }
            else
            {
                requestedCount = 0;
                reason = EnhancementTargetReasons.FolderEnumeration;
                existingTargets = new List<string>();
                root.Remove(AutoTagLiterals.TargetFilesKey);
                root.Remove(AutoTagLiterals.EnhancementUntrustedTargetsKey);
                File.WriteAllText(configPath, root.ToJsonString(_jsonOptions), new System.Text.UTF8Encoding(false));
                AppendLog(job, "enhancement missing core metadata DB audit found no files; gap-fill will use the selected folder.");
            }
        }

        var manifest = await BuildEnhancementRunManifestAsync(
            job,
            root,
            existingTargets,
            reason,
            requestedCount,
            cancellationToken);
        PersistEnhancementRunManifest(job, manifest);
        ApplyManifestTarget(job, manifest);
        var stale = Math.Max(0, manifest.RequestedCount - manifest.UsableCount);
        AppendLog(
            job,
            $"enhancement target: reason={manifest.Reason} requested={manifest.RequestedCount} usable={manifest.UsableCount} stale={stale}");
    }

    private static bool ShouldPrepareMissingCoreMetadataTargets(JsonObject enhancementRoot)
    {
        return enhancementRoot["qualityChecks"] is JsonObject qualityChecks
            && ReadBool(qualityChecks, EnabledField) == true
            && ReadBool(qualityChecks, "flagMissingTags") == true;
    }

    private async Task<EnhancementRunManifest> BuildEnhancementRunManifestAsync(
        AutoTagJob job,
        JsonObject root,
        IReadOnlyList<string> configuredTargets,
        string reason,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        var paths = configuredTargets.Count > 0
            ? configuredTargets
                .Select(NormalizePathForJob)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : EnumerateEnhancementRootAudioFiles(job.RootPath ?? root["path"]?.GetValue<string>(), root);
        if (requestedCount <= 0)
        {
            requestedCount = paths.Count;
        }

        var trackIdsByPath = paths.Count == 0
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : await _libraryRepository.GetTrackIdsByFilePathsAsync(paths, cancellationToken);
        var items = paths
            .Select(path => new EnhancementRunManifestItem
            {
                TrackId = trackIdsByPath.TryGetValue(path, out var trackId) ? trackId : null,
                OriginalPath = path,
                CurrentPath = path
            })
            .ToList();
        return new EnhancementRunManifest
        {
            Reason = reason,
            RequestedCount = requestedCount,
            UsableCount = items.Count,
            Items = items
        };
    }

    private static List<string> EnumerateEnhancementRootAudioFiles(string? rootPath, JsonObject root)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return new List<string>();
        }

        var includeSubfolders = ReadBool(root, AutoTagLiterals.IncludeSubfoldersKey) ?? true;
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(rootPath, "*.*", option)
            .Where(path => EligibleAudioExtensions.Contains(Path.GetExtension(path)))
            .Select(NormalizePathForJob)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void PersistEnhancementRunManifest(AutoTagJob job, EnhancementRunManifest manifest)
    {
        Directory.CreateDirectory(_runtimeConfigDir);
        var path = GetEnhancementManifestPath(job.Id);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(manifest, _jsonOptions),
            new System.Text.UTF8Encoding(false));
        job.EnhancementManifestPath = path;
    }

    private EnhancementRunManifest? LoadEnhancementRunManifest(AutoTagJob job)
    {
        var path = string.IsNullOrWhiteSpace(job.EnhancementManifestPath)
            ? GetEnhancementManifestPath(job.Id)
            : job.EnhancementManifestPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EnhancementRunManifest>(File.ReadAllText(path), _jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Enhancement manifest could not be read for job {JobId}.", job.Id);
            return null;
        }
    }

    private void SaveEnhancementRunManifest(AutoTagJob job, EnhancementRunManifest manifest)
    {
        ApplyManifestTarget(job, manifest);
        PersistEnhancementRunManifest(job, manifest);
        SaveJob(job);
    }

    private static void ApplyManifestTarget(AutoTagJob job, EnhancementRunManifest manifest)
    {
        job.TargetReason = manifest.Reason;
        job.TargetRequested = manifest.RequestedCount;
        job.TargetUsable = manifest.UsableCount;
        job.TotalItems = manifest.UsableCount;
    }

    private string GetEnhancementManifestPath(string jobId)
        => Path.Join(_runtimeConfigDir, $"autotag-{jobId}-manifest.json");

    private void UpdateManifestPathsFromReports(
        AutoTagJob job,
        IReadOnlyList<AutoTagLibraryOrganizer.AutoTagOrganizerReport> reports)
    {
        var manifest = LoadEnhancementRunManifest(job);
        if (manifest == null || manifest.Items.Count == 0)
        {
            return;
        }

        var moves = reports
            .SelectMany(static report => report.Entries)
            .Select(TryParseMoveFileEntry)
            .Where(static move => move.Source != null && move.Destination != null)
            .ToDictionary(
                static move => NormalizePathForJob(move.Source!),
                static move => NormalizePathForJob(move.Destination!),
                StringComparer.OrdinalIgnoreCase);
        if (moves.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var item in manifest.Items)
        {
            if (!moves.TryGetValue(item.CurrentPath, out var destination)
                && !moves.TryGetValue(item.OriginalPath, out destination))
            {
                continue;
            }

            if (string.Equals(item.CurrentPath, destination, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendLog(job, $"enhancement path updated: {item.CurrentPath} -> {destination}");
            item.CurrentPath = destination;
            changed = true;
        }

        if (changed)
        {
            SaveEnhancementRunManifest(job, manifest);
        }
    }

    private static List<FolderDto> ResolveEnhancementJobFolders(
        AutoTagJob job,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        string? featureOverride = null)
    {
        var feature = string.IsNullOrWhiteSpace(featureOverride) ? job.EnhancementFeature : featureOverride;
        JsonObject? section = feature switch
        {
            AutoTagLiterals.EnhancementFeatureGapFill => enhancementRoot["gapFilling"] as JsonObject,
            AutoTagLiterals.EnhancementFeatureFolderUniformity => enhancementRoot["folderUniformity"] as JsonObject,
            AutoTagLiterals.EnhancementFeatureQualityChecks => enhancementRoot["qualityChecks"] as JsonObject,
            AutoTagLiterals.EnhancementFeatureSidecars
                or AutoTagLiterals.EnhancementPhaseSidecarsLyrics
                or AutoTagLiterals.EnhancementPhaseSidecarsCovers
                or AutoTagLiterals.EnhancementFeatureCoverMaintenance
                => ResolveSidecarFolderSection(enhancementRoot),
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
        job.CurrentBatch = Math.Max(0, currentBatch);
        job.BatchCount = Math.Max(0, batchCount);
        job.BatchProcessed = Math.Max(0, batchProcessed);
        job.BatchSize = Math.Max(0, batchSize);
        if (job.TargetUsable > 0)
        {
            job.TotalItems = job.TargetUsable;
        }
        else
        {
            job.ProcessedItems = Math.Max(0, processed);
            job.TotalItems = Math.Max(0, total);
            if (total > 0)
            {
                job.Progress = Math.Clamp(processed / (double)total, 0d, 1d);
            }
        }

        job.CurrentPlatform = string.IsNullOrWhiteSpace(job.EnhancementFeature)
            ? AutoTagLiterals.EnhancementStage
            : job.EnhancementFeature;
        SaveJob(job);
    }

    private void PublishEnhancementPhaseHeartbeat(AutoTagJob job, string feature, string message)
    {
        SetEnhancementPhase(
            job,
            feature,
            job.ProcessedItems,
            job.TotalItems,
            job.CurrentBatch,
            job.BatchCount,
            job.BatchProcessed,
            job.BatchSize);
        var update = new TaggingStatusWrap
        {
            Platform = feature,
            Progress = job.Progress,
            Status = new TaggingStatus
            {
                Status = AutoTagLiterals.TaggingStatus,
                Path = job.RootPath ?? string.Empty,
                Message = message
            }
        };
        job.LastStatus = update;
        AppendStatusHistory(job, update);
        SaveJob(job);
    }

    private static string? BuildLyricsCoverUrl(string? coverPath)
        => string.IsNullOrWhiteSpace(coverPath)
            ? null
            : $"/api/library/image?path={Uri.EscapeDataString(coverPath)}&size=240";

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
        int batchSize,
        LyricsRefreshTrackResult? lyrics = null,
        IReadOnlyList<string>? artworkBadges = null,
        string? sourceTitle = null,
        string? sourceArtist = null,
        string? coverPath = null)
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
                Message = message,
                SourceTitle = lyrics?.Title ?? sourceTitle,
                SourceArtist = lyrics?.ArtistName ?? sourceArtist,
                LyricsTrackId = lyrics?.TrackId,
                LyricsCoverUrl = BuildLyricsCoverUrl(lyrics?.CoverPath ?? coverPath),
                LyricsBadges = lyrics?.TimingBadges.ToList() ?? new List<string>(),
                ArtworkBadges = artworkBadges?.ToList() ?? new List<string>()
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
        var alreadyRanSidecars = job.EnhancementWorkflows.Any(workflow =>
            string.Equals(workflow.Name, AutoTagLiterals.EnhancementFeatureSidecars, StringComparison.OrdinalIgnoreCase));
        if (!alreadyRanSidecars && EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot))
        {
            await RunEnhancementWorkflowAsync(
                job,
                AutoTagLiterals.EnhancementFeatureSidecars,
                token => RunConfiguredSidecarsAsync(
                    job,
                    rootPath,
                    root,
                    enhancementRoot,
                    enabledFolders,
                    configPath,
                    token),
                cancellationToken);
        }

        if (EnhancementWorkflowSelection.IsQualityChecksRunnable(enhancementRoot))
        {
            await RunEnhancementWorkflowAsync(
                job,
                AutoTagLiterals.EnhancementFeatureQualityChecks,
                token => RunConfiguredQualityChecksAsync(
                    job,
                    rootPath,
                    enhancementRoot,
                    enabledFolders,
                    configPath,
                    token),
                cancellationToken);
        }

        if (EnhancementWorkflowSelection.IsFolderUniformityRunnable(enhancementRoot))
        {
            await RunEnhancementWorkflowAsync(
                job,
                AutoTagLiterals.EnhancementFeatureFolderUniformity,
                token => RunConfiguredFolderUniformityAsync(job, rootPath, enhancementRoot, enabledFolders, configPath, token),
                cancellationToken);
        }
    }

    private async Task<bool> ApplyCompletedGapFillBatchAsync(
        AutoTagJob job,
        string configPath,
        IReadOnlyList<string> batchFiles,
        CancellationToken cancellationToken)
    {
        var currentFiles = batchFiles
            .Select(NormalizePathForJob)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currentFiles.Count == 0)
        {
            AppendLog(job, "enhancement batch skipped: no existing audio files remained after gap-fill.");
            return false;
        }

        var root = LoadConfigRoot(configPath);
        if (root?[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return false;
        }

        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        var context = BuildEnhancementBatchContext(currentFiles, currentFiles, enabledFolders);
        if (!EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot))
        {
            return false;
        }

        AppendLog(job, $"enhancement batch: gap-fill completed for {currentFiles.Count} file(s); running opted-in sidecars.");
        await RunEnhancementWorkflowAsync(
            job,
            AutoTagLiterals.EnhancementFeatureSidecars,
            token => RunConfiguredSidecarsAsync(
                job,
                job.RootPath ?? string.Empty,
                root,
                enhancementRoot,
                enabledFolders,
                configPath,
                token,
                currentFiles),
            cancellationToken);

        await EnqueueMediaRefreshForBatchAsync(job, context, cancellationToken);
        SaveJob(job);
        return false;
    }

    private async Task<EnhancementWorkflowOutcome> RunConfiguredSidecarsAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject configRoot,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        string configPath,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? batchFiles = null)
    {
        var sidecarEnabled = enhancementRoot["sidecars"] is JsonObject sidecars
            && ReadBool(sidecars, EnabledField) == true;
        var runLyrics = sidecarEnabled && EnhancementWorkflowSelection.HasSidecarLyricsActions(enhancementRoot);
        var runCovers = sidecarEnabled && EnhancementWorkflowSelection.HasExplicitCoverActions(enhancementRoot);
        if (!runLyrics && !runCovers)
        {
            return EnhancementWorkflowOutcome.Skipped("no sidecar actions are enabled.");
        }

        if (batchFiles is not null && batchFiles.Count == 0)
        {
            return EnhancementWorkflowOutcome.Skipped("no existing audio files remained for sidecars.");
        }

        AppendLog(
            job,
            batchFiles is not null
                ? $"enhancement batch: sidecars for {batchFiles.Count} file(s) (lyrics={runLyrics}, covers={runCovers})."
                : $"enhancement workflow: sidecars starting (lyrics={runLyrics}, covers={runCovers}).");

        if (runLyrics)
        {
            var lyricsOptions = BuildSidecarLyricsOptions(enhancementRoot);
            if (batchFiles is not null)
            {
                var context = BuildEnhancementBatchContext(batchFiles, batchFiles, enabledFolders);
                if (context.FilesByFolder.Count > 0)
                {
                    await _knownFileIngestionService.IngestAndVerifyAsync(context.FilesByFolder, cancellationToken);
                }

                var trackIdsByPath = await _libraryRepository.GetTrackIdsByFilePathsAsync(
                    context.CurrentFiles,
                    cancellationToken);
                var trackIds = context.CurrentFiles
                    .Select(path => trackIdsByPath.TryGetValue(path, out var trackId) ? trackId : 0)
                    .Where(static trackId => trackId > 0)
                    .Distinct()
                    .ToList();
                var missing = context.CurrentFiles.Count - trackIds.Count;
                if (missing > 0)
                {
                    AppendLog(job, $"enhancement batch: sidecars lyrics skipped {missing} file(s) with no library track id.");
                }

                if (trackIds.Count == 0)
                {
                    AppendLog(job, "enhancement batch: sidecars lyrics skipped (no indexed tracks were available).");
                }
                else
                {
                    AppendLog(job, $"enhancement batch: sidecars lyrics lookup starting ({trackIds.Count} track(s)).");
                    await RunLyricsRefreshForBatchAsync(job, trackIds, lyricsOptions, cancellationToken);
                }
            }
            else
            {
                var scopedFolders = ResolveEnhancementJobFolders(
                    job,
                    enhancementRoot,
                    enabledFolders,
                    AutoTagLiterals.EnhancementFeatureSidecars);
                var scopedFolderIds = scopedFolders
                    .Select(folder => folder.Id)
                    .Distinct()
                    .ToList();
                await RunLyricsRefreshIfRequestedAsync(job, lyricsOptions, scopedFolderIds, cancellationToken);
            }
        }

        if (runCovers)
        {
            var coverOutcome = await RunConfiguredCoverMaintenanceAsync(
                job,
                rootPath,
                configRoot,
                enhancementRoot,
                enabledFolders,
                configPath,
                cancellationToken,
                batchFiles);
            if (string.Equals(coverOutcome.Status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase))
            {
                return coverOutcome;
            }
        }

        return EnhancementWorkflowOutcome.Completed(
            batchFiles is not null
                ? $"sidecars finished for {batchFiles.Count} file(s)."
                : "sidecars finished.");
    }

    private static JsonObject? ResolveSidecarFolderSection(JsonObject enhancementRoot)
    {
        var sidecars = enhancementRoot["sidecars"] as JsonObject;
        var covers = enhancementRoot["coverMaintenance"] as JsonObject;
        if (sidecars != null && ParseFolderIds(sidecars, "folderIds").Count > 0)
        {
            return sidecars;
        }

        return covers ?? sidecars;
    }

    private async Task EnqueueMediaRefreshForBatchAsync(
        AutoTagJob job,
        EnhancementBatchContext context,
        CancellationToken cancellationToken)
    {
        if (context.FilesByFolder.Count == 0)
        {
            return;
        }

        foreach (var (folderId, files) in context.FilesByFolder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _mediaServerRefreshOutboxService.EnqueueAsync(folderId, files, cancellationToken);
        }

        AppendLog(job, $"enhancement batch media refresh queued for {context.FilesByFolder.Count} folder scope(s).");
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

        return EnhancementWorkflowSelection.IsFolderUniformityRunnable(enhancementRoot)
            || EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot)
            || EnhancementWorkflowSelection.IsQualityChecksRunnable(enhancementRoot);
    }

    private static bool HasConfiguredEnhancementWorkflows(JsonObject root)
        => EnhancementWorkflowSelection.HasConfiguredEnhancementWorkflows(root);

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
        string configPath,
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
        PublishEnhancementPhaseHeartbeat(
            job,
            AutoTagLiterals.EnhancementFeatureFolderUniformity,
            $"folder uniformity starting ({rootPaths.Count} path(s)).");
        var manifest = LoadEnhancementRunManifest(job);
        var targetedFiles = manifest is { Items.Count: > 0 }
            && !string.Equals(manifest.Reason, EnhancementTargetReasons.FolderEnumeration, StringComparison.OrdinalIgnoreCase)
            ? manifest.CurrentPaths.Where(File.Exists).ToList()
            : new List<string>();
        if (ReadBool(folderUniformity!, "enforceFolderStructure") != false)
        {
            if (targetedFiles.Count > 0)
            {
                var context = BuildEnhancementBatchContext(targetedFiles, targetedFiles, scopedFolders.Count > 0 ? scopedFolders : enabledFolders);
                await RunFolderUniformityForBatchAsync(job, configPath, context, requireSuccessfulEnhancement: false, cancellationToken);
            }
            else
            {
                await RunFolderUniformityForPathsAsync(job, folderUniformity!, rootPaths, profileState, scopedFoldersByPath, cancellationToken);
            }
        }
        else
        {
            AppendLog(job, "enhancement workflow: folder structure skipped (enforceFolderStructure is disabled).");
        }

        await RunFolderUniformityDedupeAsync(job, folderUniformity!, scopedFolders, rootPaths, enabledFolders, cancellationToken);

        AppendLog(job, "enhancement workflow: folder uniformity completed.");
        return EnhancementWorkflowOutcome.Completed($"processed {rootPaths.Count} path(s).");
    }

    private static EnhancementBatchContext BuildEnhancementBatchContext(
        IReadOnlyList<string> originalFiles,
        IReadOnlyList<string> currentFiles,
        IReadOnlyList<FolderDto> enabledFolders)
    {
        var filesByFolder = new Dictionary<long, List<string>>();
        foreach (var folder in enabledFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.RootPath))
            {
                continue;
            }

            var folderRoot = Path.GetFullPath(folder.RootPath);
            var folderFiles = currentFiles
                .Where(path => LibraryFolderPathSafety.IsSameOrDescendantPath(path, folderRoot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folderFiles.Count == 0)
            {
                continue;
            }

            filesByFolder[folder.Id] = folderFiles;
        }

        return new EnhancementBatchContext(
            originalFiles
                .Select(NormalizePathForJob)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            currentFiles
                .Select(NormalizePathForJob)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            filesByFolder);
    }

    private async Task<EnhancementBatchContext> RunFolderUniformityForBatchAsync(
        AutoTagJob job,
        string configPath,
        EnhancementBatchContext context,
        bool requireSuccessfulEnhancement,
        CancellationToken cancellationToken)
    {
        var root = LoadConfigRoot(configPath);
        if (root?[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return context;
        }
        if (!EnhancementWorkflowSelection.IsFolderUniformityRunnable(enhancementRoot)
            || enhancementRoot["folderUniformity"] is not JsonObject folderUniformity
            || ReadBool(folderUniformity, "enforceFolderStructure") == false)
        {
            return context;
        }

        var successfulBatchFiles = context.CurrentFiles
            .Select(NormalizePathForJob)
            .Where(path => (!requireSuccessfulEnhancement
                    || job.EnhancedFilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (successfulBatchFiles.Count == 0)
        {
            AppendLog(job, "enhancement batch folder uniformity skipped: no eligible files remained in this batch.");
            return context with { CurrentFiles = [] };
        }

        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        var profileState = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        var organizedCount = 0;
        var folderReports = new List<AutoTagLibraryOrganizer.AutoTagOrganizerReport>();
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

            var report = await _libraryOrganizer.OrganizeFilesWithReportAsync(
                folderRoot,
                folderFiles,
                options,
                line => AppendLog(job, $"enhancement batch folder uniformity: {line}"),
                cancellationToken);
            folderReports.Add(report);
            organizedCount += folderFiles.Count;
        }

        if (organizedCount != successfulBatchFiles.Count)
        {
            throw new InvalidOperationException(
                $"Template application resolved {organizedCount} of {successfulBatchFiles.Count} successfully enhanced batch files to enabled library folders.");
        }

        var currentFiles = ResolveCurrentBatchFiles(successfulBatchFiles, folderReports);
        UpdateManifestPathsFromReports(job, folderReports);
        AppendLog(job, $"enhancement batch folder uniformity completed: {organizedCount} file(s).");
        return BuildEnhancementBatchContext(context.OriginalFiles, currentFiles, enabledFolders);
    }

    private static List<string> ResolveCurrentBatchFiles(
        IReadOnlyList<string> files,
        IReadOnlyList<AutoTagLibraryOrganizer.AutoTagOrganizerReport> reports)
    {
        var moves = reports
            .SelectMany(static report => report.Entries)
            .Select(TryParseMoveFileEntry)
            .Where(static move => move.Source != null && move.Destination != null)
            .ToDictionary(
                static move => NormalizePathForJob(move.Source!),
                static move => NormalizePathForJob(move.Destination!),
                StringComparer.OrdinalIgnoreCase);
        var current = new List<string>();
        foreach (var file in files.Select(NormalizePathForJob).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = moves.TryGetValue(file, out var moved)
                ? moved
                : file;
            if (File.Exists(candidate) && !current.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                current.Add(candidate);
            }
        }

        return current;
    }

    private static (string? Source, string? Destination) TryParseMoveFileEntry(string entry)
    {
        const string prefix = "move-file: ";
        const string separator = " -> ";
        if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var value = entry[prefix.Length..];
        var separatorIndex = value.IndexOf(separator, StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return (null, null);
        }

        var source = value[..separatorIndex].Trim();
        var destination = value[(separatorIndex + separator.Length)..].Trim();
        return string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)
            ? (null, null)
            : (source, destination);
    }

    private static bool TryGetFolderUniformityConfig(JsonObject enhancementRoot, out JsonObject? folderUniformity)
    {
        if (enhancementRoot["folderUniformity"] is not JsonObject config
            || !EnhancementWorkflowSelection.IsFolderUniformityRunnable(enhancementRoot))
        {
            folderUniformity = null;
            return false;
        }

        folderUniformity = config;
        return true;
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
                    UpdateManifestPathsFromReports(job, [batch.Report]);
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
        return $"moved files {report.MovedFiles}; "
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

        AppendLog(
            job,
            $"enhancement workflow: folder-uniformity dedupe starting (folders={dedupeFolders.Count}, shazam={ReadBool(folderUniformity, "useShazamForDedupe") == true}).");
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
        CancellationToken cancellationToken,
        IReadOnlyList<string>? targetFiles = null)
    {
        if (!EnhancementWorkflowSelection.HasExplicitCoverActions(enhancementRoot)
            || enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance)
        {
            return EnhancementWorkflowOutcome.Skipped("cover maintenance is not configured.");
        }

        var replaceMissingEmbedded = ReadBool(coverMaintenance, "replaceMissingEmbeddedCovers") == true;
        var syncExternalCovers = ReadBool(coverMaintenance, "syncExternalCovers") == true;
        var queueAnimatedArtwork = ReadBool(coverMaintenance, "queueAnimatedArtwork") == true;
        var renameExistingAnimatedArtwork = ReadBool(coverMaintenance, "renameExistingAnimatedArtwork") == true;
        var overwriteExistingAnimatedArtwork = ReadBool(coverMaintenance, "overwriteExistingAnimatedArtwork") == true;
        var removeOldAnimatedArtwork = ReadBool(coverMaintenance, "removeOldAnimatedArtwork") == true;
        var upgradeLowResolution = ReadBool(coverMaintenance, "upgradeLowResolutionCovers") == true;
        if (!replaceMissingEmbedded
            && !syncExternalCovers
            && !queueAnimatedArtwork
            && !renameExistingAnimatedArtwork
            && !overwriteExistingAnimatedArtwork
            && !removeOldAnimatedArtwork
            && !upgradeLowResolution)
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
        var manifest = LoadEnhancementRunManifest(job);
        var sourceFiles = targetFiles is { Count: > 0 }
            ? targetFiles.Select(NormalizePathForJob).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : manifest is { Items.Count: > 0 }
                ? manifest.CurrentPaths.Where(File.Exists).ToList()
                : rootPaths
                    .Where(Directory.Exists)
                    .SelectMany(path => Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                    .Where(path => EligibleAudioExtensions.Contains(Path.GetExtension(path)))
                    .ToList();
        var albumRepresentatives = sourceFiles
            .GroupBy(path => Path.GetDirectoryName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (albumRepresentatives.Count == 0)
        {
            return EnhancementWorkflowOutcome.Skipped("no eligible audio files were found.");
        }

        var batchCount = (int)Math.Ceiling(albumRepresentatives.Count / (double)EnhancementBatchSize);
        var totalUpdated = 0;
        var totalSkipped = 0;
        var totalErrors = 0;
        AppendLog(job, $"enhancement workflow: cover maintenance starting ({albumRepresentatives.Count} unique album(s), {batchCount} batch(es)).");
        PublishEnhancementPhaseHeartbeat(
            job,
            AutoTagLiterals.EnhancementPhaseSidecarsCovers,
            $"cover maintenance starting ({albumRepresentatives.Count} album(s)).");
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = albumRepresentatives
                .Skip(batchIndex * EnhancementBatchSize)
                .Take(EnhancementBatchSize)
                .ToList();
            SetEnhancementPhase(
                job,
                AutoTagLiterals.EnhancementPhaseSidecarsCovers,
                batchIndex * EnhancementBatchSize,
                albumRepresentatives.Count,
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
                AnimatedArtworkMaxSizeMb: AppleQueueHelpers.ResolveAnimatedArtworkMaxSizeMb(settings),
                EnabledSources: enabledSources,
                CoverImageTemplate: settings.CoverImageTemplate,
                AnimatedArtworkSquareFileName: settings.AnimatedArtworkSquareFileName,
                AnimatedArtworkTallFileName: settings.AnimatedArtworkTallFileName,
                RenameExistingAnimatedArtwork: renameExistingAnimatedArtwork,
                OverwriteExistingAnimatedArtwork: overwriteExistingAnimatedArtwork,
                RemoveOldAnimatedArtwork: removeOldAnimatedArtwork,
                TargetFiles: batch,
                WriteEmbeddedCover: settings.Tags?.Cover != false,
                WriteExternalSidecar: settings.SaveArtwork,
                LocalArtworkFormat: settings.LocalArtworkFormat,
                UseShazamForUntaggedFiles: ReadBool(coverMaintenance, "useShazamForUntaggedFiles") == true);
            var result = await _coverMaintenanceService.RunAsync(
                request,
                cancellationToken,
                (album, completed, albumCount, _) =>
                {
                    var processed = Math.Min(albumRepresentatives.Count, (batchIndex * EnhancementBatchSize) + completed);
                    var status = album.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
                        ? AutoTagLiterals.ErrorStatus
                        : album.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
                            ? AutoTagLiterals.OkStatus
                            : AutoTagLiterals.SkippedStatus;
                    lock (job)
                    {
                        RecordEnhancementItemStatus(
                            job,
                            AutoTagLiterals.EnhancementPhaseSidecarsCovers,
                            album.RepresentativeFilePath ?? album.AlbumDirectory,
                            status,
                            album.Message,
                            processed,
                            albumRepresentatives.Count,
                            batchIndex + 1,
                            batchCount,
                            completed,
                            albumCount,
                            artworkBadges: album.HasAnimatedArtwork ? new[] { "animated-artwork" } : null,
                            sourceTitle: album.Album,
                            sourceArtist: album.Artist,
                            coverPath: album.CoverPath);
                    }

                    return ValueTask.CompletedTask;
                });
            totalUpdated += result.AlbumsUpdated;
            totalSkipped += result.AlbumsSkipped;
            totalErrors += result.Errors;
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
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

        PublishEnhancementPhaseHeartbeat(
            job,
            AutoTagLiterals.EnhancementFeatureQualityChecks,
            $"quality checks starting ({scopedFolderIds.Count} folder scope(s)).");
        await ReportMissingCoreMetadataAuditIfRequestedAsync(job, options, scopedFolderIds, cancellationToken);
        await RunFolderTagAlignmentIfRequestedAsync(job, configPath, options, scopedFolders, cancellationToken);
        await RunDuplicateCheckIfRequestedAsync(job, options, scopedFolders, cancellationToken);
        if (await RunQualityScannerIfRequestedAsync(job, qualityChecks, options, scopedFolderIds, cancellationToken))
        {
            return EnhancementWorkflowOutcome.Completed(
                $"staged {job.EnhancementDownloadItemCount} {job.EnhancementDownloadOperation} item(s); Enhancement stopped at the download batch boundary.");
        }
        return EnhancementWorkflowOutcome.Completed($"processed {scopedFolderIds.Count} folder scope(s).");
    }

    private async Task RunLyricsRefreshForBatchAsync(
        AutoTagJob job,
        IReadOnlyList<long> targetTrackIds,
        SidecarLyricsOptions options,
        CancellationToken cancellationToken)
    {
        var batchCount = targetTrackIds.Count == 0
            ? 0
            : (int)Math.Ceiling(targetTrackIds.Count / (double)EnhancementBatchSize);
        var processed = 0;
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var batch = targetTrackIds
                .Skip(batchIndex * EnhancementBatchSize)
                .Take(EnhancementBatchSize)
                .ToList();
            for (var itemIndex = 0; itemIndex < batch.Count; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trackId = batch[itemIndex];
                LyricsRefreshTrackResult result;
                try
                {
                    result = await _lyricsRefreshQueueService.RefreshTrackNowAsync(
                        trackId,
                        BuildLyricsRefreshOptions(options),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = LyricsRefreshTrackResult.Skipped(trackId, null, ex.Message);
                }

                processed++;
                RecordEnhancementItemStatus(
                    job,
                    AutoTagLiterals.EnhancementPhaseSidecarsLyrics,
                    result.FilePath ?? $"track {trackId}",
                    result.Success ? AutoTagLiterals.OkStatus : AutoTagLiterals.SkippedStatus,
                    result.Message,
                    processed,
                    targetTrackIds.Count,
                    batchIndex + 1,
                    batchCount,
                    itemIndex + 1,
                    batch.Count,
                    result);
            }

        }

        AppendLog(job, $"enhancement batch: sidecars lyrics lookup completed ({targetTrackIds.Count} track(s)).");
    }

    private static QualityCheckOptions BuildQualityCheckOptions(JsonObject qualityChecks)
    {
        var flagDuplicates = ReadBool(qualityChecks, "flagDuplicates") == true;
        var flagMissingTags = ReadBool(qualityChecks, "flagMissingTags") == true;
        var flagMismatchedMetadata = ReadBool(qualityChecks, "flagMismatchedMetadata") == true;
        var queueAtmosAlternatives = ReadBool(qualityChecks, "queueAtmosAlternatives") == true;
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
            QueueAtmosAlternatives: queueAtmosAlternatives,
            QueueTechnicalProfileUpgrades: queueTechnicalProfileUpgrades,
            RunQualityUpgradeStage: runQualityUpgradeStage,
            RunQualityScanner: runQualityScanner,
            TechnicalProfiles: technicalProfiles);
    }

    private static SidecarLyricsOptions BuildSidecarLyricsOptions(JsonObject enhancementRoot)
    {
        var sidecars = enhancementRoot["sidecars"] as JsonObject ?? new JsonObject();
        return new SidecarLyricsOptions(
            QueueLyricsRefresh: ReadBool(sidecars, "queueLyricsRefresh") == true,
            RemoveLineSyncedTtml: ReadBool(sidecars, "removeLineSyncedTtml") == true,
            RewriteLineSyncedTtml: ReadBool(sidecars, "rewriteLineSyncedTtml") == true);
    }

    private static LyricsRefreshOptions BuildLyricsRefreshOptions(SidecarLyricsOptions options)
    {
        return new LyricsRefreshOptions(
            RefreshLyrics: options.QueueLyricsRefresh,
            RemoveLineSyncedTtml: options.RemoveLineSyncedTtml,
            RewriteLineSyncedTtml: options.RewriteLineSyncedTtml);
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
        CancellationToken cancellationToken,
        IReadOnlyCollection<long>? targetTrackIds = null)
    {
        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
            "all",
            scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);
        tracks = FilterTracksByScopedFolders(tracks, scopedFolderIds);
        if (targetTrackIds is { Count: > 0 })
        {
            var selectedTrackIds = targetTrackIds.ToHashSet();
            tracks = tracks
                .Where(track => selectedTrackIds.Contains(track.TrackId))
                .ToList();
        }
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

        AppendLog(
            job,
            "enhancement workflow: mismatched folder/tag scan recorded; path alignment is deferred to folder uniformity.");
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

        AppendLog(
            job,
            $"enhancement workflow: duplicate check starting (folders={scopedFolders.Count}, shazam={options.UseShazamForDedupe}).");
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
        SidecarLyricsOptions options,
        List<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        if (!options.ShouldRun)
        {
            return;
        }

        var manifest = LoadEnhancementRunManifest(job);
        if (manifest is { Items.Count: > 0 })
        {
            var manifestTrackIds = manifest.TrackIds;
            var skippedUnindexed = manifest.Items.Count(item => item.TrackId is not > 0);
            if (skippedUnindexed > 0)
            {
                AppendLog(job, $"enhancement stage skip: lyrics, {skippedUnindexed} items have no track id");
            }

            if (manifestTrackIds.Count > 0)
            {
                await RunLyricsRefreshForBatchAsync(job, manifestTrackIds, options, cancellationToken);
                return;
            }
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
        var batches = BuildAlbumBoundaryBatches(
            uniqueTracks,
            static track => track.AudioFilePath);
        var batchCount = batches.Count;
        var processedTotal = 0;
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var batch = batches[batchIndex];
            for (var itemIndex = 0; itemIndex < batch.Count; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = batch[itemIndex];
                LyricsRefreshTrackResult result;
                try
                {
                    result = await _lyricsRefreshQueueService.RefreshTrackNowAsync(
                        track.TrackId,
                        BuildLyricsRefreshOptions(options),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = LyricsRefreshTrackResult.Skipped(track.TrackId, null, ex.Message);
                }

                var processed = ++processedTotal;
                RecordEnhancementItemStatus(
                    job,
                    AutoTagLiterals.EnhancementPhaseSidecarsLyrics,
                    result.FilePath ?? $"{track.ArtistName} - {track.Title}",
                    result.Success ? AutoTagLiterals.OkStatus : AutoTagLiterals.SkippedStatus,
                    result.Message,
                    processed,
                    uniqueTracks.Count,
                    batchIndex + 1,
                    batchCount,
                    itemIndex + 1,
                    batch.Count,
                    result);
            }

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
        => CoverMaintenanceProfilePreferences.ApplyToSettings(configRoot, settings);

    internal static List<List<T>> BuildAlbumBoundaryBatches<T>(
        IReadOnlyList<T> items,
        Func<T, string?> pathSelector)
    {
        var orderedAlbumGroups = items
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                AlbumDirectory = ResolveAlbumBatchDirectory(pathSelector(item), index)
            })
            .GroupBy(value => value.AlbumDirectory, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(value => value.Index))
            .Select(group => group.OrderBy(value => value.Index).Select(value => value.Item).ToList())
            .ToList();
        var batches = new List<List<T>>();
        var current = new List<T>();
        foreach (var album in orderedAlbumGroups)
        {
            if (current.Count >= EnhancementBatchSize)
            {
                batches.Add(current);
                current = new List<T>();
            }

            current.AddRange(album);
            if (current.Count >= EnhancementBatchSize)
            {
                batches.Add(current);
                current = new List<T>();
            }
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private static string ResolveAlbumBatchDirectory(string? path, int index)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"__missing_path_{index}";
        }

        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory)
            ? $"__missing_directory_{index}"
            : Path.GetFullPath(directory);
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
