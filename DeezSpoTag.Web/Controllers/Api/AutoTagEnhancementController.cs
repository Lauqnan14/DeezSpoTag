using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
public class AutoTagEnhancementController : ControllerBase
{
    private const string RunningStatus = "running";
    private const string NoLibraryFoldersMessage = "No enabled music library folders are available for folder uniformity.";
    private static readonly IReadOnlyList<string> NoLibraryFoldersErrors = new[] { NoLibraryFoldersMessage };
    private static readonly object FolderUniformityStateLock = new();
    private static FolderUniformityRunState? _folderUniformityRun;

    private const int MaxFolderUniformityLogs = 600;
    private static readonly Regex ScanPreparedRegex = new(
        @"organizer scan prepared:\s*root=(?<root>.+),\s*includeSubfolders=(?<include>true|false),\s*candidateFiles=(?<count>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex PlanPreparedRegex = new(
        @"organizer plan prepared:\s*(?<count>\d+)\s+move action\(s\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex SourceFolderProgressRegex = new(
        @"organizer processing source folder \((?<index>\d+)\s*/\s*(?<total>\d+)\):\s*(?<path>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private readonly AutoTagFolderScopeDependencies _folderScopeDependencies;
    private readonly AutoTagLibraryOrganizer _libraryOrganizer;
    private readonly QualityScannerService _qualityScannerService;
    private readonly DuplicateCleanerService _duplicateCleanerService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LyricsRefreshQueueService _lyricsRefreshQueueService;
    private readonly AutoTagProfileResolutionService _profileResolutionService;

    public AutoTagEnhancementController(
        AutoTagFolderScopeDependencies folderScopeDependencies,
        AutoTagLibraryOrganizer libraryOrganizer,
        QualityScannerService qualityScannerService,
        DuplicateCleanerService duplicateCleanerService,
        DeezSpoTagSettingsService settingsService,
        LyricsRefreshQueueService lyricsRefreshQueueService,
        AutoTagProfileResolutionService profileResolutionService)
    {
        _folderScopeDependencies = folderScopeDependencies;
        _libraryOrganizer = libraryOrganizer;
        _qualityScannerService = qualityScannerService;
        _duplicateCleanerService = duplicateCleanerService;
        _settingsService = settingsService;
        _lyricsRefreshQueueService = lyricsRefreshQueueService;
        _profileResolutionService = profileResolutionService;
    }

    private sealed class FolderUniformityRunState
    {
        public required string JobId { get; init; }
        public string Status { get; set; } = RunningStatus;
        public string Phase { get; set; } = "Preparing scope";
        public bool RunOrganizer { get; set; }
        public bool RunDedupe { get; set; }
        public int TotalFolders { get; set; }
        public int TotalSteps { get; set; }
        public int CompletedSteps { get; set; }
        public int FoldersProcessed { get; set; }
        public int FoldersSkipped { get; set; }
        public int PercentComplete { get; set; }
        public string? CurrentLibraryFolder { get; set; }
        public string? CurrentArtistFolder { get; set; }
        public int ArtistFoldersProcessed { get; set; }
        public int ArtistFoldersTotal { get; set; }
        public bool? Success { get; set; }
        public string? Message { get; set; }
        public object? Options { get; set; }
        public object? Dedupe { get; set; }
        public List<object> ReconciliationReports { get; } = new();
        public List<string> Logs { get; } = new();
        public List<string> Errors { get; } = new();
        public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAtUtc { get; set; }
    }

    private sealed record FolderUniformityResultPayload(
        bool Success,
        bool Skipped,
        string? Message,
        int FoldersProcessed,
        int FoldersSkipped,
        object? Options,
        object? Dedupe,
        IReadOnlyList<object> ReconciliationReports,
        IReadOnlyList<string> Logs,
        IReadOnlyList<string> Errors,
        string? ValidationError);

    private static object ToFolderUniformityStatusResponse(FolderUniformityRunState state)
    {
        return new
        {
            jobId = state.JobId,
            status = state.Status,
            phase = state.Phase,
            runOrganizer = state.RunOrganizer,
            runDedupe = state.RunDedupe,
            totalFolders = state.TotalFolders,
            totalSteps = state.TotalSteps,
            completedSteps = state.CompletedSteps,
            foldersProcessed = state.FoldersProcessed,
            foldersSkipped = state.FoldersSkipped,
            percentComplete = state.PercentComplete,
            currentLibraryFolder = state.CurrentLibraryFolder,
            currentArtistFolder = state.CurrentArtistFolder,
            artistFoldersProcessed = state.ArtistFoldersProcessed,
            artistFoldersTotal = state.ArtistFoldersTotal,
            success = state.Success,
            message = state.Message,
            dedupe = state.Dedupe,
            options = state.Options,
            reconciliationReports = state.ReconciliationReports.ToArray(),
            logs = state.Logs.ToArray(),
            errors = state.Errors.ToArray(),
            startedAtUtc = state.StartedAtUtc,
            finishedAtUtc = state.FinishedAtUtc
        };
    }

    private static object ToFolderUniformityResultResponse(FolderUniformityResultPayload payload)
    {
        return new
        {
            success = payload.Success,
            skipped = payload.Skipped,
            message = payload.Message,
            foldersProcessed = payload.FoldersProcessed,
            foldersSkipped = payload.FoldersSkipped,
            options = payload.Options,
            dedupe = payload.Dedupe,
            reconciliationReports = payload.ReconciliationReports,
            logs = payload.Logs,
            errors = payload.Errors
        };
    }

    [HttpPost("enhancement/folder-uniformity")]
    public async Task<IActionResult> RunFolderUniformity(
        [FromBody] EnhancementFolderUniformityRequest request,
        CancellationToken cancellationToken)
    {
        request ??= new EnhancementFolderUniformityRequest();
        var payload = await ExecuteFolderUniformityAsync(request, runState: null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(payload.ValidationError))
        {
            return BadRequest(payload.ValidationError);
        }

        return Ok(ToFolderUniformityResultResponse(payload));
    }

    [HttpPost("enhancement/folder-uniformity/start")]
    public IActionResult StartFolderUniformity([FromBody] EnhancementFolderUniformityRequest request)
    {
        request ??= new EnhancementFolderUniformityRequest();
        FolderUniformityRunState runState;

        lock (FolderUniformityStateLock)
        {
            if (_folderUniformityRun != null
                && string.Equals(_folderUniformityRun.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
            {
                return Ok(new
                {
                    started = false,
                    jobId = _folderUniformityRun.JobId,
                    state = ToFolderUniformityStatusResponse(_folderUniformityRun)
                });
            }

            runState = new FolderUniformityRunState
            {
                JobId = Guid.NewGuid().ToString("N"),
                Status = RunningStatus,
                Phase = "Preparing scope",
                PercentComplete = 0
            };
            SetFolderUniformityRun(runState);
        }

        _ = Task.Run(() => RunFolderUniformityBackgroundAsync(runState, request));

        return Ok(new
        {
            started = true,
            jobId = runState.JobId,
            state = ToFolderUniformityStatusResponse(runState)
        });
    }

    [HttpGet("enhancement/folder-uniformity/status")]
    public IActionResult GetFolderUniformityStatus([FromQuery] string? jobId)
    {
        lock (FolderUniformityStateLock)
        {
            if (_folderUniformityRun == null)
            {
                return NotFound("No folder uniformity run has been started.");
            }

            if (!string.IsNullOrWhiteSpace(jobId)
                && !string.Equals(_folderUniformityRun.JobId, jobId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return NotFound("Folder uniformity run was not found.");
            }

            return Ok(ToFolderUniformityStatusResponse(_folderUniformityRun));
        }
    }

    private async Task RunFolderUniformityBackgroundAsync(
        FolderUniformityRunState runState,
        EnhancementFolderUniformityRequest request)
    {
        try
        {
            var payload = await ExecuteFolderUniformityAsync(request, runState, CancellationToken.None);
            UpdateFolderUniformityState(runState.JobId, state =>
            {
                state.Status = payload.Success ? "completed" : "error";
                state.Success = payload.Success;
                state.Message = !string.IsNullOrWhiteSpace(payload.ValidationError)
                    ? payload.ValidationError
                    : payload.Message;
                state.FinishedAtUtc = DateTimeOffset.UtcNow;
                state.Phase = payload.Success ? "Completed" : "Completed with errors";
                state.Options = payload.Options;
                state.Dedupe = payload.Dedupe;
                state.FoldersProcessed = payload.FoldersProcessed;
                state.FoldersSkipped = payload.FoldersSkipped;
                state.CurrentLibraryFolder = null;
                state.CurrentArtistFolder = null;
                state.ArtistFoldersProcessed = 0;
                state.ArtistFoldersTotal = 0;
                state.ReconciliationReports.Clear();
                state.ReconciliationReports.AddRange(payload.ReconciliationReports);
                state.Logs.Clear();
                state.Logs.AddRange(payload.Logs);
                state.Errors.Clear();
                state.Errors.AddRange(payload.Errors);
            });
        }
        catch (OperationCanceledException)
        {
            UpdateFolderUniformityState(runState.JobId, state =>
            {
                state.Status = "canceled";
                state.Success = false;
                state.Message = "Folder uniformity run was canceled.";
                state.Phase = "Canceled";
                state.FinishedAtUtc = DateTimeOffset.UtcNow;
            });
        }
        catch (Exception ex)
        {
            UpdateFolderUniformityState(runState.JobId, state =>
            {
                state.Status = "error";
                state.Success = false;
                state.Message = ex.Message;
                state.Phase = "Failed";
                state.FinishedAtUtc = DateTimeOffset.UtcNow;
                AppendLogInternal(state, $"[error] folder uniformity run failed: {ex.Message}");
                state.Errors.Add(ex.Message);
            });
        }
    }

    private async Task<FolderUniformityResultPayload> ExecuteFolderUniformityAsync(
        EnhancementFolderUniformityRequest request,
        FolderUniformityRunState? runState,
        CancellationToken cancellationToken)
    {
        request ??= new EnhancementFolderUniformityRequest();
        ApplyFolderUniformityDefaultsFromSettings(request, _settingsService.LoadSettings());
        var runOrganizer = request.EnforceFolderStructure != false;
        var runDedupe = request.RunDedupe != false;
        var organizerOptions = BuildFolderUniformityOrganizerOptions(request);
        var options = BuildFolderUniformityOptionsSnapshot(request, organizerOptions, runOrganizer, runDedupe);
        if (!runOrganizer && !runDedupe)
        {
            return BuildFolderUniformityDisabledResult(runState?.JobId, options, runOrganizer, runDedupe);
        }

        var enabledFolders = await ResolveEnabledMusicFoldersAsync(request, cancellationToken);
        if (enabledFolders.Count == 0)
        {
            return BuildFolderUniformityNoFoldersResult(runState?.JobId, options, runOrganizer, runDedupe);
        }

        var execution = new FolderUniformityExecutionState();
        var totalSteps = CalculateTotalSteps(runOrganizer, runDedupe, enabledFolders.Count);
        InitializeFolderUniformityExecutionState(runState?.JobId, options, enabledFolders.Count, totalSteps, runOrganizer, runDedupe);

        if (runOrganizer)
        {
            var profileState = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
            await RunOrganizerStageAsync(request, runState?.JobId, organizerOptions, profileState, enabledFolders, execution, cancellationToken);
        }

        if (runDedupe)
        {
            await RunDedupeStageAsync(request, runState?.JobId, runOrganizer, enabledFolders, execution, cancellationToken);
        }

        return BuildFolderUniformityCompletedResult(options, execution);
    }

    private sealed class FolderUniformityExecutionState
    {
        public List<string> Logs { get; } = new();
        public List<string> Errors { get; } = new();
        public List<object> Reports { get; } = new();
        public int Processed { get; set; }
        public int Skipped { get; set; }
        public object? Dedupe { get; set; }
    }

    private sealed record OrganizerFolderExecutionContext(
        EnhancementFolderUniformityRequest Request,
        string? JobId,
        AutoTagOrganizerOptions OrganizerOptions,
        AutoTagProfileResolutionService.ResolvedState ProfileState,
        FolderDto Folder,
        string FolderLabel,
        int FolderIndex,
        int TotalFolders,
        FolderUniformityExecutionState Execution,
        CancellationToken CancellationToken);

    private static AutoTagOrganizerOptions BuildFolderUniformityOrganizerOptions(EnhancementFolderUniformityRequest request)
    {
        return new AutoTagOrganizerOptions
        {
            IncludeSubfolders = request.IncludeSubfolders ?? true,
            MoveMisplacedFiles = request.MoveMisplacedFiles ?? true,
            MergeIntoExistingDestinationFolders = request.MergeIntoExistingDestinationFolders != false,
            RenameFilesToTemplate = request.RenameFilesToTemplate != false,
            RemoveEmptyFolders = request.RemoveEmptyFolders != false,
            ResolveSameTrackQualityConflicts = request.ResolveSameTrackQualityConflicts != false,
            KeepBothOnUnresolvedConflicts = request.KeepBothOnUnresolvedConflicts != false,
            OnlyMoveWhenTagged = request.OnlyMoveWhenTagged == true,
            OnlyReorganizeAlbumsWithFullTrackSets = request.OnlyReorganizeAlbumsWithFullTrackSets == true,
            SkipCompilationFolders = request.SkipCompilationFolders == true,
            SkipVariousArtistsFolders = request.SkipVariousArtistsFolders == true,
            GenerateReconciliationReport = request.GenerateReconciliationReport == true,
            UseShazamForUntaggedFiles = request.UseShazamForUntaggedFiles == true,
            DuplicateConflictPolicy = string.IsNullOrWhiteSpace(request.DuplicateConflictPolicy)
                ? AutoTagOrganizerOptions.DuplicateConflictKeepBest
                : request.DuplicateConflictPolicy.Trim(),
            ArtworkPolicy = string.IsNullOrWhiteSpace(request.ArtworkPolicy)
                ? AutoTagOrganizerOptions.ArtworkPolicyPreserveExisting
                : request.ArtworkPolicy.Trim(),
            LyricsPolicy = string.IsNullOrWhiteSpace(request.LyricsPolicy)
                ? AutoTagOrganizerOptions.LyricsPolicyMerge
                : request.LyricsPolicy.Trim(),
            DuplicatesFolderName = request.DuplicatesFolderName ?? DuplicateCleanerService.DuplicatesFolderName
        };
    }

    private static object BuildFolderUniformityOptionsSnapshot(
        EnhancementFolderUniformityRequest request,
        AutoTagOrganizerOptions organizerOptions,
        bool runOrganizer,
        bool runDedupe)
    {
        return new
        {
            RunOrganizer = runOrganizer,
            OrganizerRunsBeforeDedupe = runOrganizer && runDedupe,
            IncludeSubfolders = organizerOptions.IncludeSubfolders,
            MoveMisplacedFiles = request.MoveMisplacedFiles ?? true,
            MergeIntoExistingDestinationFolders = request.MergeIntoExistingDestinationFolders != false,
            RenameFilesToTemplate = request.RenameFilesToTemplate != false,
            RemoveEmptyFolders = request.RemoveEmptyFolders != false,
            ResolveSameTrackQualityConflicts = request.ResolveSameTrackQualityConflicts != false,
            KeepBothOnUnresolvedConflicts = request.KeepBothOnUnresolvedConflicts != false,
            OnlyMoveWhenTagged = request.OnlyMoveWhenTagged == true,
            OnlyReorganizeAlbumsWithFullTrackSets = request.OnlyReorganizeAlbumsWithFullTrackSets == true,
            SkipCompilationFolders = request.SkipCompilationFolders == true,
            SkipVariousArtistsFolders = request.SkipVariousArtistsFolders == true,
            GenerateReconciliationReport = request.GenerateReconciliationReport == true,
            UseShazamForUntaggedFiles = request.UseShazamForUntaggedFiles == true,
            DuplicateConflictPolicy = organizerOptions.DuplicateConflictPolicy,
            ArtworkPolicy = organizerOptions.ArtworkPolicy,
            LyricsPolicy = organizerOptions.LyricsPolicy,
            RunDedupe = runDedupe,
            UseShazamForDedupe = request.UseShazamForDedupe == true,
            ProfileAwareFolderTemplates = true,
            UsePrimaryArtistFolders = request.UsePrimaryArtistFolders,
            MultiArtistSeparator = string.IsNullOrWhiteSpace(request.MultiArtistSeparator)
                ? null
                : request.MultiArtistSeparator.Trim(),
            CreateArtistFolder = request.CreateArtistFolder,
            ArtistNameTemplate = string.IsNullOrWhiteSpace(request.ArtistNameTemplate)
                ? null
                : request.ArtistNameTemplate.Trim(),
            CreateAlbumFolder = request.CreateAlbumFolder,
            AlbumNameTemplate = string.IsNullOrWhiteSpace(request.AlbumNameTemplate)
                ? null
                : request.AlbumNameTemplate.Trim(),
            CreateCDFolder = request.CreateCDFolder,
            CreateStructurePlaylist = request.CreateStructurePlaylist,
            CreateSingleFolder = request.CreateSingleFolder,
            CreatePlaylistFolder = request.CreatePlaylistFolder,
            PlaylistNameTemplate = string.IsNullOrWhiteSpace(request.PlaylistNameTemplate)
                ? null
                : request.PlaylistNameTemplate.Trim(),
            IllegalCharacterReplacer = string.IsNullOrWhiteSpace(request.IllegalCharacterReplacer)
                ? null
                : request.IllegalCharacterReplacer.Trim(),
            DuplicatesFolderName = string.IsNullOrWhiteSpace(request.DuplicatesFolderName)
                ? DuplicateCleanerService.DuplicatesFolderName
                : request.DuplicatesFolderName.Trim()
        };
    }

    private static FolderUniformityResultPayload BuildFolderUniformityDisabledResult(
        string? jobId,
        object options,
        bool runOrganizer,
        bool runDedupe)
    {
        UpdateFolderUniformityState(jobId, state =>
        {
            state.RunOrganizer = runOrganizer;
            state.RunDedupe = runDedupe;
            state.Options = options;
            state.TotalFolders = 0;
            state.TotalSteps = 0;
            state.CompletedSteps = 0;
            state.PercentComplete = 100;
            state.Phase = "Skipped";
        });

        return new FolderUniformityResultPayload(
            Success: true,
            Skipped: true,
            Message: "Folder uniformity and dedupe are disabled by configuration.",
            FoldersProcessed: 0,
            FoldersSkipped: 0,
            Options: options,
            Dedupe: null,
            ReconciliationReports: Array.Empty<object>(),
            Logs: Array.Empty<string>(),
            Errors: Array.Empty<string>(),
            ValidationError: null);
    }

    private static FolderUniformityResultPayload BuildFolderUniformityNoFoldersResult(
        string? jobId,
        object options,
        bool runOrganizer,
        bool runDedupe)
    {
        UpdateFolderUniformityState(jobId, state =>
        {
            state.RunOrganizer = runOrganizer;
            state.RunDedupe = runDedupe;
            state.Options = options;
            state.TotalFolders = 0;
            state.TotalSteps = 0;
            state.CompletedSteps = 0;
            state.Phase = "No folders available";
            state.Errors.Add(NoLibraryFoldersMessage);
        });

        return new FolderUniformityResultPayload(
            Success: false,
            Skipped: false,
            Message: NoLibraryFoldersMessage,
            FoldersProcessed: 0,
            FoldersSkipped: 0,
            Options: options,
            Dedupe: null,
            ReconciliationReports: Array.Empty<object>(),
            Logs: Array.Empty<string>(),
            Errors: NoLibraryFoldersErrors,
            ValidationError: NoLibraryFoldersMessage);
    }

    private async Task<List<FolderDto>> ResolveEnabledMusicFoldersAsync(
        EnhancementFolderUniformityRequest request,
        CancellationToken cancellationToken)
    {
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(
            _folderScopeDependencies.LibraryRepository,
            _folderScopeDependencies.LibraryConfigStore,
            cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled
                && !string.IsNullOrWhiteSpace(folder.RootPath)
                && IsMusicFolder(folder))
            .ToList();
        var scopedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(request.FolderIds, request.FolderId, enabledFolders);
        if (scopedFolderIds.Count == 0)
        {
            return enabledFolders;
        }

        return enabledFolders
            .Where(folder => scopedFolderIds.Contains(folder.Id))
            .ToList();
    }

    private static int CalculateTotalSteps(bool runOrganizer, bool runDedupe, int folderCount)
    {
        var organizerFolderCount = runOrganizer ? folderCount : 0;
        var totalSteps = organizerFolderCount + (runDedupe ? 1 : 0);
        return totalSteps <= 0 ? 1 : totalSteps;
    }

    private static void InitializeFolderUniformityExecutionState(
        string? jobId,
        object options,
        int enabledFolderCount,
        int totalSteps,
        bool runOrganizer,
        bool runDedupe)
    {
        UpdateFolderUniformityState(jobId, state =>
        {
            state.RunOrganizer = runOrganizer;
            state.RunDedupe = runDedupe;
            state.Options = options;
            state.TotalFolders = enabledFolderCount;
            state.TotalSteps = totalSteps;
            state.CompletedSteps = 0;
            state.FoldersProcessed = 0;
            state.FoldersSkipped = 0;
            state.CurrentLibraryFolder = null;
            state.CurrentArtistFolder = null;
            state.ArtistFoldersProcessed = 0;
            state.ArtistFoldersTotal = 0;
            state.Phase = runOrganizer
                ? $"Running folder uniformity (0/{enabledFolderCount})"
                : "Running dedupe";
            state.Logs.Clear();
            state.Errors.Clear();
            state.ReconciliationReports.Clear();
        });
    }

    private async Task RunOrganizerStageAsync(
        EnhancementFolderUniformityRequest request,
        string? jobId,
        AutoTagOrganizerOptions organizerOptions,
        AutoTagProfileResolutionService.ResolvedState profileState,
        IReadOnlyList<FolderDto> enabledFolders,
        FolderUniformityExecutionState execution,
        CancellationToken cancellationToken)
    {
        var folderIndex = 0;
        foreach (var folder in enabledFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            folderIndex++;
            var folderLabel = string.IsNullOrWhiteSpace(folder.DisplayName) ? $"Folder {folder.Id}" : folder.DisplayName!;
            BeginOrganizerFolderStep(jobId, folderLabel, folderIndex, enabledFolders.Count);
            try
            {
                await ProcessOrganizerFolderAsync(
                    new OrganizerFolderExecutionContext(
                        request,
                        jobId,
                        organizerOptions,
                        profileState,
                        folder,
                        folderLabel,
                        folderIndex,
                        enabledFolders.Count,
                        execution,
                        cancellationToken));
            }
            finally
            {
                CompleteOrganizerFolderStep(jobId, folderLabel, enabledFolders.Count, execution);
            }
        }
    }

    private static void BeginOrganizerFolderStep(string? jobId, string folderLabel, int folderIndex, int totalFolders)
    {
        UpdateFolderUniformityState(jobId, state =>
        {
            state.CurrentLibraryFolder = folderLabel;
            state.CurrentArtistFolder = null;
            state.ArtistFoldersProcessed = 0;
            state.ArtistFoldersTotal = 0;
            state.Phase = $"Processing {folderLabel} ({folderIndex}/{totalFolders})";
        });
    }

    private async Task ProcessOrganizerFolderAsync(OrganizerFolderExecutionContext context)
    {
        if (!TryBuildFolderOrganizerOptions(
                context.OrganizerOptions,
                context.ProfileState,
                context.Folder,
                context.Request,
                out var folderOrganizerOptions,
                out var profileError))
        {
            RegisterOrganizerSkip(context.JobId, context.Execution, $"[{context.FolderLabel}] skipped: {profileError}", profileError);
            return;
        }

        var rootPath = context.Folder.RootPath?.Trim();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            RegisterOrganizerSkip(context.JobId, context.Execution, $"[{context.FolderLabel}] skipped: folder path is missing or does not exist.", null);
            return;
        }

        context.Execution.Processed++;
        try
        {
            await ExecuteOrganizerFolderAsync(context, rootPath, folderOrganizerOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RegisterOrganizerFailure(context.JobId, context.Execution, context.FolderLabel, ex);
        }
    }

    private static void RegisterOrganizerSkip(
        string? jobId,
        FolderUniformityExecutionState execution,
        string message,
        string? error)
    {
        execution.Skipped++;
        if (!string.IsNullOrWhiteSpace(error))
        {
            execution.Errors.Add(error);
        }

        AddFolderUniformityLog(execution.Logs, message);
        AppendFolderUniformityLog(jobId, message);
        if (!string.IsNullOrWhiteSpace(error))
        {
            UpdateFolderUniformityState(jobId, state =>
            {
                if (state.Errors.Count < MaxFolderUniformityLogs)
                {
                    state.Errors.Add(error);
                }
            });
        }
    }

    private async Task ExecuteOrganizerFolderAsync(
        OrganizerFolderExecutionContext context,
        string rootPath,
        AutoTagOrganizerOptions folderOrganizerOptions)
    {
        var report = folderOrganizerOptions.GenerateReconciliationReport
            ? new AutoTagLibraryOrganizer.AutoTagOrganizerReport()
            : null;
        await _libraryOrganizer.OrganizePathAsync(rootPath, folderOrganizerOptions, report, message =>
        {
            var line = $"[{context.FolderLabel}] {message}";
            AddFolderUniformityLog(context.Execution.Logs, line);
            AppendFolderUniformityLog(context.JobId, line);
            UpdateOrganizerProgressState(context.JobId, message, context.FolderLabel, context.FolderIndex, context.TotalFolders);
        });
        context.CancellationToken.ThrowIfCancellationRequested();
        if (report == null)
        {
            return;
        }

        var reportEntry = new
        {
            folderId = context.Folder.Id,
            folderName = context.FolderLabel,
            report.CandidateFiles,
            report.PlannedMoves,
            report.MovedFolders,
            report.MovedFiles,
            report.MovedSidecars,
            report.MovedLeftovers,
            report.ReplacedDuplicates,
            report.QuarantinedDuplicates,
            report.KeptLowerQualityDuplicates,
            report.PreservedExistingArtwork,
            report.MergedLyricsSidecars,
            report.SkippedUntagged,
            report.SkippedCompilationFolders,
            report.SkippedVariousArtistsFolders,
            report.SkippedIncompleteAlbums,
            report.SkippedExistingFolderMerges,
            report.SkippedConflicts,
            entries = report.Entries
        };
        context.Execution.Reports.Add(reportEntry);
        UpdateFolderUniformityState(context.JobId, state =>
        {
            state.ReconciliationReports.Add(reportEntry);
        });
    }

    private static void UpdateOrganizerProgressState(
        string? jobId,
        string message,
        string folderLabel,
        int folderIndex,
        int totalFolders)
    {
        if (TryParseSourceFolderProgress(message, out var sourceFolderIndex, out var sourceFolderTotal, out var sourceFolderPath))
        {
            UpdateFolderUniformityState(jobId, state =>
            {
                state.CurrentLibraryFolder = folderLabel;
                state.CurrentArtistFolder = sourceFolderPath;
                state.ArtistFoldersProcessed = sourceFolderIndex;
                state.ArtistFoldersTotal = sourceFolderTotal;
                state.Phase = $"Processing {folderLabel} ({folderIndex}/{totalFolders}) • {sourceFolderIndex}/{sourceFolderTotal} source folders";
            });
            return;
        }

        if (TryParseOrganizerScanPrepared(message, out var candidateFiles, out var includeSubfolders))
        {
            UpdateFolderUniformityState(jobId, state =>
            {
                state.CurrentLibraryFolder = folderLabel;
                state.CurrentArtistFolder = null;
                state.ArtistFoldersProcessed = 0;
                state.ArtistFoldersTotal = 0;
                state.Phase = $"Scanning {folderLabel} ({folderIndex}/{totalFolders}) • {candidateFiles} candidate files • includeSubfolders={includeSubfolders.ToString().ToLowerInvariant()}";
            });
            return;
        }

        if (TryParseOrganizerPlanPrepared(message, out var plannedMoveActions))
        {
            UpdateFolderUniformityState(jobId, state =>
            {
                state.CurrentLibraryFolder = folderLabel;
                state.CurrentArtistFolder = null;
                state.ArtistFoldersProcessed = 0;
                state.ArtistFoldersTotal = 0;
                state.Phase = $"Planning {folderLabel} ({folderIndex}/{totalFolders}) • {plannedMoveActions} move actions";
            });
            return;
        }

        if (!IsOrganizerNoMoveActionsMessage(message))
        {
            return;
        }

        UpdateFolderUniformityState(jobId, state =>
        {
            state.CurrentLibraryFolder = folderLabel;
            state.CurrentArtistFolder = null;
            state.ArtistFoldersProcessed = 0;
            state.ArtistFoldersTotal = 0;
            state.Phase = $"No folder changes needed for {folderLabel} ({folderIndex}/{totalFolders})";
        });
    }

    private static void RegisterOrganizerFailure(
        string? jobId,
        FolderUniformityExecutionState execution,
        string folderLabel,
        Exception ex)
    {
        var errorMessage = $"[{folderLabel}] {ex.Message}";
        var logMessage = $"[{folderLabel}] organizer failed: {ex.Message}";
        execution.Errors.Add(errorMessage);
        AddFolderUniformityLog(execution.Logs, logMessage);
        UpdateFolderUniformityState(jobId, state =>
        {
            if (state.Errors.Count < MaxFolderUniformityLogs)
            {
                state.Errors.Add(errorMessage);
            }

            AppendLogInternal(state, logMessage);
        });
    }

    private static void CompleteOrganizerFolderStep(
        string? jobId,
        string folderLabel,
        int totalFolders,
        FolderUniformityExecutionState execution)
    {
        UpdateFolderUniformityState(jobId, state =>
        {
            state.CurrentLibraryFolder = folderLabel;
            state.CurrentArtistFolder = null;
            state.ArtistFoldersProcessed = 0;
            state.ArtistFoldersTotal = 0;
            state.FoldersProcessed = execution.Processed;
            state.FoldersSkipped = execution.Skipped;
            state.CompletedSteps++;
            state.Phase = $"Processed {execution.Processed + execution.Skipped}/{totalFolders} folders";
        });
    }

    private async Task RunDedupeStageAsync(
        EnhancementFolderUniformityRequest request,
        string? jobId,
        bool ranOrganizer,
        IReadOnlyList<FolderDto> enabledFolders,
        FolderUniformityExecutionState execution,
        CancellationToken cancellationToken)
    {
        UpdateFolderUniformityState(jobId, state =>
        {
            state.CurrentArtistFolder = null;
            state.ArtistFoldersProcessed = 0;
            state.ArtistFoldersTotal = 0;
            state.Phase = ranOrganizer ? "Running dedupe after organizer" : "Running dedupe";
        });

        try
        {
            var dedupeResult = await _duplicateCleanerService.ScanAsync(
                enabledFolders.ToList(),
                new DuplicateCleanerOptions
                {
                    UseDuplicatesFolder = true,
                    DuplicatesFolderName = request.DuplicatesFolderName ?? DuplicateCleanerService.DuplicatesFolderName,
                    UseShazamForIdentity = request.UseShazamForDedupe == true,
                    ConflictPolicy = string.IsNullOrWhiteSpace(request.DuplicateConflictPolicy)
                        ? AutoTagOrganizerOptions.DuplicateConflictKeepBest
                        : request.DuplicateConflictPolicy.Trim()
                },
                cancellationToken);
            execution.Dedupe = new
            {
                filesScanned = dedupeResult.FilesScanned,
                duplicatesFound = dedupeResult.DuplicatesFound,
                deleted = dedupeResult.Deleted,
                spaceFreedBytes = dedupeResult.SpaceFreedBytes,
                duplicatesFolderName = dedupeResult.DuplicatesFolderName,
                usedShazamForIdentity = dedupeResult.UsedShazamForIdentity
            };

            UpdateFolderUniformityState(jobId, state =>
            {
                state.Dedupe = execution.Dedupe;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errorMessage = $"[dedupe] {ex.Message}";
            var logMessage = $"[dedupe] failed: {ex.Message}";
            execution.Errors.Add(errorMessage);
            AddFolderUniformityLog(execution.Logs, logMessage);
            UpdateFolderUniformityState(jobId, state =>
            {
                if (state.Errors.Count < MaxFolderUniformityLogs)
                {
                    state.Errors.Add(errorMessage);
                }

                AppendLogInternal(state, logMessage);
            });
        }
        finally
        {
            UpdateFolderUniformityState(jobId, state =>
            {
                state.CompletedSteps++;
                state.CurrentArtistFolder = null;
                state.ArtistFoldersProcessed = 0;
                state.ArtistFoldersTotal = 0;
                state.Phase = "Dedupe stage completed";
            });
        }
    }

    private static void AddFolderUniformityLog(List<string> logs, string message)
    {
        if (logs.Count < MaxFolderUniformityLogs)
        {
            logs.Add(message);
        }
    }

    private static FolderUniformityResultPayload BuildFolderUniformityCompletedResult(
        object options,
        FolderUniformityExecutionState execution)
    {
        var summary = $"Folder uniformity finished: {execution.Processed} processed, {execution.Skipped} skipped.";
        return new FolderUniformityResultPayload(
            Success: execution.Errors.Count == 0,
            Skipped: false,
            Message: summary,
            FoldersProcessed: execution.Processed,
            FoldersSkipped: execution.Skipped,
            Options: options,
            Dedupe: execution.Dedupe,
            ReconciliationReports: execution.Reports,
            Logs: execution.Logs,
            Errors: execution.Errors,
            ValidationError: null);
    }

    private static void AppendFolderUniformityLog(string? jobId, string message)
    {
        UpdateFolderUniformityState(jobId, state => AppendLogInternal(state, message));
    }

    private static void AppendLogInternal(FolderUniformityRunState state, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (state.Logs.Count < MaxFolderUniformityLogs)
        {
            state.Logs.Add(message);
        }
    }

    private static void UpdateFolderUniformityState(string? jobId, Action<FolderUniformityRunState> update)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        lock (FolderUniformityStateLock)
        {
            if (_folderUniformityRun == null)
            {
                return;
            }

            if (!string.Equals(_folderUniformityRun.JobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            update(_folderUniformityRun);
            if (string.Equals(_folderUniformityRun.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
            {
                var totalSteps = Math.Max(1, _folderUniformityRun.TotalSteps);
                var completed = Math.Clamp(_folderUniformityRun.CompletedSteps, 0, totalSteps);
                var progressSteps = (double)completed;
                if (_folderUniformityRun.RunOrganizer
                    && _folderUniformityRun.ArtistFoldersTotal > 0
                    && _folderUniformityRun.ArtistFoldersProcessed > 0)
                {
                    var sourceFolderFraction = Math.Clamp(
                        (double)_folderUniformityRun.ArtistFoldersProcessed / _folderUniformityRun.ArtistFoldersTotal,
                        0d,
                        0.999d);
                    progressSteps = Math.Min(totalSteps, progressSteps + sourceFolderFraction);
                }

                _folderUniformityRun.PercentComplete = (int)Math.Round(progressSteps * 100d / totalSteps);
                return;
            }

            _folderUniformityRun.PercentComplete = 100;
        }
    }

    private static void SetFolderUniformityRun(FolderUniformityRunState runState)
    {
        _folderUniformityRun = runState;
    }

    private static bool IsMusicFolder(FolderDto folder)
    {
        var mode = folder.DesiredQuality?.Trim().ToLowerInvariant();
        return mode is not "video" and not "podcast";
    }

    private static bool TryParseSourceFolderProgress(
        string? message,
        out int sourceFolderIndex,
        out int sourceFolderTotal,
        out string sourceFolderPath)
    {
        sourceFolderIndex = 0;
        sourceFolderTotal = 0;
        sourceFolderPath = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = SourceFolderProgressRegex.Match(message.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["index"].Value, out sourceFolderIndex)
            || !int.TryParse(match.Groups["total"].Value, out sourceFolderTotal))
        {
            return false;
        }

        sourceFolderPath = match.Groups["path"].Value.Trim();
        return sourceFolderIndex > 0
            && sourceFolderTotal > 0
            && sourceFolderIndex <= sourceFolderTotal
            && !string.IsNullOrWhiteSpace(sourceFolderPath);
    }

    private static bool TryParseOrganizerScanPrepared(
        string? message,
        out int candidateFiles,
        out bool includeSubfolders)
    {
        candidateFiles = 0;
        includeSubfolders = false;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = ScanPreparedRegex.Match(message.Trim());
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups["count"].Value, out candidateFiles)
            && bool.TryParse(match.Groups["include"].Value, out includeSubfolders)
            && candidateFiles >= 0;
    }

    private static bool TryParseOrganizerPlanPrepared(string? message, out int plannedMoveActions)
    {
        plannedMoveActions = 0;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = PlanPreparedRegex.Match(message.Trim());
        return match.Success
            && int.TryParse(match.Groups["count"].Value, out plannedMoveActions)
            && plannedMoveActions >= 0;
    }

    private static bool IsOrganizerNoMoveActionsMessage(string? message)
    {
        return string.Equals(
            message?.Trim(),
            "organizer no move actions generated",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildFolderOrganizerOptions(
        AutoTagOrganizerOptions baseOptions,
        AutoTagProfileResolutionService.ResolvedState profileState,
        FolderDto folder,
        EnhancementFolderUniformityRequest request,
        out AutoTagOrganizerOptions folderOptions,
        out string? error)
    {
        folderOptions = CloneOrganizerOptions(baseOptions);
        error = null;

        var profile = AutoTagProfileResolutionService.ResolveFolderProfile(
            profileState,
            folder.Id,
            folder.AutoTagProfileId);
        if (profile == null)
        {
            error = "destination music folder requires a valid AutoTag profile.";
            return false;
        }

        // Folder Uniformity must honor each folder/profile structure as the source of truth.
        // Request-level structure values are applied first, then profile values win.
        ApplyRequestOrganizerOverrides(folderOptions, request);
        ApplyProfileTechnicalOverrides(folderOptions, profile.Technical);
        ApplyProfileFolderStructureOverrides(folderOptions, profile.FolderStructure);
        return true;
    }

    private static AutoTagOrganizerOptions CloneOrganizerOptions(AutoTagOrganizerOptions source)
    {
        return new AutoTagOrganizerOptions
        {
            OnlyMoveWhenTagged = source.OnlyMoveWhenTagged,
            MoveTaggedPath = source.MoveTaggedPath,
            MoveUntaggedPath = source.MoveUntaggedPath,
            DryRun = source.DryRun,
            IncludeSubfolders = source.IncludeSubfolders,
            MoveMisplacedFiles = source.MoveMisplacedFiles,
            RenameFilesToTemplate = source.RenameFilesToTemplate,
            RemoveEmptyFolders = source.RemoveEmptyFolders,
            MergeIntoExistingDestinationFolders = source.MergeIntoExistingDestinationFolders,
            ResolveSameTrackQualityConflicts = source.ResolveSameTrackQualityConflicts,
            KeepBothOnUnresolvedConflicts = source.KeepBothOnUnresolvedConflicts,
            OnlyReorganizeAlbumsWithFullTrackSets = source.OnlyReorganizeAlbumsWithFullTrackSets,
            SkipCompilationFolders = source.SkipCompilationFolders,
            SkipVariousArtistsFolders = source.SkipVariousArtistsFolders,
            GenerateReconciliationReport = source.GenerateReconciliationReport,
            UseShazamForUntaggedFiles = source.UseShazamForUntaggedFiles,
            DuplicateConflictPolicy = source.DuplicateConflictPolicy,
            DuplicatesFolderName = source.DuplicatesFolderName,
            ArtworkPolicy = source.ArtworkPolicy,
            LyricsPolicy = source.LyricsPolicy,
            PreferredExtensions = source.PreferredExtensions.ToList(),
            UsePrimaryArtistFoldersOverride = source.UsePrimaryArtistFoldersOverride,
            MultiArtistSeparatorOverride = source.MultiArtistSeparatorOverride,
            CreateArtistFolderOverride = source.CreateArtistFolderOverride,
            ArtistNameTemplateOverride = source.ArtistNameTemplateOverride,
            CreateAlbumFolderOverride = source.CreateAlbumFolderOverride,
            AlbumNameTemplateOverride = source.AlbumNameTemplateOverride,
            CreateCDFolderOverride = source.CreateCDFolderOverride,
            CreateStructurePlaylistOverride = source.CreateStructurePlaylistOverride,
            CreateSingleFolderOverride = source.CreateSingleFolderOverride,
            CreatePlaylistFolderOverride = source.CreatePlaylistFolderOverride,
            PlaylistNameTemplateOverride = source.PlaylistNameTemplateOverride,
            IllegalCharacterReplacerOverride = source.IllegalCharacterReplacerOverride,
            TechnicalSettingsOverride = source.TechnicalSettingsOverride
        };
    }

    private static void ApplyProfileTechnicalOverrides(AutoTagOrganizerOptions options, TechnicalTagSettings? technical)
    {
        if (technical == null)
        {
            return;
        }

        options.TechnicalSettingsOverride = technical;
        options.UsePrimaryArtistFoldersOverride = technical.SingleAlbumArtist;
        options.MultiArtistSeparatorOverride = string.IsNullOrWhiteSpace(technical.MultiArtistSeparator)
            ? "default"
            : technical.MultiArtistSeparator.Trim();
    }

    private static void ApplyProfileFolderStructureOverrides(AutoTagOrganizerOptions options, FolderStructureSettings? folderStructure)
    {
        if (folderStructure == null)
        {
            return;
        }

        options.CreateArtistFolderOverride = folderStructure.CreateArtistFolder;
        options.ArtistNameTemplateOverride = string.IsNullOrWhiteSpace(folderStructure.ArtistNameTemplate)
            ? "%artist%"
            : folderStructure.ArtistNameTemplate.Trim();
        options.CreateAlbumFolderOverride = folderStructure.CreateAlbumFolder;
        options.AlbumNameTemplateOverride = string.IsNullOrWhiteSpace(folderStructure.AlbumNameTemplate)
            ? "%album%"
            : folderStructure.AlbumNameTemplate.Trim();
        options.CreateCDFolderOverride = folderStructure.CreateCDFolder;
        options.CreateStructurePlaylistOverride = folderStructure.CreateStructurePlaylist;
        options.CreateSingleFolderOverride = folderStructure.CreateSingleFolder;
        options.CreatePlaylistFolderOverride = folderStructure.CreatePlaylistFolder;
        options.PlaylistNameTemplateOverride = string.IsNullOrWhiteSpace(folderStructure.PlaylistNameTemplate)
            ? "%playlist%"
            : folderStructure.PlaylistNameTemplate.Trim();
        options.IllegalCharacterReplacerOverride = string.IsNullOrWhiteSpace(folderStructure.IllegalCharacterReplacer)
            ? "_"
            : folderStructure.IllegalCharacterReplacer.Trim();
    }

    private static void ApplyRequestOrganizerOverrides(AutoTagOrganizerOptions options, EnhancementFolderUniformityRequest request)
    {
        if (request.UsePrimaryArtistFolders.HasValue)
        {
            options.UsePrimaryArtistFoldersOverride = request.UsePrimaryArtistFolders.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.MultiArtistSeparator))
        {
            options.MultiArtistSeparatorOverride = request.MultiArtistSeparator.Trim();
        }

        if (request.CreateArtistFolder.HasValue)
        {
            options.CreateArtistFolderOverride = request.CreateArtistFolder.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.ArtistNameTemplate))
        {
            options.ArtistNameTemplateOverride = request.ArtistNameTemplate.Trim();
        }

        if (request.CreateAlbumFolder.HasValue)
        {
            options.CreateAlbumFolderOverride = request.CreateAlbumFolder.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.AlbumNameTemplate))
        {
            options.AlbumNameTemplateOverride = request.AlbumNameTemplate.Trim();
        }

        if (request.CreateCDFolder.HasValue)
        {
            options.CreateCDFolderOverride = request.CreateCDFolder.Value;
        }

        if (request.CreateStructurePlaylist.HasValue)
        {
            options.CreateStructurePlaylistOverride = request.CreateStructurePlaylist.Value;
        }

        if (request.CreateSingleFolder.HasValue)
        {
            options.CreateSingleFolderOverride = request.CreateSingleFolder.Value;
        }

        if (request.CreatePlaylistFolder.HasValue)
        {
            options.CreatePlaylistFolderOverride = request.CreatePlaylistFolder.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.PlaylistNameTemplate))
        {
            options.PlaylistNameTemplateOverride = request.PlaylistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.IllegalCharacterReplacer))
        {
            options.IllegalCharacterReplacerOverride = request.IllegalCharacterReplacer.Trim();
        }
    }

    private static void ApplyFolderUniformityDefaultsFromSettings(
        EnhancementFolderUniformityRequest request,
        DeezSpoTagSettings settings)
    {
        request.UsePrimaryArtistFolders ??= settings.Tags?.SingleAlbumArtist;
        if (string.IsNullOrWhiteSpace(request.MultiArtistSeparator))
        {
            request.MultiArtistSeparator = settings.Tags?.MultiArtistSeparator;
        }

        request.CreateArtistFolder ??= settings.CreateArtistFolder;
        if (string.IsNullOrWhiteSpace(request.ArtistNameTemplate))
        {
            request.ArtistNameTemplate = settings.ArtistNameTemplate;
        }

        request.CreateAlbumFolder ??= settings.CreateAlbumFolder;
        if (string.IsNullOrWhiteSpace(request.AlbumNameTemplate))
        {
            request.AlbumNameTemplate = settings.AlbumNameTemplate;
        }

        request.CreateCDFolder ??= settings.CreateCDFolder;
        request.CreateStructurePlaylist ??= settings.CreateStructurePlaylist;
        request.CreateSingleFolder ??= settings.CreateSingleFolder;
        request.CreatePlaylistFolder ??= settings.CreatePlaylistFolder;

        if (string.IsNullOrWhiteSpace(request.PlaylistNameTemplate))
        {
            request.PlaylistNameTemplate = settings.PlaylistNameTemplate;
        }

        if (string.IsNullOrWhiteSpace(request.IllegalCharacterReplacer))
        {
            request.IllegalCharacterReplacer = settings.IllegalCharacterReplacer;
        }
    }

    [HttpPost("enhancement/quality-checks")]
    public async Task<IActionResult> RunEnhancementQualityChecks(
        [FromBody] EnhancementQualityChecksRequest request,
        CancellationToken cancellationToken)
    {
        var technicalProfiles = NormalizeTechnicalProfiles(request.TechnicalProfiles);
        var runQualityScanner = request.FlagMissingTags == true
            || request.FlagMismatchedMetadata == true
            || request.QueueAtmosAlternatives == true
            || technicalProfiles.Count > 0;
        var runDuplicateCheck = request.FlagDuplicates == true;
        var runLyricsRefresh = request.QueueLyricsRefresh == true;
        if (!runQualityScanner && !runDuplicateCheck && !runLyricsRefresh)
        {
            return BadRequest("Enable at least one quality check to run.");
        }

        var (scopeError, enabledFolders, scopedFolderIds) = await ResolveEnhancementScopeAsync(request, cancellationToken);
        if (scopeError != null)
        {
            return scopeError;
        }

        var qualityScanner = runQualityScanner
            ? await StartEnhancementQualityScannerAsync(request, technicalProfiles, scopedFolderIds, cancellationToken)
            : null;
        var duplicateCheck = runDuplicateCheck
            ? await RunEnhancementDuplicateCheckAsync(request, enabledFolders, cancellationToken)
            : null;
        var lyricsRefresh = runLyricsRefresh
            ? await RunEnhancementLyricsRefreshAsync(request, technicalProfiles, scopedFolderIds, cancellationToken)
            : null;

        return Ok(new
        {
            success = true,
            qualityScanner,
            duplicateCheck,
            lyricsRefresh
        });
    }

    [HttpGet("enhancement/technical-profiles")]
    public async Task<IActionResult> GetEnhancementTechnicalProfiles(
        [FromQuery] long? folderId,
        [FromQuery] string? folderIds,
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(
            _folderScopeDependencies.LibraryRepository,
            _folderScopeDependencies.LibraryConfigStore,
            cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled && !string.IsNullOrWhiteSpace(folder.RootPath))
            .ToList();
        var folderIdsFromQuery = AutoTagFolderScopeHelper.ParseFolderIdsQuery(folderIds);
        var selectedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(folderIdsFromQuery, folderId, enabledFolders);

        if (selectedFolderIds.Count > 0 && enabledFolders.All(folder => !selectedFolderIds.Contains(folder.Id)))
        {
            return BadRequest("Selected library folders were not found or are disabled.");
        }

        var resolvedScope = string.Equals(scope, "watchlist", StringComparison.OrdinalIgnoreCase)
            ? "watchlist"
            : "all";
        var tracks = await _folderScopeDependencies.LibraryRepository.GetQualityScanTracksAsync(
            resolvedScope,
            selectedFolderIds.Count == 1 ? selectedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);
        if (selectedFolderIds.Count > 1)
        {
            var allowed = selectedFolderIds.ToHashSet();
            tracks = tracks
                .Where(track => track.DestinationFolderId.HasValue && allowed.Contains(track.DestinationFolderId.Value))
                .ToList();
        }

        var profiles = tracks
            .Select(FormatTechnicalProfile)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                value = group.First(),
                count = group.Count()
            })
            .OrderByDescending(item => item.count)
            .ThenBy(item => item.value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new
        {
            scope = resolvedScope,
            folderIds = selectedFolderIds,
            totalTracks = tracks.Count,
            profiles
        });
    }

    private async Task<(IActionResult? Error, List<FolderDto> EnabledFolders, IReadOnlyList<long> ScopedFolderIds)> ResolveEnhancementScopeAsync(
        EnhancementQualityChecksRequest request,
        CancellationToken cancellationToken)
    {
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(
            _folderScopeDependencies.LibraryRepository,
            _folderScopeDependencies.LibraryConfigStore,
            cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled && !string.IsNullOrWhiteSpace(folder.RootPath))
            .ToList();
        var scopedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(request.FolderIds, request.FolderId, enabledFolders);
        if (scopedFolderIds.Count == 0)
        {
            return (null, enabledFolders, scopedFolderIds);
        }

        enabledFolders = enabledFolders
            .Where(folder => scopedFolderIds.Contains(folder.Id))
            .ToList();
        if (enabledFolders.Count == 0)
        {
            return (BadRequest("Selected library folders were not found or are disabled."), new List<FolderDto>(), new List<long>());
        }

        return (null, enabledFolders, scopedFolderIds);
    }

    private async Task<object> StartEnhancementQualityScannerAsync(
        EnhancementQualityChecksRequest request,
        IReadOnlyCollection<string> technicalProfiles,
        IReadOnlyList<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        var minSampleRateHz = request.MinSampleRateKhz.HasValue && request.MinSampleRateKhz.Value > 0
            ? (int?)Math.Clamp((int)Math.Round(request.MinSampleRateKhz.Value * 1000d), 1000, 768000)
            : null;
        var started = await _qualityScannerService.StartAsync(
            new QualityScannerStartRequest
            {
                Scope = request.Scope,
                FolderId = scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
                MinFormat = request.MinFormat,
                MinBitDepth = request.MinBitDepth,
                MinSampleRateHz = minSampleRateHz,
                QueueAtmosAlternatives = request.QueueAtmosAlternatives,
                CooldownMinutes = request.CooldownMinutes,
                Trigger = "enhancement",
                MarkAutomationWindow = false,
                TechnicalProfiles = technicalProfiles.ToList(),
                FolderIds = scopedFolderIds.ToList()
            },
            cancellationToken);
        return new
        {
            requested = true,
            started,
            state = _qualityScannerService.GetState()
        };
    }

    private async Task<object> RunEnhancementDuplicateCheckAsync(
        EnhancementQualityChecksRequest request,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (enabledFolders.Count == 0)
        {
            return new
            {
                filesScanned = 0,
                duplicatesFound = 0,
                deleted = 0,
                spaceFreedBytes = 0,
                duplicatesFolderName = DuplicateCleanerService.DuplicatesFolderName,
                usedShazamForIdentity = false
            };
        }

        var result = await _duplicateCleanerService.ScanAsync(
            enabledFolders.ToList(),
            new DuplicateCleanerOptions
            {
                UseDuplicatesFolder = request.UseDuplicatesFolder ?? true,
                DuplicatesFolderName = request.DuplicatesFolderName ?? DuplicateCleanerService.DuplicatesFolderName,
                UseShazamForIdentity = request.UseShazamForDedupe == true
            },
            cancellationToken);
        return new
        {
            filesScanned = result.FilesScanned,
            duplicatesFound = result.DuplicatesFound,
            deleted = result.Deleted,
            spaceFreedBytes = result.SpaceFreedBytes,
            duplicatesFolderName = result.DuplicatesFolderName,
            usedShazamForIdentity = result.UsedShazamForIdentity
        };
    }

    private async Task<object> RunEnhancementLyricsRefreshAsync(
        EnhancementQualityChecksRequest request,
        IReadOnlyCollection<string> technicalProfiles,
        IReadOnlyList<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        if (!LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            return new
            {
                requested = 0,
                enqueued = 0,
                skipped = 0,
                disabledByTechnicalPreference = true
            };
        }

        var tracks = await _folderScopeDependencies.LibraryRepository.GetQualityScanTracksAsync(
            request.Scope,
            scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);
        if (scopedFolderIds.Count > 1)
        {
            var allowed = scopedFolderIds.ToHashSet();
            tracks = tracks
                .Where(track => track.DestinationFolderId.HasValue && allowed.Contains(track.DestinationFolderId.Value))
                .ToList();
        }

        if (technicalProfiles.Count > 0)
        {
            var allowedProfiles = technicalProfiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            tracks = tracks
                .Where(track => allowedProfiles.Contains(FormatTechnicalProfile(track)))
                .ToList();
        }

        var trackIds = tracks
            .Select(track => track.TrackId)
            .Distinct()
            .ToList();
        var enqueueResult = _lyricsRefreshQueueService.Enqueue(trackIds);
        return new
        {
            requested = enqueueResult.Requested,
            enqueued = enqueueResult.Enqueued,
            skipped = enqueueResult.Skipped,
            disabledByTechnicalPreference = false
        };
    }

    private static string FormatTechnicalProfile(QualityScanTrackDto track)
    {
        var extension = string.IsNullOrWhiteSpace(track.BestExtension)
            ? "UNKNOWN"
            : track.BestExtension.Trim().ToUpperInvariant();
        var bitDepth = track.BestBitsPerSample.HasValue && track.BestBitsPerSample.Value > 0
            ? $"{track.BestBitsPerSample.Value}-bit"
            : "unknown";
        var sampleRate = track.BestSampleRateHz.HasValue && track.BestSampleRateHz.Value > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{track.BestSampleRateHz.Value / 1000d:0.0} kHz")
            : "unknown";
        return $"{extension} • {bitDepth} • {sampleRate}";
    }

    private static IReadOnlyList<string> NormalizeTechnicalProfiles(IReadOnlyList<string>? source)
    {
        if (source is null || source.Count == 0)
        {
            return Array.Empty<string>();
        }

        return source
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }
}
