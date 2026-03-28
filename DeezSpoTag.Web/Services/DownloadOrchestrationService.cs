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
        string DownloadRootPath);
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
    private string? _activeEnhancementJobId;

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
            return new DownloadGateDecision(true, string.Empty, false);
        }

        string? runningEnhancementJobId = null;
        var hasEnhancementStage = _enhancementStageRunning || _autoTagService.TryGetRunningEnhancementJobId(out runningEnhancementJobId);
        if (hasEnhancementStage)
        {
            var paused = await TryPauseEnhancementForIncomingDownloadAsync(runningEnhancementJobId, cancellationToken);
            if (paused || !TaggingInProgress)
            {
                return new DownloadGateDecision(true, string.Empty, paused);
            }
        }

        if (_autoTagService.TryGetRunningEnrichmentJobId(out var runningEnrichmentJobId))
        {
            var paused = await TryPauseEnrichmentForIncomingDownloadAsync(runningEnrichmentJobId);
            if (paused || !TaggingInProgress)
            {
                return new DownloadGateDecision(true, string.Empty, false);
            }
        }

        if (_autoTagService.TryGetAnyRunningJobId(out var runningJobId))
        {
            var paused = await TryPauseAnyRunningAutoTagForIncomingDownloadAsync(runningJobId);
            if (paused || !TaggingInProgress)
            {
                return new DownloadGateDecision(true, string.Empty, false);
            }

            return new DownloadGateDecision(false, "Downloads paused while AutoTag is still stopping.", false);
        }

        _logger.LogWarning("AutoTag reported running jobs but no active job id was found. Allowing download.");
        return new DownloadGateDecision(true, string.Empty, false);
    }

    public void MarkDownloadQueued()
    {
        _queueIdleSince = null;
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
            // Resume-only path: enrichment context can be unavailable (for example, no
            // pending completed downloads), but an interrupted enhancement run may still
            // need to continue once the queue is idle.
            await ResumeInterruptedEnhancementAfterEnrichmentAsync(cancellationToken);
            return;
        }

        await RunPipelineEnrichmentAsync(context, cancellationToken);
        if (!await EnsurePipelineStillIdleAsync(cancellationToken))
        {
            return;
        }

        await ResumeInterruptedEnhancementAfterEnrichmentAsync(cancellationToken);
        if (!await EnsurePipelineStillIdleAsync(cancellationToken))
        {
            return;
        }

        await RunPostAutoTagStagesAsync(cancellationToken);
        _lastPipelineCompletedAt = context.PipelineStartedAt;
    }

    private async Task ResumeInterruptedEnhancementAfterEnrichmentAsync(CancellationToken cancellationToken)
    {
        var resumeFolderIds = ConsumeEnhancementResumeFolders();
        if (resumeFolderIds.Count == 0)
        {
            return;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Automation: resuming interrupted enhancement after enrichment for folder(s): {string.Join(", ", resumeFolderIds)}."));

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
            _lastPipelineCompletedAt = pipelineStartedAt;
            return null;
        }

        var profileContext = await BuildAutomationProfileContextAsync(cancellationToken);
        var pendingItems = await GetPendingPostDownloadItemsAsync(cancellationToken);
        var automationProfile = ResolveAutomationProfileForPendingDownloads(profileContext, pendingItems);
        if (automationProfile == null)
        {
            _logger.LogWarning("Orchestration skipped: destination folder has no valid current AutoTag profile.");
            _lastPipelineCompletedAt = pipelineStartedAt;
            return null;
        }

        var configJson = GetAutoTagConfigJson(automationProfile);
        if (string.IsNullOrWhiteSpace(configJson))
        {
            _logger.LogWarning("Orchestration skipped: the destination folder profile could not be materialized into AutoTag config.");
            _lastPipelineCompletedAt = pipelineStartedAt;
            return null;
        }

        return new PipelineRunContext(
            pipelineStartedAt,
            configJson,
            automationProfile,
            GetAutoTagStages(configJson),
            downloadRootPath);
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
                _logger.LogInformation("Automation halted: downloads started before enhancement target {RootPath}.", target.RootPath);
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
                    "warning",
                    $"Automation: enhancement skipped for {target.RootPath} (folder has no valid current AutoTag profile)."));
                return new EnhancementTargetRunResult(false, false);
            }

            var profileConfigJson = _configBuilder.BuildConfigJson(enhancementProfile);
            if (string.IsNullOrWhiteSpace(profileConfigJson))
            {
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "warning",
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
            _pipelineRequested = true;
            _queueIdleSince = null;
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Automation: enhancement pause requested for incoming download."));

            var stopped = await _autoTagService.StopJobAsync(jobId);
            if (stopped)
            {
                _logger.LogInformation("Automation enhancement job {JobId} pause requested for incoming download.", jobId);
            }
            else
            {
                _logger.LogInformation("Automation enhancement job {JobId} could not be paused (already stopped).", jobId);
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
                _logger.LogInformation("Automation enrichment job {JobId} pause requested for incoming download.", enrichmentJobId);
            }
            else
            {
                _logger.LogInformation("Automation enrichment job {JobId} could not be paused (already stopped).", enrichmentJobId);
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
                _logger.LogInformation("AutoTag job {JobId} pause requested for incoming download.", jobId);
            }
            else
            {
                _logger.LogInformation("AutoTag job {JobId} could not be paused (already stopped).", jobId);
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
        catch (Exception ex) when (ex is not OperationCanceledException) {
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
        catch (Exception ex) when (ex is not OperationCanceledException) {
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
        return queueItems
            .Where(item =>
                item.DestinationFolderId.HasValue
                && string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && item.UpdatedAt > _lastPipelineCompletedAt)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .ToList();
    }

    private TaggingProfile? ResolveAutomationProfileForPendingDownloads(
        AutomationProfileContext context,
        IReadOnlyList<DownloadQueueItem> pendingItems)
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
