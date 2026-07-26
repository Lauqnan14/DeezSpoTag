using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Plex;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadOrchestrationService : BackgroundService, IDownloadQueueExecutionGate
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
        string DownloadRootPath,
        IReadOnlyList<PipelineWorkGroup> Groups);
    private sealed record PipelineWorkGroup(
        long DestinationFolderId,
        TaggingProfile AutomationProfile,
        string AutomationConfigJson,
        AutoTagStages Stages,
        IReadOnlyList<DownloadQueueItem> PendingItems,
        IReadOnlyList<string> PendingQueueUuids,
        IReadOnlyList<string> SourceFilePaths,
        IReadOnlyDictionary<string, DateTimeOffset> PendingCompletionMarkers);
    private sealed record PipelineEnrichmentResult(string Status, bool SafeToContinue, bool SafeToPersist);
    private sealed record EnhancementTargetPlan(List<EnhancementTarget> Targets, List<EnhancementTarget> DueTargets);
    private sealed record EnhancementTargetRunResult(bool Attempted, bool PausedForEnrichment);
    private sealed record EnhancementPauseRequest(
        string? FallbackJobId,
        EnhancementPauseReason Reason,
        string ConfigLogMessage);
    public enum OrchestrationPhase
    {
        Idle,
        Downloading,
        RetrySweep,
        EnrichmentCountdown,
        Enriching,
        EnhancementRunning,
        EnhancementResumeCooldown
    }
    public sealed record OrchestrationStatusSnapshot(
        OrchestrationPhase Phase,
        DateTimeOffset PhaseEnteredUtc,
        DateTimeOffset? QueueIdleSinceUtc,
        DateTimeOffset? CountdownUntilUtc,
        DateTimeOffset? LastEnrichmentFinishedUtc,
        DateTimeOffset? EnhancementResumeNotBeforeUtc,
        bool PipelineRequested,
        bool RetrySweepPending,
        bool EnhancementInterruptedByEnrichment,
        int ActiveDownloadCount,
        bool TaggingInProgress);

    private enum EnhancementPauseReason
    {
        PendingPipeline
    }

    private static bool IsInterruptibleEnhancementTrigger(string? trigger)
    {
        return string.Equals(trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
               || string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAutomationInterruptibleEnhancementTrigger(string? trigger)
    {
        return string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPauseEnhancementJobForEnrichment(AutoTagJob? job, EnhancementPauseReason reason)
    {
        if (job == null
            || !string.Equals(job.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(job.RunIntent, AutoTagLiterals.RunIntentEnhancementOnly, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(job.RunIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (reason == EnhancementPauseReason.PendingPipeline
            && IsAutomationInterruptibleEnhancementTrigger(job.Trigger))
        {
            return true;
        }

        return IsInterruptibleEnhancementTrigger(job.Trigger);
    }
    private sealed record EnhancementExecutionResult(List<EnhancementTarget> AttemptedTargets, bool PausedForEnrichment);
    private sealed class EnhancementScheduleState
    {
        public Dictionary<string, DateTimeOffset> LastRunByFolderId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LastScheduleByFolderId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class ProcessedCompletionState
    {
        public Dictionary<string, DateTimeOffset> ProcessedByQueueItem { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class OrchestrationRuntimeState
    {
        public bool EnhancementResumeAwaitingPipelineCompletion { get; set; }
        public bool EnhancementInterruptedByEnrichment { get; set; }
        public DateTimeOffset? LastEnrichmentFinishedUtc { get; set; }
        public DateTimeOffset? EnhancementResumeNotBeforeUtc { get; set; }
        public List<string> PendingEnhancementResumeFolderIds { get; set; } = new();
        public List<string> PendingEnhancementResumeRootPaths { get; set; } = new();
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
    private const string ErrorLogLevel = "error";
    private const string EnrichmentStatusRunning = "running";
    private const string EnrichmentStatusCompleted = "completed";
    private const string EnrichmentStatusFailed = "failed";
    private const string EnrichmentStatusCanceled = "canceled";
    private const string EnrichmentStatusInterrupted = "interrupted";
    private const string EnrichmentStatusNotRequired = "not_required";
    private const string QueueStatusQueued = "queued";
    private const string QueueStatusInQueue = "inqueue";
    private const string QueueStatusRetrying = "retrying";
    private const string QueueStatusDownloading = "downloading";
    private const string QueueStatusComplete = "complete";
    private const string QueueStatusCancelled = "cancelled";
    private static readonly TimeSpan EnhancementResumeDelay = TimeSpan.FromMinutes(30);
    private const string FolderContentVideo = "video";
    private const string FolderContentPodcast = "podcast";
    private const string FolderContentAtmos = "atmos";
    private const string FolderContentOther = "other";
    private static readonly JsonSerializerOptions ScheduleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly DownloadQueueRepository _queueRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly AutoTagService _autoTagService;
    private readonly AutoTagDownloadMoveService _downloadMoveService;
    private readonly AutoTagConfigBuilder _configBuilder;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly KnownLibraryFileIngestionService _knownFileIngestionService;
    private readonly MediaServerLibraryRefreshService _mediaServerLibraryRefreshService;
    private readonly DownloadRetryScheduler _retryScheduler;
    private readonly TrackAnalysisBackgroundService _analysisService;
    private readonly VibeAnalysisSettingsStore _vibeSettingsStore;
    private readonly WatchlistRunSignal? _watchlistRunSignal;
    private readonly LibraryConfigStore _configStore;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly DownloadQueueWakeSignal _queueWakeSignal;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DownloadOrchestrationService> _logger;
    private readonly string _enhancementSchedulePath;
    private readonly string _processedCompletionPath;
    private readonly string _orchestrationStatePath;
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _enhancementPauseLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly TimeSpan _downloadIdleDelay = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _orchestrationRecheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleRecoveryRecheckDelay = TimeSpan.FromMinutes(15);
    private readonly object _enhancementResumeLock = new();
    private readonly HashSet<string> _pendingEnhancementResumeFolderIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingEnhancementResumeRootPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _processedCompletionByQueueItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _processedCompletionStateLock = new();
    private readonly object _phaseLock = new();
    private volatile bool _processedCompletionStateLoaded;
    private int _wakeSignalPending;

    private DateTimeOffset? _queueIdleSince;
    private DateTimeOffset _lastPipelineCompletedAt = DateTimeOffset.UtcNow;
    private bool _pipelineRequested;
    private bool _wasQueueActive;
    private bool _taggingInProgress;
    private volatile bool _postDownloadPipelineInProgress;
    private volatile bool _enhancementStageRunning;
    private volatile bool _enhancementPauseRequested;
    private volatile bool _enhancementResumeAwaitingPipelineCompletion;
    private volatile bool _enhancementInterruptedByEnrichment;
    private string? _activeEnhancementJobId;
    private DateTimeOffset? _lastStagingGateLogAt;
    private string? _lastStagingGateLogReason;
    private DateTimeOffset? _lastEnrichmentFinishedUtc;
    private DateTimeOffset? _enhancementResumeNotBeforeUtc;
    private int _lastKnownActiveDownloadCount;
    private OrchestrationPhase _phase = OrchestrationPhase.Idle;
    private DateTimeOffset _phaseEnteredUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _countdownUntilUtc;

    public DownloadOrchestrationService(
        IServiceProvider serviceProvider,
        IWebHostEnvironment env,
        ILogger<DownloadOrchestrationService> logger)
    {
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _libraryRepository = serviceProvider.GetRequiredService<LibraryRepository>();
        _autoTagService = serviceProvider.GetRequiredService<AutoTagService>();
        _downloadMoveService = serviceProvider.GetRequiredService<AutoTagDownloadMoveService>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _configBuilder = serviceProvider.GetRequiredService<AutoTagConfigBuilder>();
        _profileResolutionService = serviceProvider.GetRequiredService<AutoTagProfileResolutionService>();
        _knownFileIngestionService = serviceProvider.GetRequiredService<KnownLibraryFileIngestionService>();
        _mediaServerLibraryRefreshService = serviceProvider.GetRequiredService<MediaServerLibraryRefreshService>();
        _retryScheduler = serviceProvider.GetRequiredService<DownloadRetryScheduler>();
        _analysisService = serviceProvider.GetRequiredService<TrackAnalysisBackgroundService>();
        _vibeSettingsStore = serviceProvider.GetRequiredService<VibeAnalysisSettingsStore>();
        _watchlistRunSignal = serviceProvider.GetService<WatchlistRunSignal>();
        _configStore = serviceProvider.GetRequiredService<LibraryConfigStore>();
        _workCoordinator = serviceProvider.GetRequiredService<BackgroundWorkCoordinator>();
        _queueWakeSignal = serviceProvider.GetRequiredService<DownloadQueueWakeSignal>();
        _configuration = serviceProvider.GetRequiredService<IConfiguration>();
        _logger = logger;

        var configuredDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        var dataRoot = string.IsNullOrWhiteSpace(configuredDataDir)
            ? Path.Join(env.ContentRootPath, "Data")
            : configuredDataDir;
        var autoTagDataDir = Path.Join(dataRoot, "autotag");
        Directory.CreateDirectory(autoTagDataDir);
        _enhancementSchedulePath = Path.Join(autoTagDataDir, "enhancement-schedule-state.json");
        _processedCompletionPath = Path.Join(autoTagDataDir, "processed-completions.json");
        _orchestrationStatePath = Path.Join(autoTagDataDir, "download-orchestration-state.json");
        DownloadQueueRepository.QueueStateChanged += OnQueueStateChanged;
        _autoTagService.JobCompleted += OnAutoTagJobCompleted;
        LoadOrchestrationRuntimeState();
    }

    public bool TaggingInProgress => _taggingInProgress || _autoTagService.HasRunningJobs();

    public OrchestrationStatusSnapshot GetStatusSnapshot()
    {
        lock (_phaseLock)
        {
            return new OrchestrationStatusSnapshot(
                _phase,
                _phaseEnteredUtc,
                _queueIdleSince,
                _countdownUntilUtc,
                _lastEnrichmentFinishedUtc,
                _enhancementResumeNotBeforeUtc,
                _pipelineRequested,
                _retryScheduler.HasPendingRetries,
                _enhancementInterruptedByEnrichment,
                _lastKnownActiveDownloadCount,
                TaggingInProgress);
        }
    }

    private void SetPhase(OrchestrationPhase nextPhase, DateTimeOffset? countdownUntilUtc = null)
    {
        lock (_phaseLock)
        {
            if (_phase != nextPhase)
            {
                _phase = nextPhase;
                _phaseEnteredUtc = DateTimeOffset.UtcNow;
            }

            _countdownUntilUtc = countdownUntilUtc;
        }
    }

    private bool IsProtectedEnrichmentPipelineRunning()
        => _postDownloadPipelineInProgress
           || _taggingInProgress
           || _autoTagService.TryGetRunningEnrichmentJobId(out _);

    public override void Dispose()
    {
        SaveOrchestrationRuntimeState();
        DownloadQueueRepository.QueueStateChanged -= OnQueueStateChanged;
        _autoTagService.JobCompleted -= OnAutoTagJobCompleted;
        base.Dispose();
    }

    private void LoadOrchestrationRuntimeState()
    {
        try
        {
            if (!File.Exists(_orchestrationStatePath))
            {
                return;
            }

            var json = File.ReadAllText(_orchestrationStatePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<OrchestrationRuntimeState>(json, ScheduleJsonOptions);
            if (state == null)
            {
                return;
            }

            _enhancementResumeAwaitingPipelineCompletion = state.EnhancementResumeAwaitingPipelineCompletion;
            _enhancementInterruptedByEnrichment = state.EnhancementInterruptedByEnrichment;
            _lastEnrichmentFinishedUtc = state.LastEnrichmentFinishedUtc;
            _enhancementResumeNotBeforeUtc = state.EnhancementResumeNotBeforeUtc;
            RestorePendingEnhancementResumeWork(
                state.PendingEnhancementResumeFolderIds,
                state.PendingEnhancementResumeRootPaths);
            if (HasPendingEnhancementResumeFolders())
            {
                _enhancementResumeAwaitingPipelineCompletion = true;
                _enhancementResumeNotBeforeUtc ??= DateTimeOffset.UtcNow.Add(EnhancementResumeDelay);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to load download orchestration runtime state.");
        }
    }

    private void SaveOrchestrationRuntimeState()
    {
        try
        {
            var state = new OrchestrationRuntimeState
            {
                EnhancementResumeAwaitingPipelineCompletion = _enhancementResumeAwaitingPipelineCompletion,
                EnhancementInterruptedByEnrichment = _enhancementInterruptedByEnrichment,
                LastEnrichmentFinishedUtc = _lastEnrichmentFinishedUtc,
                EnhancementResumeNotBeforeUtc = _enhancementResumeNotBeforeUtc,
                PendingEnhancementResumeFolderIds = _pendingEnhancementResumeFolderIds.ToList(),
                PendingEnhancementResumeRootPaths = _pendingEnhancementResumeRootPaths.ToList()
            };

            var json = JsonSerializer.Serialize(state, ScheduleJsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(_orchestrationStatePath)!);
            File.WriteAllText(_orchestrationStatePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save download orchestration runtime state.");
        }
    }

    private void RestorePendingEnhancementResumeWork(IEnumerable<string>? folderIds, IEnumerable<string>? rootPaths)
    {
        lock (_enhancementResumeLock)
        {
            if (folderIds != null)
            {
                foreach (var folderId in folderIds)
                {
                    if (!string.IsNullOrWhiteSpace(folderId))
                    {
                        _pendingEnhancementResumeFolderIds.Add(folderId.Trim());
                    }
                }
            }

            if (rootPaths != null)
            {
                foreach (var rootPath in rootPaths)
                {
                    var normalizedRoot = NormalizePathScope(rootPath);
                    if (!string.IsNullOrWhiteSpace(normalizedRoot))
                    {
                        _pendingEnhancementResumeRootPaths.Add(normalizedRoot);
                    }
                }
            }
        }
    }

    private void QueueEnhancementResumeFolder(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return;
        }

        var queued = false;
        lock (_enhancementResumeLock)
        {
            queued = _pendingEnhancementResumeFolderIds.Add(folderId.Trim());
        }

        if (queued)
        {
            MarkEnhancementResumeQueued();
        }
    }

    private void QueueEnhancementResumeRootPath(string rootPath, bool immediateAfterPipeline = false)
    {
        var normalizedRoot = NormalizePathScope(rootPath);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return;
        }

        var queued = false;
        lock (_enhancementResumeLock)
        {
            queued = _pendingEnhancementResumeRootPaths.Add(normalizedRoot);
        }

        if (queued)
        {
            MarkEnhancementResumeQueued(immediateAfterPipeline);
        }
    }

    private void MarkEnhancementResumeQueued(bool immediateAfterPipeline = false)
    {
        _enhancementResumeAwaitingPipelineCompletion = true;
        _enhancementResumeNotBeforeUtc = immediateAfterPipeline
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow.Add(EnhancementResumeDelay);
        SaveOrchestrationRuntimeState();
    }

    private async Task<List<string>> ConsumeEnhancementResumeFoldersAsync(CancellationToken cancellationToken)
    {
        List<string> folderIds;
        List<string> rootPaths;
        List<string> originalFolderIds;
        lock (_enhancementResumeLock)
        {
            if (_pendingEnhancementResumeFolderIds.Count == 0
                && _pendingEnhancementResumeRootPaths.Count == 0)
            {
                return new List<string>();
            }

            folderIds = _pendingEnhancementResumeFolderIds.ToList();
            originalFolderIds = folderIds.ToList();
            rootPaths = _pendingEnhancementResumeRootPaths.ToList();
        }

        if (rootPaths.Count > 0)
        {
            var profileContext = await BuildAutomationProfileContextAsync(cancellationToken);
            folderIds.AddRange(profileContext.FoldersById.Values
                .Where(IsEnhancementEligibleFolder)
                .Where(folder => rootPaths.Any(rootPath => PathScopesOverlap(folder.RootPath, rootPath)))
                .Select(folder => folder.Id.ToString(CultureInfo.InvariantCulture)));
        }

        lock (_enhancementResumeLock)
        {
            foreach (var folderId in originalFolderIds)
            {
                _pendingEnhancementResumeFolderIds.Remove(folderId);
            }

            foreach (var rootPath in rootPaths)
            {
                _pendingEnhancementResumeRootPaths.Remove(rootPath);
            }
        }

        SaveOrchestrationRuntimeState();
        return folderIds
            .Where(folderId => !string.IsNullOrWhiteSpace(folderId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<DownloadQueueExecutionDecision> EvaluateDownloadGateAsync(CancellationToken cancellationToken = default)
        => EvaluateDownloadGateAsync(allowManualQueueDuringEnrichment: false, cancellationToken);

    public Task<DownloadQueueExecutionDecision> EvaluateManualQueueGateAsync(CancellationToken cancellationToken = default)
        => EvaluateDownloadGateAsync(allowManualQueueDuringEnrichment: true, cancellationToken);

    public Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(
        CancellationToken cancellationToken = default)
        => EvaluateDownloadGateAsync(cancellationToken);

    private async Task<DownloadQueueExecutionDecision> EvaluateDownloadGateAsync(
        bool allowManualQueueDuringEnrichment,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_postDownloadPipelineInProgress)
        {
            return allowManualQueueDuringEnrichment
                ? AllowDownloads()
                : DenyDownloads("post_download_finalization", "Downloads waiting for post-enrichment finalization to finish.");
        }

        if (_autoTagService.TryGetRunningEnrichmentJobId(out _))
        {
            return allowManualQueueDuringEnrichment
                ? AllowDownloads()
                : DenyDownloads("enrichment_running", "Downloads waiting for enrichment to finish.");
        }

        var runningJobDecision = TryResolveRunningJobGateDecision(allowManualQueueDuringEnrichment);
        if (runningJobDecision != null)
        {
            return runningJobDecision;
        }

        return AllowDownloads();
    }

    private static DownloadQueueExecutionDecision AllowDownloads(bool enhancementPaused = false)
        => new(true, "open", string.Empty, enhancementPaused);

    private static DownloadQueueExecutionDecision DenyDownloads(string reasonCode, string message)
        => new(false, reasonCode, message);

    private DownloadQueueExecutionDecision? TryResolveRunningJobGateDecision(bool allowManualQueueDuringEnrichment)
    {
        if (!_autoTagService.TryGetAnyRunningJobId(out var runningJobId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(runningJobId))
        {
            _logger.LogWarning("AutoTag reported a running job with an empty id. Allowing download.");
            return AllowDownloads();
        }

        var runningJob = _autoTagService.GetJob(runningJobId);
        if (runningJob is not null
            && string.Equals(runningJob.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            && string.Equals(runningJob.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            return allowManualQueueDuringEnrichment
                ? AllowDownloads()
                : DenyDownloads("enrichment_running", "Downloads waiting for enrichment to finish.");
        }

        return AllowDownloads();
    }

    public void MarkDownloadQueued()
    {
        if (IsProtectedEnrichmentPipelineRunning())
        {
            SignalWake();
        }
        else
        {
            _queueIdleSince = null;
            SetPhase(OrchestrationPhase.Downloading);
            SignalWake(resetIdleCountdown: true);
        }

        if (HasPendingEnhancementResumeFolders())
        {
            _enhancementResumeAwaitingPipelineCompletion = true;
        }
    }

    public void MarkRetryQueued()
    {
        if (IsProtectedEnrichmentPipelineRunning())
        {
            SignalWake();
            return;
        }

        _queueIdleSince = null;
        SetPhase(OrchestrationPhase.Downloading);
        SignalWake(resetIdleCountdown: true);
    }

    private void OnQueueStateChanged(DownloadQueueRepository.QueueStateChangedEvent stateChanged)
    {
        if (string.IsNullOrWhiteSpace(stateChanged.Status))
        {
            return;
        }

        var normalizedStatus = stateChanged.Status.Trim().ToLowerInvariant();
        if (normalizedStatus is QueueStatusQueued or QueueStatusInQueue or AutoTagLiterals.RunningStatus or QueueStatusRetrying or QueueStatusDownloading)
        {
            if (IsProtectedEnrichmentPipelineRunning())
            {
                SignalWake();
            }
            else
            {
                _queueIdleSince = null;
                SignalWake(resetIdleCountdown: true);
            }
            return;
        }

        if (normalizedStatus is AutoTagLiterals.CompletedStatus or QueueStatusComplete or AutoTagLiterals.FailedStatus or AutoTagLiterals.CanceledStatus or QueueStatusCancelled)
        {
            SignalWake();
        }
    }

    private async void OnAutoTagJobCompleted(AutoTagJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.EnhancementDownloadBatchId))
        {
            try
            {
                if (string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    var released = await _queueRepository.ReleaseEnhancementBatchAsync(
                        job.EnhancementDownloadBatchId,
                        CancellationToken.None);
                    if (released > 0 && !string.IsNullOrWhiteSpace(job.RootPath))
                    {
                        QueueEnhancementResumeRootPath(job.RootPath, immediateAfterPipeline: true);
                    }
                    _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                        DateTimeOffset.UtcNow,
                        "info",
                        $"Automation: released Enhancement download batch {job.EnhancementDownloadBatchId} ({released} item(s)); the next batch will wait for pipeline settlement."));
                }
                else
                {
                    await _queueRepository.CancelEnhancementBatchAsync(
                        job.EnhancementDownloadBatchId,
                        $"Enhancement job ended with status {job.Status}.",
                        CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to finalize Enhancement download batch {BatchId}.", job.EnhancementDownloadBatchId);
            }
        }

        QueueInterruptedEnhancementResume(job);
        SignalWake();
        _queueWakeSignal.Pulse();
    }

    private void QueueInterruptedEnhancementResume(AutoTagJob? job)
    {
        if (job == null
            || !string.Equals(job.RunIntent, AutoTagLiterals.RunIntentEnhancementOnly, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(job.RootPath))
        {
            return;
        }

        if (!string.Equals(job.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(job.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(job.Status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsAutomationPausedEnhancementJob(job))
        {
            return;
        }

        QueueEnhancementResumeRootPath(job.RootPath);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: enhancement resume queued for {job.RootPath}; resume is delayed for 30 minute(s)."));
    }

    private static bool IsAutomationPausedEnhancementJob(AutoTagJob job)
        => string.Equals(job.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
           && job.Error?.StartsWith("Paused by automation.", StringComparison.OrdinalIgnoreCase) == true;

    private void SignalWake(bool resetIdleCountdown = false)
    {
        if (resetIdleCountdown)
        {
            _queueIdleSince = null;
        }

        if (Interlocked.Exchange(ref _wakeSignalPending, 1) == 1)
        {
            return;
        }

        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            Interlocked.Exchange(ref _wakeSignalPending, 0);
        }
    }

    private async Task WaitForWakeAsync(CancellationToken cancellationToken)
    {
        var timeout = GetNextWakeDelay();

        try
        {
            await _wakeSignal.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _wakeSignalPending, 0);
        }
    }

    private TimeSpan GetNextWakeDelay()
    {
        DateTimeOffset? countdownUntilUtc;
        lock (_phaseLock)
        {
            countdownUntilUtc = _countdownUntilUtc;
        }

        var now = DateTimeOffset.UtcNow;
        OrchestrationPhase phase;
        lock (_phaseLock)
        {
            phase = _phase;
        }
        var deadlines = new List<DateTimeOffset>(3)
        {
            now.Add(phase == OrchestrationPhase.Idle
                ? IdleRecoveryRecheckDelay
                : _orchestrationRecheckDelay)
        };

        if (countdownUntilUtc.HasValue && countdownUntilUtc.Value > now)
        {
            deadlines.Add(countdownUntilUtc.Value);
        }

        if (_enhancementResumeNotBeforeUtc.HasValue && _enhancementResumeNotBeforeUtc.Value > now)
        {
            deadlines.Add(_enhancementResumeNotBeforeUtc.Value);
        }

        var nextDeadline = deadlines.Min();
        var delay = nextDeadline - now;
        return delay <= TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "DownloadOrchestration"))
        {
            return;
        }

        await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);
        var orphanedHeldItems = await _queueRepository.CancelOrphanedEnhancementBatchesAsync(stoppingToken);
        if (orphanedHeldItems > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                WarningLogLevel,
                $"Automation: canceled {orphanedHeldItems} incomplete held Enhancement item(s) recovered after restart."));
        }
        SetPhase(OrchestrationPhase.Idle);

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
                await WaitForWakeAsync(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Orchestration tick coordinates retries, enrichment gates, and enhancement scheduling in a single state-machine pass.")]
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var runnableDownloadCount = await _queueRepository.GetRunnableDownloadCountAsync(cancellationToken);
        _lastKnownActiveDownloadCount = runnableDownloadCount;
        var hasRunnableDownloads = runnableDownloadCount > 0;
        var hasActiveDownloads = hasRunnableDownloads
            || await _queueRepository.HasActiveDownloadsAsync(cancellationToken);
        UpdateQueueActivityState(now, hasActiveDownloads);

        var hasPendingPostDownloadEnrichment = await HasPendingPostDownloadEnrichmentAsync(cancellationToken);
        if (hasPendingPostDownloadEnrichment)
        {
            _pipelineRequested = true;
        }

        if (await TryRunRetrySweepAsync(hasRunnableDownloads, cancellationToken))
        {
            return;
        }

        if (await TryRunEnrichmentPipelineAsync(now, hasActiveDownloads, hasPendingPostDownloadEnrichment, cancellationToken))
        {
            return;
        }

        if (_enhancementStageRunning || _autoTagService.TryGetRunningEnhancementJobId(out _))
        {
            SetPhase(OrchestrationPhase.EnhancementRunning);
            return;
        }

        if (!await _pipelineLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await RunScheduledEnhancementIfDueAsync(cancellationToken);
            if (_enhancementResumeAwaitingPipelineCompletion)
            {
                SetPhase(OrchestrationPhase.EnhancementResumeCooldown, _enhancementResumeNotBeforeUtc);
            }
            else if (_enhancementStageRunning || _autoTagService.TryGetRunningEnhancementJobId(out _))
            {
                SetPhase(OrchestrationPhase.EnhancementRunning);
            }
            else if (!hasRunnableDownloads)
            {
                SetPhase(OrchestrationPhase.Idle);
            }
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private void UpdateQueueActivityState(DateTimeOffset now, bool hasActiveDownloads)
    {
        if (hasActiveDownloads)
        {
            _wasQueueActive = true;
            if (!IsProtectedEnrichmentPipelineRunning())
            {
                _queueIdleSince = null;
                SetPhase(OrchestrationPhase.Downloading);
            }
            return;
        }

        var queueBecameIdle = _wasQueueActive;
        _wasQueueActive = false;
        if (queueBecameIdle)
        {
            _queueIdleSince = now;
            return;
        }

        _queueIdleSince ??= now;
    }

    private async Task<bool> TryRunRetrySweepAsync(bool hasRunnableDownloads, CancellationToken cancellationToken)
    {
        if (hasRunnableDownloads
            || !await _retryScheduler.HasPendingRetriesAsync(cancellationToken))
        {
            return false;
        }

        _queueIdleSince = null;
        SetPhase(OrchestrationPhase.RetrySweep);
        if (!await _pipelineLock.WaitAsync(0, cancellationToken))
        {
            return true;
        }

        try
        {
            var requeued = await _retryScheduler.RunRetrySweepAsync(cancellationToken);
            if (requeued)
            {
                _queueIdleSince = null;
                SetPhase(OrchestrationPhase.Downloading);
                return true;
            }

            _queueIdleSince = DateTimeOffset.UtcNow;
            return true;
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private async Task<bool> TryRunEnrichmentPipelineAsync(
        DateTimeOffset now,
        bool hasActiveDownloads,
        bool hasPendingPostDownloadEnrichment,
        CancellationToken cancellationToken)
    {
        if (!_pipelineRequested)
        {
            return false;
        }

        if (hasActiveDownloads)
        {
            // Enrichment starts only after the entire active queue has been empty for 15 seconds.
            SetPhase(OrchestrationPhase.Downloading);
            return true;
        }

        var idleSince = _queueIdleSince ?? now;
        if ((DateTimeOffset.UtcNow - idleSince) < _downloadIdleDelay)
        {
            SetPhase(OrchestrationPhase.EnrichmentCountdown, idleSince + _downloadIdleDelay);
            return true;
        }

        if (_autoTagService.HasRunningJobs())
        {
            if (hasPendingPostDownloadEnrichment)
            {
                _ = await TryPauseEnhancementForPendingPipelineAsync(cancellationToken);
            }

            SchedulePendingEnrichmentRecheck(now);
            return true;
        }

        if (!await _pipelineLock.WaitAsync(0, cancellationToken))
        {
            SchedulePendingEnrichmentRecheck(now);
            return true;
        }

        try
        {
            SetPhase(OrchestrationPhase.Enriching);
            var finalized = await FinalizePipelineRunAsync(cancellationToken);
            if (!finalized && await RearmPendingPipelineIfNeededAsync(cancellationToken))
            {
                return true;
            }

            SetPhase(OrchestrationPhase.Idle);
            return true;
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private async Task<bool> FinalizePipelineRunAsync(CancellationToken cancellationToken)
    {
        var enrichmentCompleted = await RunPipelineAsync(cancellationToken);
        if (!enrichmentCompleted)
        {
            return false;
        }

        var finishedAt = DateTimeOffset.UtcNow;
        _lastEnrichmentFinishedUtc = finishedAt;
        if (_enhancementInterruptedByEnrichment)
        {
            _enhancementResumeNotBeforeUtc ??= finishedAt.Add(EnhancementResumeDelay);
            _enhancementResumeAwaitingPipelineCompletion = true;
        }

        SaveOrchestrationRuntimeState();
        return true;
    }

    private void SchedulePendingEnrichmentRecheck(DateTimeOffset now)
    {
        var idleSince = _queueIdleSince ?? now;
        var targetUtc = idleSince + _downloadIdleDelay;
        if (targetUtc <= now)
        {
            targetUtc = now.Add(_downloadIdleDelay);
        }

        SetPhase(OrchestrationPhase.EnrichmentCountdown, targetUtc);
    }

    private async Task<bool> RearmPendingPipelineIfNeededAsync(CancellationToken cancellationToken)
    {
        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            _queueIdleSince = null;
            SetPhase(OrchestrationPhase.Downloading);
            return true;
        }

        if (!await HasPendingPostDownloadEnrichmentAsync(cancellationToken))
        {
            return false;
        }

        var idleSince = DateTimeOffset.UtcNow;
        _pipelineRequested = true;
        _queueIdleSince = idleSince;
        SetPhase(OrchestrationPhase.EnrichmentCountdown, idleSince + _downloadIdleDelay);
        return true;
    }

    private async Task<bool> RunPipelineAsync(CancellationToken cancellationToken)
    {
        _postDownloadPipelineInProgress = true;
        var allGroupsFinalized = true;
        try
        {
            var context = await PreparePipelineRunContextAsync(cancellationToken);
            if (context is null)
            {
                return false;
            }

            foreach (var group in context.Groups)
            {
                var enrichmentResult = ResolveReusableEnrichmentResult(group);
                if (enrichmentResult is null)
                {
                    await _queueRepository.SetEnrichmentStatusAsync(
                        group.PendingQueueUuids,
                        EnrichmentStatusRunning,
                        cancellationToken);
                    enrichmentResult = await RunPipelineEnrichmentAsync(context, group, cancellationToken);
                    await ApplyGroupEnrichmentStatusAsync(group, enrichmentResult.Status, cancellationToken);
                }

                if (!IsFinalizationAllowed(enrichmentResult.Status))
                {
                    await MarkPostDownloadFinalizationBlockedAsync(group, cancellationToken);
                    _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                        DateTimeOffset.UtcNow,
                        WarningLogLevel,
                        $"Automation: post-download finalization blocked for destination folder {group.DestinationFolderId} (enrichment status={enrichmentResult.Status})."));
                    allGroupsFinalized = false;
                    continue;
                }

                var finalizationCompleted = await RunPostDownloadFinalizationAsync(
                    context,
                    group,
                    enrichmentResult,
                    cancellationToken);

                if (finalizationCompleted)
                {
                    await RunPostAutoTagStagesAsync(group, cancellationToken);
                }

                if (finalizationCompleted)
                {
                    await PersistPipelineCompletionMarkersAsync(context, group, cancellationToken);
                }
                else
                {
                    allGroupsFinalized = false;
                }
            }

            return allGroupsFinalized;
        }
        finally
        {
            _postDownloadPipelineInProgress = false;
            _queueWakeSignal.Pulse();
        }
    }

    private async Task<bool> ResumePausedEnhancementAsync(CancellationToken cancellationToken)
    {
        var resumeFolderIds = await ConsumeEnhancementResumeFoldersAsync(cancellationToken);
        if (resumeFolderIds.Count == 0)
        {
            return false;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: resuming paused enhancement for folder(s): {string.Join(", ", resumeFolderIds)}."));

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
        else
        {
            _enhancementResumeAwaitingPipelineCompletion = false;
            _enhancementInterruptedByEnrichment = false;
            _enhancementResumeNotBeforeUtc = null;
            SaveOrchestrationRuntimeState();
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
        var pendingItems = await GetPendingPostDownloadItemsAsync(cancellationToken, profileContext.FoldersById);
        pendingItems = await CloseCompletedItemsWithoutStagingFilesAsync(pendingItems, downloadRootPath, cancellationToken);
        if (pendingItems.Count == 0)
        {
            _logger.LogInformation("Orchestration skipped: no AutoTag-eligible completed downloads found.");
            return null;
        }

        var groups = BuildPipelineWorkGroups(profileContext, pendingItems, downloadRootPath);
        if (groups.Count == 0)
        {
            _logger.LogWarning("Orchestration skipped: no completed download groups had a valid AutoTag profile and candidate source files.");
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
            downloadRootPath,
            groups);
    }

    private async Task<List<DownloadQueueItem>> CloseCompletedItemsWithoutStagingFilesAsync(
        List<DownloadQueueItem> pendingItems,
        string downloadRootPath,
        CancellationToken cancellationToken)
    {
        if (pendingItems.Count == 0)
        {
            return pendingItems;
        }

        var remaining = new List<DownloadQueueItem>(pendingItems.Count);
        var closedMarkers = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pendingItems)
        {
            if (PayloadHasExistingSourceUnderRoot(item.PayloadJson, downloadRootPath))
            {
                remaining.Add(item);
                continue;
            }

            if (IsFinalizationRecoveryItem(item)
                && await _downloadMoveService.CanResumeFinalizationAsync(item, downloadRootPath, cancellationToken))
            {
                remaining.Add(item);
                continue;
            }

            if (HasVerifiedFinalDestination(item, downloadRootPath))
            {
                await _queueRepository.MarkMoveNotRequiredAsync(item.QueueUuid, cancellationToken);
                await _queueRepository.SetEnrichmentStatusAsync(item.QueueUuid, EnrichmentStatusNotRequired, cancellationToken);
                var marker = BuildCompletionMarker(item);
                if (!string.IsNullOrWhiteSpace(marker))
                {
                    closedMarkers[marker] = item.UpdatedAt;
                }
                continue;
            }

            if (HasRecordedFinalDestination(item))
            {
                await _queueRepository.MarkMoveNotRequiredAsync(item.QueueUuid, cancellationToken);
                await _queueRepository.SetEnrichmentStatusAsync(item.QueueUuid, EnrichmentStatusNotRequired, cancellationToken);
                var marker = BuildCompletionMarker(item);
                if (!string.IsNullOrWhiteSpace(marker))
                {
                    closedMarkers[marker] = item.UpdatedAt;
                }
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    $"Automation: completed download {item.QueueUuid} already recorded final destinations; no staging artifact remains to finalize."));
                continue;
            }

            await _queueRepository.MarkMoveFailedAsync(item.QueueUuid, cancellationToken);
            await _queueRepository.SetEnrichmentStatusAsync(item.QueueUuid, EnrichmentStatusInterrupted, cancellationToken);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                ErrorLogLevel,
                $"Automation: completed download {item.QueueUuid} lost its staging artifact before enrichment/finalization and has no verified library destination."));
        }

        if (closedMarkers.Count > 0)
        {
            MarkCompletedItemsAsProcessed(closedMarkers);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: closed {closedMarkers.Count} completed download item(s) whose staging files were already finalized."));
        }

        return remaining;
    }

    private static bool HasVerifiedFinalDestination(DownloadQueueItem item, string downloadRootPath)
    {
        if (string.IsNullOrWhiteSpace(item.FinalDestinationsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.FinalDestinationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var source = property.Name;
                var destination = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(destination)
                    || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
                    || IsPathWithinScope(destination, downloadRootPath))
                {
                    continue;
                }

                var destinationIo = DownloadPathResolver.ResolveIoPath(destination);
                if (!string.IsNullOrWhiteSpace(destinationIo) && File.Exists(destinationIo))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool HasRecordedFinalDestination(DownloadQueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FinalDestinationsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.FinalDestinationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var destination = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(destination))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private async Task<PipelineEnrichmentResult> RunPipelineEnrichmentAsync(
        PipelineRunContext context,
        PipelineWorkGroup group,
        CancellationToken cancellationToken)
    {
        if (group.Stages.HasEnrichment)
        {
            return await RunPostDownloadEnrichmentAsync(
                group.AutomationConfigJson,
                group.AutomationProfile,
                context.DownloadRootPath,
                group.DestinationFolderId,
                group.SourceFilePaths,
                cancellationToken);
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: post-download enrichment skipped for destination folder {group.DestinationFolderId} (no enrichment tags configured)."));
        return new PipelineEnrichmentResult("skipped_no_enrichment_tags", SafeToContinue: true, SafeToPersist: true);
    }

    private async Task<PipelineEnrichmentResult> RunPostDownloadEnrichmentAsync(
        string automationConfigJson,
        TaggingProfile? automationProfile,
        string downloadRootPath,
        long destinationFolderId,
        IReadOnlyCollection<string> sourceFilePaths,
        CancellationToken cancellationToken)
    {
        if (sourceFilePaths.Count == 0 || !HasCandidateStagingAudioFiles(sourceFilePaths))
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: post-download enrichment skipped for destination folder {destinationFolderId} (no candidate audio files under {downloadRootPath})."));
            return new PipelineEnrichmentResult("skipped_no_candidate_files", SafeToContinue: true, SafeToPersist: true);
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: post-download enrichment starting for destination folder {destinationFolderId} ({downloadRootPath})."));

        AutoTagJob? enrichmentJob = null;
        try
        {
            _taggingInProgress = true;
            var enrichmentConfig = ClearEnhancementTags(automationConfigJson);
            enrichmentJob = await _autoTagService.StartJob(
                downloadRootPath,
                enrichmentConfig,
                new AutoTagService.StartJobOptions(
                    Trigger: AutoTagLiterals.AutomationTrigger,
                    ProfileId: automationProfile?.Id,
                    ProfileName: automationProfile?.Name,
                    RunIntent: AutoTagLiterals.RunIntentDownloadEnrichment));
            if (enrichmentJob == null)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    $"Automation: post-download enrichment skipped for destination folder {destinationFolderId} because downloads are active."));
                return new PipelineEnrichmentResult("skipped_downloads_active", SafeToContinue: false, SafeToPersist: false);
            }

            await WaitForJobCompletionAsync(enrichmentJob, cancellationToken);
        }
        finally
        {
            _taggingInProgress = false;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: enrichment finished for destination folder {destinationFolderId} (status={enrichmentJob?.Status ?? "skipped"})."));

        return ResolvePipelineEnrichmentResult(enrichmentJob);
    }

    private async Task<bool> RunPostDownloadFinalizationAsync(
        PipelineRunContext context,
        PipelineWorkGroup group,
        PipelineEnrichmentResult enrichmentResult,
        CancellationToken cancellationToken)
    {
        foreach (var queueUuid in group.PendingQueueUuids)
        {
            await _queueRepository.MarkMoveRunningAsync(queueUuid, cancellationToken);
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: post-download finalization starting for destination folder {group.DestinationFolderId} (enrichment status={enrichmentResult.Status})."));

        try
        {
            var technicalUpgrade = ResolveTechnicalUpgradeFinalization(group.PendingItems);
            if (technicalUpgrade.IsTechnicalUpgrade)
            {
                if (technicalUpgrade.SourceAudioPaths.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Technical quality upgrade is missing its indexed source-file identity.");
                }
                var missingOriginal = technicalUpgrade.SourceAudioPaths
                    .FirstOrDefault(path => !File.Exists(path));
                if (!string.IsNullOrWhiteSpace(missingOriginal))
                {
                    throw new InvalidOperationException(
                        $"Technical quality upgrade source file is no longer indexed on disk: {missingOriginal}");
                }
            }
            var summary = await _downloadMoveService.MoveForRootWithSummaryAsync(
                context.DownloadRootPath,
                new AutoTagOrganizerOptions
                {
                    BatchScopedFilesOnly = true,
                    ResolveSameTrackQualityConflicts = true,
                    RequireIncomingQualityReplacement = technicalUpgrade.IsTechnicalUpgrade,
                    DuplicateConflictPolicy = technicalUpgrade.IsTechnicalUpgrade
                        ? AutoTagOrganizerOptions.DuplicateConflictMoveToDuplicates
                        : AutoTagOrganizerOptions.DuplicateConflictKeepBest,
                    DuplicatesFolderName = technicalUpgrade.DuplicatesFolderName
                },
                group.SourceFilePaths,
                Array.Empty<string>(),
                cancellationToken);

            var sourceFilesRemain = HasExistingGroupSourceFiles(group);
            if (summary.FailedCount > 0 || !string.IsNullOrWhiteSpace(summary.Error))
            {
                await MarkPostDownloadFinalizationFailedAsync(group, cancellationToken);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    ErrorLogLevel,
                    $"Automation: post-download finalization failed for destination folder {group.DestinationFolderId} (moved={summary.MovedCount}, skipped={summary.SkippedCount}, failed={summary.FailedCount}, error={summary.Error ?? "none"})."));
                return false;
            }

            if (summary.MovedCount == 0
                && summary.SkippedCount == 0
                && summary.ChangedFilePaths.Count == 0
                && sourceFilesRemain)
            {
                await MarkPostDownloadFinalizationFailedAsync(group, cancellationToken);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    ErrorLogLevel,
                    $"Automation: post-download finalization failed for destination folder {group.DestinationFolderId} (no files were moved or finalized)."));
                return false;
            }

            if (!sourceFilesRemain && summary.MovedCount == 0 && summary.ChangedFilePaths.Count == 0)
            {
                await MarkPostDownloadFinalizationNotRequiredAsync(group, cancellationToken);
            }
            else
            {
                if (!await IngestMovedFilesBeforeWatchlistFinalizationAsync(group, summary.ChangedFilePaths, cancellationToken))
                {
                    await MarkPostDownloadFinalizationFailedAsync(group, cancellationToken);
                    return false;
                }
                if (technicalUpgrade.IsTechnicalUpgrade)
                {
                    await _downloadMoveService.QuarantineReplacedEnhancementSourcesAsync(
                        group.DestinationFolderId,
                        technicalUpgrade.SourceAudioPaths,
                        summary.ChangedFilePaths,
                        technicalUpgrade.DuplicatesFolderName,
                        cancellationToken);
                    await RemoveQuarantinedEnhancementSourcesFromIndexAsync(group, cancellationToken);
                }

                var postMoveDispatch = await PersistWatchlistFinalizationOutboxAsync(
                    group,
                    summary.ChangedFilePaths,
                    cancellationToken);
                if (postMoveDispatch.NonWatchlistPresent)
                {
                    await RefreshConfiguredMediaServersForNonWatchlistMoveAsync(cancellationToken);
                }
            }

            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: post-download finalization completed for destination folder {group.DestinationFolderId} (moved={summary.MovedCount}, skipped={summary.SkippedCount}, failed={summary.FailedCount})."));
            return true;
        }
        catch (OperationCanceledException)
        {
            await MarkPostDownloadFinalizationPendingAsync(group, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkPostDownloadFinalizationFailedAsync(group, cancellationToken);
            _logger.LogWarning(ex, "Post-download finalization failed for destination folder {DestinationFolderId}.", group.DestinationFolderId);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                ErrorLogLevel,
                $"Automation: post-download finalization failed for destination folder {group.DestinationFolderId} ({ex.Message})."));
            return false;
        }
    }

    private static (bool IsTechnicalUpgrade, string DuplicatesFolderName, IReadOnlyList<string> SourceAudioPaths)
        ResolveTechnicalUpgradeFinalization(
        IReadOnlyList<DownloadQueueItem> items)
    {
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? duplicatesFolderName = null;
        var foundTechnicalUpgrade = false;
        foreach (var item in items)
        {
            var payload = QueuePreResolutionPayload.ParseOrEmpty(item.PayloadJson);
            var operation = payload["EnhancementOperation"]?.ToString()
                ?? payload["enhancementOperation"]?.ToString();
            if (!string.Equals(operation, "technical-quality-upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foundTechnicalUpgrade = true;
            var sourcePath = payload["EnhancementSourceAudioPath"]?.ToString()
                ?? payload["enhancementSourceAudioPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                sourcePaths.Add(sourcePath.Trim());
            }
            duplicatesFolderName ??= payload["EnhancementDuplicatesFolderName"]?.ToString()
                ?? payload["enhancementDuplicatesFolderName"]?.ToString();
        }

        return !foundTechnicalUpgrade
            ? (false, DuplicateCleanerService.DuplicatesFolderName, Array.Empty<string>())
            : (
                true,
                string.IsNullOrWhiteSpace(duplicatesFolderName)
                    ? DuplicateCleanerService.DuplicatesFolderName
                    : duplicatesFolderName.Trim(),
                sourcePaths.ToList());
    }

    private async Task RemoveQuarantinedEnhancementSourcesFromIndexAsync(
        PipelineWorkGroup group,
        CancellationToken cancellationToken)
    {
        var removedSourcePaths = group.PendingItems
            .Select(item => QueuePreResolutionPayload.ParseOrEmpty(item.PayloadJson))
            .Select(payload => payload["EnhancementSourceAudioPath"]?.ToString()
                ?? payload["enhancementSourceAudioPath"]?.ToString())
            .Where(path => !string.IsNullOrWhiteSpace(path) && !File.Exists(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (removedSourcePaths.Count == 0)
        {
            return;
        }

        await _libraryRepository.RemoveLocalAudioFilesByPathAsync(
            group.DestinationFolderId,
            removedSourcePaths,
            cancellationToken);
    }

    private async Task<bool> IngestMovedFilesBeforeWatchlistFinalizationAsync(
        PipelineWorkGroup group,
        List<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        var movedFilesByDestination = await GetRecentMovedAudioFilesByDestinationAsync(
            group.PendingQueueUuids,
            cancellationToken);
        if (movedFilesByDestination.Count == 0
            && group.DestinationFolderId > 0
            && changedFilePaths.Count > 0)
        {
            movedFilesByDestination = new Dictionary<long, List<string>>
            {
                [group.DestinationFolderId] = changedFilePaths.ToList()
            };
        }

        var changedFileCount = movedFilesByDestination.Sum(pair => pair.Value.Count);
        if (changedFileCount <= 0)
        {
            return true;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: verifying direct library DB ingestion for {changedFileCount} finalized file(s) before watchlist sync."));

        var ingestion = await _knownFileIngestionService.IngestAndVerifyAsync(
            movedFilesByDestination,
            cancellationToken);
        if (ingestion.IsComplete)
        {
            return true;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            ErrorLogLevel,
            $"Automation: post-download finalization blocked because {ingestion.MissingFilePaths.Count} finalized audio file(s) were not ingested into the library DB."));
        foreach (var missingPath in ingestion.MissingFilePaths.Take(10))
        {
            _logger.LogWarning("Finalized file was not ingested into library DB before watchlist sync: {Path}", missingPath);
        }

        return false;
    }

    private async Task<(bool WatchlistQueued, bool NonWatchlistPresent)> PersistWatchlistFinalizationOutboxAsync(
        PipelineWorkGroup group,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        var queued = false;
        var nonWatchlistPresent = false;
        foreach (var item in group.PendingItems)
        {
            var claims = await _libraryRepository.GetPlaylistWatchDownloadClaimsAsync(
                item.QueueUuid,
                status: null,
                cancellationToken);
            if (claims.Count == 0 && !WatchlistFinalizationService.PayloadHasWatchlistContext(item.PayloadJson))
            {
                nonWatchlistPresent = true;
                continue;
            }
            var itemFinalPaths = DownloadQueueRepository.GetExistingMaterializedFilePaths(item);
            if (itemFinalPaths.Count == 0)
            {
                itemFinalPaths = changedFilePaths
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            await _libraryRepository.UpsertWatchlistFinalizationOutboxAsync(
                item.QueueUuid,
                item.PayloadJson,
                itemFinalPaths,
                cancellationToken);
            queued |= itemFinalPaths.Count > 0;
        }
        if (queued)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _watchlistRunSignal?.Request(WatchlistWakeReason.Finalization);
        }
        return (queued, nonWatchlistPresent);
    }

    private async Task RefreshConfiguredMediaServersForNonWatchlistMoveAsync(CancellationToken cancellationToken)
    {
        var refresh = await _mediaServerLibraryRefreshService.RefreshConfiguredServersAsync(cancellationToken);
        if (!refresh.IsComplete)
        {
            _logger.LogWarning(
                "Post-download media-server refresh was incomplete for a non-Watchlist download: refreshedServers={RefreshedServers}/{ConfiguredServers}, failedServers={FailedServers}.",
                refresh.RefreshedServerCount,
                refresh.ConfiguredServerCount,
                string.Join(',', refresh.FailedServers));
        }
    }

    private static bool HasExistingGroupSourceFiles(PipelineWorkGroup group)
    {
        return group.SourceFilePaths.Any(path =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var ioPath = DownloadPathResolver.ResolveIoPath(path);
            return !string.IsNullOrWhiteSpace(ioPath) && File.Exists(ioPath);
        });
    }

    private async Task MarkPostDownloadFinalizationFailedAsync(PipelineWorkGroup group, CancellationToken cancellationToken)
    {
        foreach (var queueUuid in group.PendingQueueUuids)
        {
            await _queueRepository.MarkMoveFailedAsync(queueUuid, cancellationToken);
        }
    }

    private async Task MarkPostDownloadFinalizationPendingAsync(PipelineWorkGroup group, CancellationToken cancellationToken)
    {
        foreach (var queueUuid in group.PendingQueueUuids)
        {
            await _queueRepository.MarkMovePendingAsync(queueUuid, cancellationToken);
        }
    }

    private async Task MarkPostDownloadFinalizationBlockedAsync(PipelineWorkGroup group, CancellationToken cancellationToken)
    {
        foreach (var queueUuid in group.PendingQueueUuids)
        {
            await _queueRepository.MarkMoveBlockedAsync(queueUuid, cancellationToken);
        }
    }

    private async Task MarkPostDownloadFinalizationNotRequiredAsync(PipelineWorkGroup group, CancellationToken cancellationToken)
    {
        foreach (var queueUuid in group.PendingQueueUuids)
        {
            await _queueRepository.MarkMoveNotRequiredAsync(queueUuid, cancellationToken);
        }

    }

    private static bool HasCandidateStagingAudioFiles(IEnumerable<string> sourceFilePaths)
    {
        return sourceFilePaths.Any(path =>
            !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && StagingAudioExtensions.Contains(Path.GetExtension(path)));
    }

    private static PipelineEnrichmentResult ResolvePipelineEnrichmentResult(AutoTagJob? enrichmentJob)
    {
        var status = enrichmentJob?.Status ?? "skipped";
        if (string.Equals(status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new PipelineEnrichmentResult(status, SafeToContinue: true, SafeToPersist: true);
        }

        if (string.Equals(status, AutoTagLiterals.SkippedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new PipelineEnrichmentResult(status, SafeToContinue: true, SafeToPersist: false);
        }

        if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.ErrorStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new PipelineEnrichmentResult(status, SafeToContinue: false, SafeToPersist: false);
        }

        return new PipelineEnrichmentResult(status, SafeToContinue: false, SafeToPersist: false);
    }

    private async Task ApplyGroupEnrichmentStatusAsync(
        PipelineWorkGroup group,
        string enrichmentResultStatus,
        CancellationToken cancellationToken)
    {
        var mappedStatus = MapEnrichmentResultToQueueStatus(enrichmentResultStatus);
        await _queueRepository.SetEnrichmentStatusAsync(group.PendingQueueUuids, mappedStatus, cancellationToken);
    }

    private static string MapEnrichmentResultToQueueStatus(string? enrichmentResultStatus)
    {
        var normalized = enrichmentResultStatus?.Trim().ToLowerInvariant();
        return normalized switch
        {
            AutoTagLiterals.CompletedStatus => EnrichmentStatusCompleted,
            AutoTagLiterals.SkippedStatus => EnrichmentStatusNotRequired,
            "skipped_no_enrichment_tags" => EnrichmentStatusNotRequired,
            "skipped_no_candidate_files" => EnrichmentStatusInterrupted,
            "skipped_downloads_active" => EnrichmentStatusInterrupted,
            "blocked" => EnrichmentStatusInterrupted,
            AutoTagLiterals.CanceledStatus => EnrichmentStatusCanceled,
            AutoTagLiterals.InterruptedStatus => EnrichmentStatusInterrupted,
            AutoTagLiterals.PausedStatus => EnrichmentStatusInterrupted,
            AutoTagLiterals.FailedStatus => EnrichmentStatusFailed,
            AutoTagLiterals.ErrorStatus => EnrichmentStatusFailed,
            _ => EnrichmentStatusFailed
        };
    }

    private static bool IsFinalizationAllowed(string? enrichmentResultStatus)
    {
        var normalized = enrichmentResultStatus?.Trim().ToLowerInvariant();
        return normalized is AutoTagLiterals.CompletedStatus
            or AutoTagLiterals.SkippedStatus
            or "skipped_no_enrichment_tags";
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
                    item.FinalDestinationsJson,
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
        string? finalDestinationsJson,
        HashSet<string> queueUuidSet,
        out long destinationFolderId,
        out HashSet<string> candidatePaths)
    {
        destinationFolderId = 0;
        candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!sourceDestinationFolderId.HasValue
            || !string.Equals(status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(queueUuid)
            || !queueUuidSet.Contains(queueUuid))
        {
            return false;
        }

        CollectFinalDestinationJsonPaths(finalDestinationsJson, candidatePaths);
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

                LogStagingEnhancementGate($"unrelated audio file present in download staging; not blocking scheduled enhancement ({filePath})");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            LogStagingEnhancementScanBypass(ex, $"download staging scan failed ({ex.Message}); scheduled enhancement will continue");
            return false;
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
            _logger.LogInformation("Automation: staging gate observed ({Reason}).", reason);
        }
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            now,
            "info",
            $"Automation: staging gate observed ({reason})."));
    }

    private void LogStagingEnhancementScanBypass(Exception exception, string reason)
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

        _logger.LogWarning(exception, "Automation: staging gate scan failed; scheduled enhancement will continue ({Reason}).", reason);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            now,
            "warning",
            $"Automation: staging gate scan failed; scheduled enhancement will continue ({reason})."));
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

    private async Task RunPostAutoTagStagesAsync(PipelineWorkGroup group, CancellationToken cancellationToken)
    {
        var movedFilesByDestination = await GetRecentMovedAudioFilesByDestinationAsync(
            group.PendingQueueUuids,
            cancellationToken);
        var changedFolderIds = movedFilesByDestination.Keys
            .Where(folderId => folderId > 0)
            .OrderBy(folderId => folderId)
            .ToList();
        if (changedFolderIds.Count == 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: post-download direct library ingestion skipped (no moved library files detected)."));
            return;
        }

        var changedFileCount = movedFilesByDestination.Sum(pair => pair.Value.Count);
        if (changedFileCount <= 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: post-download direct library ingestion skipped (no moved library file paths detected)."));
            return;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: post-download direct library ingestion starting for {changedFileCount} file(s) in folder(s): {string.Join(", ", changedFolderIds)}."));

        var ingestion = await _knownFileIngestionService.IngestAndVerifyAsync(
            movedFilesByDestination,
            cancellationToken);
        if (!ingestion.IsComplete)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                ErrorLogLevel,
                $"Automation: post-download direct library ingestion incomplete; {ingestion.MissingFilePaths.Count} finalized audio file(s) are missing from the library DB."));
            return;
        }

        var vibeSettings = await _vibeSettingsStore.LoadAsync();
        if (vibeSettings.Enabled)
        {
            var signaled = _analysisService.TrySignalBackgroundAnalysis(Math.Clamp(vibeSettings.BatchSize, 10, 500));
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                signaled ? "info" : "warning",
                signaled
                    ? "Automation: vibe analysis signaled after direct library ingestion completed."
                    : "Automation: vibe analysis signal skipped because analysis is already running or queued."));
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

        if (ShouldDeferEnhancementForDownloadStagingAudio(cancellationToken))
        {
            return;
        }

        if (await ShouldDeferEnhancementResumeForDownloadPipelineAsync(cancellationToken))
        {
            return;
        }

        var pausedWhileResuming = await ResumePausedEnhancementAsync(cancellationToken);
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

        if (executionResult.AttemptedTargets.Count > 0)
        {
            await UpdateEnhancementScheduleStateAsync(executionResult.AttemptedTargets, DateTimeOffset.UtcNow);
        }

        return executionResult.PausedForEnrichment;
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
                "Automation: forcing enhancement resume for paused run (bypassing schedule delay)."));
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
            var runResult = await RunEnhancementTargetAsync(
                target,
                profileContext,
                sourceLabel,
                cancellationToken);
            if (runResult.Attempted)
            {
                attemptedTargets.Add(target);
            }

            if (runResult.PausedForEnrichment)
            {
                return new EnhancementExecutionResult(attemptedTargets, true);
            }
        }

        return new EnhancementExecutionResult(attemptedTargets, false);
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
        var attemptedJobs = new List<AutoTagJob>();
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
            var enabledFeatures = GetEnabledEnhancementFeatures(enhancementConfig);
            if (enabledFeatures.Count == 0)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    $"Automation: enhancement skipped for {target.RootPath} (profile has no gap-fill tags or enhancement workflows)."));
                return new EnhancementTargetRunResult(true, false);
            }

            var enhancementGroupId = Guid.NewGuid().ToString("N");
            foreach (var feature in enabledFeatures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var featureConfig = BuildEnhancementFeatureConfig(enhancementConfig, feature, target.FolderId);
                enhancementJob = await _autoTagService.StartJob(
                    target.RootPath,
                    featureConfig,
                    new AutoTagService.StartJobOptions(
                        Trigger: AutoTagLiterals.ScheduleTrigger,
                        ProfileId: enhancementProfile.Id,
                        ProfileName: enhancementProfile.Name,
                        RunIntent: AutoTagLiterals.RunIntentEnhancementOnly,
                        EnhancementFeature: feature,
                        EnhancementGroupId: enhancementGroupId));
                if (enhancementJob == null)
                {
                    _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                        DateTimeOffset.UtcNow,
                        "info",
                        $"Automation: enhancement section {feature} skipped for {target.RootPath} because downloads are active."));
                    return new EnhancementTargetRunResult(attemptedJobs.Count > 0, false);
                }

                attemptedJobs.Add(enhancementJob);
                MarkEnhancementStageStarted(enhancementJob);
                await WaitForJobCompletionAsync(enhancementJob, cancellationToken);
                MarkEnhancementStageFinished();

                if ((string.Equals(enhancementJob.Status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(enhancementJob.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(enhancementJob.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase))
                    && _enhancementPauseRequested)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Automation enhancement failed for target {RootPath}.", target.RootPath);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                ErrorLogLevel,
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
            $"Automation: enhancement ({sourceLabel}) finished for {target.RootPath} "
            + $"({attemptedJobs.Count} section job(s), last status={enhancementJob?.Status ?? "skipped"})."));

        if (enhancementJob != null
            && (string.Equals(enhancementJob.Status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
                || string.Equals(enhancementJob.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
                || string.Equals(enhancementJob.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase))
            && _enhancementPauseRequested)
        {
            QueueEnhancementResumeFolder(target.FolderId);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    $"Automation: enhancement paused to prioritize pending post-download enrichment ({target.RootPath})."));
                return new EnhancementTargetRunResult(false, true);
            }

        var attempted = attemptedJobs.Any(job =>
            !string.Equals(job.Status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(job.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(job.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase));
        return new EnhancementTargetRunResult(attempted, false);
    }

    private void MarkEnhancementStageStarted(AutoTagJob? job)
    {
        _enhancementPauseRequested = false;
        _enhancementStageRunning = true;
        _activeEnhancementJobId = job?.Id;
        SetPhase(OrchestrationPhase.EnhancementRunning);
    }

    private void MarkEnhancementStageFinished()
    {
        _enhancementStageRunning = false;
        _activeEnhancementJobId = null;
        if (_enhancementResumeAwaitingPipelineCompletion)
        {
            SetPhase(OrchestrationPhase.EnhancementResumeCooldown, _enhancementResumeNotBeforeUtc);
        }
    }

    private async Task<bool> TryPauseEnhancementForPendingPipelineAsync(CancellationToken cancellationToken)
    {
        string? runningEnhancementJobId = null;
        if (!_enhancementStageRunning && !_autoTagService.TryGetRunningEnhancementJobId(out runningEnhancementJobId))
        {
            return false;
        }

        return await TryPauseEnhancementAsync(
            new EnhancementPauseRequest(
                runningEnhancementJobId,
                EnhancementPauseReason.PendingPipeline,
                "Automation: enhancement pause requested to prioritize pending post-download enrichment."),
            cancellationToken);
    }

    private async Task<bool> TryPauseEnhancementAsync(
        EnhancementPauseRequest request,
        CancellationToken cancellationToken)
    {
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
                jobId = request.FallbackJobId;
            }

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return false;
            }

            var runningJob = _autoTagService.GetJob(jobId);
            if (!ShouldPauseEnhancementJobForEnrichment(runningJob, request.Reason))
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
                request.ConfigLogMessage));

            var stopped = await _autoTagService.StopJobAsync(jobId, "automation");
            if (stopped)
            {
                if (request.Reason == EnhancementPauseReason.PendingPipeline)
                {
                    _enhancementInterruptedByEnrichment = true;
                    SaveOrchestrationRuntimeState();
                }

                QueueResumeFoldersForPausedEnhancementJob(jobId);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    LogEnhancementPauseSuccess(jobId);
                }
                return true;
            }

            _enhancementPauseRequested = false;
            _enhancementResumeAwaitingPipelineCompletion = false;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                LogEnhancementPauseAlreadyStopped(jobId);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnhancementPauseFailure(ex);
            return false;
        }
        finally
        {
            _enhancementPauseLock.Release();
        }
    }

    private void LogEnhancementPauseSuccess(string jobId)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Automation enhancement job {JobId} paused to prioritize pending post-download enrichment.",
            jobId);
    }

    private void LogEnhancementPauseAlreadyStopped(string jobId)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Automation enhancement job {JobId} could not be paused while prioritizing pending post-download enrichment.",
            jobId);
    }

    private void LogEnhancementPauseFailure(Exception exception)
    {
        _logger.LogWarning(exception, "Failed to pause enhancement for pending post-download enrichment.");
    }

    private bool HasPendingEnhancementResumeFolders()
    {
        lock (_enhancementResumeLock)
        {
            return _pendingEnhancementResumeFolderIds.Count > 0
                   || _pendingEnhancementResumeRootPaths.Count > 0;
        }
    }

    private async Task<bool> ShouldDeferEnhancementResumeForDownloadPipelineAsync(CancellationToken cancellationToken)
    {
        if (!_enhancementResumeAwaitingPipelineCompletion)
        {
            return false;
        }

        if (await _queueRepository.HasRunnableDownloadsAsync(cancellationToken))
        {
            SetPhase(OrchestrationPhase.EnhancementResumeCooldown, _enhancementResumeNotBeforeUtc);
            return true;
        }

        if (_autoTagService.HasRunningJobs())
        {
            SetPhase(OrchestrationPhase.EnhancementResumeCooldown, _enhancementResumeNotBeforeUtc);
            return true;
        }

        if (await HasPendingPostDownloadEnrichmentAsync(cancellationToken))
        {
            _pipelineRequested = true;
            SetPhase(OrchestrationPhase.EnhancementResumeCooldown, _enhancementResumeNotBeforeUtc);
            return true;
        }

        if (_pipelineRequested)
        {
            SetPhase(OrchestrationPhase.EnhancementResumeCooldown, _enhancementResumeNotBeforeUtc);
            return true;
        }

        if (_enhancementResumeNotBeforeUtc.HasValue && DateTimeOffset.UtcNow < _enhancementResumeNotBeforeUtc.Value)
        {
            SetPhase(OrchestrationPhase.EnhancementResumeCooldown, _enhancementResumeNotBeforeUtc);
            return true;
        }

        if (!HasPendingEnhancementResumeFolders())
        {
            _enhancementResumeAwaitingPipelineCompletion = false;
            _enhancementInterruptedByEnrichment = false;
            _enhancementResumeNotBeforeUtc = null;
            SaveOrchestrationRuntimeState();
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Automation: download pipeline settled; enhancement resume unlocked."));
        return false;
    }

    private void QueueResumeFoldersForPausedEnhancementJob(string jobId)
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

        QueueEnhancementResumeRootPath(job.RootPath);
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

    private string? GetAutoTagConfigJson(TaggingProfile? profile)
        => profile is null
            ? null
            : _configBuilder.BuildConfigJson(profile);

    private List<PipelineWorkGroup> BuildPipelineWorkGroups(
        AutomationProfileContext profileContext,
        IReadOnlyList<DownloadQueueItem> pendingItems,
        string downloadRootPath)
    {
        var groups = new List<PipelineWorkGroup>();
        foreach (var destinationGroup in pendingItems
                     .Where(item => item.DestinationFolderId.HasValue)
                     .GroupBy(item => item.DestinationFolderId!.Value)
                     .OrderBy(group => group.Key))
        {
            var destinationFolderId = destinationGroup.Key;
            var folderProfileReference = profileContext.FoldersById.TryGetValue(destinationFolderId, out var folder)
                ? folder.AutoTagProfileId
                : null;
            var profile = ResolveAutomationProfileForFolder(
                profileContext,
                destinationFolderId.ToString(CultureInfo.InvariantCulture),
                folderProfileReference);
            if (profile == null)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    WarningLogLevel,
                    $"Automation: completed downloads skipped for destination folder {destinationFolderId} (folder has no valid current AutoTag profile)."));
                continue;
            }

            var configJson = GetAutoTagConfigJson(profile);
            if (string.IsNullOrWhiteSpace(configJson))
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    WarningLogLevel,
                    $"Automation: completed downloads skipped for destination folder {destinationFolderId} (profile config could not be built)."));
                continue;
            }

            var items = destinationGroup
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Id)
                .ToList();
            var recoveryItems = items
                .Where(IsFinalizationRecoveryItem)
                .ToList();
            AddPipelineWorkGroup(
                groups,
                destinationFolderId,
                profile,
                configJson,
                recoveryItems,
                ResolveRecordedSourceAudioFilesUnderRoot(recoveryItems, downloadRootPath));

            var recoveryQueueUuids = recoveryItems
                .Select(item => item.QueueUuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var itemsWithSourceFiles = items
                .Where(item => !recoveryQueueUuids.Contains(item.QueueUuid))
                .Where(item => PayloadHasExistingSourceUnderRoot(item.PayloadJson, downloadRootPath))
                .ToList();
            AddPipelineWorkGroup(
                groups,
                destinationFolderId,
                profile,
                configJson,
                itemsWithSourceFiles,
                ResolveExistingSourceAudioFilesUnderRoot(itemsWithSourceFiles, downloadRootPath));

            if (recoveryItems.Count == 0 && itemsWithSourceFiles.Count == 0)
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    WarningLogLevel,
                    $"Automation: completed downloads skipped for destination folder {destinationFolderId} (no candidate source audio files remain under download staging)."));
            }
        }

        if (groups.Count > 1)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: split completed downloads into {groups.Count} destination/profile group(s)."));
        }

        return groups;
    }

    private static void AddPipelineWorkGroup(
        List<PipelineWorkGroup> groups,
        long destinationFolderId,
        TaggingProfile profile,
        string configJson,
        IReadOnlyList<DownloadQueueItem> items,
        List<string> sourceFiles)
    {
        if (items.Count == 0 || sourceFiles.Count == 0)
        {
            return;
        }

        var scopedConfigJson = ApplyTargetFiles(configJson, sourceFiles);
        groups.Add(new PipelineWorkGroup(
            destinationFolderId,
            profile,
            scopedConfigJson,
            GetAutoTagStages(scopedConfigJson),
            items,
            items
                .Select(item => item.QueueUuid)
                .Where(queueUuid => !string.IsNullOrWhiteSpace(queueUuid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            sourceFiles,
            BuildCompletionMarkers(items)));
    }

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
            return new AutoTagStages(enrichmentCount > 0, enhancementCount > 0 || HasConfiguredEnhancementWorkflows(root));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoTagStages(false, false);
        }
    }

    private static List<string> GetEnabledEnhancementFeatures(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)
            || JsonNode.Parse(configJson) is not JsonObject root)
        {
            return new List<string>();
        }

        var features = new List<string>();
        if (ReadArrayCount(root, "gapFillTags") > 0)
        {
            features.Add(AutoTagLiterals.EnhancementFeatureGapFill);
        }

        if (root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return features;
        }

        if (IsFolderUniformityWorkflowEnabled(enhancementRoot))
        {
            features.Add(AutoTagLiterals.EnhancementFeatureFolderUniformity);
        }
        if (IsCoverMaintenanceWorkflowEnabled(enhancementRoot))
        {
            features.Add(AutoTagLiterals.EnhancementFeatureCoverMaintenance);
        }
        if (IsQualityChecksWorkflowEnabled(enhancementRoot))
        {
            features.Add(AutoTagLiterals.EnhancementFeatureQualityChecks);
        }

        return features;
    }

    private static string BuildEnhancementFeatureConfig(
        string configJson,
        string selectedFeature,
        string folderId)
    {
        if (JsonNode.Parse(configJson) is not JsonObject root)
        {
            throw new InvalidOperationException("Enhancement profile config is invalid.");
        }

        var enhancementRoot = root[AutoTagLiterals.EnhancementStage] as JsonObject ?? new JsonObject();
        root[AutoTagLiterals.EnhancementStage] = enhancementRoot;
        SetEnhancementSectionState(
            enhancementRoot,
            "folderUniformity",
            string.Equals(selectedFeature, AutoTagLiterals.EnhancementFeatureFolderUniformity, StringComparison.OrdinalIgnoreCase),
            folderId);
        SetEnhancementSectionState(
            enhancementRoot,
            "coverMaintenance",
            string.Equals(selectedFeature, AutoTagLiterals.EnhancementFeatureCoverMaintenance, StringComparison.OrdinalIgnoreCase),
            folderId);
        SetEnhancementSectionState(
            enhancementRoot,
            "qualityChecks",
            string.Equals(selectedFeature, AutoTagLiterals.EnhancementFeatureQualityChecks, StringComparison.OrdinalIgnoreCase),
            folderId);
        SetEnhancementSectionState(
            enhancementRoot,
            "gapFilling",
            string.Equals(selectedFeature, AutoTagLiterals.EnhancementFeatureGapFill, StringComparison.OrdinalIgnoreCase),
            folderId);

        if (!string.Equals(selectedFeature, AutoTagLiterals.EnhancementFeatureGapFill, StringComparison.OrdinalIgnoreCase))
        {
            root["gapFillTags"] = new JsonArray();
        }
        root.Remove(AutoTagLiterals.TargetFilesKey);

        return root.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    private static void SetEnhancementSectionState(
        JsonObject enhancementRoot,
        string sectionName,
        bool enabled,
        string folderId)
    {
        var section = enhancementRoot[sectionName] as JsonObject ?? new JsonObject();
        section["enabled"] = enabled;
        var folderIds = new JsonArray();
        if (long.TryParse(folderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericFolderId))
        {
            folderIds.Add(numericFolderId);
        }
        section["folderIds"] = folderIds;
        enhancementRoot[sectionName] = section;
    }

    private static string ClearEnhancementTags(string configJson)
    {
        return ClearStageTags(configJson, clearEnrichment: false, clearEnhancement: true);
    }

    private static string ClearEnrichmentTags(string configJson)
    {
        return ClearStageTags(configJson, clearEnrichment: true, clearEnhancement: false);
    }

    private static string ApplyTargetFiles(string configJson, List<string> targetFiles)
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

    private static bool HasConfiguredEnhancementWorkflows(JsonObject root)
    {
        return root[AutoTagLiterals.EnhancementStage] is JsonObject enhancementRoot
            && (IsFolderUniformityWorkflowEnabled(enhancementRoot)
                || IsCoverMaintenanceWorkflowEnabled(enhancementRoot)
                || IsQualityChecksWorkflowEnabled(enhancementRoot));
    }

    private static bool IsFolderUniformityWorkflowEnabled(JsonObject enhancementRoot)
    {
        return enhancementRoot["folderUniformity"] is JsonObject config
            && ReadBool(config, "enabled") == true
            && (ReadBool(config, "enforceFolderStructure") != false || ReadBool(config, "runDedupe") != false);
    }

    private static bool IsCoverMaintenanceWorkflowEnabled(JsonObject enhancementRoot)
    {
        if (enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance
            || ReadBool(coverMaintenance, "enabled") != true)
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
            || ReadBool(qualityChecks, "enabled") != true)
        {
            return false;
        }

        return ReadBool(qualityChecks, "flagDuplicates") == true
            || ReadBool(qualityChecks, "flagMissingTags") == true
            || ReadBool(qualityChecks, "flagMismatchedMetadata") == true
            || ReadBool(qualityChecks, "queueAtmosAlternatives") == true
            || ReadBool(qualityChecks, "queueLyricsRefresh") == true
            || ReadBool(qualityChecks, "queueTechnicalProfileUpgrades") == true;
    }

    private static bool? ReadBool(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<bool>(out var boolValue) ? boolValue : null;
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

    private async Task<List<DownloadQueueItem>> GetPendingPostDownloadItemsAsync(
        CancellationToken cancellationToken,
        IReadOnlyDictionary<long, FolderDto>? foldersById = null)
    {
        EnsureProcessedCompletionStateLoaded();
        var queueItems = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var completedItems = await RecoverMissingDestinationFoldersAsync(
            queueItems
                .Where(item => string.Equals(item.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Id)
                .ToList(),
            cancellationToken);
        LogCompletedDownloadEligibilityDiagnostics(completedItems, foldersById);
        completedItems = completedItems
            .Where(item => item.DestinationFolderId.HasValue)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .ToList();
        var freshItems = completedItems
            .Where(item => item.UpdatedAt > _lastPipelineCompletedAt)
            .Where(IsCompletedItemUnprocessed)
            .Where(NeedsEnrichmentPipelineWork)
            .ToList();
        if (freshItems.Count > 0)
        {
            return await FilterAutoTagEligiblePendingItemsAsync(freshItems, foldersById, cancellationToken);
        }

        var recoveredItems = completedItems
            .Where(IsCompletedItemUnprocessed)
            .Where(NeedsEnrichmentPipelineWork)
            .ToList();
        return await FilterAutoTagEligiblePendingItemsAsync(recoveredItems, foldersById, cancellationToken);
    }

    private void LogCompletedDownloadEligibilityDiagnostics(
        List<DownloadQueueItem> completedItems,
        IReadOnlyDictionary<long, FolderDto>? foldersById)
    {
        if (completedItems.Count == 0)
        {
            return;
        }

        var missingDestination = completedItems.Count(item => !item.DestinationFolderId.HasValue);
        if (missingDestination > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                WarningLogLevel,
                $"Automation: ignored {missingDestination} completed download item(s) without destination folder metadata."));
        }

        if (foldersById == null || foldersById.Count == 0)
        {
            return;
        }

        var ineligible = completedItems.Count(item =>
            item.DestinationFolderId.HasValue
            && foldersById.TryGetValue(item.DestinationFolderId.Value, out var folder)
            && !RequiresAutoTagProfile(folder));
        if (ineligible > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Automation: ignored {ineligible} completed download item(s) for folders that do not require AutoTag profiles."));
        }
    }

    private async Task PersistPipelineCompletionMarkersAsync(
        PipelineRunContext context,
        PipelineWorkGroup group,
        CancellationToken cancellationToken)
    {
        var safeMarkers = await FilterCompletedMarkersReadyToPersistAsync(context, group, cancellationToken);
        MarkCompletedItemsAsProcessed(safeMarkers);
        _lastPipelineCompletedAt = context.PipelineStartedAt;
    }

    private async Task<IReadOnlyDictionary<string, DateTimeOffset>> FilterCompletedMarkersReadyToPersistAsync(
        PipelineRunContext context,
        PipelineWorkGroup group,
        CancellationToken cancellationToken)
    {
        if (group.PendingCompletionMarkers.Count == 0)
        {
            return group.PendingCompletionMarkers;
        }

        var currentItems = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var currentByMarker = currentItems
            .Select(item => new { Marker = BuildCompletionMarker(item), Item = item })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Marker))
            .ToDictionary(entry => entry.Marker, entry => entry.Item, StringComparer.OrdinalIgnoreCase);
        var safeMarkers = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var (marker, updatedAt) in group.PendingCompletionMarkers)
        {
            if (!currentByMarker.TryGetValue(marker, out var currentItem))
            {
                safeMarkers[marker] = updatedAt;
                continue;
            }

            if (IsFinalizationComplete(currentItem.FinalizationStatus)
                || !PayloadHasExistingSourceUnderRoot(currentItem.PayloadJson, context.DownloadRootPath))
            {
                safeMarkers[marker] = currentItem.UpdatedAt > updatedAt ? currentItem.UpdatedAt : updatedAt;
            }
        }

        var deferredCount = group.PendingCompletionMarkers.Count - safeMarkers.Count;
        if (deferredCount > 0)
        {
            _logger.LogWarning(
                "Post-download automation left {DeferredCount} completed download item(s) with source files still under the download root; they remain eligible for recovery.",
                deferredCount);
        }

        return safeMarkers;
    }

    private async Task<List<DownloadQueueItem>> RecoverMissingDestinationFoldersAsync(
        List<DownloadQueueItem> completedItems,
        CancellationToken cancellationToken)
    {
        if (completedItems.Count == 0 || !_libraryRepository.IsConfigured)
        {
            return completedItems;
        }

        var preferencesByPlaylist = new Dictionary<string, PlaylistWatchPreferenceDto?>(StringComparer.OrdinalIgnoreCase);
        var recoveredItems = new List<DownloadQueueItem>(completedItems.Count);
        foreach (var item in completedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.DestinationFolderId.HasValue
                || string.IsNullOrWhiteSpace(item.PayloadJson)
                || string.IsNullOrWhiteSpace(item.QueueUuid)
                || !TryReadWatchlistContext(item.PayloadJson, out var source, out var playlistId))
            {
                recoveredItems.Add(item);
                continue;
            }

            var preferenceKey = $"{source.Trim().ToLowerInvariant()}|{playlistId.Trim()}";
            if (!preferencesByPlaylist.TryGetValue(preferenceKey, out var preference))
            {
                preference = await _libraryRepository.GetPlaylistWatchPreferenceAsync(
                    source,
                    playlistId,
                    cancellationToken);
                preferencesByPlaylist[preferenceKey] = preference;
            }

            if (preference?.DestinationFolderId is not long destinationFolderId)
            {
                recoveredItems.Add(item);
                continue;
            }

            await _queueRepository.UpdateQueueMetadataAsync(
                item.QueueUuid,
                item.QualityRank,
                item.ContentType,
                destinationFolderId,
                cancellationToken);

            if (TryRewritePayloadDestinationFolderId(item.PayloadJson, destinationFolderId, out var payloadJson))
            {
                await _queueRepository.UpdatePayloadAsync(item.QueueUuid, payloadJson, cancellationToken);
            }
            else
            {
                payloadJson = item.PayloadJson;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recovered destination folder {DestinationFolderId} for completed monitored download {QueueUuid} from playlist preference {Source}:{PlaylistId}.",
                    destinationFolderId,
                    item.QueueUuid,
                    source,
                    playlistId);
            }

            recoveredItems.Add(item with
            {
                DestinationFolderId = destinationFolderId,
                PayloadJson = payloadJson
            });
        }

        return recoveredItems;
    }

    private static bool TryReadWatchlistContext(string payloadJson, out string source, out string playlistId)
    {
        source = string.Empty;
        playlistId = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (TryGetPropertyIgnoreCase(root, "sourceIds", out var sourceIds)
                && sourceIds.ValueKind == JsonValueKind.Object
                && TryReadStringPropertyIgnoreCase(sourceIds, "watchlist_source", out source)
                && TryReadStringPropertyIgnoreCase(sourceIds, "watchlist_playlist", out playlistId))
            {
                return !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(playlistId);
            }

            var hasSource = TryReadStringPropertyIgnoreCase(root, "watchlistSource", out source)
                || TryReadStringPropertyIgnoreCase(root, "watchlist_source", out source);
            var hasPlaylist = TryReadStringPropertyIgnoreCase(root, "watchlistPlaylistId", out playlistId)
                || TryReadStringPropertyIgnoreCase(root, "watchlist_playlist", out playlistId);
            return hasSource && hasPlaylist
                && !string.IsNullOrWhiteSpace(source)
                && !string.IsNullOrWhiteSpace(playlistId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRewritePayloadDestinationFolderId(
        string payloadJson,
        long destinationFolderId,
        out string updatedPayloadJson)
    {
        updatedPayloadJson = payloadJson;
        try
        {
            var node = JsonNode.Parse(payloadJson) as JsonObject;
            if (node == null)
            {
                return false;
            }

            node["destinationFolderId"] = destinationFolderId;
            node["DestinationFolderId"] = destinationFolderId;
            updatedPayloadJson = node.ToJsonString(ScheduleJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsCompletedItemUnprocessed(DownloadQueueItem item)
    {
        if (!IsFinalizationComplete(item.FinalizationStatus))
        {
            return true;
        }

        var marker = BuildCompletionMarker(item);
        if (string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        if (!_processedCompletionByQueueItem.TryGetValue(marker, out var processedAt))
        {
            return true;
        }

        return item.UpdatedAt > processedAt;
    }

    private static bool NeedsEnrichmentPipelineWork(DownloadQueueItem item)
    {
        var status = item.EnrichmentStatus?.Trim().ToLowerInvariant();
        var enrichmentComplete = status is EnrichmentStatusCompleted or EnrichmentStatusNotRequired;
        return !enrichmentComplete || !IsFinalizationComplete(item.FinalizationStatus);
    }

    private static bool IsFinalizationRecoveryItem(DownloadQueueItem item)
    {
        var enrichmentStatus = item.EnrichmentStatus?.Trim().ToLowerInvariant();
        return (enrichmentStatus is EnrichmentStatusCompleted or EnrichmentStatusNotRequired)
            && !IsFinalizationComplete(item.FinalizationStatus);
    }

    private static PipelineEnrichmentResult? ResolveReusableEnrichmentResult(PipelineWorkGroup group)
    {
        if (group.PendingItems.Count == 0)
        {
            return null;
        }

        var statuses = group.PendingItems
            .Select(item => item.EnrichmentStatus?.Trim().ToLowerInvariant())
            .ToList();
        if (statuses.Any(status => status is not EnrichmentStatusCompleted and not EnrichmentStatusNotRequired))
        {
            return null;
        }

        var resultStatus = statuses.Contains(EnrichmentStatusCompleted, StringComparer.OrdinalIgnoreCase)
            ? AutoTagLiterals.CompletedStatus
            : "skipped_no_enrichment_tags";
        return new PipelineEnrichmentResult(resultStatus, SafeToContinue: true, SafeToPersist: true);
    }

    private static bool IsFinalizationComplete(string? finalizationStatus)
    {
        var normalized = finalizationStatus?.Trim().ToLowerInvariant();
        return normalized is "moved" or "not_required";
    }

    private static Dictionary<string, DateTimeOffset> BuildCompletionMarkers(IEnumerable<DownloadQueueItem> items)
    {
        var markers = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var marker = BuildCompletionMarker(item);
            if (string.IsNullOrWhiteSpace(marker))
            {
                continue;
            }

            if (!markers.TryGetValue(marker, out var existing) || item.UpdatedAt > existing)
            {
                markers[marker] = item.UpdatedAt;
            }
        }

        return markers;
    }

    private void MarkCompletedItemsAsProcessed(IReadOnlyDictionary<string, DateTimeOffset> markers)
    {
        EnsureProcessedCompletionStateLoaded();
        var changed = false;
        foreach (var (marker, updatedAt) in markers)
        {
            if (string.IsNullOrWhiteSpace(marker))
            {
                continue;
            }

            if (!_processedCompletionByQueueItem.TryGetValue(marker, out var existing) || updatedAt > existing)
            {
                _processedCompletionByQueueItem[marker] = updatedAt;
                changed = true;
            }
        }

        if (changed)
        {
            PruneProcessedCompletionMarkers();
            SaveProcessedCompletionState();
        }
    }

    private static string BuildCompletionMarker(DownloadQueueItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.QueueUuid))
        {
            return $"queue:{item.QueueUuid.Trim()}";
        }

        return item.Id > 0 ? $"id:{item.Id}" : string.Empty;
    }

    private void EnsureProcessedCompletionStateLoaded()
    {
        if (_processedCompletionStateLoaded)
        {
            return;
        }

        lock (_processedCompletionStateLock)
        {
            if (_processedCompletionStateLoaded)
            {
                return;
            }

            try
            {
                if (File.Exists(_processedCompletionPath))
                {
                    var json = File.ReadAllText(_processedCompletionPath);
                    var state = JsonSerializer.Deserialize<ProcessedCompletionState>(json, ScheduleJsonOptions);
                    if (state?.ProcessedByQueueItem is { Count: > 0 })
                    {
                        _processedCompletionByQueueItem.Clear();
                        foreach (var (key, value) in state.ProcessedByQueueItem)
                        {
                            if (!string.IsNullOrWhiteSpace(key))
                            {
                                _processedCompletionByQueueItem[key] = value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load processed completion state.");
            }
            finally
            {
                _processedCompletionStateLoaded = true;
            }
        }
    }

    private void SaveProcessedCompletionState()
    {
        try
        {
            var snapshot = new ProcessedCompletionState
            {
                ProcessedByQueueItem = new Dictionary<string, DateTimeOffset>(_processedCompletionByQueueItem, StringComparer.OrdinalIgnoreCase)
            };
            var json = JsonSerializer.Serialize(snapshot, ScheduleJsonOptions);
            File.WriteAllText(_processedCompletionPath, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save processed completion state.");
        }
    }

    private void PruneProcessedCompletionMarkers()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-61);
        var staleKeys = _processedCompletionByQueueItem
            .Where(pair => pair.Value < cutoff)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in staleKeys)
        {
            _processedCompletionByQueueItem.Remove(key);
        }
    }

    private async Task<List<DownloadQueueItem>> FilterAutoTagEligiblePendingItemsAsync(
        List<DownloadQueueItem> pendingItems,
        IReadOnlyDictionary<long, FolderDto>? foldersById,
        CancellationToken cancellationToken)
    {
        if (pendingItems.Count == 0 || !_libraryRepository.IsConfigured)
        {
            return pendingItems;
        }

        var effectiveFoldersById = foldersById;
        if (effectiveFoldersById is null || effectiveFoldersById.Count == 0)
        {
            var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
            effectiveFoldersById = folders.ToDictionary(folder => folder.Id);
        }

        return pendingItems
            .Where(item =>
                !item.DestinationFolderId.HasValue
                || !effectiveFoldersById.TryGetValue(item.DestinationFolderId.Value, out var folder)
                || RequiresAutoTagProfile(folder))
            .ToList();
    }

    private static List<string> ResolveExistingSourceAudioFilesUnderRoot(
        IEnumerable<DownloadQueueItem> items,
        string rootPath)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var sourceFile in ResolveExistingSourceAudioFilesUnderRoot(item.PayloadJson, rootPath))
            {
                files.Add(sourceFile);
            }
        }

        return files
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ResolveRecordedSourceAudioFilesUnderRoot(
        IEnumerable<DownloadQueueItem> items,
        string rootPath)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.PayloadJson))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                var root = document.RootElement;
                AddRecordedAudioPath(root, "filePath", rootPath, files);
                if (!TryGetPropertyIgnoreCase(root, "files", out var filesElement)
                    || filesElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var fileElement in filesElement.EnumerateArray())
                {
                    if (fileElement.ValueKind != JsonValueKind.Object
                        || TryReadStringPropertyIgnoreCase(fileElement, "type", out var type)
                        && string.Equals(type, "artwork", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddRecordedAudioPath(fileElement, "path", rootPath, files);
                }
            }
            catch (JsonException)
            {
                // Malformed queue payloads are handled by the normal lost-artifact path.
            }
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddRecordedAudioPath(
        JsonElement source,
        string propertyName,
        string rootPath,
        ISet<string> files)
    {
        if (!TryReadStringPropertyIgnoreCase(source, propertyName, out var path)
            || !IsPathUnderRoot(rootPath, path)
            || !StagingAudioExtensions.Contains(Path.GetExtension(path)))
        {
            return;
        }

        files.Add(NormalizePathScope(path));
    }

    private static List<string> ResolveExistingSourceAudioFilesUnderRoot(string? payloadJson, string rootPath)
    {
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(rootPath))
        {
            return files;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return files;
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

                AddExistingAudioFiles(ioPath, files);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return files;
        }

        return files;
    }

    private static void AddExistingAudioFiles(string ioPath, List<string> files)
    {
        if (File.Exists(ioPath))
        {
            var extension = Path.GetExtension(ioPath);
            if (!string.IsNullOrWhiteSpace(extension) && StagingAudioExtensions.Contains(extension))
            {
                files.Add(NormalizePathScope(ioPath));
            }
            return;
        }

        if (!Directory.Exists(ioPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(ioPath, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!string.IsNullOrWhiteSpace(extension) && StagingAudioExtensions.Contains(extension))
            {
                files.Add(NormalizePathScope(file));
            }
        }
    }

    private static bool PayloadHasExistingSourceUnderRoot(string? payloadJson, string rootPath)
    {
        return ResolveExistingSourceAudioFilesUnderRoot(payloadJson, rootPath).Count > 0;
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

    private static void CollectFinalDestinationJsonPaths(string? finalDestinationsJson, HashSet<string> paths)
    {
        if (paths == null || string.IsNullOrWhiteSpace(finalDestinationsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(finalDestinationsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var finalPath in root.EnumerateObject()
                         .Where(static finalDestinationEntry => finalDestinationEntry.Value.ValueKind == JsonValueKind.String)
                         .Select(static finalDestinationEntry => finalDestinationEntry.Value.GetString())
                         .Where(static finalPath => !string.IsNullOrWhiteSpace(finalPath))
                         .Select(static finalPath => finalPath!))
            {
                paths.Add(finalPath);
            }
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
        return PathComparisonHelper.IsPathUnderRoot(rootPath, candidatePath);
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

    private static bool RequiresAutoTagProfile(FolderDto folder) =>
        ResolveFolderContentType(folder) is not FolderContentVideo and not FolderContentPodcast;

    private static string ResolveFolderContentType(FolderDto folder)
    {
        var normalizedDesiredQuality = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedDesiredQuality))
        {
            return FolderContentOther;
        }

        if (normalizedDesiredQuality.Contains(FolderContentAtmos, StringComparison.Ordinal))
        {
            return FolderContentAtmos;
        }

        if (normalizedDesiredQuality.Contains(FolderContentVideo, StringComparison.Ordinal))
        {
            return FolderContentVideo;
        }

        if (normalizedDesiredQuality.Contains(FolderContentPodcast, StringComparison.Ordinal))
        {
            return FolderContentPodcast;
        }

        if (normalizedDesiredQuality == "0")
        {
            var fallback = $"{folder.DisplayName} {folder.RootPath}".ToLowerInvariant();
            if (fallback.Contains(FolderContentVideo, StringComparison.Ordinal))
            {
                return FolderContentVideo;
            }

            if (fallback.Contains(FolderContentPodcast, StringComparison.Ordinal))
            {
                return FolderContentPodcast;
            }
        }

        return FolderContentOther;
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
        if (!IsJobRunning(job.Id))
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

        // Handle race: job can complete between StartJob and event subscription.
        if (!IsJobRunning(job.Id))
        {
            _autoTagService.JobCompleted -= Handler;
            return Task.CompletedTask;
        }

        return WaitForCompletionAsync(job.Id, completion.Task, Handler, cancellationToken);
    }

    private async Task WaitForCompletionAsync(
        string jobId,
        Task completionTask,
        Action<AutoTagJob> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var finishedTask = await Task.WhenAny(completionTask, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
                if (finishedTask == completionTask || !IsJobRunning(jobId))
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _autoTagService.JobCompleted -= handler;
        }
    }

    private bool IsJobRunning(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        var job = _autoTagService.GetJob(jobId);
        return string.Equals(job?.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase);
    }
}
