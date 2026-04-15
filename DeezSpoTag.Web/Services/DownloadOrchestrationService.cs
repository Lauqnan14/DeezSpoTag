using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Plex;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadOrchestrationService : BackgroundService
{
    private sealed record AutoTagStages(bool HasEnrichment, bool HasEnhancement);
    private sealed record EnhancementTarget(
        string FolderId,
        string RootPath,
        string? FolderProfileReference,
        TimeSpan? ScheduleInterval,
        bool IsDue,
        DateTimeOffset? LastRunAtUtc);
    private sealed record AutomationProfileContext(
        List<TaggingProfile> Profiles,
        AutoTagDefaultsDto Defaults,
        TaggingProfile? DefaultProfile,
        IReadOnlyDictionary<long, FolderDto> FoldersById);
    private sealed record PipelineRunContext(
        DateTimeOffset PipelineStartedAt,
        string AutomationConfigJson,
        TaggingProfile? AutomationProfile,
        AutoTagStages Stages,
        string DownloadRootPath,
        IReadOnlyList<string> PendingQueueUuids);
    private sealed record EnhancementTargetPlan(List<EnhancementTarget> Targets, List<EnhancementTarget> DueTargets);
    private sealed record EnhancementTargetRunResult(bool Attempted, bool PausedForDownload);
    private sealed record EnhancementExecutionResult(List<EnhancementTarget> AttemptedTargets, bool PausedForDownload, bool AbortedForDownload);
    public sealed record DownloadGateDecision(bool Allowed, string Message, bool EnhancementPaused);
    private sealed class EnhancementScheduleState
    {
        public Dictionary<string, DateTimeOffset> LastRunByFolderId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LastScheduleByFolderId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex ScheduleTokenRegex = new(
        @"^\s*(\d+)\s*([dhwm])\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));
    private static readonly HashSet<string> StagingAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".wav",
        ".aiff",
        ".aif",
        ".alac",
        ".m4a",
        ".m4b",
        ".mp4",
        ".aac",
        ".mp3",
        ".wma",
        ".ogg",
        ".opus",
        ".oga",
        ".ape",
        ".wv",
        ".mp2",
        ".mp1",
        ".tta",
        ".dsf",
        ".dff",
        ".mka"
    };
    private static readonly TimeSpan StagingGateLogThrottle = TimeSpan.FromMinutes(1);
    private const string WarningLogLevel = "warning";
    private static readonly JsonSerializerOptions ScheduleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly DownloadQueueRepository _queueRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly AutoTagService _autoTagService;
    private readonly AutoTagConfigBuilder _configBuilder;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LibraryScanRunner _scanRunner;
    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexApiClient;
    private readonly TrackAnalysisBackgroundService _analysisService;
    private readonly VibeAnalysisSettingsStore _vibeSettingsStore;
    private readonly LibraryConfigStore _configStore;
    private readonly ILogger<DownloadOrchestrationService> _logger;
    private readonly string _enhancementSchedulePath;
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _enhancementPauseLock = new(1, 1);
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _downloadIdleDelay = TimeSpan.FromSeconds(15);
    private readonly object _enhancementResumeLock = new();
    private readonly HashSet<string> _pendingEnhancementResumeFolderIds = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset? _queueIdleSince;
    private DateTimeOffset _lastPipelineCompletedAt = DateTimeOffset.UtcNow;
    private bool _pipelineRequested;
    private bool _taggingInProgress;
    private volatile bool _enhancementStageRunning;
    private volatile bool _enhancementPauseRequested;
    private volatile bool _enhancementResumeAwaitingPipelineCompletion;
    private string? _activeEnhancementJobId;
    private DateTimeOffset? _lastStagingGateLogAt;
    private string? _lastStagingGateLogReason;

    public DownloadOrchestrationService(
        IServiceProvider serviceProvider,
        IWebHostEnvironment env,
        ILogger<DownloadOrchestrationService> logger)
    {
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _libraryRepository = serviceProvider.GetRequiredService<LibraryRepository>();
        _autoTagService = serviceProvider.GetRequiredService<AutoTagService>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _configBuilder = serviceProvider.GetRequiredService<AutoTagConfigBuilder>();
        _profileResolutionService = serviceProvider.GetRequiredService<AutoTagProfileResolutionService>();
        _scanRunner = serviceProvider.GetRequiredService<LibraryScanRunner>();
        _platformAuthService = serviceProvider.GetRequiredService<PlatformAuthService>();
        _plexApiClient = serviceProvider.GetRequiredService<PlexApiClient>();
        _analysisService = serviceProvider.GetRequiredService<TrackAnalysisBackgroundService>();
        _vibeSettingsStore = serviceProvider.GetRequiredService<VibeAnalysisSettingsStore>();
        _configStore = serviceProvider.GetRequiredService<LibraryConfigStore>();
        _logger = logger;

        var configuredDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        var dataRoot = string.IsNullOrWhiteSpace(configuredDataDir)
            ? Path.Join(env.ContentRootPath, "Data")
            : configuredDataDir;
        var autoTagDataDir = Path.Join(dataRoot, "autotag");
        Directory.CreateDirectory(autoTagDataDir);
        _enhancementSchedulePath = Path.Join(autoTagDataDir, "enhancement-schedule-state.json");
    }

    public bool TaggingInProgress => _taggingInProgress || _autoTagService.HasRunningJobs();

    private void QueueEnhancementResumeFolder(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return;
        }

        lock (_enhancementResumeLock)
        {
            _pendingEnhancementResumeFolderIds.Add(folderId.Trim());
        }
    }

    private void QueueEnhancementResumeFolders(IEnumerable<string> folderIds)
    {
        if (folderIds == null)
        {
            return;
        }

        lock (_enhancementResumeLock)
        {
            foreach (var folderId in folderIds)
            {
                if (string.IsNullOrWhiteSpace(folderId))
                {
                    continue;
                }

                _pendingEnhancementResumeFolderIds.Add(folderId.Trim());
            }
        }
    }

    private List<string> ConsumeEnhancementResumeFolders()
    {
        lock (_enhancementResumeLock)
        {
            if (_pendingEnhancementResumeFolderIds.Count == 0)
            {
                return new List<string>();
            }

            var folders = _pendingEnhancementResumeFolderIds.ToList();
            _pendingEnhancementResumeFolderIds.Clear();
            return folders;
        }
    }

    public async Task<DownloadGateDecision> EvaluateDownloadGateAsync(CancellationToken cancellationToken = default)
    {
        if (!TaggingInProgress)
        {
            return AllowDownloads();
        }

        if (_autoTagService.TryGetRunningEnrichmentJobId(out _))
        {
            return DenyDownloads("Downloads paused while enrichment is running.");
        }

        var enhancementDecision = await TryResolveEnhancementGateDecisionAsync(cancellationToken);
        if (enhancementDecision != null)
        {
            return enhancementDecision;
        }

        var enrichmentPauseDecision = await TryResolveEnrichmentPauseDecisionAsync();
        if (enrichmentPauseDecision != null)
        {
            return enrichmentPauseDecision;
        }

        var runningJobDecision = await TryResolveRunningJobGateDecisionAsync();
        if (runningJobDecision != null)
        {
            return runningJobDecision;
        }

        _logger.LogWarning("AutoTag reported running jobs but no active job id was found. Allowing download.");
        return AllowDownloads();
    }

    private static DownloadGateDecision AllowDownloads(bool enhancementPaused = false)
        => new(true, string.Empty, enhancementPaused);

    private static DownloadGateDecision DenyDownloads(string message)
        => new(false, message, false);

    private async Task<DownloadGateDecision?> TryResolveEnhancementGateDecisionAsync(CancellationToken cancellationToken)
    {
        string? runningEnhancementJobId = null;
        var hasEnhancementStage = _enhancementStageRunning || _autoTagService.TryGetRunningEnhancementJobId(out runningEnhancementJobId);
        if (!hasEnhancementStage)
        {
            return null;
        }

        var paused = await TryPauseEnhancementForIncomingDownloadAsync(runningEnhancementJobId, cancellationToken);
        return paused || !TaggingInProgress
            ? AllowDownloads(paused)
            : null;
    }

    private async Task<DownloadGateDecision?> TryResolveEnrichmentPauseDecisionAsync()
    {
        if (!_autoTagService.TryGetRunningEnrichmentJobId(out var runningEnrichmentJobId))
        {
            return null;
        }

        var paused = await TryPauseEnrichmentForIncomingDownloadAsync(runningEnrichmentJobId);
        return paused || !TaggingInProgress
            ? AllowDownloads()
            : null;
    }

    private async Task<DownloadGateDecision?> TryResolveRunningJobGateDecisionAsync()
    {
        if (!_autoTagService.TryGetAnyRunningJobId(out var runningJobId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(runningJobId))
        {
            return DenyDownloads("Downloads paused while AutoTag state is resolving.");
        }

        var runningJob = _autoTagService.GetJob(runningJobId);
        var runIntentDecision = TryResolveRunIntentGateDecision(runningJob);
        if (runIntentDecision != null)
        {
            return runIntentDecision;
        }

        var paused = await TryPauseAnyRunningAutoTagForIncomingDownloadAsync(runningJobId);
        if (paused || !TaggingInProgress)
        {
            return AllowDownloads();
        }

        return DenyDownloads("Downloads paused while AutoTag is still stopping.");
    }

    private static DownloadGateDecision? TryResolveRunIntentGateDecision(AutoTagJob? runningJob)
    {
        if (runningJob == null)
        {
            return null;
        }

        if (string.Equals(runningJob.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            return DenyDownloads("Downloads paused while enrichment is running.");
        }

        if (string.Equals(runningJob.RunIntent, AutoTagLiterals.RunIntentEnhancementOnly, StringComparison.OrdinalIgnoreCase)
            || string.Equals(runningJob.RunIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DenyDownloads("Downloads paused while AutoTag stage is unknown (protecting enrichment).");
    }

    public void MarkDownloadQueued()
    {
        _queueIdleSince = null;
        if (HasPendingEnhancementResumeFolders())
        {
            _enhancementResumeAwaitingPipelineCompletion = true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Download orchestration tick failed.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var hasActiveDownloads = await _queueRepository.HasActiveDownloadsAsync(cancellationToken);
        if (hasActiveDownloads)
        {
            _queueIdleSince = null;
            return;
        }

        _queueIdleSince ??= DateTimeOffset.UtcNow;
        var idleDuration = DateTimeOffset.UtcNow - _queueIdleSince.Value;
        var hasPendingPostDownloadEnrichment = await HasPendingPostDownloadEnrichmentAsync(cancellationToken);
        if (hasPendingPostDownloadEnrichment)
        {
            _pipelineRequested = true;
        }

        if (!_pipelineRequested)
        {
            if (idleDuration < _downloadIdleDelay)
            {
                return;
            }

            if (_autoTagService.HasRunningJobs())
            {
                return;
            }

            if (!await _pipelineLock.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                await RunScheduledEnhancementIfDueAsync(cancellationToken);
            }
            finally
            {
                _pipelineLock.Release();
            }
            return;
        }

        if (idleDuration < _downloadIdleDelay)
        {
            return;
        }

        if (_autoTagService.HasRunningJobs())
        {
            if (hasPendingPostDownloadEnrichment)
            {
                _ = await TryPauseEnhancementForPendingPipelineAsync(cancellationToken);
            }
            return;
        }

        if (!await _pipelineLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await RunPipelineAsync(cancellationToken);
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private async Task RunPipelineAsync(CancellationToken cancellationToken)
    {
        var context = await PreparePipelineRunContextAsync(cancellationToken);
        if (context is null)
        {
            return;
        }

        await RunPipelineEnrichmentAsync(context, cancellationToken);
        if (!await EnsurePipelineStillIdleAsync(cancellationToken))
        {
            return;
        }

        if (await RunRecentDownloadEnhancementAsync(context, cancellationToken))
        {
            return;
        }

        if (!await EnsurePipelineStillIdleAsync(cancellationToken))
        {
            return;
        }

        await RunPostAutoTagStagesAsync(cancellationToken);
        _lastPipelineCompletedAt = context.PipelineStartedAt;
    }

    private async Task<bool> ResumeInterruptedEnhancementAsync(CancellationToken cancellationToken)
    {
        var resumeFolderIds = ConsumeEnhancementResumeFolders();
        if (resumeFolderIds.Count == 0)
        {
            return false;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: resuming interrupted enhancement for folder(s): {string.Join(", ", resumeFolderIds)}."));

        var pausedAgain = await RunEnhancementStageAsync(
            forceRunEvenIfNotDue: true,
            sourceLabel: "resume",
            quietWhenNoDue: false,
            restrictedFolderIds: resumeFolderIds,
            cancellationToken: cancellationToken);

        if (pausedAgain)
        {
            _pipelineRequested = true;
            _queueIdleSince = null;
        }

        return pausedAgain;
    }

    private async Task<PipelineRunContext?> PreparePipelineRunContextAsync(CancellationToken cancellationToken)
    {
        var pipelineStartedAt = DateTimeOffset.UtcNow;
        _pipelineRequested = false;
        _enhancementPauseRequested = false;

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            _logger.LogInformation("Orchestration skipped: downloads became active again.");
            return null;
        }

        if (!TryResolveDownloadEnrichmentRoot(out var downloadRootPath, out var error))
        {
            _logger.LogWarning("Orchestration skipped: {Reason}", error);
            return null;
        }

        var profileContext = await BuildAutomationProfileContextAsync(cancellationToken);
        var pendingItems = await GetPendingPostDownloadItemsAsync(cancellationToken);
        var automationProfile = ResolveAutomationProfileForPendingDownloads(profileContext, pendingItems);
        if (automationProfile == null)
        {
            _logger.LogWarning("Orchestration skipped: destination folder has no valid current AutoTag profile.");
            return null;
        }

        var configJson = GetAutoTagConfigJson(automationProfile);
        if (string.IsNullOrWhiteSpace(configJson))
        {
            _logger.LogWarning("Orchestration skipped: the destination folder profile could not be materialized into AutoTag config.");
            return null;
        }

        var recoveredCount = pendingItems.Count(item => item.UpdatedAt <= _lastPipelineCompletedAt);
        if (recoveredCount > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Orchestration recovered {RecoveredCount} stale completed download task(s) from download root for post-download enrichment.",
                    recoveredCount);
            }
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: recovered {recoveredCount} stale completed download task(s) from download root for enrichment."));
        }

        return new PipelineRunContext(
            pipelineStartedAt,
            configJson,
            automationProfile,
            GetAutoTagStages(configJson),
            downloadRootPath,
            pendingItems
                .Select(item => item.QueueUuid)
                .Where(queueUuid => !string.IsNullOrWhiteSpace(queueUuid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private async Task RunPipelineEnrichmentAsync(PipelineRunContext context, CancellationToken cancellationToken)
    {
        if (context.Stages.HasEnrichment)
        {
            await RunPostDownloadEnrichmentAsync(
                context.AutomationConfigJson,
                context.AutomationProfile,
                context.DownloadRootPath,
                cancellationToken);
            return;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Automation: post-download enrichment skipped (no enrichment tags configured)."));
    }

    private async Task RunPostDownloadEnrichmentAsync(
        string automationConfigJson,
        TaggingProfile? automationProfile,
        string downloadRootPath,
        CancellationToken cancellationToken)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: post-download enrichment starting for {downloadRootPath}."));

        AutoTagJob? enrichmentJob = null;
        try
        {
            _taggingInProgress = true;
            var enrichmentConfig = ClearEnhancementTags(automationConfigJson);
            enrichmentJob = await _autoTagService.StartJob(
                downloadRootPath,
                enrichmentConfig,
                AutoTagLiterals.AutomationTrigger,
                automationProfile?.Technical,
                automationProfile?.Id,
                automationProfile?.Name,
                AutoTagLiterals.RunIntentDownloadEnrichment);
            await WaitForJobCompletionAsync(enrichmentJob, cancellationToken);
        }
        finally
        {
            _taggingInProgress = false;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: enrichment finished (status={enrichmentJob?.Status ?? "skipped"})."));
    }

    private async Task<bool> RunRecentDownloadEnhancementAsync(
        PipelineRunContext context,
        CancellationToken cancellationToken)
    {
        if (context.PendingQueueUuids.Count == 0)
        {
            return false;
        }

        if (ShouldDeferEnhancementForDownloadStagingAudio(cancellationToken))
        {
            _pipelineRequested = true;
            _queueIdleSince = null;
            return true;
        }

        var movedFilesByDestination = await GetRecentMovedAudioFilesByDestinationAsync(
            context.PendingQueueUuids,
            cancellationToken);
        if (movedFilesByDestination.Count == 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: recent-download enhancement skipped (no moved audio files found in library destinations)."));
            return false;
        }

        var profileContext = await BuildAutomationProfileContextAsync(cancellationToken);
        foreach (var destination in movedFilesByDestination.OrderBy(entry => entry.Key))
        {
            var pausePipeline = await ProcessRecentDownloadEnhancementDestinationAsync(
                destination,
                profileContext,
                cancellationToken);
            if (pausePipeline)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ProcessRecentDownloadEnhancementDestinationAsync(
        KeyValuePair<long, List<string>> destination,
        AutomationProfileContext profileContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await TryPauseRecentEnhancementForActiveDownloadsAsync(cancellationToken))
        {
            return true;
        }

        if (!TryResolveRecentEnhancementFolder(destination, profileContext, out var folder, out var normalizedFolderRoot)
            || folder == null)
        {
            return false;
        }

        var scopedFiles = ResolveScopedRecentEnhancementFiles(destination.Value, normalizedFolderRoot!);
        if (scopedFiles.Count == 0)
        {
            return false;
        }

        var recentDownloadWindowHours = ResolveRecentDownloadWindowHours(profileContext.Defaults);
        if (!TryApplyRecentDownloadWindow(folder.RootPath, recentDownloadWindowHours, ref scopedFiles))
        {
            return false;
        }

        var enhancementProfile = ResolveRecentEnhancementProfile(destination.Key, folder.AutoTagProfileId, profileContext, folder.RootPath);
        if (enhancementProfile == null)
        {
            return false;
        }

        if (!TryBuildRecentEnhancementConfig(folder.RootPath, enhancementProfile, scopedFiles, out var enhancementConfig))
        {
            return false;
        }

        await TryPauseGlobalEnhancementForRecentDownloadsAsync(cancellationToken);

        var enhancementJob = await RunRecentEnhancementJobAsync(
            folder.RootPath,
            scopedFiles.Count,
            enhancementConfig,
            enhancementProfile,
            cancellationToken);
        return HandleRecentEnhancementPauseState(enhancementJob, folder.RootPath);
    }

    private async Task<bool> TryPauseRecentEnhancementForActiveDownloadsAsync(CancellationToken cancellationToken)
    {
        if (!await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            return false;
        }

        _pipelineRequested = true;
        _queueIdleSince = null;
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Automation: recent-download enhancement deferred because downloads became active."));
        return true;
    }

    private async Task<bool> TryPauseGlobalEnhancementForRecentDownloadsAsync(CancellationToken cancellationToken)
    {
        if (!_autoTagService.TryGetRunningEnhancementJobId(out var runningEnhancementJobId)
            || string.IsNullOrWhiteSpace(runningEnhancementJobId))
        {
            return false;
        }

        var runningJob = _autoTagService.GetJob(runningEnhancementJobId);
        if (!ShouldPauseForRecentDownloadEnhancement(runningJob))
        {
            return false;
        }

        await _enhancementPauseLock.WaitAsync(cancellationToken);
        try
        {
            if (!_autoTagService.TryGetRunningEnhancementJobId(out runningEnhancementJobId)
                || string.IsNullOrWhiteSpace(runningEnhancementJobId))
            {
                return false;
            }

            runningJob = _autoTagService.GetJob(runningEnhancementJobId);
            if (!ShouldPauseForRecentDownloadEnhancement(runningJob))
            {
                return false;
            }

            _enhancementPauseRequested = true;
            _enhancementResumeAwaitingPipelineCompletion = true;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: pausing global enhancement to prioritize recent-download enhancement."));

            var stopped = await _autoTagService.StopJobAsync(runningEnhancementJobId);
            if (!stopped)
            {
                return false;
            }

            await QueueResumeFoldersForPausedEnhancementJobAsync(runningEnhancementJobId, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Automation paused enhancement job {JobId} to prioritize recent-download enhancement.",
                    runningEnhancementJobId);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to pause global enhancement for recent-download enhancement.");
            return false;
        }
        finally
        {
            _enhancementPauseLock.Release();
        }
    }

    private static bool ShouldPauseForRecentDownloadEnhancement(AutoTagJob? job)
    {
        if (job == null
            || !string.Equals(job.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.RunIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private bool TryResolveRecentEnhancementFolder(
        KeyValuePair<long, List<string>> destination,
        AutomationProfileContext profileContext,
        out FolderDto? folder,
        out string? normalizedFolderRoot)
    {
        folder = null;
        normalizedFolderRoot = null;
        if (!profileContext.FoldersById.TryGetValue(destination.Key, out folder)
            || !IsEnhancementEligibleFolder(folder))
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                WarningLogLevel,
                $"Automation: recent-download enhancement skipped for destination folder {destination.Key} (folder missing, disabled, or not enhancement-eligible)."));
            return false;
        }

        normalizedFolderRoot = NormalizePathScope(folder.RootPath);
        if (!string.IsNullOrWhiteSpace(normalizedFolderRoot))
        {
            return true;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            WarningLogLevel,
            $"Automation: recent-download enhancement skipped for destination folder {destination.Key} (invalid folder root path)."));
        return false;
    }

    private static List<string> ResolveScopedRecentEnhancementFiles(
        IReadOnlyCollection<string> candidateFiles,
        string normalizedFolderRoot)
    {
        return candidateFiles
            .Select(NormalizePathScope)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => IsPathWithinScope(path, normalizedFolderRoot))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryApplyRecentDownloadWindow(
        string folderRootPath,
        int recentDownloadWindowHours,
        ref List<string> scopedFiles)
    {
        if (recentDownloadWindowHours <= 0)
        {
            return true;
        }

        var recentFiles = FilterRecentFiles(scopedFiles, recentDownloadWindowHours);
        if (recentFiles.Count > 0)
        {
            scopedFiles = recentFiles;
            return true;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: recent-download enhancement skipped for {folderRootPath} (no files within last {recentDownloadWindowHours} hour(s))."));
        return false;
    }

    private TaggingProfile? ResolveRecentEnhancementProfile(
        long destinationFolderId,
        string? folderProfileReference,
        AutomationProfileContext profileContext,
        string folderRootPath)
    {
        var folderId = destinationFolderId.ToString(CultureInfo.InvariantCulture);
        var enhancementProfile = ResolveAutomationProfileForFolder(profileContext, folderId, folderProfileReference);
        if (enhancementProfile != null)
        {
            return enhancementProfile;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            WarningLogLevel,
            $"Automation: recent-download enhancement skipped for {folderRootPath} (folder has no valid current AutoTag profile)."));
        return null;
    }

    private bool TryBuildRecentEnhancementConfig(
        string folderRootPath,
        TaggingProfile enhancementProfile,
        List<string> scopedFiles,
        out string enhancementConfig)
    {
        enhancementConfig = string.Empty;
        var profileConfigJson = _configBuilder.BuildConfigJson(enhancementProfile);
        if (string.IsNullOrWhiteSpace(profileConfigJson))
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                WarningLogLevel,
                $"Automation: recent-download enhancement skipped for {folderRootPath} (folder profile config could not be built)."));
            return false;
        }

        enhancementConfig = ClearEnrichmentTags(profileConfigJson);
        enhancementConfig = ApplyEnhancementTargetFiles(enhancementConfig, scopedFiles);
        if (GetAutoTagStages(enhancementConfig).HasEnhancement)
        {
            return true;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: recent-download enhancement skipped for {folderRootPath} (profile has no enhancement tags)."));
        return false;
    }

    private async Task<AutoTagJob?> RunRecentEnhancementJobAsync(
        string folderRootPath,
        int scopedFileCount,
        string enhancementConfig,
        TaggingProfile enhancementProfile,
        CancellationToken cancellationToken)
    {
        AutoTagJob? enhancementJob = null;
        try
        {
            _taggingInProgress = true;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: recent-download enhancement starting for {folderRootPath} ({scopedFileCount} file(s))."));
            enhancementJob = await _autoTagService.StartJob(
                folderRootPath,
                enhancementConfig,
                AutoTagLiterals.AutomationTrigger,
                enhancementProfile.Technical,
                enhancementProfile.Id,
                enhancementProfile.Name,
                AutoTagLiterals.RunIntentEnhancementRecentDownloads);
            MarkEnhancementStageStarted(enhancementJob);
            await WaitForJobCompletionAsync(enhancementJob, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Automation recent-download enhancement failed for folder {RootPath}.", folderRootPath);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Automation: recent-download enhancement failed for {folderRootPath} ({ex.Message})."));
        }
        finally
        {
            MarkEnhancementStageFinished();
            _taggingInProgress = false;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: recent-download enhancement finished for {folderRootPath} (status={enhancementJob?.Status ?? "skipped"})."));
        return enhancementJob;
    }

    private bool HandleRecentEnhancementPauseState(AutoTagJob? enhancementJob, string folderRootPath)
    {
        if (enhancementJob != null
            && string.Equals(enhancementJob.Status, "canceled", StringComparison.OrdinalIgnoreCase)
            && _enhancementPauseRequested)
        {
            _pipelineRequested = true;
            _queueIdleSince = null;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: recent-download enhancement paused for incoming downloads ({folderRootPath})."));
            return true;
        }

        if (enhancementJob != null
            && string.Equals(enhancementJob.Status, "blocked", StringComparison.OrdinalIgnoreCase)
            && enhancementJob.Error?.Contains("Downloads active", StringComparison.OrdinalIgnoreCase) == true)
        {
            _pipelineRequested = true;
            _queueIdleSince = null;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: recent-download enhancement deferred because downloads became active."));
            return true;
        }

        return false;
    }

    private static int ResolveRecentDownloadWindowHours(AutoTagDefaultsDto defaults)
    {
        var resolved = defaults.RecentDownloadWindowHours ?? AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
        return resolved < 0 ? AutoTagDefaultsDto.DefaultRecentDownloadWindowHours : resolved;
    }

    private static List<string> FilterRecentFiles(List<string> files, int recentDownloadWindowHours)
    {
        if (recentDownloadWindowHours <= 0 || files.Count == 0)
        {
            return files;
        }

        var cutoffUtc = DateTimeOffset.UtcNow.AddHours(-recentDownloadWindowHours).UtcDateTime;
        var recentFiles = new List<string>(files.Count);
        foreach (var file in files)
        {
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                if (lastWriteUtc >= cutoffUtc)
                {
                    recentFiles.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable files.
            }
        }

        return recentFiles;
    }

    private async Task<Dictionary<long, List<string>>> GetRecentMovedAudioFilesByDestinationAsync(
        IReadOnlyCollection<string> queueUuids,
        CancellationToken cancellationToken)
    {
        if (queueUuids.Count == 0)
        {
            return new Dictionary<long, List<string>>();
        }

        var queueUuidSet = queueUuids
            .Where(queueUuid => !string.IsNullOrWhiteSpace(queueUuid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (queueUuidSet.Count == 0)
        {
            return new Dictionary<long, List<string>>();
        }

        var items = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var grouped = new Dictionary<long, HashSet<string>>();

        foreach (var item in items)
        {
            if (!TryResolveRecentMovedItem(
                    item.DestinationFolderId,
                    item.Status,
                    item.QueueUuid,
                    item.PayloadJson,
                    queueUuidSet,
                    out var destinationFolderId,
                    out var candidatePaths))
            {
                continue;
            }

            var files = GetOrCreateDestinationGroup(grouped, destinationFolderId);
            AddEligibleRecentMovedFiles(candidatePaths, files, cancellationToken);
        }

        return grouped.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool TryResolveRecentMovedItem(
        long? sourceDestinationFolderId,
        string? status,
        string? queueUuid,
        string? payloadJson,
        HashSet<string> queueUuidSet,
        out long destinationFolderId,
        out HashSet<string> candidatePaths)
    {
        destinationFolderId = 0;
        candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!sourceDestinationFolderId.HasValue
            || !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(queueUuid)
            || !queueUuidSet.Contains(queueUuid))
        {
            return false;
        }

        CollectPayloadFinalDestinationPaths(payloadJson, candidatePaths);
        if (candidatePaths.Count == 0)
        {
            return false;
        }

        destinationFolderId = sourceDestinationFolderId.Value;
        return true;
    }

    private static HashSet<string> GetOrCreateDestinationGroup(
        Dictionary<long, HashSet<string>> grouped,
        long destinationFolderId)
    {
        if (!grouped.TryGetValue(destinationFolderId, out var files))
        {
            files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            grouped[destinationFolderId] = files;
        }

        return files;
    }

    private static void AddEligibleRecentMovedFiles(
        HashSet<string> candidatePaths,
        HashSet<string> files,
        CancellationToken cancellationToken)
    {
        foreach (var candidatePath in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryResolveEligibleRecentMovedFile(candidatePath, out var normalizedPath))
            {
                files.Add(normalizedPath);
            }
        }
    }

    private static bool TryResolveEligibleRecentMovedFile(string candidatePath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        var ioPath = DownloadPathResolver.ResolveIoPath(candidatePath);
        if (string.IsNullOrWhiteSpace(ioPath))
        {
            return false;
        }

        normalizedPath = NormalizePathScope(ioPath);
        if (string.IsNullOrWhiteSpace(normalizedPath) || !File.Exists(normalizedPath))
        {
            return false;
        }

        var extension = Path.GetExtension(normalizedPath);
        return !string.IsNullOrWhiteSpace(extension) && StagingAudioExtensions.Contains(extension);
    }

    private async Task<bool> EnsurePipelineStillIdleAsync(CancellationToken cancellationToken)
    {
        if (!await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            return true;
        }

        _logger.LogInformation("Automation halted: downloads started during AutoTag.");
        _pipelineRequested = true;
        return false;
    }

    private bool ShouldDeferEnhancementForDownloadStagingAudio(CancellationToken cancellationToken)
    {
        if (!TryResolveDownloadEnrichmentRoot(out var downloadRootPath, out var error))
        {
            LogStagingEnhancementGate($"download staging root unavailable ({error})");
            return true;
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };

            foreach (var filePath in Directory.EnumerateFiles(downloadRootPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension) || !StagingAudioExtensions.Contains(extension))
                {
                    continue;
                }

                LogStagingEnhancementGate($"audio file still present in download staging ({filePath})");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStagingEnhancementGate($"download staging scan failed ({ex.Message})");
            return true;
        }

        _lastStagingGateLogAt = null;
        _lastStagingGateLogReason = null;
        return false;
    }

    private void LogStagingEnhancementGate(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (string.Equals(reason, _lastStagingGateLogReason, StringComparison.Ordinal)
            && _lastStagingGateLogAt.HasValue
            && now - _lastStagingGateLogAt.Value < StagingGateLogThrottle)
        {
            return;
        }

        _lastStagingGateLogAt = now;
        _lastStagingGateLogReason = reason;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Automation: enhancement deferred by staging gate ({Reason}).", reason);
        }
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            now,
            "info",
            $"Automation: enhancement deferred by staging gate ({reason})."));
    }

    private bool TryResolveDownloadEnrichmentRoot(out string downloadRootPath, out string error)
    {
        return ConfiguredDownloadRootResolver.TryResolve(
            _settingsService,
            "download location",
            "download location is not configured.",
            out downloadRootPath,
            out error);
    }

    private async Task RunPostAutoTagStagesAsync(CancellationToken cancellationToken)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Automation: library scan starting after AutoTag."));

        await _scanRunner.RunAsync(
            refreshImages: false,
            reset: false,
            folderId: null,
            skipSpotifyFetch: false,
            cacheSpotifyImages: false,
            cancellationToken: cancellationToken);

        await TriggerPlexScanAsync(cancellationToken);

        var vibeSettings = await _vibeSettingsStore.LoadAsync();
        if (vibeSettings.Enabled)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: vibe analysis starting after library scan."));

            await _analysisService.AnalyzeNowAsync(Math.Clamp(vibeSettings.BatchSize, 10, 500), cancellationToken);
        }
        else
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: vibe analysis skipped (disabled)."));
        }
    }

    private async Task<bool> HasPendingPostDownloadEnrichmentAsync(CancellationToken cancellationToken)
    {
        var pendingItems = await GetPendingPostDownloadItemsAsync(cancellationToken);
        return pendingItems.Count > 0;
    }

    private async Task RunScheduledEnhancementIfDueAsync(CancellationToken cancellationToken)
    {
        if (await HasPendingPostDownloadEnrichmentAsync(cancellationToken))
        {
            _pipelineRequested = true;
            return;
        }

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            return;
        }

        if (ShouldDeferEnhancementForDownloadStagingAudio(cancellationToken))
        {
            return;
        }

        if (await ShouldDeferEnhancementResumeForDownloadPipelineAsync(cancellationToken))
        {
            return;
        }

        var pausedWhileResuming = await ResumeInterruptedEnhancementAsync(cancellationToken);
        if (pausedWhileResuming || _pipelineRequested)
        {
            return;
        }

        _ = await RunEnhancementStageAsync(
            forceRunEvenIfNotDue: false,
            sourceLabel: "schedule",
            quietWhenNoDue: true,
            cancellationToken: cancellationToken);
    }

    private async Task<bool> RunEnhancementStageAsync(
        bool forceRunEvenIfNotDue,
        string sourceLabel,
        bool quietWhenNoDue = false,
        IReadOnlyCollection<string>? restrictedFolderIds = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await BuildEnhancementTargetPlanAsync(
            forceRunEvenIfNotDue,
            quietWhenNoDue,
            cancellationToken,
            restrictedFolderIds);
        if (plan is null)
        {
            return false;
        }

        var profileContext = await BuildAutomationProfileContextAsync(cancellationToken);
        var executionResult = await ExecuteEnhancementTargetsAsync(
            plan.DueTargets,
            profileContext,
            sourceLabel,
            cancellationToken);

        if (executionResult.AbortedForDownload)
        {
            return false;
        }

        if (executionResult.AttemptedTargets.Count > 0)
        {
            await UpdateEnhancementScheduleStateAsync(executionResult.AttemptedTargets, DateTimeOffset.UtcNow);
        }

        return executionResult.PausedForDownload;
    }

    private async Task<EnhancementTargetPlan?> BuildEnhancementTargetPlanAsync(
        bool forceRunEvenIfNotDue,
        bool quietWhenNoDue,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? restrictedFolderIds = null)
    {
        var now = DateTimeOffset.UtcNow;
        var targets = await ResolveEnhancementTargetsAsync(now, cancellationToken);
        if (restrictedFolderIds != null && restrictedFolderIds.Count > 0)
        {
            var allowedFolderIds = restrictedFolderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            targets = targets
                .Where(target => allowedFolderIds.Contains(target.FolderId))
                .ToList();
        }

        var dueTargets = targets.Where(target => target.IsDue).ToList();
        var skippedBySchedule = targets.Where(target => !target.IsDue).ToList();

        if (forceRunEvenIfNotDue && targets.Count > 0 && dueTargets.Count == 0)
        {
            dueTargets = targets.ToList();
            skippedBySchedule = new List<EnhancementTarget>();
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: forcing enhancement resume for interrupted run (bypassing schedule delay)."));
        }

        if (!quietWhenNoDue)
        {
            LogSkippedEnhancementTargets(skippedBySchedule, now);
        }

        if (targets.Count == 0)
        {
            if (!quietWhenNoDue)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    "Automation: enhancement skipped (no AutoTag-enabled folders with schedules)."));
            }

            return null;
        }

        if (dueTargets.Count == 0)
        {
            if (!quietWhenNoDue)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    "Automation: enhancement skipped (no folders due by schedule)."));
            }

            return null;
        }

        return new EnhancementTargetPlan(targets, dueTargets);
    }

    private void LogSkippedEnhancementTargets(IEnumerable<EnhancementTarget> skippedTargets, DateTimeOffset now)
    {
        foreach (var skipped in skippedTargets)
        {
            var wait = skipped.ScheduleInterval.HasValue && skipped.LastRunAtUtc.HasValue
                ? skipped.ScheduleInterval.Value - (now - skipped.LastRunAtUtc.Value)
                : TimeSpan.Zero;
            var waitSuffix = wait > TimeSpan.Zero
                ? $" next due in {(int)Math.Ceiling(wait.TotalDays)} day(s)"
                : string.Empty;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: enhancement skipped for {skipped.RootPath} (schedule not due).{waitSuffix}"));
        }
    }

    private async Task<EnhancementExecutionResult> ExecuteEnhancementTargetsAsync(
        IEnumerable<EnhancementTarget> dueTargets,
        AutomationProfileContext profileContext,
        string sourceLabel,
        CancellationToken cancellationToken)
    {
        var attemptedTargets = new List<EnhancementTarget>();
        foreach (var target in dueTargets)
        {
            if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
            {
                var remainingTargets = dueTargets
                    .SkipWhile(candidate => !string.Equals(candidate.FolderId, target.FolderId, StringComparison.OrdinalIgnoreCase))
                    .Select(candidate => candidate.FolderId)
                    .ToList();
                QueueEnhancementResumeFolders(remainingTargets);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Automation halted: downloads started before enhancement target {RootPath}.", target.RootPath);
                }
                _pipelineRequested = true;
                _queueIdleSince = null;
                return new EnhancementExecutionResult(attemptedTargets, false, true);
            }

            var runResult = await RunEnhancementTargetAsync(
                target,
                profileContext,
                sourceLabel,
                cancellationToken);
            if (runResult.Attempted)
            {
                attemptedTargets.Add(target);
            }

            if (runResult.PausedForDownload)
            {
                return new EnhancementExecutionResult(attemptedTargets, true, false);
            }
        }

        return new EnhancementExecutionResult(attemptedTargets, false, false);
    }

    private async Task<EnhancementTargetRunResult> RunEnhancementTargetAsync(
        EnhancementTarget target,
        AutomationProfileContext profileContext,
        string sourceLabel,
        CancellationToken cancellationToken)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: enhancement ({sourceLabel}) starting for {target.RootPath}."));

        AutoTagJob? enhancementJob = null;
        try
        {
            _taggingInProgress = true;
            var enhancementProfile = ResolveAutomationProfileForFolder(
                profileContext,
                target.FolderId,
                target.FolderProfileReference);
            if (enhancementProfile == null)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    WarningLogLevel,
                    $"Automation: enhancement skipped for {target.RootPath} (folder has no valid current AutoTag profile)."));
                return new EnhancementTargetRunResult(false, false);
            }

            var profileConfigJson = _configBuilder.BuildConfigJson(enhancementProfile);
            if (string.IsNullOrWhiteSpace(profileConfigJson))
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    WarningLogLevel,
                    $"Automation: enhancement skipped for {target.RootPath} (folder profile config could not be built)."));
                return new EnhancementTargetRunResult(false, false);
            }

            var enhancementConfig = ClearEnrichmentTags(profileConfigJson);
            if (!GetAutoTagStages(enhancementConfig).HasEnhancement)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    $"Automation: enhancement skipped for {target.RootPath} (profile has no enhancement tags)."));
                return new EnhancementTargetRunResult(true, false);
            }

            enhancementJob = await _autoTagService.StartJob(
                target.RootPath,
                enhancementConfig,
                AutoTagLiterals.ScheduleTrigger,
                enhancementProfile?.Technical,
                enhancementProfile?.Id,
                enhancementProfile?.Name,
                AutoTagLiterals.RunIntentEnhancementOnly);
            MarkEnhancementStageStarted(enhancementJob);
            await WaitForJobCompletionAsync(enhancementJob, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Automation enhancement failed for target {RootPath}.", target.RootPath);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Automation: enhancement failed for {target.RootPath} ({ex.Message})."));
            return new EnhancementTargetRunResult(true, false);
        }
        finally
        {
            MarkEnhancementStageFinished();
            _taggingInProgress = false;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: enhancement ({sourceLabel}) finished for {target.RootPath} (status={enhancementJob?.Status ?? "skipped"})."));

        if (enhancementJob != null
            && string.Equals(enhancementJob.Status, "canceled", StringComparison.OrdinalIgnoreCase)
            && _enhancementPauseRequested)
        {
            QueueEnhancementResumeFolder(target.FolderId);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: enhancement paused for incoming downloads ({target.RootPath})."));
            return new EnhancementTargetRunResult(false, true);
        }

        var attempted = enhancementJob != null
            && !string.Equals(enhancementJob.Status, "canceled", StringComparison.OrdinalIgnoreCase);
        return new EnhancementTargetRunResult(attempted, false);
    }

    private void MarkEnhancementStageStarted(AutoTagJob? job)
    {
        _enhancementPauseRequested = false;
        _enhancementStageRunning = true;
        _activeEnhancementJobId = job?.Id;
    }

    private void MarkEnhancementStageFinished()
    {
        _enhancementStageRunning = false;
        _activeEnhancementJobId = null;
    }

    private async Task<bool> TryPauseEnhancementForIncomingDownloadAsync(string? fallbackEnhancementJobId, CancellationToken cancellationToken)
    {
        if (!_enhancementStageRunning && string.IsNullOrWhiteSpace(fallbackEnhancementJobId))
        {
            return false;
        }

        if (_enhancementPauseRequested)
        {
            return true;
        }

        await _enhancementPauseLock.WaitAsync(cancellationToken);
        try
        {
            if (_enhancementPauseRequested)
            {
                return true;
            }

            var jobId = _activeEnhancementJobId;
            if (string.IsNullOrWhiteSpace(jobId))
            {
                jobId = fallbackEnhancementJobId;
            }
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return false;
            }

            _enhancementPauseRequested = true;
            _enhancementResumeAwaitingPipelineCompletion = true;
            _pipelineRequested = true;
            _queueIdleSince = null;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: enhancement pause requested for incoming download."));

            var stopped = await _autoTagService.StopJobAsync(jobId);
            if (stopped)
            {
                await QueueResumeFoldersForPausedEnhancementJobAsync(jobId, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Automation enhancement job {JobId} pause requested for incoming download.", jobId);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Automation enhancement job {JobId} could not be paused (already stopped).", jobId);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to request enhancement pause for incoming download.");
            return false;
        }
        finally
        {
            _enhancementPauseLock.Release();
        }
    }

    private async Task<bool> TryPauseEnhancementForPendingPipelineAsync(CancellationToken cancellationToken)
    {
        string? runningEnhancementJobId = null;
        if (!_enhancementStageRunning && !_autoTagService.TryGetRunningEnhancementJobId(out runningEnhancementJobId))
        {
            return false;
        }

        if (_enhancementPauseRequested)
        {
            return true;
        }

        await _enhancementPauseLock.WaitAsync(cancellationToken);
        try
        {
            if (_enhancementPauseRequested)
            {
                return true;
            }

            var jobId = _activeEnhancementJobId;
            if (string.IsNullOrWhiteSpace(jobId))
            {
                jobId = runningEnhancementJobId;
            }

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return false;
            }

            _enhancementPauseRequested = true;
            _enhancementResumeAwaitingPipelineCompletion = true;
            _pipelineRequested = true;
            _queueIdleSince = null;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: enhancement pause requested to prioritize pending post-download enrichment."));

            var stopped = await _autoTagService.StopJobAsync(jobId);
            if (stopped)
            {
                await QueueResumeFoldersForPausedEnhancementJobAsync(jobId, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Automation enhancement job {JobId} paused to prioritize pending post-download enrichment.",
                        jobId);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Automation enhancement job {JobId} could not be paused while prioritizing pending post-download enrichment.",
                        jobId);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to pause enhancement for pending post-download enrichment.");
            return false;
        }
        finally
        {
            _enhancementPauseLock.Release();
        }
    }

    private bool HasPendingEnhancementResumeFolders()
    {
        lock (_enhancementResumeLock)
        {
            return _pendingEnhancementResumeFolderIds.Count > 0;
        }
    }

    private async Task<bool> ShouldDeferEnhancementResumeForDownloadPipelineAsync(CancellationToken cancellationToken)
    {
        if (!_enhancementResumeAwaitingPipelineCompletion)
        {
            return false;
        }

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            return true;
        }

        if (_autoTagService.HasRunningJobs())
        {
            return true;
        }

        if (await HasPendingPostDownloadEnrichmentAsync(cancellationToken))
        {
            _pipelineRequested = true;
            return true;
        }

        if (_pipelineRequested)
        {
            return true;
        }

        _enhancementResumeAwaitingPipelineCompletion = false;
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Automation: download pipeline settled; enhancement resume unlocked."));
        return false;
    }

    private async Task QueueResumeFoldersForPausedEnhancementJobAsync(string jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        var job = _autoTagService.GetJob(jobId);
        if (job == null)
        {
            return;
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.RunIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(job.RootPath))
        {
            return;
        }

        var normalizedJobRoot = NormalizePathScope(job.RootPath);
        if (string.IsNullOrWhiteSpace(normalizedJobRoot))
        {
            return;
        }

        var profileContext = await BuildAutomationProfileContextAsync(cancellationToken);
        var resumeFolderIds = profileContext.FoldersById.Values
            .Where(IsEnhancementEligibleFolder)
            .Where(folder => PathScopesOverlap(folder.RootPath, normalizedJobRoot))
            .Select(folder => folder.Id.ToString(CultureInfo.InvariantCulture))
            .ToList();

        if (resumeFolderIds.Count == 0)
        {
            return;
        }

        QueueEnhancementResumeFolders(resumeFolderIds);
    }

    private static bool PathScopesOverlap(string candidateScope, string comparisonScope)
    {
        if (string.IsNullOrWhiteSpace(candidateScope) || string.IsNullOrWhiteSpace(comparisonScope))
        {
            return false;
        }

        var normalizedCandidate = NormalizePathScope(candidateScope);
        var normalizedComparison = NormalizePathScope(comparisonScope);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedComparison))
        {
            return false;
        }

        return IsPathWithinScope(normalizedCandidate, normalizedComparison)
               || IsPathWithinScope(normalizedComparison, normalizedCandidate);
    }

    private static bool IsPathWithinScope(string candidatePath, string scopePath)
    {
        if (string.Equals(candidatePath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var scopeWithSeparator = EnsureTrailingDirectorySeparator(scopePath);
        return candidatePath.StartsWith(scopeWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathScope(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.EndsWith(Path.DirectorySeparatorChar)
            || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private async Task<bool> TryPauseEnrichmentForIncomingDownloadAsync(string? enrichmentJobId)
    {
        if (string.IsNullOrWhiteSpace(enrichmentJobId))
        {
            return false;
        }

        try
        {
            _pipelineRequested = true;
            _queueIdleSince = null;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: enrichment pause requested for incoming download."));

            var stopped = await _autoTagService.StopJobAsync(enrichmentJobId);
            if (stopped)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Automation enrichment job {JobId} pause requested for incoming download.", enrichmentJobId);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Automation enrichment job {JobId} could not be paused (already stopped).", enrichmentJobId);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to request enrichment pause for incoming download.");
            return false;
        }
    }

    private async Task<bool> TryPauseAnyRunningAutoTagForIncomingDownloadAsync(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        try
        {
            _pipelineRequested = true;
            _queueIdleSince = null;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: active AutoTag job pause requested for incoming download."));

            var stopped = await _autoTagService.StopJobAsync(jobId);
            if (stopped)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("AutoTag job {JobId} pause requested for incoming download.", jobId);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("AutoTag job {JobId} could not be paused (already stopped).", jobId);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to request AutoTag pause for incoming download.");
            return false;
        }
    }

    private async Task TriggerPlexScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: Plex scan starting after library scan."));

            var authState = await _platformAuthService.LoadAsync();
            var plex = authState.Plex;
            if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    "Automation: Plex scan skipped (Plex is not configured)."));
                return;
            }

            var sections = await _plexApiClient.GetLibrarySectionsAsync(plex.Url, plex.Token, cancellationToken);
            var musicSections = sections
                .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
                .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (musicSections.Count == 0)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    "Automation: Plex scan skipped (no music libraries found)."));
                return;
            }

            var refreshed = 0;
            foreach (var section in musicSections)
            {
                refreshed += await _plexApiClient.RefreshLibraryAsync(plex.Url, plex.Token, section.Key, cancellationToken)
                    ? 1
                    : 0;
            }

            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: Plex scan requested for {musicSections.Count} music libraries (refreshed={refreshed})."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Automation Plex scan failed.");
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Automation: Plex scan failed ({ex.Message})."));
        }
    }

    private string? GetAutoTagConfigJson(TaggingProfile? profile)
        => profile is null
            ? null
            : _configBuilder.BuildConfigJson(profile);

    private static AutoTagStages GetAutoTagStages(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new AutoTagStages(false, false);
        }

        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root)
            {
                return new AutoTagStages(false, false);
            }

            var enrichmentCount = ReadArrayCount(root, "tags");
            var enhancementCount = ReadArrayCount(root, "gapFillTags");
            return new AutoTagStages(enrichmentCount > 0, enhancementCount > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoTagStages(false, false);
        }
    }

    private static string ClearEnhancementTags(string configJson)
    {
        return ClearStageTags(configJson, clearEnrichment: false, clearEnhancement: true);
    }

    private static string ClearEnrichmentTags(string configJson)
    {
        return ClearStageTags(configJson, clearEnrichment: true, clearEnhancement: false);
    }

    private static string ApplyEnhancementTargetFiles(string configJson, List<string> targetFiles)
    {
        if (string.IsNullOrWhiteSpace(configJson) || targetFiles.Count == 0)
        {
            return configJson;
        }

        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root)
            {
                return configJson;
            }

            var files = targetFiles
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                return configJson;
            }

            var targetFilesNode = new JsonArray();
            foreach (var path in files)
            {
                targetFilesNode.Add(path);
            }

            root[AutoTagLiterals.TargetFilesKey] = targetFilesNode;
            return root.ToJsonString(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static string ClearStageTags(string configJson, bool clearEnrichment, bool clearEnhancement)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root)
            {
                return configJson;
            }

            if (clearEnrichment)
            {
                root["tags"] = new JsonArray();
            }

            if (clearEnhancement)
            {
                root["gapFillTags"] = new JsonArray();
            }

            return root.ToJsonString(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static int ReadArrayCount(JsonObject root, string key)
    {
        return root[key] is JsonArray array ? array.Count : 0;
    }

    private async Task<AutomationProfileContext> BuildAutomationProfileContextAsync(CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        return new AutomationProfileContext(state.Profiles, state.Defaults, state.DefaultProfile, state.FoldersById);
    }

    private static TaggingProfile? ResolveAutomationProfileForFolder(
        AutomationProfileContext context,
        string folderId,
        string? folderProfileReference)
    {
        if (long.TryParse(folderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFolderId)
            && context.FoldersById.TryGetValue(parsedFolderId, out var folder))
        {
            var normalizedFolderProfile = ResolveProfileReference(context.Profiles, folder.AutoTagProfileId);
            if (normalizedFolderProfile != null)
            {
                return normalizedFolderProfile;
            }
        }

        if (!string.IsNullOrWhiteSpace(folderProfileReference))
        {
            var assignedProfile = ResolveProfileReference(context.Profiles, folderProfileReference);
            if (assignedProfile != null)
            {
                return assignedProfile;
            }
        }

        return null;
    }

    private async Task<List<DownloadQueueItem>> GetPendingPostDownloadItemsAsync(CancellationToken cancellationToken)
    {
        var queueItems = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var completedItems = queueItems
            .Where(item =>
                item.DestinationFolderId.HasValue
                && string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .ToList();

        var freshItems = completedItems
            .Where(item => item.UpdatedAt > _lastPipelineCompletedAt)
            .ToList();
        if (freshItems.Count > 0)
        {
            return freshItems;
        }

        if (!TryResolveDownloadEnrichmentRoot(out var downloadRootPath, out _))
        {
            return freshItems;
        }

        return completedItems
            .Where(item => PayloadHasExistingSourceUnderRoot(item.PayloadJson, downloadRootPath))
            .ToList();
    }

    private static bool PayloadHasExistingSourceUnderRoot(string? payloadJson, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectPayloadSourcePaths(root, candidatePaths);

            foreach (var candidatePath in candidatePaths)
            {
                if (!IsPathUnderRoot(rootPath, candidatePath))
                {
                    continue;
                }

                var ioPath = DownloadPathResolver.ResolveIoPath(candidatePath);
                if (string.IsNullOrWhiteSpace(ioPath))
                {
                    continue;
                }

                if (File.Exists(ioPath))
                {
                    return true;
                }

                if (Directory.Exists(ioPath)
                    && Directory.EnumerateFiles(ioPath, "*", SearchOption.AllDirectories).Any())
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    private static void CollectPayloadSourcePaths(JsonElement root, HashSet<string> paths)
    {
        if (TryReadStringPropertyIgnoreCase(root, "filePath", out var filePath))
        {
            paths.Add(filePath);
        }

        if (TryReadStringPropertyIgnoreCase(root, "albumPath", out var albumPath))
        {
            paths.Add(albumPath);
        }

        if (TryReadStringPropertyIgnoreCase(root, "artistPath", out var artistPath))
        {
            paths.Add(artistPath);
        }

        if (TryReadStringPropertyIgnoreCase(root, "extrasPath", out var extrasPath))
        {
            paths.Add(extrasPath);
        }

        if (!TryGetPropertyIgnoreCase(root, "files", out var filesElement)
            || filesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (fileElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryReadStringPropertyIgnoreCase(fileElement, "path", out var path))
            {
                paths.Add(path);
            }

            if (TryReadStringPropertyIgnoreCase(fileElement, "albumPath", out var nestedAlbumPath))
            {
                paths.Add(nestedAlbumPath);
            }

            if (TryReadStringPropertyIgnoreCase(fileElement, "artistPath", out var nestedArtistPath))
            {
                paths.Add(nestedArtistPath);
            }
        }
    }

    private static void CollectPayloadFinalDestinationPaths(string? payloadJson, HashSet<string> paths)
    {
        if (paths == null || string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (TryGetPropertyIgnoreCase(root, "finalDestinations", out var finalDestinationsElement)
                && finalDestinationsElement.ValueKind == JsonValueKind.Object)
            {
                var finalDestinationPaths = finalDestinationsElement.EnumerateObject()
                    .Select(pathEntry => pathEntry.Value.ValueKind == JsonValueKind.String
                        ? pathEntry.Value.GetString()
                        : null)
                    .Where(static path => !string.IsNullOrWhiteSpace(path));
                foreach (var finalPath in finalDestinationPaths)
                {
                    paths.Add(finalPath!);
                }
            }

            // Keep legacy fallback paths for payloads that do not yet persist finalDestinations.
            CollectPayloadSourcePaths(root, paths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return;
        }
    }

    private static bool TryReadStringPropertyIgnoreCase(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = property.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        return false;
    }

    private static bool IsPathUnderRoot(string rootPath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        var candidateIo = DownloadPathResolver.ResolveIoPath(candidatePath);
        if (string.IsNullOrWhiteSpace(rootIo) || string.IsNullOrWhiteSpace(candidateIo))
        {
            return false;
        }

        try
        {
            if (!DownloadPathResolver.IsSmbPath(rootIo))
            {
                rootIo = Path.GetFullPath(rootIo);
            }

            if (!DownloadPathResolver.IsSmbPath(candidateIo))
            {
                candidateIo = Path.GetFullPath(candidateIo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        var normalizedRoot = rootIo.Replace('\\', '/').TrimEnd('/');
        var normalizedCandidate = candidateIo.Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private TaggingProfile? ResolveAutomationProfileForPendingDownloads(
        AutomationProfileContext context,
        List<DownloadQueueItem> pendingItems)
    {
        if (pendingItems.Count == 0)
        {
            return null;
        }

        var distinctFolderIds = pendingItems
            .Select(item => item.DestinationFolderId!.Value)
            .Distinct()
            .ToList();
        if (distinctFolderIds.Count > 1)
        {
            _logger.LogWarning(
                "Multiple destination folders were queued for one post-download automation pass ({FolderIds}). The latest completed item will determine the AutoTag profile.",
                string.Join(", ", distinctFolderIds));
        }

        var selectedItem = pendingItems[0];
        var selectedFolderId = selectedItem.DestinationFolderId!.Value;
        var selectedFolderReference = context.FoldersById.TryGetValue(selectedFolderId, out var folder)
            ? folder.AutoTagProfileId
            : null;
        var selectedProfile = ResolveAutomationProfileForFolder(
            context,
            selectedFolderId.ToString(CultureInfo.InvariantCulture),
            selectedFolderReference);

        if (selectedProfile == null)
        {
            _logger.LogWarning(
                "No resolvable AutoTag profile was found for destination folder {FolderId}.",
                selectedFolderId);
            return null;
        }

        return selectedProfile;
    }

    private static TaggingProfile? ResolveProfileReference(IEnumerable<TaggingProfile> profiles, string? reference)
    {
        if (profiles == null || string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var value = reference.Trim();
        return profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, value, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(profile =>
                string.Equals(profile.Name, value, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<EnhancementTarget>> ResolveEnhancementTargetsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return new List<EnhancementTarget>();
        }

        var profileContext = await BuildAutomationProfileContextAsync(cancellationToken);
        var folders = profileContext.FoldersById.Values.ToList();
        Dictionary<string, string> schedules = profileContext.Defaults.LibrarySchedules
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var state = await LoadEnhancementScheduleStateAsync();
        var targets = new List<EnhancementTarget>();
        var dirtyState = false;
        var activeScheduleFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (!TryBuildEnhancementTarget(folder, schedules, state, now, out var target, out var folderStateDirty))
            {
                continue;
            }

            dirtyState |= folderStateDirty;
            targets.Add(target);
            activeScheduleFolderIds.Add(target.FolderId);
        }

        dirtyState |= RemoveInactiveScheduleEntries(state.LastRunByFolderId, activeScheduleFolderIds);
        dirtyState |= RemoveInactiveScheduleEntries(state.LastScheduleByFolderId, activeScheduleFolderIds);

        if (dirtyState)
        {
            await SaveEnhancementScheduleStateAsync(state);
        }

        return targets;
    }

    private static bool TryBuildEnhancementTarget(
        FolderDto folder,
        Dictionary<string, string> schedules,
        EnhancementScheduleState state,
        DateTimeOffset now,
        out EnhancementTarget target,
        out bool stateDirty)
    {
        target = default!;
        stateDirty = false;
        if (!IsEnhancementEligibleFolder(folder))
        {
            return false;
        }

        var key = folder.Id.ToString();
        schedules.TryGetValue(key, out var rawSchedule);
        if (!TryParseScheduleInterval(rawSchedule, out var interval))
        {
            return false;
        }

        var scheduleToken = BuildScheduleStateToken(interval);
        var hasLastRun = state.LastRunByFolderId.TryGetValue(key, out var storedLastRun);
        if (!state.LastScheduleByFolderId.TryGetValue(key, out var existingScheduleToken)
            || !string.Equals(existingScheduleToken, scheduleToken, StringComparison.OrdinalIgnoreCase))
        {
            state.LastScheduleByFolderId[key] = scheduleToken;
            stateDirty = true;
        }

        // Seed first-run schedule baseline so newly scheduled folders do not run immediately.
        if (!hasLastRun)
        {
            storedLastRun = now;
            state.LastRunByFolderId[key] = storedLastRun;
            stateDirty = true;
        }

        var lastRun = (DateTimeOffset?)storedLastRun;
        var isDue = !lastRun.HasValue || (now - lastRun.Value) >= interval;
        target = new EnhancementTarget(
            key,
            folder.RootPath,
            folder.AutoTagProfileId,
            interval,
            isDue,
            lastRun);
        return true;
    }

    private static bool IsEnhancementEligibleFolder(FolderDto folder)
    {
        return folder.Enabled
               && folder.AutoTagEnabled
               && !string.IsNullOrWhiteSpace(folder.RootPath);
    }

    private static bool RemoveInactiveScheduleEntries<TValue>(
        Dictionary<string, TValue> source,
        HashSet<string> activeScheduleFolderIds)
    {
        var staleKeys = source.Keys
            .Where(folderId => !activeScheduleFolderIds.Contains(folderId))
            .ToList();
        if (staleKeys.Count == 0)
        {
            return false;
        }

        foreach (var folderId in staleKeys)
        {
            source.Remove(folderId);
        }

        return true;
    }

    private static string BuildScheduleStateToken(TimeSpan interval)
    {
        return interval.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryParseScheduleInterval(string? rawSchedule, out TimeSpan interval)
    {
        interval = default;

        if (string.IsNullOrWhiteSpace(rawSchedule))
        {
            return false;
        }

        var normalized = rawSchedule.Trim().ToLowerInvariant();
        var match = ScheduleTokenRegex.Match(normalized);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, out var amount)
            && amount > 0)
        {
            var unit = match.Groups[2].Value;
            interval = unit switch
            {
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                "w" => TimeSpan.FromDays(amount * 7d),
                "m" => TimeSpan.FromDays(amount * 30d),
                _ => default
            };
            return interval > TimeSpan.Zero;
        }

        if (int.TryParse(normalized, out var days) && days > 0)
        {
            interval = TimeSpan.FromDays(days);
            return true;
        }

        return false;
    }

    private async Task<EnhancementScheduleState> LoadEnhancementScheduleStateAsync()
    {
        try
        {
            if (!File.Exists(_enhancementSchedulePath))
            {
                return new EnhancementScheduleState();
            }

            var json = await File.ReadAllTextAsync(_enhancementSchedulePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new EnhancementScheduleState();
            }

            var loaded = JsonSerializer.Deserialize<EnhancementScheduleState>(json, ScheduleJsonOptions);
            if (loaded == null)
            {
                return new EnhancementScheduleState();
            }

            loaded.LastRunByFolderId ??= new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            loaded.LastScheduleByFolderId ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load enhancement schedule state.");
            return new EnhancementScheduleState();
        }
    }

    private async Task UpdateEnhancementScheduleStateAsync(IEnumerable<EnhancementTarget> attemptedTargets, DateTimeOffset completedAtUtc)
    {
        var targets = attemptedTargets.ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var state = await LoadEnhancementScheduleStateAsync();
        foreach (var target in targets)
        {
            state.LastRunByFolderId[target.FolderId] = completedAtUtc;
        }

        await SaveEnhancementScheduleStateAsync(state);
    }

    private async Task SaveEnhancementScheduleStateAsync(EnhancementScheduleState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, ScheduleJsonOptions);
            await File.WriteAllTextAsync(_enhancementSchedulePath, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save enhancement schedule state.");
        }
    }

    private Task WaitForJobCompletionAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<AutoTagJob>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(AutoTagJob completed)
        {
            if (string.Equals(completed.Id, job.Id, StringComparison.OrdinalIgnoreCase))
            {
                completion.TrySetResult(completed);
            }
        }

        _autoTagService.JobCompleted += Handler;

        return WaitForCompletionAsync(completion.Task, Handler, cancellationToken);
    }

    private async Task WaitForCompletionAsync(
        Task completionTask,
        Action<AutoTagJob> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            await completionTask.WaitAsync(cancellationToken);
        }
        finally
        {
            _autoTagService.JobCompleted -= handler;
        }
    }
}
