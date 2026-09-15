using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.AutoTag;
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
    private sealed record FolderUniformityIdentity(string? Title, string? Artist, string? Album);
    private sealed record FolderUniformityOperation(string ItemKind, string OperationKind, string SourcePath, string? DestinationPath);

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

        // A checks run with the missing-metadata audit on narrows the run's targets to the
        // audited files, whatever else the profile has switched on: gap filling, sidecars and
        // folder tidy-up then repair exactly those files. The gate is the job's checks marker
        // plus the audit toggle — never the legacy enabled flag, and no longer "nothing else
        // selected" — so ticking a second check cannot silently widen the run back to the
        // whole library.
        if (ShouldNarrowToMissingCoreMetadata(IsChecksRun(job), enhancementRoot) && existingTargets.Count == 0)
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

    /// <summary>
    /// True when a checks run has the missing-core-metadata audit switched on. The audit's file
    /// list then becomes the run's target set, so the profile's repair sections work on exactly
    /// the files the audit found. The gate is the job's checks marker plus the audit toggle; the
    /// legacy <c>qualityChecks.enabled</c> flag is still written as the run marker but is no
    /// longer consulted here, and a second ticked check no longer disables the narrowing.
    /// </summary>
    private static bool IsChecksRun(AutoTagJob job)
        => IsQualityChecksOnlyRun(job)
           || job.SelectedEnhancementFeatures.Contains(
               EnhancementWorkflowSelection.QualityChecks,
               StringComparer.OrdinalIgnoreCase);

    internal static bool ShouldNarrowToMissingCoreMetadata(bool isChecksRun, JsonObject enhancementRoot)
    {
        return isChecksRun
            && EnhancementWorkflowSelection.HasConfiguredQualityChecks(enhancementRoot)
            && enhancementRoot["qualityChecks"] is JsonObject qualityChecks
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

    private void UpdateManifestPathsFromDuplicateModifications(
        AutoTagJob job,
        IReadOnlyList<DuplicateCleanModification> modifications)
    {
        var manifest = LoadEnhancementRunManifest(job);
        if (manifest == null || modifications.Count == 0)
        {
            return;
        }

        var bySource = modifications.ToDictionary(
            modification => NormalizePathForJob(modification.SourcePath),
            modification => NormalizePathForJob(modification.DestinationPath ?? modification.SourcePath),
            StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var item in manifest.Items)
        {
            if (!bySource.TryGetValue(NormalizePathForJob(item.CurrentPath), out var destination))
            {
                continue;
            }

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
        lock (job)
        {
            job.CurrentPhase = phase;
            job.CurrentBatch = Math.Max(0, currentBatch);
            job.BatchCount = Math.Max(0, batchCount);
            job.BatchProcessed = Math.Max(0, batchProcessed);
            job.BatchSize = Math.Max(0, batchSize);
            job.ProcessedItems = Math.Max(job.ProcessedItems, Math.Max(0, processed));
            job.TotalItems = job.TargetUsable > 0
                ? job.TargetUsable
                : Math.Max(0, total);
            if (job.TotalItems > 0)
            {
                job.Progress = Math.Clamp(job.ProcessedItems / (double)job.TotalItems, 0d, 1d);
            }

            job.CurrentPlatform = string.IsNullOrWhiteSpace(job.EnhancementFeature)
                ? AutoTagLiterals.EnhancementStage
                : job.EnhancementFeature;
            SaveJob(job);
        }
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
        string? coverPath = null,
        bool countOutcome = true,
        long? trackId = null,
        string? activityState = null,
        string? itemKind = null,
        string? operationKind = null,
        string? sourcePath = null,
        string? destinationPath = null,
        string? sourceAlbum = null,
        string? artistImageUrl = null,
        string? albumImageUrl = null)
    {
        lock (job)
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
                    ActivityState = activityState,
                    Path = path,
                    Message = message,
                    ItemKind = itemKind,
                    OperationKind = operationKind,
                    SourcePath = sourcePath,
                    DestinationPath = destinationPath,
                    SourceAlbum = sourceAlbum,
                    ArtistImageUrl = artistImageUrl,
                    AlbumImageUrl = albumImageUrl,
                    SourceTitle = lyrics?.Title ?? sourceTitle,
                    SourceArtist = lyrics?.ArtistName ?? sourceArtist,
                    LyricsTrackId = lyrics?.TrackId ?? trackId,
                    LyricsCoverUrl = BuildLyricsCoverUrl(lyrics?.CoverPath ?? coverPath),
                    LyricsBadges = lyrics?.TimingBadges.ToList() ?? new List<string>(),
                    ArtworkBadges = artworkBadges?.ToList() ?? new List<string>()
                }
            };
            job.LastStatus = update;
            AppendStatusHistory(job, update);
            if (countOutcome)
            {
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
            }
            SaveJob(job);
        }
    }

    private async Task RunIntegratedEnhancementWorkflowsAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        bool includesEnhancementWorkflows,
        CancellationToken cancellationToken,
        AutoTagMoveSummary? autoMoveSummary = null)
    {
        if (!includesEnhancementWorkflows
            || !ShouldRunIntegratedWorkflowsForIntent(job.RunIntent)
            || !IsEnhancementWorkflowTrigger(job.Trigger))
        {
            return;
        }

        var root = LoadConfigRoot(configPath);
        if (root == null || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return;
        }

        // Manual enrichment just moved its fully enriched files to the destination
        // library folder. Sidecar lookups run on those moved files with their library
        // track identity, after the library has ingested the moved paths.
        var movedFiles = IsManualEnrichmentRunIntent(job.RunIntent) && autoMoveSummary is { MovedCount: > 0 }
            ? autoMoveSummary.ChangedFilePaths
            : null;
        if (movedFiles != null)
        {
            await IngestKnownFilesAfterAutoMoveAsync(job, autoMoveSummary!, cancellationToken);
        }

        if (IsManualEnrichmentRunIntent(job.RunIntent) && movedFiles != null
            && EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot))
        {
            var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
            await RunManualEnrichmentBatchSidecarsAsync(
                job,
                configPath,
                root,
                enhancementRoot,
                enabledFolders,
                autoMoveSummary!,
                movedFiles,
                cancellationToken);
            return;
        }

        // Quality checks and folder uniformity keep the enhancement-intent gate: their
        // folder scoping is built for enhancement runs. Manual enrichment gets the
        // sidecar lookup only; download enrichment gets neither (its sidecars are
        // produced by the download prefetch).
        if (!ShouldRunEnhancementForIntent(job.RunIntent))
        {
            return;
        }

        // Quality Checks is deliberately NOT part of the combined run. It only inspects
        // and flags; it does not tag or move files, so it runs as its own job triggered
        // from its own section. See RunQualityChecksOnlyAsync.
        if (IsQualityChecksOnlyRun(job))
        {
            var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
            await RunQualityChecksOnlyAsync(
                job,
                rootPath,
                enhancementRoot,
                enabledFolders,
                configPath,
                cancellationToken);
            return;
        }

        var selected = ResolveSelectedEnhancementFeatures(job, root, enhancementRoot);
        PersistResolvedEnhancementSelection(job, selected);
        if (!selected.Contains(EnhancementWorkflowSelection.GapFill, StringComparer.OrdinalIgnoreCase)
            && (selected.Contains(EnhancementWorkflowSelection.Sidecars, StringComparer.OrdinalIgnoreCase)
                || string.Equals(job.FolderUniformityRunMode, EnhancementWorkflowSelection.FolderUniformityModeBatchScoped, StringComparison.OrdinalIgnoreCase)))
        {
            await RunWorkflowOnlyEnhancementBatchesAsync(job, configPath, cancellationToken);
            return;
        }

        if (string.Equals(job.FolderUniformityRunMode, EnhancementWorkflowSelection.FolderUniformityModeLibraryWide, StringComparison.OrdinalIgnoreCase))
        {
            var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
            await RunEnhancementWorkflowAsync(
                job,
                AutoTagLiterals.EnhancementFeatureFolderUniformity,
                token => RunConfiguredFolderUniformityAsync(job, rootPath, enhancementRoot, enabledFolders, configPath, token),
                cancellationToken);
        }
    }

    /// <summary>
    /// True when the job was started for Quality Checks alone — either from the section's own
    /// "Run Selected Checks" action or any other single-feature trigger. A combined run never
    /// carries a single feature, so this cannot match one.
    /// </summary>
    private static bool IsQualityChecksOnlyRun(AutoTagJob job)
        => string.Equals(
            job.EnhancementFeature,
            AutoTagLiterals.EnhancementFeatureQualityChecks,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The independent Quality Checks execution path. Runs the configured checks and nothing
    /// else: no gap filling, no sidecars, no folder uniformity, and no auto-move, because the
    /// checks only report on files rather than rewriting them.
    /// </summary>
    private async Task RunQualityChecksOnlyAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject enhancementRoot,
        IReadOnlyList<DeezSpoTag.Services.Library.FolderDto> enabledFolders,
        string configPath,
        CancellationToken cancellationToken)
    {
        // Quality Checks is manual-only: it runs on demand from its own section and never as part
        // of a scheduled or combined run. Because it has no schedule tick, its runnability comes
        // from the checks being configured rather than from the legacy "enabled" flag.
        if (!EnhancementWorkflowSelection.HasConfiguredQualityChecks(enhancementRoot))
        {
            return;
        }

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

    private async Task RunCoordinatedEnhancementBatchAsync(
        AutoTagJob job,
        string configPath,
        AutoTagCompletedBatch batch,
        CancellationToken cancellationToken)
    {
        var root = LoadConfigRoot(configPath);
        if (root?[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return;
        }

        var selected = ResolveSelectedEnhancementFeatures(job, root, enhancementRoot);
        PersistResolvedEnhancementSelection(job, selected);
        var pending = job.EnhancementBatchState.Pending;
        if (pending == null || pending.BatchNumber != batch.BatchNumber)
        {
            pending = new AutoTagPendingEnhancementBatch
            {
                BatchNumber = batch.BatchNumber,
                BatchCount = batch.BatchCount,
                OriginalPaths = batch.Files.Select(NormalizePathForJob).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                CurrentPaths = batch.Files.Select(NormalizePathForJob).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            if (selected.Contains(EnhancementWorkflowSelection.GapFill, StringComparer.OrdinalIgnoreCase))
            {
                pending.CompletedFeatures.Add(EnhancementWorkflowSelection.GapFill);
            }
            job.EnhancementBatchState.Pending = pending;
            job.EnhancementBatchState.BatchCount = batch.BatchCount;
            SaveJob(job);
        }

        var currentFiles = pending.CurrentPaths
            .Select(NormalizePathForJob)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currentFiles.Count == 0)
        {
            AppendLog(job, "enhancement batch skipped: no existing audio files remained after gap-fill.");
            job.EnhancementBatchState.NextBatchIndex = Math.Max(job.EnhancementBatchState.NextBatchIndex, batch.BatchNumber);
            job.EnhancementBatchState.Pending = null;
            SaveJob(job);
            return;
        }

        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        if (selected.Contains(EnhancementWorkflowSelection.Sidecars, StringComparer.OrdinalIgnoreCase)
            && !pending.CompletedFeatures.Contains(EnhancementWorkflowSelection.Sidecars, StringComparer.OrdinalIgnoreCase))
        {
            AppendLog(job, $"enhancement batch {batch.BatchNumber}/{batch.BatchCount}: running Sidecars for {currentFiles.Count} file(s).");
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
            pending.CompletedFeatures.Add(EnhancementWorkflowSelection.Sidecars);
            SaveJob(job);
        }

        if (string.Equals(job.FolderUniformityRunMode, EnhancementWorkflowSelection.FolderUniformityModeBatchScoped, StringComparison.OrdinalIgnoreCase)
            && !pending.CompletedFeatures.Contains(EnhancementWorkflowSelection.FolderUniformity, StringComparer.OrdinalIgnoreCase))
        {
            AppendLog(job, $"enhancement batch {batch.BatchNumber}/{batch.BatchCount}: running Folder Uniformity for {currentFiles.Count} file(s).");
            var context = BuildEnhancementBatchContext(pending.OriginalPaths, currentFiles, enabledFolders);
            context = await RunFolderUniformityForBatchAsync(
                job,
                configPath,
                context,
                requireSuccessfulEnhancement: false,
                batch.BatchNumber,
                batch.BatchCount,
                cancellationToken);
            pending.CurrentPaths = context.CurrentFiles.ToList();
            pending.CompletedFeatures.Add(EnhancementWorkflowSelection.FolderUniformity);
            SaveJob(job);
        }

        job.EnhancementBatchState.NextBatchIndex = Math.Max(job.EnhancementBatchState.NextBatchIndex, batch.BatchNumber);
        job.EnhancementBatchState.Pending = null;
        SaveJob(job);
    }

    private async Task ReplayPendingEnhancementBatchAsync(AutoTagJob job, string configPath, CancellationToken cancellationToken)
    {
        var pending = job.EnhancementBatchState.Pending;
        if (pending == null)
        {
            return;
        }

        AppendLog(job, $"resume: replaying unfinished enhancement batch {pending.BatchNumber}/{pending.BatchCount}.");
        await RunCoordinatedEnhancementBatchAsync(
            job,
            configPath,
            new AutoTagCompletedBatch(pending.BatchNumber, pending.BatchCount, pending.OriginalPaths),
            cancellationToken);
    }

    private async Task RunWorkflowOnlyEnhancementBatchesAsync(AutoTagJob job, string configPath, CancellationToken cancellationToken)
    {
        var root = LoadConfigRoot(configPath);
        if (root == null)
        {
            return;
        }

        var manifest = LoadEnhancementRunManifest(job);
        var files = (manifest?.CurrentPaths ?? EnumerateEnhancementRootAudioFiles(job.RootPath, root))
            .Where(File.Exists)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ranges = EnhancementBatchPlanner.BuildRanges(files, files.Count, EnhancementBatchSize);
        job.EnhancementBatchState.BatchCount = ranges.Count;
        SaveJob(job);
        for (var index = job.EnhancementBatchState.NextBatchIndex; index < ranges.Count; index++)
        {
            var (start, end) = ranges[index];
            await RunCoordinatedEnhancementBatchAsync(
                job,
                configPath,
                new AutoTagCompletedBatch(index + 1, ranges.Count, files.GetRange(start, end - start)),
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> ResolveSelectedEnhancementFeatures(
        AutoTagJob job,
        JsonObject configRoot,
        JsonObject enhancementRoot)
    {
        // A checks run carries the profile's configuration rather than a feature list: its
        // stored selection is the checks run marker, so the sections come from the profile.
        if (job.SelectedEnhancementFeatures.Count > 0 && !IsQualityChecksOnlyRun(job))
        {
            return EnhancementWorkflowSelection.OrderSelectedFeatures(job.SelectedEnhancementFeatures);
        }

        var selected = new List<string>();
        if (EnhancementWorkflowSelection.IsGapFillRunnable(configRoot)) selected.Add(EnhancementWorkflowSelection.GapFill);
        if (EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot)) selected.Add(EnhancementWorkflowSelection.Sidecars);
        if (EnhancementWorkflowSelection.IsFolderUniformityRunnable(enhancementRoot)) selected.Add(EnhancementWorkflowSelection.FolderUniformity);
        return EnhancementWorkflowSelection.OrderSelectedFeatures(selected);
    }

    private void PersistResolvedEnhancementSelection(AutoTagJob job, IReadOnlyList<string> selected)
    {
        if (job.SelectedEnhancementFeatures.Count == 0)
        {
            job.SelectedEnhancementFeatures = selected.ToList();
        }
        job.FolderUniformityRunMode ??= EnhancementWorkflowSelection.ResolveFolderUniformityRunMode(selected);
        SaveJob(job);
    }

    /// <summary>
    /// Manual enrichment sidecars run on the files at their moved destination paths.
    /// The destination library folder is the containment root for the cover pass, and
    /// the library has already ingested the moved paths, so the lyrics lookup resolves
    /// each file's track identity the same way the enhancement-run batch flow does.
    /// </summary>
    private async Task RunManualEnrichmentBatchSidecarsAsync(
        AutoTagJob job,
        string configPath,
        JsonObject root,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        AutoTagMoveSummary autoMoveSummary,
        IReadOnlyList<string> movedFiles,
        CancellationToken cancellationToken)
    {
        var currentFiles = movedFiles
            .Select(NormalizePathForJob)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currentFiles.Count == 0)
        {
            AppendLog(job, "manual enrichment sidecars skipped: no existing audio files remained after the move.");
            return;
        }

        var destinationRoot = autoMoveSummary.DestinationRoots.FirstOrDefault(rootPath => !string.IsNullOrWhiteSpace(rootPath))
            ?? job.RootPath
            ?? string.Empty;

        AppendLog(job, $"manual enrichment: running opted-in sidecars for {currentFiles.Count} moved file(s).");
        await RunEnhancementWorkflowAsync(
            job,
            AutoTagLiterals.EnhancementFeatureSidecars,
            token => RunConfiguredSidecarsAsync(
                job,
                destinationRoot,
                root,
                enhancementRoot,
                enabledFolders,
                configPath,
                token,
                currentFiles),
            cancellationToken);

        SaveJob(job);
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

        // Sidecars run as a single per-file pass, the way downloads are processed: every
        // file is visited in album order, its lyrics are handled, and the album's artwork
        // is fetched once by the first file of that album. The message each file shows
        // names exactly what that file is about to fetch, so a lyrics-only run never
        // claims artwork work and an artwork-only run never claims lyrics work.
        // Settings come from both places and the split is deliberate: the enhancement tab's
        // sidecars and coverMaintenance sections decide *which* actions run, while the assigned
        // profile supplies *how* they run (artwork sidecar / embedded cover, cover template,
        // animated-artwork file names and formats, local artwork format, and the technical
        // lyrics block). Neither source overrides the other; they answer different questions.
        var lyricsOptions = BuildSidecarLyricsOptions(enhancementRoot);
        var coverScope = ResolveSidecarCoverScope(enhancementRoot);
        var orderedFiles = await ResolveSidecarRunFilesAsync(
            job,
            configRoot,
            enabledFolders,
            batchFiles,
            cancellationToken);
        if (orderedFiles.Count == 0)
        {
            return EnhancementWorkflowOutcome.Skipped("no existing audio files were found for sidecars.");
        }

        AppendLog(
            job,
            batchFiles is not null
                ? $"enhancement batch: sidecars for {orderedFiles.Count} file(s) (lyrics={runLyrics}, covers={runCovers})."
                : $"enhancement workflow: sidecars starting (lyrics={runLyrics}, covers={runCovers}).");

        var trackIdsByPath = await _libraryRepository.GetTrackIdsByFilePathsAsync(
            orderedFiles,
            cancellationToken);
        if (runLyrics)
        {
            var indexedCount = orderedFiles.Count(path => trackIdsByPath.ContainsKey(path));
            var unindexed = orderedFiles.Count - indexedCount;
            if (unindexed > 0)
            {
                AppendLog(job, $"enhancement batch: sidecars lyrics skipped {unindexed} file(s) with no library track id.");
            }

            if (indexedCount > 0)
            {
                AppendLog(job, $"enhancement batch: sidecars lyrics lookup starting ({indexedCount} track(s)).");
            }
        }

        var orderedRun = OrderSidecarRunFilesByAlbum(orderedFiles);
        var artworkAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<EnhancementWorkflowOutcome>();
        var processed = 0;
        var artworkAlbumsServiced = 0;
        var lyricsTracksHandled = 0;
        foreach (var filePath in orderedRun)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            var trackId = trackIdsByPath.TryGetValue(filePath, out var resolvedTrackId) ? resolvedTrackId : 0;
            var albumKey = ResolveSidecarAlbumKey(filePath);
            var ownsAlbumArtwork = runCovers && artworkAlbums.Add(albumKey);
            var handlesLyrics = runLyrics && trackId > 0;

            var fetchMessage = SidecarFetchActivity.Describe(new SidecarFetchWork(
                ownsAlbumArtwork && coverScope.FetchesStillArtwork,
                ownsAlbumArtwork && coverScope.FetchesAnimatedArtwork,
                false,
                handlesLyrics && lyricsOptions.QueueLyricsRefresh));
            RecordSidecarFetchStatus(job, filePath, fetchMessage, processed, orderedRun.Count, trackId);

            if (ownsAlbumArtwork)
            {
                outcomes.Add(await RunConfiguredCoverMaintenanceAsync(
                    job,
                    rootPath,
                    configRoot,
                    enhancementRoot,
                    enabledFolders,
                    configPath,
                    cancellationToken,
                    [filePath],
                    suppressFetchActivity: true));
                artworkAlbumsServiced++;

                // Keep the file's activity visible while its lyrics are still being fetched.
                if (handlesLyrics)
                {
                    RecordSidecarFetchStatus(job, filePath, fetchMessage, processed, orderedRun.Count, trackId);
                }
            }

            if (handlesLyrics)
            {
                await RunLyricsRefreshForBatchAsync(
                    job,
                    [trackId],
                    lyricsOptions,
                    cancellationToken,
                    suppressFetchActivity: true);
                lyricsTracksHandled++;
            }
        }

        var failed = outcomes.FirstOrDefault(outcome =>
            string.Equals(outcome.Status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(failed.Status))
        {
            return failed;
        }

        AppendLog(
            job,
            $"enhancement workflow: sidecars finished ({processed} file(s), {artworkAlbumsServiced} album artwork pass(es), {lyricsTracksHandled} lyrics track(s)).");
        return EnhancementWorkflowOutcome.Completed(
            batchFiles is not null
                ? $"sidecars finished for {processed} file(s)."
                : "sidecars finished.");
    }

    private void RecordSidecarFetchStatus(
        AutoTagJob job,
        string filePath,
        string? fetchMessage,
        int processed,
        int total,
        long trackId)
    {
        if (fetchMessage == null)
        {
            return;
        }

        RecordEnhancementItemStatus(
            job,
            AutoTagLiterals.EnhancementFeatureSidecars,
            filePath,
            AutoTagLiterals.TaggingStatus,
            fetchMessage,
            processed,
            total,
            1,
            1,
            processed,
            total,
            countOutcome: false,
            trackId: trackId > 0 ? trackId : null,
            activityState: "fetchingSidecars");
    }

    private sealed record SidecarCoverScope(bool FetchesStillArtwork, bool FetchesAnimatedArtwork);

    private static SidecarCoverScope ResolveSidecarCoverScope(JsonObject enhancementRoot)
    {
        if (enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance)
        {
            return new SidecarCoverScope(false, false);
        }

        var fetchesStill = ReadBool(coverMaintenance, "replaceMissingEmbeddedCovers") == true
                           || ReadBool(coverMaintenance, "syncExternalCovers") == true
                           || ReadBool(coverMaintenance, "upgradeLowResolutionCovers") == true;
        var fetchesAnimated = ReadBool(coverMaintenance, "queueAnimatedArtwork") == true;
        return new SidecarCoverScope(fetchesStill, fetchesAnimated);
    }

    private static string ResolveSidecarAlbumKey(string filePath)
        => Path.GetDirectoryName(NormalizePathForJob(filePath)) ?? string.Empty;

    private static List<string> OrderSidecarRunFilesByAlbum(IReadOnlyList<string> files)
        => files
            .OrderBy(ResolveSidecarAlbumKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<IReadOnlyList<string>> ResolveSidecarRunFilesAsync(
        AutoTagJob job,
        JsonObject configRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        IReadOnlyList<string>? batchFiles,
        CancellationToken cancellationToken)
    {
        if (batchFiles is { Count: > 0 })
        {
            var context = BuildEnhancementBatchContext(batchFiles, batchFiles, enabledFolders);
            if (context.FilesByFolder.Count > 0)
            {
                await _knownFileIngestionService.IngestAndVerifyAsync(context.FilesByFolder, cancellationToken);
            }

            return context.CurrentFiles
                .Select(NormalizePathForJob)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var manifest = LoadEnhancementRunManifest(job);
        var files = manifest is { Items.Count: > 0 }
            ? manifest.CurrentPaths
            : EnumerateEnhancementRootAudioFiles(job.RootPath, configRoot);
        return files
            .Select(NormalizePathForJob)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private bool ShouldRunIntegratedEnhancementWorkflows(AutoTagJob job, string configPath)
    {
        if (!ShouldRunIntegratedWorkflowsForIntent(job.RunIntent) || !IsEnhancementWorkflowTrigger(job.Trigger))
        {
            return false;
        }

        var root = LoadConfigRoot(configPath);
        if (root == null || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return false;
        }

        // Quality Checks is manual-only, so it does not join the combined run. When the job was
        // started for the checks alone (its own "Run Selected Checks" action), it gets the
        // dedicated path below.
        if (IsQualityChecksOnlyRun(job))
        {
            return EnhancementWorkflowSelection.HasConfiguredQualityChecks(enhancementRoot);
        }

        return EnhancementWorkflowSelection.IsFolderUniformityRunnable(enhancementRoot)
            || EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot);
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
            || string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, AutoTagLiterals.RecoveryTrigger, StringComparison.OrdinalIgnoreCase);
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
        int batchNumber,
        int batchCount,
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

        var identities = successfulBatchFiles.ToDictionary(
            NormalizePathForJob,
            CaptureFolderUniformityIdentity,
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < successfulBatchFiles.Count; index++)
        {
            var source = NormalizePathForJob(successfulBatchFiles[index]);
            identities.TryGetValue(source, out var identity);
            RecordEnhancementItemStatus(
                job,
                AutoTagLiterals.EnhancementFeatureFolderUniformity,
                source,
                AutoTagLiterals.TaggingStatus,
                "Applying the configured file and folder templates.",
                index,
                successfulBatchFiles.Count,
                batchNumber,
                batchCount,
                index,
                successfulBatchFiles.Count,
                sourceTitle: identity?.Title,
                sourceArtist: identity?.Artist,
                countOutcome: false,
                itemKind: "file",
                operationKind: "processing",
                sourcePath: source,
                destinationPath: source,
                sourceAlbum: identity?.Album,
                artistImageUrl: ResolveIdentityArtworkUrl(source, identity?.Artist, true),
                albumImageUrl: ResolveIdentityArtworkUrl(source, identity?.Album, false));
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
        var moveMap = folderReports
            .SelectMany(static report => report.Entries)
            .Select(TryParseMoveFileEntry)
            .Where(static move => move.Source != null && move.Destination != null)
            .ToDictionary(
                static move => NormalizePathForJob(move.Source!),
                static move => NormalizePathForJob(move.Destination!),
                StringComparer.OrdinalIgnoreCase);
        var processedInBatch = 0;
        foreach (var source in successfulBatchFiles)
        {
            var normalizedSource = NormalizePathForJob(source);
            var destination = moveMap.TryGetValue(normalizedSource, out var moved) ? moved : normalizedSource;
            identities.TryGetValue(normalizedSource, out var identity);
            processedInBatch++;
            RecordEnhancementItemStatus(
                job,
                AutoTagLiterals.EnhancementFeatureFolderUniformity,
                destination,
                AutoTagLiterals.OkStatus,
                moveMap.ContainsKey(normalizedSource) ? "File moved to match the configured templates." : "File already matched the configured templates.",
                processedInBatch,
                successfulBatchFiles.Count,
                batchNumber,
                batchCount,
                processedInBatch,
                successfulBatchFiles.Count,
                sourceTitle: identity?.Title,
                sourceArtist: identity?.Artist,
                itemKind: "file",
                operationKind: moveMap.ContainsKey(normalizedSource) ? "moved" : "unchanged",
                sourcePath: normalizedSource,
                destinationPath: destination,
                sourceAlbum: identity?.Album,
                artistImageUrl: ResolveIdentityArtworkUrl(destination, identity?.Artist, true),
                albumImageUrl: ResolveIdentityArtworkUrl(destination, identity?.Album, false));
        }
        foreach (var operation in folderReports
                     .SelectMany(static report => report.Entries)
                     .Select(TryParseFolderUniformityOperation)
                     .Where(static operation => operation != null
                         && (operation.ItemKind == "folder" || operation.OperationKind is "quarantined" or "replaced" or "reconciled")))
        {
            var parsed = operation!;
            RecordEnhancementItemStatus(
                job,
                AutoTagLiterals.EnhancementFeatureFolderUniformity,
                parsed.DestinationPath ?? parsed.SourcePath,
                parsed.OperationKind == "skipped" ? AutoTagLiterals.SkippedStatus : AutoTagLiterals.OkStatus,
                $"{parsed.ItemKind} {parsed.OperationKind}",
                Math.Min(Math.Max(1, processedInBatch), successfulBatchFiles.Count),
                successfulBatchFiles.Count,
                batchNumber,
                batchCount,
                Math.Min(Math.Max(1, processedInBatch), successfulBatchFiles.Count),
                successfulBatchFiles.Count,
                itemKind: parsed.ItemKind,
                operationKind: parsed.OperationKind,
                sourcePath: parsed.SourcePath,
                destinationPath: parsed.DestinationPath,
                albumImageUrl: ResolveFolderArtworkUrl(Path.GetDirectoryName(parsed.DestinationPath ?? parsed.SourcePath), false));
        }
        if (ReadBool(folderUniformity, "runDedupe") != false)
        {
            var duplicateResult = await _duplicateCleanerService.ScanFilesAsync(
                enabledFolders,
                currentFiles,
                new DuplicateCleanerOptions
                {
                    UseDuplicatesFolder = true,
                    DuplicatesFolderName = folderUniformity!["duplicatesFolderName"]?.GetValue<string>() ?? DuplicateCleanerService.DuplicatesFolderName,
                    UseShazamForIdentity = ReadBool(folderUniformity, "useShazamForDedupe") == true,
                    ConflictPolicy = folderUniformity["duplicateConflictPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.DuplicateConflictKeepBest
                },
                cancellationToken);
            var removed = duplicateResult.Modifications
                .Select(modification => NormalizePathForJob(modification.SourcePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            UpdateManifestPathsFromDuplicateModifications(job, duplicateResult.Modifications);
            currentFiles = currentFiles.Where(path => !removed.Contains(NormalizePathForJob(path))).ToList();
            foreach (var modification in duplicateResult.Modifications)
            {
                processedInBatch++;
                RecordEnhancementItemStatus(
                    job,
                    AutoTagLiterals.EnhancementFeatureFolderUniformity,
                    modification.DestinationPath ?? modification.SourcePath,
                    AutoTagLiterals.OkStatus,
                    $"Duplicate {modification.OperationKind}.",
                    Math.Min(processedInBatch, Math.Max(1, successfulBatchFiles.Count)),
                    successfulBatchFiles.Count,
                    batchNumber,
                    batchCount,
                    Math.Min(processedInBatch, Math.Max(1, successfulBatchFiles.Count)),
                    successfulBatchFiles.Count,
                    itemKind: modification.ItemKind,
                    operationKind: modification.OperationKind,
                    sourcePath: modification.SourcePath,
                    destinationPath: modification.DestinationPath,
                    albumImageUrl: ResolveFolderArtworkUrl(Path.GetDirectoryName(modification.DestinationPath ?? modification.SourcePath), false));
            }
            AppendLog(job, $"enhancement batch folder uniformity dedupe completed: scanned={duplicateResult.FilesScanned}, duplicates={duplicateResult.DuplicatesFound}, changed={duplicateResult.Modifications.Count}.");
        }
        AppendLog(job, $"enhancement batch folder uniformity completed: {organizedCount} file(s).");
        return BuildEnhancementBatchContext(context.OriginalFiles, currentFiles, enabledFolders);
    }

    private static FolderUniformityIdentity CaptureFolderUniformityIdentity(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return new FolderUniformityIdentity(
                file.Tag.Title,
                file.Tag.AlbumArtists.FirstOrDefault() ?? file.Tag.Performers.FirstOrDefault(),
                file.Tag.Album);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FolderUniformityIdentity(null, null, null);
        }
    }

    private static FolderUniformityOperation? TryParseFolderUniformityOperation(string entry)
    {
        var separator = entry.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        var rawKind = entry[..separator].Trim().ToLowerInvariant();
        var payload = entry[(separator + 1)..].Trim();
        var arrow = payload.IndexOf(" -> ", StringComparison.Ordinal);
        var reverseArrow = payload.IndexOf(" <= ", StringComparison.Ordinal);
        string source;
        string? destination = null;
        if (arrow >= 0)
        {
            source = payload[..arrow].Trim();
            destination = payload[(arrow + 4)..].Trim();
        }
        else if (reverseArrow >= 0)
        {
            destination = payload[..reverseArrow].Trim();
            source = payload[(reverseArrow + 4)..].Trim();
        }
        else
        {
            source = payload.Split(" (", 2, StringSplitOptions.None)[0].Trim();
        }

        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathRooted(source))
        {
            return null;
        }

        var operationKind = rawKind.StartsWith("move", StringComparison.Ordinal) ? "moved"
            : rawKind.StartsWith("quarantine", StringComparison.Ordinal) ? "quarantined"
            : rawKind.StartsWith("replace", StringComparison.Ordinal) ? "replaced"
            : rawKind.StartsWith("reconcile", StringComparison.Ordinal) ? "reconciled"
            : rawKind.StartsWith("merge", StringComparison.Ordinal) ? "merged"
            : rawKind.StartsWith("delete", StringComparison.Ordinal) || rawKind.StartsWith("discard", StringComparison.Ordinal) ? "deleted"
            : rawKind.StartsWith("skip", StringComparison.Ordinal) || rawKind.StartsWith("preserve", StringComparison.Ordinal) ? "skipped"
            : rawKind.StartsWith("noop", StringComparison.Ordinal) ? "unchanged"
            : rawKind;
        var itemKind = rawKind.Contains("folder", StringComparison.Ordinal) ? "folder" : "file";
        return new FolderUniformityOperation(itemKind, operationKind, source, destination);
    }

    private static string? ResolveFolderArtworkUrl(string? folderPath, bool artist)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        var names = artist
            ? new[] { "artist.jpg", "folder.jpg", "cover.jpg", "artist.png", "folder.png" }
            : new[] { "cover.jpg", "folder.jpg", "front.jpg", "cover.png", "folder.png", "front.png" };
        var artworkPath = names.Select(name => Path.Join(folderPath, name)).FirstOrDefault(System.IO.File.Exists);
        return string.IsNullOrWhiteSpace(artworkPath)
            ? null
            : $"/api/library/image?path={Uri.EscapeDataString(artworkPath)}&size=240";
    }

    private static string? ResolveIdentityArtworkUrl(string filePath, string? identity, bool artist)
    {
        var directories = new List<string>();
        var current = Path.GetDirectoryName(filePath);
        for (var depth = 0; depth < 5 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            directories.Add(current);
            current = Path.GetDirectoryName(current);
        }

        if (!string.IsNullOrWhiteSpace(identity))
        {
            var matched = directories.FirstOrDefault(directory => string.Equals(
                Path.GetFileName(directory),
                identity,
                StringComparison.OrdinalIgnoreCase));
            var matchedUrl = ResolveFolderArtworkUrl(matched, artist);
            if (!string.IsNullOrWhiteSpace(matchedUrl))
            {
                return matchedUrl;
            }
        }

        if (artist)
        {
            return null;
        }

        return directories
            .Take(2)
            .Select(directory => ResolveFolderArtworkUrl(directory, false))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
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
        const string prefix = AutoTagProtocol.MoveFileEntryPrefix;
        const string separator = AutoTagProtocol.MoveFileEntrySeparator;
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

            var identities = Directory.EnumerateFiles(path, "*", searchOption)
                .Where(file => EligibleAudioExtensions.Contains(Path.GetExtension(file)))
                .Select(NormalizePathForJob)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(file => file, CaptureFolderUniformityIdentity, StringComparer.OrdinalIgnoreCase);

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
                    var resolvedBySource = batch.Report.Entries
                        .Select(TryParseMoveFileEntry)
                        .Where(static move => move.Source != null && move.Destination != null)
                        .ToDictionary(
                            static move => NormalizePathForJob(move.Source!),
                            static move => NormalizePathForJob(move.Destination!),
                            StringComparer.OrdinalIgnoreCase);
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
                        var source = NormalizePathForJob(batch.Files[index]);
                        var destination = resolvedBySource.TryGetValue(source, out var moved) ? moved : source;
                        identities.TryGetValue(source, out var identity);
                        RecordEnhancementItemStatus(
                            job,
                            AutoTagLiterals.EnhancementFeatureFolderUniformity,
                            destination,
                            AutoTagLiterals.OkStatus,
                            summary,
                            processedFiles,
                            totalFiles,
                            batch.BatchNumber,
                            batch.BatchCount,
                            index + 1,
                            batch.Files.Count,
                            sourceTitle: identity?.Title,
                            sourceArtist: identity?.Artist,
                            itemKind: "file",
                            operationKind: string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ? "unchanged" : "moved",
                            sourcePath: source,
                            destinationPath: destination,
                            sourceAlbum: identity?.Album,
                            artistImageUrl: ResolveIdentityArtworkUrl(destination, identity?.Artist, true),
                            albumImageUrl: ResolveIdentityArtworkUrl(destination, identity?.Album, false));
                    }

                    foreach (var parsed in batch.Report.Entries
                                 .Select(TryParseFolderUniformityOperation)
                                 .Where(static operation => operation?.ItemKind == "folder"))
                    {
                        RecordEnhancementItemStatus(
                            job,
                            AutoTagLiterals.EnhancementFeatureFolderUniformity,
                            parsed!.DestinationPath ?? parsed.SourcePath,
                            parsed.OperationKind == "skipped" ? AutoTagLiterals.SkippedStatus : AutoTagLiterals.OkStatus,
                            $"folder {parsed.OperationKind}",
                            processedFiles,
                            totalFiles,
                            batch.BatchNumber,
                            batch.BatchCount,
                            batch.Files.Count,
                            batch.Files.Count,
                            itemKind: "folder",
                            operationKind: parsed.OperationKind,
                            sourcePath: parsed.SourcePath,
                            destinationPath: parsed.DestinationPath);
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
        UpdateManifestPathsFromDuplicateModifications(job, duplicateResult.Modifications);
        foreach (var modification in duplicateResult.Modifications)
        {
            RecordEnhancementItemStatus(
                job,
                AutoTagLiterals.EnhancementFeatureFolderUniformity,
                modification.DestinationPath ?? modification.SourcePath,
                AutoTagLiterals.OkStatus,
                $"Duplicate {modification.OperationKind}.",
                job.ProcessedItems,
                job.TotalItems,
                Math.Max(1, job.CurrentBatch),
                Math.Max(1, job.BatchCount),
                job.BatchProcessed,
                Math.Max(1, job.BatchSize),
                itemKind: modification.ItemKind,
                operationKind: modification.OperationKind,
                sourcePath: modification.SourcePath,
                destinationPath: modification.DestinationPath);
        }
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
        IReadOnlyList<string>? targetFiles = null,
        bool suppressFetchActivity = false)
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

        // Conflicting sidecar toggles are mutually exclusive: at most one of a pair may
        // be active. A stored configuration that still holds both (legacy data) enables
        // neither; the user must explicitly re-enable exactly one in the UI.
        if (renameExistingAnimatedArtwork && overwriteExistingAnimatedArtwork)
        {
            renameExistingAnimatedArtwork = false;
            overwriteExistingAnimatedArtwork = false;
        }

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
        var settings = BuildEnhancementLyricsSettings(configRoot);
        ApplyProfileArtworkExtras(configRoot, settings);
        var enabledSources = ResolveProfileCoverSources(settings);
        if (ShouldSkipCoverMaintenanceForMissingStillArtworkSource(
                replaceMissingEmbedded,
                syncExternalCovers,
                upgradeLowResolution,
                enabledSources.Count))
        {
            AppendLog(job, "enhancement workflow: cover maintenance skipped (assigned profile has no compatible still-artwork source enabled).");
            return EnhancementWorkflowOutcome.Skipped("assigned profile has no compatible still-artwork source enabled.");
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

        // One unified sidecar pass: attribute each album's artwork outcome to a
        // single representative track so the sidecar tab shows one per-track
        // card per album (merged with that track's lyrics card) instead of a
        // separate album-titled artwork run.
        var representativeTracks = await ResolveAlbumRepresentativeTracksAsync(
            albumRepresentatives,
            cancellationToken);

        var batchCount = (int)Math.Ceiling(albumRepresentatives.Count / (double)EnhancementBatchSize);
        var totalUpdated = 0;
        var totalSkipped = 0;
        var totalErrors = 0;
        if (!suppressFetchActivity)
        {
            AppendLog(job, $"enhancement workflow: cover maintenance starting ({albumRepresentatives.Count} unique album(s), {batchCount} batch(es)).");
            PublishEnhancementPhaseHeartbeat(
                job,
                AutoTagLiterals.EnhancementFeatureSidecars,
                $"cover maintenance starting ({albumRepresentatives.Count} album(s)).");
        }

        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = albumRepresentatives
                .Skip(batchIndex * EnhancementBatchSize)
                .Take(EnhancementBatchSize)
                .ToList();
            if (!suppressFetchActivity)
            {
                PublishEnhancementPhaseHeartbeat(
                    job,
                    AutoTagLiterals.EnhancementFeatureSidecars,
                    $"Fetching cover artwork for batch {batchIndex + 1} of {batchCount}.");
            }
            SetEnhancementPhase(
                job,
                AutoTagLiterals.EnhancementFeatureSidecars,
                batchIndex * EnhancementBatchSize,
                albumRepresentatives.Count,
                batchIndex + 1,
                batchCount,
                0,
                batch.Count);
            var request = new CoverLibraryMaintenanceRequest(
                RootPaths: rootPaths,
                IncludeSubfolders: true,
                WorkerCount: 1,
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
                UseShazamForUntaggedFiles: ReadBool(coverMaintenance, "useShazamForUntaggedFiles") == true,
                Settings: settings);
            for (var itemIndex = 0; itemIndex < batch.Count; itemIndex++)
            {
                var filePath = batch[itemIndex];
                var representative = ResolveSidecarCoverRepresentative(
                    representativeTracks,
                    Path.GetDirectoryName(filePath) ?? string.Empty,
                    filePath);
                var coverPlan = await _coverMaintenanceService.PlanAsync(filePath, request, cancellationToken);
                // Artist artwork is deliberately absent here: it is processed and updated
                // on its own path, never as part of the sidecar pass.
                var fetchMessage = SidecarFetchActivity.Describe(new SidecarFetchWork(
                    coverPlan.FetchStillArtwork,
                    coverPlan.FetchAnimatedArtwork,
                    false,
                    false));
                if (!suppressFetchActivity && fetchMessage != null)
                {
                    RecordEnhancementItemStatus(
                        job,
                        AutoTagLiterals.EnhancementFeatureSidecars,
                        representative?.FilePath ?? filePath,
                        AutoTagLiterals.TaggingStatus,
                        fetchMessage,
                        Math.Min(albumRepresentatives.Count, (batchIndex * EnhancementBatchSize) + itemIndex + 1),
                        albumRepresentatives.Count,
                        batchIndex + 1,
                        batchCount,
                        itemIndex + 1,
                        batch.Count,
                        sourceTitle: representative?.Title,
                        sourceArtist: representative?.Artist,
                        countOutcome: false,
                        trackId: representative?.TrackId,
                        activityState: "fetchingSidecars");
                }
            }

            var result = await _coverMaintenanceService.RunAsync(
                request,
                cancellationToken,
                onAlbumCompleted: (album, completed, albumCount, _) =>
                {
                    var processed = Math.Min(albumRepresentatives.Count, (batchIndex * EnhancementBatchSize) + completed);
                    var status = album.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
                        ? AutoTagLiterals.ErrorStatus
                        : album.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
                            ? AutoTagLiterals.OkStatus
                            : AutoTagLiterals.SkippedStatus;
                    var representative = ResolveSidecarCoverRepresentative(
                        representativeTracks,
                        album.AlbumDirectory,
                        album.RepresentativeFilePath);
                    var primaryPath = representative?.FilePath ?? album.RepresentativeFilePath ?? album.AlbumDirectory;
                    var animatedBadges = album.HasAnimatedArtwork ? new[] { "animated-artwork" } : null;
                    long? representativeTrackId = representative is { TrackId: > 0 } ? representative.TrackId : null;
                    lock (job)
                    {
                        // One card per album, carried by its representative track:
                        // the sidecar tab merges it with that track's lyrics card
                        // (unified lyrics + artwork pass, like the download flow).
                        RecordEnhancementItemStatus(
                            job,
                            AutoTagLiterals.EnhancementFeatureSidecars,
                            primaryPath,
                            status,
                            album.Message,
                            processed,
                            albumRepresentatives.Count,
                            batchIndex + 1,
                            batchCount,
                            completed,
                            albumCount,
                            artworkBadges: animatedBadges,
                            sourceTitle: representative?.Title,
                            sourceArtist: representative?.Artist,
                            coverPath: album.CoverPath,
                            trackId: representativeTrackId);
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

    internal static bool ShouldSkipCoverMaintenanceForMissingStillArtworkSource(
        bool replaceMissingEmbedded,
        bool syncExternalCovers,
        bool upgradeLowResolution,
        int enabledSourceCount)
        => (replaceMissingEmbedded || syncExternalCovers || upgradeLowResolution)
           && enabledSourceCount == 0;

    private sealed record SidecarAlbumRepresentative(string FilePath, long TrackId, string? Title, string? Artist);

    private async Task<IReadOnlyDictionary<string, SidecarAlbumRepresentative>> ResolveAlbumRepresentativeTracksAsync(
        IReadOnlyList<string> albumRepresentatives,
        CancellationToken cancellationToken)
    {
        var representatives = new Dictionary<string, SidecarAlbumRepresentative>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var trackIdsByPath = await _libraryRepository.GetTrackIdsByFilePathsAsync(
                albumRepresentatives,
                cancellationToken);
            foreach (var path in albumRepresentatives)
            {
                var directory = Path.GetDirectoryName(path) ?? string.Empty;
                if (representatives.ContainsKey(directory))
                {
                    continue;
                }

                var trackId = trackIdsByPath.TryGetValue(path, out var resolved) ? resolved : 0;
                string? title = null;
                string? artist = null;
                if (trackId > 0)
                {
                    try
                    {
                        var identity = await _libraryRepository.GetLocalTrackIdentityAsync(trackId, cancellationToken);
                        if (identity != null)
                        {
                            title = identity.Title;
                            artist = identity.Artist;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Identity enrichment is best-effort; artwork reporting proceeds.
                    }
                }

                representatives[directory] = new SidecarAlbumRepresentative(path, trackId, title, artist);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Track attribution is best-effort; fall back to file-level reporting.
        }

        return representatives;
    }

    private static SidecarAlbumRepresentative? ResolveSidecarCoverRepresentative(
        IReadOnlyDictionary<string, SidecarAlbumRepresentative> representativeTracks,
        string albumDirectory,
        string? representativeFilePath)
    {
        if (representativeTracks.TryGetValue(albumDirectory, out var representative))
        {
            return representative;
        }

        if (string.IsNullOrWhiteSpace(representativeFilePath))
        {
            return null;
        }

        representativeTracks.TryGetValue(
            Path.GetDirectoryName(representativeFilePath) ?? string.Empty,
            out representative);
        return representative;
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
        CancellationToken cancellationToken,
        bool suppressFetchActivity = false)
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

                // Resolve the track's real identity once so the sidecar
                // progress cards show the actual title/artist (and cover
                // fallback path) instead of a bare "track {id}" placeholder.
                string? trackFilePath = null;
                string? sourceTitle = null;
                string? sourceArtist = null;
                try
                {
                    trackFilePath = await _libraryRepository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
                    var identity = await _libraryRepository.GetLocalTrackIdentityAsync(trackId, cancellationToken);
                    if (identity != null)
                    {
                        sourceTitle = identity.Title;
                        sourceArtist = identity.Artist;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Identity enrichment is best-effort; refresh proceeds.
                }

                var refreshOptions = BuildLyricsRefreshOptions(options);
                var refreshPlan = await _lyricsRefreshQueueService.PlanTrackRefreshAsync(
                    trackId,
                    refreshOptions,
                    cancellationToken);
                LyricsRefreshTrackResult result;
                if (!suppressFetchActivity && refreshPlan.ShouldFetchLyrics)
                {
                    RecordEnhancementItemStatus(
                        job,
                        AutoTagLiterals.EnhancementFeatureSidecars,
                        refreshPlan.FilePath ?? trackFilePath ?? $"track {trackId}",
                        AutoTagLiterals.TaggingStatus,
                        SidecarFetchActivity.Describe(new SidecarFetchWork(false, false, false, true)),
                        processed + 1,
                        targetTrackIds.Count,
                        batchIndex + 1,
                        batchCount,
                        itemIndex + 1,
                        batch.Count,
                        sourceTitle: sourceTitle,
                        sourceArtist: sourceArtist,
                        countOutcome: false,
                        trackId: trackId,
                        activityState: "fetchingSidecars");
                }
                try
                {
                    result = await _lyricsRefreshQueueService.RefreshTrackNowAsync(
                        trackId,
                        refreshOptions,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = LyricsRefreshTrackResult.Skipped(trackId, null, ex.Message);
                }

                processed++;
                RecordEnhancementItemStatus(
                    job,
                    AutoTagLiterals.EnhancementFeatureSidecars,
                    result.FilePath ?? trackFilePath ?? $"track {trackId}",
                    result.Success ? AutoTagLiterals.OkStatus : AutoTagLiterals.SkippedStatus,
                    result.Message,
                    processed,
                    targetTrackIds.Count,
                    batchIndex + 1,
                    batchCount,
                    itemIndex + 1,
                    batch.Count,
                    result,
                    sourceTitle: sourceTitle,
                    sourceArtist: sourceArtist);
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
        var removeLineSyncedTtml = ReadBool(sidecars, "removeLineSyncedTtml") == true;
        var rewriteLineSyncedTtml = ReadBool(sidecars, "rewriteLineSyncedTtml") == true;

        // Conflicting sidecar toggles are mutually exclusive: at most one of a pair may
        // be active. A stored configuration that still holds both (legacy data) enables
        // neither; the user must explicitly re-enable exactly one in the UI.
        if (removeLineSyncedTtml && rewriteLineSyncedTtml)
        {
            removeLineSyncedTtml = false;
            rewriteLineSyncedTtml = false;
        }

        return new SidecarLyricsOptions(
            QueueLyricsRefresh: ReadBool(sidecars, "queueLyricsRefresh") == true,
            RemoveLineSyncedTtml: removeLineSyncedTtml,
            RewriteLineSyncedTtml: rewriteLineSyncedTtml);
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

    private static long? ResolveAtmosDestinationFolderId(JsonObject qualityChecks)
        => qualityChecks["atmosDestinationFolderId"] is JsonValue value
           && value.TryGetValue<long>(out var folderId)
           && folderId > 0
            ? folderId
            : null;

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

        // The run's Atmos destination: the profile's picker when the user chose one, with the
        // global multi-quality destination staying as the fallback.
        var atmosDestinationFolderId = ResolveAtmosDestinationFolderId(qualityChecks);

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
                cancellationToken,
                atmosDestinationFolderId: atmosDestinationFolderId))
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
        IReadOnlyCollection<long>? targetTrackIds = null,
        long? atmosDestinationFolderId = null)
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
            .OrderBy(track => ArtistOrderKey.ResolveMainArtistKey([track.ArtistName], null), StringComparer.Ordinal)
            .ThenBy(track => track.AlbumTitle, StringComparer.OrdinalIgnoreCase)
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
                AtmosDestinationFolderId = atmosDestinationFolderId,
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
        // Enhancement runs are alphabetical by main artist: order the albums (and
        // their tracks) by main artist before the album-boundary batching.
        uniqueTracks.Sort(CompareQualityScanTracksByArtist);
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
                    AutoTagLiterals.EnhancementFeatureSidecars,
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

    private static int CompareQualityScanTracksByArtist(QualityScanTrackDto left, QualityScanTrackDto right)
    {
        var artistComparison = string.Compare(
            ArtistOrderKey.ResolveMainArtistKey([left.ArtistName], null),
            ArtistOrderKey.ResolveMainArtistKey([right.ArtistName], null),
            StringComparison.Ordinal);
        if (artistComparison != 0)
        {
            return artistComparison;
        }

        var albumComparison = string.Compare(left.AlbumTitle, right.AlbumTitle, StringComparison.OrdinalIgnoreCase);
        if (albumComparison != 0)
        {
            return albumComparison;
        }

        return (left.TrackNumber ?? int.MaxValue).CompareTo(right.TrackNumber ?? int.MaxValue);
    }

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
