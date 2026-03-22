using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
public class AutoTagEnhancementController : ControllerBase
{
    private static readonly object FolderUniformityStateLock = new();
    private static FolderUniformityRunState? _folderUniformityRun;

    private const int MaxFolderUniformityLogs = 600;

    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _libraryConfigStore;
    private readonly AutoTagLibraryOrganizer _libraryOrganizer;
    private readonly QualityScannerService _qualityScannerService;
    private readonly DuplicateCleanerService _duplicateCleanerService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LyricsRefreshQueueService _lyricsRefreshQueueService;
    private readonly AutoTagProfileResolutionService _profileResolutionService;

    public AutoTagEnhancementController(
        LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        AutoTagLibraryOrganizer libraryOrganizer,
        QualityScannerService qualityScannerService,
        DuplicateCleanerService duplicateCleanerService,
        DeezSpoTagSettingsService settingsService,
        LyricsRefreshQueueService lyricsRefreshQueueService,
        AutoTagProfileResolutionService profileResolutionService)
    {
        _libraryRepository = libraryRepository;
        _libraryConfigStore = libraryConfigStore;
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
        public string Status { get; set; } = "running";
        public string Phase { get; set; } = "Preparing scope";
        public bool RunOrganizer { get; set; }
        public bool RunDedupe { get; set; }
        public int TotalFolders { get; set; }
        public int TotalSteps { get; set; }
        public int CompletedSteps { get; set; }
        public int FoldersProcessed { get; set; }
        public int FoldersSkipped { get; set; }
        public int PercentComplete { get; set; }
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
        var payload = await ExecuteFolderUniformityAsync(request, cancellationToken, runState: null);
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
                && string.Equals(_folderUniformityRun.Status, "running", StringComparison.OrdinalIgnoreCase))
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
                Status = "running",
                Phase = "Preparing scope",
                PercentComplete = 0
            };
            _folderUniformityRun = runState;
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
            var payload = await ExecuteFolderUniformityAsync(request, CancellationToken.None, runState);
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
        CancellationToken cancellationToken,
        FolderUniformityRunState? runState)
    {
        request ??= new EnhancementFolderUniformityRequest();
        var runOrganizer = request.EnforceFolderStructure != false;
        var runDedupe = request.RunDedupe != false;

        var organizerOptions = new AutoTagOrganizerOptions
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

        var options = new
        {
            RunOrganizer = runOrganizer,
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
            DuplicatesFolderName = string.IsNullOrWhiteSpace(request.DuplicatesFolderName)
                ? DuplicateCleanerService.DuplicatesFolderName
                : request.DuplicatesFolderName.Trim()
        };

        if (!runOrganizer && !runDedupe)
        {
            UpdateFolderUniformityState(runState?.JobId, state =>
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

        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(_libraryRepository, _libraryConfigStore, cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled
                && !string.IsNullOrWhiteSpace(folder.RootPath)
                && IsMusicFolder(folder))
            .ToList();
        var scopedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(request.FolderIds, request.FolderId, enabledFolders);
        if (scopedFolderIds.Count > 0)
        {
            enabledFolders = enabledFolders
                .Where(folder => scopedFolderIds.Contains(folder.Id))
                .ToList();
        }

        if (enabledFolders.Count == 0)
        {
            UpdateFolderUniformityState(runState?.JobId, state =>
            {
                state.RunOrganizer = runOrganizer;
                state.RunDedupe = runDedupe;
                state.Options = options;
                state.TotalFolders = 0;
                state.TotalSteps = 0;
                state.CompletedSteps = 0;
                state.Phase = "No folders available";
                state.Errors.Add("No enabled music library folders are available for folder uniformity.");
            });
            return new FolderUniformityResultPayload(
                Success: false,
                Skipped: false,
                Message: "No enabled music library folders are available for folder uniformity.",
                FoldersProcessed: 0,
                FoldersSkipped: 0,
                Options: options,
                Dedupe: null,
                ReconciliationReports: Array.Empty<object>(),
                Logs: Array.Empty<string>(),
                Errors: new[] { "No enabled music library folders are available for folder uniformity." },
                ValidationError: "No enabled music library folders are available for folder uniformity.");
        }

        var profileState = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);

        var logs = new List<string>();
        var errors = new List<string>();
        var reports = new List<object>();
        var processed = 0;
        var skipped = 0;
        object? dedupe = null;
        var organizerFolderCount = runOrganizer ? enabledFolders.Count : 0;
        var totalSteps = organizerFolderCount + (runDedupe ? 1 : 0);
        if (totalSteps <= 0)
        {
            totalSteps = 1;
        }

        UpdateFolderUniformityState(runState?.JobId, state =>
        {
            state.RunOrganizer = runOrganizer;
            state.RunDedupe = runDedupe;
            state.Options = options;
            state.TotalFolders = enabledFolders.Count;
            state.TotalSteps = totalSteps;
            state.CompletedSteps = 0;
            state.FoldersProcessed = 0;
            state.FoldersSkipped = 0;
            state.Phase = runOrganizer
                ? $"Running folder uniformity (0/{enabledFolders.Count})"
                : "Running dedupe";
            state.Logs.Clear();
            state.Errors.Clear();
            state.ReconciliationReports.Clear();
        });

        if (runOrganizer)
        {
            var folderIndex = 0;
            foreach (var folder in enabledFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                folderIndex++;
                var folderLabel = string.IsNullOrWhiteSpace(folder.DisplayName) ? $"Folder {folder.Id}" : folder.DisplayName!;
                UpdateFolderUniformityState(runState?.JobId, state =>
                {
                    state.Phase = $"Processing {folderLabel} ({folderIndex}/{enabledFolders.Count})";
                });

                if (!TryBuildFolderOrganizerOptions(organizerOptions, profileState, folder, out var folderOrganizerOptions, out var profileError))
                {
                    skipped++;
                    if (!string.IsNullOrWhiteSpace(profileError))
                    {
                        errors.Add(profileError);
                    }

                    var skippedMessage = $"[{folderLabel}] skipped: {profileError}";
                    if (logs.Count < MaxFolderUniformityLogs)
                    {
                        logs.Add(skippedMessage);
                    }

                    AppendFolderUniformityLog(runState?.JobId, skippedMessage);
                    UpdateFolderUniformityState(runState?.JobId, state =>
                    {
                        if (!string.IsNullOrWhiteSpace(profileError) && state.Errors.Count < MaxFolderUniformityLogs)
                        {
                            state.Errors.Add(profileError);
                        }

                        state.FoldersProcessed = processed;
                        state.FoldersSkipped = skipped;
                        state.CompletedSteps++;
                        state.Phase = $"Processed {processed + skipped}/{enabledFolders.Count} folders";
                    });
                    continue;
                }

                var rootPath = folder.RootPath?.Trim();
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                {
                    skipped++;
                    var skippedMessage = $"[{folderLabel}] skipped: folder path is missing or does not exist.";
                    if (logs.Count < MaxFolderUniformityLogs)
                    {
                        logs.Add(skippedMessage);
                    }

                    AppendFolderUniformityLog(runState?.JobId, skippedMessage);
                    UpdateFolderUniformityState(runState?.JobId, state =>
                    {
                        state.FoldersProcessed = processed;
                        state.FoldersSkipped = skipped;
                        state.CompletedSteps++;
                        state.Phase = $"Processed {processed + skipped}/{enabledFolders.Count} folders";
                    });
                    continue;
                }

                try
                {
                    processed++;
                    var report = folderOrganizerOptions.GenerateReconciliationReport
                        ? new AutoTagLibraryOrganizer.AutoTagOrganizerReport()
                        : null;
                    await _libraryOrganizer.OrganizePathAsync(rootPath, folderOrganizerOptions, report, message =>
                    {
                        var line = $"[{folderLabel}] {message}";
                        if (logs.Count < MaxFolderUniformityLogs)
                        {
                            logs.Add(line);
                        }

                        AppendFolderUniformityLog(runState?.JobId, line);
                    });

                    if (report != null)
                    {
                        var reportEntry = new
                        {
                            folderId = folder.Id,
                            folderName = folderLabel,
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
                        reports.Add(reportEntry);
                        UpdateFolderUniformityState(runState?.JobId, state =>
                        {
                            state.ReconciliationReports.Add(reportEntry);
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var errorMessage = $"[{folderLabel}] {ex.Message}";
                    errors.Add(errorMessage);
                    var logMessage = $"[{folderLabel}] organizer failed: {ex.Message}";
                    if (logs.Count < MaxFolderUniformityLogs)
                    {
                        logs.Add(logMessage);
                    }

                    UpdateFolderUniformityState(runState?.JobId, state =>
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
                    UpdateFolderUniformityState(runState?.JobId, state =>
                    {
                        state.FoldersProcessed = processed;
                        state.FoldersSkipped = skipped;
                        state.CompletedSteps++;
                        state.Phase = $"Processed {processed + skipped}/{enabledFolders.Count} folders";
                    });
                }
            }
        }

        if (runDedupe)
        {
            UpdateFolderUniformityState(runState?.JobId, state =>
            {
                state.Phase = "Running dedupe";
            });

            try
            {
                var dedupeResult = await _duplicateCleanerService.ScanAsync(
                    enabledFolders.ToList(),
                    new DuplicateCleanerOptions
                    {
                        UseDuplicatesFolder = true,
                        DuplicatesFolderName = request.DuplicatesFolderName ?? DuplicateCleanerService.DuplicatesFolderName,
                        UseShazamForIdentity = request.UseShazamForDedupe == true
                    },
                    cancellationToken);
                dedupe = new
                {
                    filesScanned = dedupeResult.FilesScanned,
                    duplicatesFound = dedupeResult.DuplicatesFound,
                    deleted = dedupeResult.Deleted,
                    spaceFreedBytes = dedupeResult.SpaceFreedBytes,
                    duplicatesFolderName = dedupeResult.DuplicatesFolderName,
                    usedShazamForIdentity = dedupeResult.UsedShazamForIdentity
                };

                UpdateFolderUniformityState(runState?.JobId, state =>
                {
                    state.Dedupe = dedupe;
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var errorMessage = $"[dedupe] {ex.Message}";
                errors.Add(errorMessage);
                var logMessage = $"[dedupe] failed: {ex.Message}";
                if (logs.Count < MaxFolderUniformityLogs)
                {
                    logs.Add(logMessage);
                }

                UpdateFolderUniformityState(runState?.JobId, state =>
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
                UpdateFolderUniformityState(runState?.JobId, state =>
                {
                    state.CompletedSteps++;
                    state.Phase = "Dedupe stage completed";
                });
            }
        }

        var summary = $"Folder uniformity finished: {processed} processed, {skipped} skipped.";
        return new FolderUniformityResultPayload(
            Success: errors.Count == 0,
            Skipped: false,
            Message: summary,
            FoldersProcessed: processed,
            FoldersSkipped: skipped,
            Options: options,
            Dedupe: dedupe,
            ReconciliationReports: reports,
            Logs: logs,
            Errors: errors,
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
            if (string.Equals(_folderUniformityRun.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                var totalSteps = Math.Max(1, _folderUniformityRun.TotalSteps);
                var completed = Math.Clamp(_folderUniformityRun.CompletedSteps, 0, totalSteps);
                _folderUniformityRun.PercentComplete = (int)Math.Round((double)completed * 100d / totalSteps);
                return;
            }

            _folderUniformityRun.PercentComplete = 100;
        }
    }

    private static bool IsMusicFolder(FolderDto folder)
    {
        var mode = folder.DesiredQuality?.Trim().ToLowerInvariant();
        return mode is not "video" and not "podcast";
    }

    private static bool TryBuildFolderOrganizerOptions(
        AutoTagOrganizerOptions baseOptions,
        AutoTagProfileResolutionService.ResolvedState profileState,
        FolderDto folder,
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
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(_libraryRepository, _libraryConfigStore, cancellationToken);
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
        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
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
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(_libraryRepository, _libraryConfigStore, cancellationToken);
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

        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
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
