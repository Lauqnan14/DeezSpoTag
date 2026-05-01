using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.CoverPort;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{
    private readonly record struct EnhancementWorkflowOutcome(string Status, string Message)
    {
        public static EnhancementWorkflowOutcome Completed(string message) => new(AutoTagLiterals.CompletedStatus, message);
        public static EnhancementWorkflowOutcome Skipped(string message) => new("skipped", message);
    }

    private sealed record QualityCheckOptions(
        bool FlagDuplicates,
        bool UseDuplicatesFolder,
        bool UseShazamForDedupe,
        string? DuplicatesFolderName,
        bool QueueLyricsRefresh,
        bool QueueAtmosAlternatives,
        bool RunQualityUpgradeStage,
        bool RunQualityScanner,
        IReadOnlyList<string> TechnicalProfiles)
    {
        public bool ShouldRunAnyWorkflow => RunQualityScanner || FlagDuplicates || QueueLyricsRefresh;
    }

    private async Task RunIntegratedEnhancementWorkflowsAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        bool includesEnhancementStage,
        CancellationToken cancellationToken)
    {
        if (!includesEnhancementStage
            || !ShouldRunEnhancementForIntent(job.RunIntent)
            || !string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase)
            || !IsEnhancementWorkflowTrigger(job.Trigger))
        {
            return;
        }

        var root = LoadConfigRoot(configPath);
        if (root == null || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return;
        }

        var enhancementLyricsSettings = BuildEnhancementLyricsSettings(root);
        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        await RunEnhancementWorkflowAsync(
            job,
            "folder-uniformity",
            token => RunConfiguredFolderUniformityAsync(job, rootPath, enhancementRoot, enabledFolders, token),
            cancellationToken);
        await RunEnhancementWorkflowAsync(
            job,
            "cover-maintenance",
            token => RunConfiguredCoverMaintenanceAsync(job, rootPath, enhancementRoot, enabledFolders, token),
            cancellationToken);
        await RunEnhancementWorkflowAsync(
            job,
            "quality-checks",
            token => RunConfiguredQualityChecksAsync(job, rootPath, enhancementRoot, enhancementLyricsSettings, enabledFolders, token),
            cancellationToken);
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
            result.Status = AutoTagLiterals.InterruptedStatus;
            result.Message = "interrupted";
            throw;
        }
        catch (Exception ex)
        {
            result.Status = AutoTagLiterals.FailedStatus;
            result.Message = ex.Message;
            AppendLog(job, $"enhancement workflow: {name} failed ({ex.Message})");
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

        var settings = _settingsService.LoadSettings();
        var profileState = scopedFolders.Count > 0
            ? await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken)
            : null;
        var scopedFoldersByPath = BuildScopedFoldersByPath(scopedFolders);

        AppendLog(job, $"enhancement workflow: folder uniformity starting ({rootPaths.Count} path(s)).");
        await RunFolderUniformityForPathsAsync(job, folderUniformity!, rootPaths, settings, profileState, scopedFoldersByPath, cancellationToken);
        await RunFolderUniformityDedupeAsync(job, folderUniformity!, scopedFolders, rootPaths, enabledFolders, cancellationToken);

        AppendLog(job, "enhancement workflow: folder uniformity completed.");
        return EnhancementWorkflowOutcome.Completed($"processed {rootPaths.Count} path(s).");
    }

    private static bool TryGetFolderUniformityConfig(JsonObject enhancementRoot, out JsonObject? folderUniformity)
    {
        if (enhancementRoot["folderUniformity"] is not JsonObject config
            || ReadBool(config, "enforceFolderStructure") != true)
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
        DeezSpoTagSettings settings,
        AutoTagProfileResolutionService.ResolvedState? profileState,
        Dictionary<string, FolderDto> scopedFoldersByPath,
        CancellationToken cancellationToken)
    {
        foreach (var path in rootPaths)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var options = BuildFolderUniformityOptions(folderUniformity, settings);
            if (!TryApplyFolderUniformityProfile(job, path, options, profileState, scopedFoldersByPath))
            {
                continue;
            }

            var organizerReport = options.GenerateReconciliationReport
                ? new AutoTagLibraryOrganizer.AutoTagOrganizerReport()
                : null;
            await _libraryOrganizer.OrganizePathAsync(path, options, organizerReport, line => AppendLog(job, $"folder uniformity: {line}"));
            if (organizerReport != null)
            {
                AppendLog(job,
                    $"folder uniformity report: planned={organizerReport.PlannedMoves}, files={organizerReport.MovedFiles}, sidecars={organizerReport.MovedSidecars}, duplicate-replacements={organizerReport.ReplacedDuplicates}, duplicate-quarantine={organizerReport.QuarantinedDuplicates}.");
            }
        }
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
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance)
        {
            return EnhancementWorkflowOutcome.Skipped("cover maintenance is not configured.");
        }

        var replaceMissingEmbedded = ReadBool(coverMaintenance, "replaceMissingEmbeddedCovers") == true;
        var syncExternalCovers = ReadBool(coverMaintenance, "syncExternalCovers") == true;
        var queueAnimatedArtwork = ReadBool(coverMaintenance, "queueAnimatedArtwork") == true;
        if (!replaceMissingEmbedded && !syncExternalCovers && !queueAnimatedArtwork)
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
        var settings = _settingsService.LoadSettings();
        var request = new CoverLibraryMaintenanceRequest(
            RootPaths: rootPaths,
            IncludeSubfolders: true,
            WorkerCount: workerCount,
            UpgradeLowResolutionCovers: true,
            MinResolution: minResolution,
            TargetResolution: Math.Max(minResolution, 1200),
            SizeTolerancePercent: 25,
            PreserveSourceFormat: false,
            ReplaceMissingEmbeddedCovers: replaceMissingEmbedded,
            SyncExternalCovers: syncExternalCovers,
            QueueAnimatedArtwork: queueAnimatedArtwork,
            AppleStorefront: string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront,
            AnimatedArtworkMaxResolution: settings.Video?.AppleMusicVideoMaxResolution ?? 2160,
            EnabledSources: null,
            CoverImageTemplate: settings.CoverImageTemplate);

        AppendLog(job, $"enhancement workflow: cover maintenance starting ({rootPaths.Count} path(s)).");
        var result = await _coverMaintenanceService.RunAsync(request, cancellationToken);
        AppendLog(job, $"enhancement workflow: cover maintenance finished ({result.Message})");
        return EnhancementWorkflowOutcome.Completed(result.Message);
    }

    private async Task<EnhancementWorkflowOutcome> RunConfiguredQualityChecksAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject enhancementRoot,
        DeezSpoTagSettings enhancementLyricsSettings,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (enhancementRoot["qualityChecks"] is not JsonObject qualityChecks)
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

        await StartQualityScannerIfRequestedAsync(job, qualityChecks, options, scopedFolderIds, cancellationToken);
        await RunDuplicateCheckIfRequestedAsync(job, options, scopedFolders, cancellationToken);
        await RunLyricsRefreshIfRequestedAsync(job, options, scopedFolderIds, enhancementLyricsSettings, cancellationToken);
        return EnhancementWorkflowOutcome.Completed($"processed {scopedFolderIds.Count} folder scope(s).");
    }

    private static QualityCheckOptions BuildQualityCheckOptions(JsonObject qualityChecks)
    {
        var flagDuplicates = ReadBool(qualityChecks, "flagDuplicates") == true;
        var flagMissingTags = ReadBool(qualityChecks, "flagMissingTags") == true;
        var flagMismatchedMetadata = ReadBool(qualityChecks, "flagMismatchedMetadata") == true;
        var queueAtmosAlternatives = ReadBool(qualityChecks, "queueAtmosAlternatives") == true;
        var queueLyricsRefresh = ReadBool(qualityChecks, "queueLyricsRefresh") == true;
        var technicalProfiles = ReadStringList(qualityChecks, "technicalProfiles")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var runQualityUpgradeStage = flagMissingTags || flagMismatchedMetadata || technicalProfiles.Count > 0;
        var runQualityScanner = flagMissingTags || flagMismatchedMetadata || queueAtmosAlternatives || technicalProfiles.Count > 0;
        return new QualityCheckOptions(
            FlagDuplicates: flagDuplicates,
            UseDuplicatesFolder: ReadBool(qualityChecks, "useDuplicatesFolder") != false,
            UseShazamForDedupe: ReadBool(qualityChecks, "useShazamForDedupe") == true,
            DuplicatesFolderName: qualityChecks["duplicatesFolderName"]?.GetValue<string>(),
            QueueLyricsRefresh: queueLyricsRefresh,
            QueueAtmosAlternatives: queueAtmosAlternatives,
            RunQualityUpgradeStage: runQualityUpgradeStage,
            RunQualityScanner: runQualityScanner,
            TechnicalProfiles: technicalProfiles);
    }

    private async Task StartQualityScannerIfRequestedAsync(
        AutoTagJob job,
        JsonObject qualityChecks,
        QualityCheckOptions options,
        List<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        if (!options.RunQualityScanner)
        {
            return;
        }

        var started = await _qualityScannerService.StartAsync(
            new QualityScannerStartRequest
            {
                Scope = "all",
                FolderId = scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
                RunQualityUpgradeStage = options.RunQualityUpgradeStage,
                QueueAtmosAlternatives = options.QueueAtmosAlternatives,
                CooldownMinutes = ReadOptionalInt(qualityChecks, "cooldownMinutes"),
                Trigger = "enhancement",
                MarkAutomationWindow = false,
                TechnicalProfiles = options.TechnicalProfiles,
                FolderIds = scopedFolderIds
            },
            cancellationToken);
        AppendLog(job, started
            ? "enhancement workflow: quality scanner started."
            : "enhancement workflow: quality scanner skipped (already running).");
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
        DeezSpoTagSettings lyricsSettings,
        CancellationToken cancellationToken)
    {
        if (!options.QueueLyricsRefresh)
        {
            return;
        }

        if (!LyricsSettingsPolicy.CanFetchLyrics(lyricsSettings))
        {
            AppendLog(job, "enhancement workflow: lyrics refresh skipped (lyrics fetching disabled by settings).");
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
        tracks = FilterTracksByTechnicalProfiles(tracks, options.TechnicalProfiles);

        var trackIds = tracks
            .Select(track => track.TrackId)
            .Distinct()
            .ToList();
        var enqueueResult = _lyricsRefreshQueueService.Enqueue(trackIds, lyricsSettings);
        AppendLog(job,
            $"enhancement workflow: lyrics refresh queued (requested={enqueueResult.Requested}, enqueued={enqueueResult.Enqueued}, skipped={enqueueResult.Skipped}).");
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

    private static IReadOnlyList<QualityScanTrackDto> FilterTracksByTechnicalProfiles(
        IReadOnlyList<QualityScanTrackDto> tracks,
        IReadOnlyList<string> technicalProfiles)
    {
        if (technicalProfiles.Count == 0)
        {
            return tracks;
        }

        var allowedProfiles = technicalProfiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tracks
                .Where(track => allowedProfiles.Contains(QualityScanTrackFormatter.FormatTechnicalProfile(track)))
                .ToList();
    }

    private async Task<IReadOnlyList<FolderDto>> ResolveEnabledMusicFoldersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FolderDto> folders;
        try
        {
            folders = _libraryRepository.IsConfigured
                ? await _libraryRepository.GetFoldersAsync(cancellationToken)
                : _activityLog.GetFolders();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            folders = _activityLog.GetFolders();
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
