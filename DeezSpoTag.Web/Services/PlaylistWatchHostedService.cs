using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistWatchHostedService : BackgroundService
{
    private const string ArtistKind = "artist";
    private const string PlaylistKind = "playlist";
    private const string PlaylistWatchType = "playlist";
    private const int SourceCircuitFailureThreshold = 2;
    private const int SourceCircuitCooldownSeconds = 300;
    private readonly IServiceProvider _serviceProvider;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlaylistWatchHostedService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _itemLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRun = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAllowedRun = new();
    private DateTimeOffset _lastDestinationRepairUtc = DateTimeOffset.MinValue;
    private int _artistRoundRobinIndex;
    private int _triggerPending;

    public PlaylistWatchHostedService(
        IServiceProvider serviceProvider,
        BackgroundWorkCoordinator workCoordinator,
        IConfiguration configuration,
        ILogger<PlaylistWatchHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _workCoordinator = workCoordinator;
        _configuration = configuration;
        _logger = logger;
    }

    public PlaylistWatchHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<PlaylistWatchHostedService> logger)
        : this(
            serviceProvider,
            new BackgroundWorkCoordinator(),
            configuration,
            logger)
    {
    }

    public PlaylistWatchHostedService(
        IServiceProvider serviceProvider,
        ILogger<PlaylistWatchHostedService> logger)
        : this(
            serviceProvider,
            new BackgroundWorkCoordinator(),
            new ConfigurationBuilder().Build(),
            logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "Watchlist"))
        {
            return;
        }

        _logger.LogInformation("Playlist watch service started.");
        await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                var delay = GetWatchInterval();
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Playlist watch service stopped.");
    }

    private TimeSpan GetWatchInterval()
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var settings = settingsService.LoadSettings();
        var seconds = settings.WatchPollIntervalSeconds;
        if (seconds < 1)
        {
            seconds = 1;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    public Task TriggerRunOnceAsync(CancellationToken cancellationToken = default)
        => RunTriggeredOnceAsync(cancellationToken);

    public void ResetPlaylistRuntimeState(string source, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        var normalizedSource = NormalizeSource(source);
        var normalizedSourceId = sourceId.Trim();
        var key = $"playlist:{normalizedSource}:{normalizedSourceId}";
        _consecutiveFailures.TryRemove(key, out _);
        _nextAllowedRun.TryRemove(key, out _);
        _lastRun.TryRemove(key, out _);
    }

    public void ResetPlaylistRuntimeStateForAll(IReadOnlyCollection<PlaylistWatchlistDto> playlists)
    {
        if (playlists == null || playlists.Count == 0)
        {
            return;
        }

        foreach (var playlist in playlists)
        {
            if (playlist == null)
            {
                continue;
            }

            ResetPlaylistRuntimeState(playlist.Source, playlist.SourceId);
        }
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Watch cycle entrypoint intentionally centralizes lock, failure handling, and lifecycle semantics.")]
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (!await _runLock.WaitAsync(0, stoppingToken))
        {
            return;
        }

        var coalescedTrigger = Interlocked.Exchange(ref _triggerPending, 0) != 0;
        if (coalescedTrigger && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Running scheduled watchlist cycle with coalesced trigger notifications.");
        }
        await ExecuteLockedRunAsync(stoppingToken);
    }

    private async Task RunTriggeredOnceAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _triggerPending, 1);
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        Interlocked.Exchange(ref _triggerPending, 0);
        await ExecuteLockedRunAsync(cancellationToken);
    }

    private async Task ExecuteLockedRunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOneWatchCycleAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Playlist watch run failed.");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task RunOneWatchCycleAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var settings = settingsService.LoadSettings();
        if (!settings.WatchEnabled)
        {
            _logger.LogDebug("Watchlist disabled in settings.");
            return;
        }

        var runQueueBudget = scope.ServiceProvider.GetService<WatchlistRunQueueBudgetService>();
        var queueRepository = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Download.Queue.DownloadQueueRepository>();
        var previousWatchlistRunActive = await queueRepository.HasActiveWatchlistDownloadsAsync(stoppingToken);
        var queueBudget = previousWatchlistRunActive ? 0 : Math.Max(1, settings.WatchMaxItemsPerRun);
        var blockReason = previousWatchlistRunActive
            ? WatchlistQueueBlockReason.PreviousWatchlistRunActive
            : WatchlistQueueBlockReason.None;
        var runQueueBudgetToken = runQueueBudget?.BeginRun(queueBudget, blockReason) ?? 0;
        if (previousWatchlistRunActive && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist queue admission deferred because downloads from the previous watchlist run are still active.");
        }
        try
        {
            await RunWatchCycleCoreAsync(scope.ServiceProvider, settings, runQueueBudget, stoppingToken);
        }
        finally
        {
            if (runQueueBudget != null && runQueueBudgetToken != 0)
            {
                runQueueBudget.EndRun(runQueueBudgetToken);
            }
        }
    }

    private async Task RunWatchCycleCoreAsync(
        IServiceProvider serviceProvider,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        WatchlistRunQueueBudgetService? runQueueBudget,
        CancellationToken stoppingToken)
    {
        var repository = serviceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            _logger.LogDebug("Watchlist skipped - library DB not configured.");
            return;
        }

        var profileResolutionService = serviceProvider.GetRequiredService<AutoTagProfileResolutionService>();
        await TryRepairWatchlistDestinationIntegrityAsync(repository, profileResolutionService, stoppingToken);
        var playlistItems = BuildPlaylistWatchItems(await repository.GetPlaylistWatchlistAsync(stoppingToken));
        var artistItems = BuildArtistWatchItems(await repository.GetWatchlistAsync(stoppingToken));
        var allItems = BuildCombinedWatchItems(playlistItems, artistItems);
        if (allItems.Count == 0)
        {
            CleanupStaleState(Array.Empty<WatchItem>());
            return;
        }

        CleanupStaleState(allItems);
        await SeedPersistedLastRunsAsync(allItems, repository, stoppingToken);
        var playlistRunResult = await ProcessPlaylistWatchItemsAsync(
            playlistItems,
            settings,
            repository,
            serviceProvider,
            runQueueBudget,
            stoppingToken);
        if (playlistRunResult.AbortedRun)
        {
            return;
        }

        await ProcessArtistWatchItemsAsync(
            artistItems,
            settings,
            serviceProvider,
            runQueueBudget,
            stoppingToken);
    }

    private async Task TryRepairWatchlistDestinationIntegrityAsync(
        LibraryRepository repository,
        AutoTagProfileResolutionService profileResolutionService,
        CancellationToken stoppingToken)
    {
        if ((DateTimeOffset.UtcNow - _lastDestinationRepairUtc) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        var validFolderIds = await ResolveWatchlistDestinationFolderIdsAsync(profileResolutionService, stoppingToken);
        var repairResult = await repository.RepairWatchlistDestinationEligibilityAsync(validFolderIds, stoppingToken);
        _lastDestinationRepairUtc = DateTimeOffset.UtcNow;
        if ((repairResult.PlaylistPreferencesUpdated <= 0 && repairResult.ArtistPreferencesUpdated <= 0)
            || !_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist destination integrity repair applied: playlistPreferencesUpdated={PlaylistUpdated}, artistPreferencesUpdated={ArtistUpdated}",
            repairResult.PlaylistPreferencesUpdated,
            repairResult.ArtistPreferencesUpdated);
    }

    private static async Task<HashSet<long>> ResolveWatchlistDestinationFolderIdsAsync(
        AutoTagProfileResolutionService profileResolutionService,
        CancellationToken cancellationToken)
        => await WatchlistDestinationFolderResolver.GetValidFolderIdsAsync(profileResolutionService, cancellationToken);

    private static List<WatchItem> BuildCombinedWatchItems(
        IReadOnlyList<WatchItem> playlistItems,
        IReadOnlyList<WatchItem> artistItems)
    {
        var items = new List<WatchItem>(playlistItems.Count + artistItems.Count);
        items.AddRange(playlistItems);
        items.AddRange(artistItems);
        return items;
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Playlist watch scheduler loop preserves queue budget, backoff, and circuit-breaker behavior in one control flow.")]
    private async Task<PlaylistRunResult> ProcessPlaylistWatchItemsAsync(
        IReadOnlyList<WatchItem> playlistItems,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        LibraryRepository repository,
        IServiceProvider serviceProvider,
        WatchlistRunQueueBudgetService? runQueueBudget,
        CancellationToken stoppingToken)
    {
        var runStartedUtc = DateTimeOffset.UtcNow;
        if (playlistItems.Count == 0)
        {
            return new PlaylistRunResult(AbortedRun: false);
        }

        var schedulerState = await repository.GetWatchlistSchedulerStateAsync(PlaylistWatchType, stoppingToken);
        var staleSchedulerState = IsStaleActivePlaylistState(schedulerState, settings)
            ? schedulerState
            : null;
        if (staleSchedulerState != null)
        {
            _logger.LogWarning(
                "Watchlist active playlist state was stale and will be released. source={Source}, sourceId={SourceId}, activeStartedUtc={ActiveStartedUtc}, lastProgressUtc={LastProgressUtc}, zeroQueueStreak={ZeroQueueStreak}",
                staleSchedulerState.ActiveSource,
                staleSchedulerState.ActiveSourceId,
                staleSchedulerState.ActiveStartedUtc,
                staleSchedulerState.LastProgressUtc,
                staleSchedulerState.ZeroQueueStreak);
            await SaveSchedulerStateAsync(
                repository,
                activeSource: null,
                activeSourceId: null,
                activeStartedUtc: null,
                lastProgressUtc: DateTimeOffset.UtcNow,
                zeroQueueStreak: 0,
                stoppingToken);
            schedulerState = null;
        }

        var activeItem = ResolveActivePlaylistItem(playlistItems, schedulerState);
        if (activeItem == null)
        {
            activeItem = ResolveNextPlaylistItem(playlistItems);
            if (activeItem == null)
            {
                return new PlaylistRunResult(AbortedRun: false);
            }

            await SaveSchedulerStateAsync(
                repository,
                activeItem.Source,
                activeItem.Playlist?.SourceId,
                activeStartedUtc: DateTimeOffset.UtcNow,
                lastProgressUtc: schedulerState?.LastProgressUtc,
                zeroQueueStreak: 0,
                stoppingToken);
        }

        var failFastAbort = false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;
        var succeeded = 0;
        var failed = 0;
        var skippedByBackoff = 0;
        var skippedByDelayWindow = 0;
        var skippedByLockBusy = 0;
        var resolutionAttempts = 0;
        var maxResolutionAttemptsPerRun = Math.Max(1, settings.WatchMaxTracksPerPlaylistCheck);
        while (activeItem != null)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (resolutionAttempts >= maxResolutionAttemptsPerRun)
            {
                failFastAbort = true;
                break;
            }
            if (!visited.Add(activeItem.Key))
            {
                break;
            }

            var sourceCircuit = await repository.GetWatchlistSourceCircuitStateAsync(
                PlaylistWatchType,
                activeItem.Source,
                stoppingToken);
            if (sourceCircuit is { } openCircuit && IsCircuitOpen(openCircuit))
            {
                var openUntilUtc = openCircuit.OpenUntilUtc;
                await PersistPlaylistSchedulerStateAsync(
                    activeItem,
                    serviceProvider,
                    "circuit_open",
                    string.IsNullOrWhiteSpace(openCircuit.Reason) ? "Source circuit breaker open." : openCircuit.Reason,
                    openUntilUtc,
                    _consecutiveFailures.TryGetValue(activeItem.Key, out var circuitFailures) ? circuitFailures : 0,
                    stoppingToken);
                activeItem = await AdvanceToNextPlaylistAsync(playlistItems, repository, activeItem, stoppingToken);
                continue;
            }

            var eligibility = GetEligibility(activeItem, settings);
            switch (eligibility)
            {
                case WatchItemEligibility.Backoff:
                    await PersistPlaylistSchedulerStateAsync(
                        activeItem,
                        serviceProvider,
                        "backoff",
                        "Waiting for backoff window before retry.",
                        _nextAllowedRun.TryGetValue(activeItem.Key, out var nextAllowedUtc) ? nextAllowedUtc : null,
                        _consecutiveFailures.TryGetValue(activeItem.Key, out var backoffFailures) ? backoffFailures : null,
                        stoppingToken);
                    skippedByBackoff++;
                    activeItem = await AdvanceToNextPlaylistAsync(playlistItems, repository, activeItem, stoppingToken);
                    continue;
                case WatchItemEligibility.DelayWindow:
                    await PersistPlaylistSchedulerStateAsync(
                        activeItem,
                        serviceProvider,
                        "pending",
                        "Waiting for next scheduled interval.",
                        null,
                        _consecutiveFailures.TryGetValue(activeItem.Key, out var pendingFailures) ? pendingFailures : 0,
                        stoppingToken);
                    skippedByDelayWindow++;
                    activeItem = await AdvanceToNextPlaylistAsync(playlistItems, repository, activeItem, stoppingToken);
                    continue;
            }

            var execution = await TryProcessItemAsync(activeItem, settings, serviceProvider, stoppingToken);
            if (execution.Outcome == WatchItemRunOutcome.LockBusy)
            {
                skippedByLockBusy++;
                activeItem = await AdvanceToNextPlaylistAsync(playlistItems, repository, activeItem, stoppingToken);
                continue;
            }

            processed++;
            if (execution.Outcome == WatchItemRunOutcome.Success)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            var remainingBudget = runQueueBudget?.GetRemaining() ?? int.MaxValue;
            var playlistResult = execution.PlaylistResult;
            resolutionAttempts += Math.Max(1, playlistResult?.AttemptedTracks ?? 0);
            var queuedThisAttempt = playlistResult?.QueuedTracks ?? 0;
            if (playlistResult is { SystemicFailures: > 0 } systemicFailureResult)
            {
                await OpenSourceCircuitAsync(
                    repository,
                    activeItem.Source,
                    systemicFailureResult.FailureFingerprint,
                    systemicFailureResult.FailureMessage,
                    stoppingToken);
                await SaveSchedulerStateAsync(
                    repository,
                    activeItem.Source,
                    activeItem.Playlist?.SourceId,
                    schedulerState?.ActiveStartedUtc ?? DateTimeOffset.UtcNow,
                    schedulerState?.LastProgressUtc,
                    zeroQueueStreak: 0,
                    stoppingToken);
                break;
            }

            if (execution.Outcome == WatchItemRunOutcome.Failure && execution.SystemicFailure)
            {
                await OpenSourceCircuitAsync(
                    repository,
                    activeItem.Source,
                    fingerprint: "hosted_service_exception",
                    reason: execution.FailureMessage,
                    stoppingToken);
                await SaveSchedulerStateAsync(
                    repository,
                    activeItem.Source,
                    activeItem.Playlist?.SourceId,
                    schedulerState?.ActiveStartedUtc ?? DateTimeOffset.UtcNow,
                    schedulerState?.LastProgressUtc,
                    zeroQueueStreak: 0,
                    stoppingToken);
                break;
            }

            var decision = ResolvePlaylistAdvanceDecision(playlistResult, remainingBudget);
            var queueProgressed = queuedThisAttempt > 0;
            var zeroQueueStreak = 0;
            if (!queueProgressed)
            {
                schedulerState = await repository.GetWatchlistSchedulerStateAsync(PlaylistWatchType, stoppingToken);
                zeroQueueStreak = (schedulerState?.ZeroQueueStreak ?? 0) + 1;
                await SaveSchedulerStateAsync(
                    repository,
                    activeItem.Source,
                    activeItem.Playlist?.SourceId,
                    schedulerState?.ActiveStartedUtc ?? DateTimeOffset.UtcNow,
                    schedulerState?.LastProgressUtc,
                    zeroQueueStreak,
                    stoppingToken);
            }

            await SaveSchedulerStateAsync(
                repository,
                activeItem.Source,
                activeItem.Playlist?.SourceId,
                schedulerState?.ActiveStartedUtc ?? DateTimeOffset.UtcNow,
                queueProgressed ? DateTimeOffset.UtcNow : schedulerState?.LastProgressUtc,
                zeroQueueStreak: queueProgressed ? 0 : zeroQueueStreak,
                stoppingToken);
            await repository.UpsertWatchlistSourceCircuitStateAsync(
                new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                    PlaylistWatchType,
                    activeItem.Source,
                    IsOpen: false,
                    OpenUntilUtc: null,
                    Reason: null,
                    Fingerprint: null,
                    FailureCount: 0),
                stoppingToken);

            if (decision == PlaylistAdvanceDecision.StopRunKeepActive)
            {
                break;
            }

            if (decision == PlaylistAdvanceDecision.StopRunClearActive)
            {
                _ = await AdvanceToNextPlaylistAsync(playlistItems, repository, activeItem, stoppingToken);
                break;
            }

            activeItem = await AdvanceToNextPlaylistAsync(playlistItems, repository, activeItem, stoppingToken);
        }

        var elapsedMs = (DateTimeOffset.UtcNow - runStartedUtc).TotalMilliseconds;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist playlist run summary: total={TotalItems}, processed={Processed}, ok={Succeeded}, failed={Failed}, attempts={Attempts}/{AttemptLimit}, skipBackoff={SkippedBackoff}, skipCooldown={SkippedCooldown}, skipLock={SkippedLock}, abort={Aborted}, elapsedMs={ElapsedMs:0}",
                playlistItems.Count,
                processed,
                succeeded,
                failed,
                resolutionAttempts,
                maxResolutionAttemptsPerRun,
                skippedByBackoff,
                skippedByDelayWindow,
                skippedByLockBusy,
                failFastAbort,
                elapsedMs);
        }

        return new PlaylistRunResult(failFastAbort);
    }

    private static PlaylistAdvanceDecision ResolvePlaylistAdvanceDecision(
        PlaylistWatchService.PlaylistReconciliationResult? result,
        int remainingRunBudget)
    {
        if (result == null)
        {
            return PlaylistAdvanceDecision.Advance;
        }

        if (result.KeepActivePlaylist)
        {
            return PlaylistAdvanceDecision.StopRunKeepActive;
        }

        if (string.Equals(
                result.QueueStopReason,
                PlaylistWatchService.WatchQueueStopReason.PreviousWatchlistRunActive.ToString(),
                StringComparison.Ordinal))
        {
            return PlaylistAdvanceDecision.Advance;
        }

        if (remainingRunBudget <= 0)
        {
            return PlaylistAdvanceDecision.StopRunClearActive;
        }

        if (IsBlockingPlaylistStopReason(result.QueueStopReason))
        {
            return PlaylistAdvanceDecision.StopRunKeepActive;
        }

        if (result.RemainingQueueableTracks <= 0)
        {
            return PlaylistAdvanceDecision.Advance;
        }

        return PlaylistAdvanceDecision.Advance;
    }

    private static bool IsBlockingPlaylistStopReason(string? queueStopReason)
        => string.Equals(
                queueStopReason,
                PlaylistWatchService.WatchQueueStopReason.DownloadGate.ToString(),
                StringComparison.Ordinal)
            || string.Equals(
                queueStopReason,
                PlaylistWatchService.WatchQueueStopReason.TrackDeferred.ToString(),
                StringComparison.Ordinal);

    private async Task ProcessArtistWatchItemsAsync(
        IReadOnlyList<WatchItem> artistItems,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        WatchlistRunQueueBudgetService? runQueueBudget,
        CancellationToken stoppingToken)
    {
        if (artistItems.Count == 0)
        {
            return;
        }

        var startIndex = _artistRoundRobinIndex % artistItems.Count;
        var visited = 0;
        var lastVisitedIndex = -1;
        while (visited < artistItems.Count)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if ((runQueueBudget?.GetRemaining() ?? int.MaxValue) <= 0)
            {
                break;
            }

            var index = (startIndex + visited) % artistItems.Count;
            visited++;
            lastVisitedIndex = index;
            var item = artistItems[index];
            var eligibility = GetEligibility(item, settings);
            if (eligibility != WatchItemEligibility.Eligible)
            {
                continue;
            }

            _ = await TryProcessItemAsync(item, settings, serviceProvider, stoppingToken);
        }

        if (artistItems.Count > 0)
        {
            _artistRoundRobinIndex = lastVisitedIndex >= 0
                ? (lastVisitedIndex + 1) % artistItems.Count
                : (startIndex + 1) % artistItems.Count;
        }
    }

    private async Task<WatchItemExecutionOutcome> TryProcessItemAsync(
        WatchItem item,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken)
    {
        var itemLock = _itemLocks.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
        if (!await itemLock.WaitAsync(0, stoppingToken))
        {
            return new WatchItemExecutionOutcome(WatchItemRunOutcome.LockBusy, null, false, null);
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var playlistResult = await RunItemAsync(item, serviceProvider, stoppingToken);
            _lastRun[item.Key] = DateTimeOffset.UtcNow;
            _consecutiveFailures.TryRemove(item.Key, out _);
            _nextAllowedRun.TryRemove(item.Key, out _);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist item succeeded: key={WatchItemKey}, kind={Kind}, source={Source}, elapsedMs={ElapsedMs:0}",
                    item.Key,
                    item.Kind,
                    item.Source,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            return new WatchItemExecutionOutcome(WatchItemRunOutcome.Success, playlistResult, false, null);
        }
        catch (OperationCanceledException ex) when (!stoppingToken.IsCancellationRequested)
        {
            return await RecordItemFailureAsync(item, settings, serviceProvider, startedUtc, stopwatch, ex, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordItemFailureAsync(item, settings, serviceProvider, startedUtc, stopwatch, ex, stoppingToken);
        }
        finally
        {
            itemLock.Release();
        }
    }

    private async Task<WatchItemExecutionOutcome> RecordItemFailureAsync(
        WatchItem item,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        DateTimeOffset startedUtc,
        Stopwatch stopwatch,
        Exception ex,
        CancellationToken cancellationToken)
    {
        var failures = _consecutiveFailures.AddOrUpdate(item.Key, 1, static (_, current) => Math.Min(current + 1, 12));
        var baseDelaySeconds = item.Kind == ArtistKind
            ? Math.Max(1, settings.WatchDelayBetweenArtistsSeconds)
            : Math.Max(1, settings.WatchDelayBetweenPlaylistsSeconds);
        var backoffSeconds = Math.Min(
            600,
            baseDelaySeconds * (int)Math.Pow(2, Math.Min(failures - 1, 6)));
        var nextRunUtc = startedUtc.AddSeconds(backoffSeconds);
        _nextAllowedRun[item.Key] = nextRunUtc;
        await PersistPlaylistSchedulerStateAsync(item, serviceProvider, "backoff", ex.Message, nextRunUtc, failures, cancellationToken);
        if (ShouldEmitBackoffWarning(failures))
        {
            _logger.LogWarning(
                ex,
                "Watchlist item failed: key={WatchItemKey}, kind={Kind}, source={Source}, failures={Failures}, backoffSeconds={BackoffSeconds}, nextRunUtc={NextRunUtc}, elapsedMs={ElapsedMs:0}",
                item.Key,
                item.Kind,
                item.Source,
                failures,
                backoffSeconds,
                nextRunUtc,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist item still failing under backoff threshold: key={WatchItemKey}, kind={Kind}, source={Source}, failures={Failures}, backoffSeconds={BackoffSeconds}, nextRunUtc={NextRunUtc}, elapsedMs={ElapsedMs:0}",
                    item.Key,
                    item.Kind,
                    item.Source,
                    failures,
                    backoffSeconds,
                    nextRunUtc,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        return new WatchItemExecutionOutcome(
            WatchItemRunOutcome.Failure,
            null,
            IsLikelySystemicFailure(ex.Message),
            ex.Message);
    }

    private static async Task PersistPlaylistSchedulerStateAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        string status,
        string? message,
        DateTimeOffset? nextAttemptUtc,
        int? consecutiveFailures,
        CancellationToken cancellationToken)
    {
        if (item.Kind != PlaylistKind || item.Playlist is null)
        {
            return;
        }

        var repository = serviceProvider.GetService<LibraryRepository>();
        if (repository == null || !repository.IsConfigured)
        {
            return;
        }

        var state = await repository.GetPlaylistWatchStateAsync(item.Playlist.Source, item.Playlist.SourceId, cancellationToken);
        if (state != null
            && string.Equals(state.LastRunStatus, status, StringComparison.Ordinal)
            && string.Equals(state.LastRunMessage, message, StringComparison.Ordinal)
            && state.ConsecutiveFailures == consecutiveFailures
            && Nullable.Equals(state.NextAttemptUtc, nextAttemptUtc))
        {
            return;
        }

        await repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                item.Playlist.Source,
                item.Playlist.SourceId,
                state?.SnapshotId ?? item.Playlist.SnapshotId,
                state?.TrackCount ?? item.Playlist.TrackCount,
                state?.BatchNextOffset,
                state?.BatchProcessingSnapshotId,
                state?.LastCheckedUtc,
                status,
                message,
                nextAttemptUtc,
                consecutiveFailures),
            cancellationToken);
    }

    private static List<WatchItem> BuildPlaylistWatchItems(IReadOnlyList<PlaylistWatchlistDto> playlists)
    {
        var items = new List<WatchItem>(playlists.Count);
        foreach (var playlist in playlists)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
            {
                continue;
            }
            var key = $"playlist:{playlist.Source}:{playlist.SourceId}";
            items.Add(new WatchItem(PlaylistKind, key, NormalizeSource(playlist.Source), playlist, null));
        }

        return items;
    }

    private static List<WatchItem> BuildArtistWatchItems(IReadOnlyList<WatchlistArtistDto> artists)
    {
        var items = new List<WatchItem>(artists.Count);
        foreach (var artist in artists)
        {
            var key = $"artist:{artist.ArtistId}";
            items.Add(new WatchItem(ArtistKind, key, ArtistKind, null, artist));
        }

        return items;
    }

    private async Task SeedPersistedLastRunsAsync(
        IReadOnlyList<WatchItem> items,
        LibraryRepository repository,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lastRun.ContainsKey(item.Key))
            {
                continue;
            }

            var lastCheckedUtc = await ResolvePersistedLastCheckedUtcAsync(item, repository, cancellationToken);
            if (lastCheckedUtc.HasValue)
            {
                _lastRun.TryAdd(item.Key, lastCheckedUtc.Value);
            }
        }
    }

    private static async Task<DateTimeOffset?> ResolvePersistedLastCheckedUtcAsync(
        WatchItem item,
        LibraryRepository repository,
        CancellationToken cancellationToken)
    {
        if (item.Kind == ArtistKind)
        {
            return item.Artist?.LastCheckedUtc;
        }

        if (item.Playlist is null)
        {
            return null;
        }

        var state = await repository.GetPlaylistWatchStateAsync(
            item.Playlist.Source,
            item.Playlist.SourceId,
            cancellationToken);
        return state?.LastCheckedUtc;
    }

    private static WatchItem? ResolveActivePlaylistItem(
        IReadOnlyList<WatchItem> playlistItems,
        WatchlistSchedulerStateDto? state)
    {
        if (playlistItems.Count == 0)
        {
            return null;
        }

        if (state == null
            || string.IsNullOrWhiteSpace(state.ActiveSource)
            || string.IsNullOrWhiteSpace(state.ActiveSourceId))
        {
            return ResolveNextPlaylistItem(playlistItems);
        }

        return playlistItems.FirstOrDefault(item =>
            item.Kind == PlaylistKind
            && string.Equals(item.Source, NormalizeSource(state.ActiveSource), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Playlist?.SourceId, state.ActiveSourceId, StringComparison.OrdinalIgnoreCase))
            ?? ResolveNextPlaylistItem(playlistItems);
    }

    private static bool IsStaleActivePlaylistState(
        WatchlistSchedulerStateDto? state,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        if (state == null
            || string.IsNullOrWhiteSpace(state.ActiveSource)
            || string.IsNullOrWhiteSpace(state.ActiveSourceId))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var intervalSeconds = Math.Max(1, settings.WatchPollIntervalSeconds);
        var staleWindow = TimeSpan.FromSeconds(Math.Max(900, intervalSeconds * 3));
        var progressReference = state.LastProgressUtc ?? state.ActiveStartedUtc;
        if (progressReference.HasValue && now - progressReference.Value > staleWindow)
        {
            return true;
        }

        return state.ZeroQueueStreak >= 3
            && state.ActiveStartedUtc.HasValue
            && now - state.ActiveStartedUtc.Value > TimeSpan.FromSeconds(Math.Max(60, intervalSeconds));
    }

    private static WatchItem? ResolveNextPlaylistItem(IReadOnlyList<WatchItem> playlistItems)
    {
        if (playlistItems.Count == 0)
        {
            return null;
        }

        return playlistItems.FirstOrDefault(item => item.Kind == PlaylistKind && item.Playlist != null);
    }

    private static async Task<WatchItem?> AdvanceToNextPlaylistAsync(
        IReadOnlyList<WatchItem> playlistItems,
        LibraryRepository repository,
        WatchItem? currentItem,
        CancellationToken stoppingToken)
    {
        var nextItem = ResolveNextPlaylistItemAfter(playlistItems, currentItem) ?? ResolveNextPlaylistItem(playlistItems);
        if (nextItem == null)
        {
            await SaveSchedulerStateAsync(
                repository,
                activeSource: null,
                activeSourceId: null,
                activeStartedUtc: null,
                lastProgressUtc: DateTimeOffset.UtcNow,
                zeroQueueStreak: 0,
                stoppingToken);
            return null;
        }

        await SaveSchedulerStateAsync(
            repository,
            nextItem.Source,
            nextItem.Playlist?.SourceId,
            activeStartedUtc: DateTimeOffset.UtcNow,
            lastProgressUtc: DateTimeOffset.UtcNow,
            zeroQueueStreak: 0,
            stoppingToken);
        return nextItem;
    }

    private static WatchItem? ResolveNextPlaylistItemAfter(IReadOnlyList<WatchItem> playlistItems, WatchItem? currentItem)
    {
        if (playlistItems.Count == 0 || currentItem == null)
        {
            return null;
        }

        var currentIndex = -1;
        for (var index = 0; index < playlistItems.Count; index++)
        {
            if (string.Equals(playlistItems[index].Key, currentItem.Key, StringComparison.Ordinal))
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return null;
        }

        for (var offset = 1; offset <= playlistItems.Count; offset++)
        {
            var index = (currentIndex + offset) % playlistItems.Count;
            var candidate = playlistItems[index];
            if (candidate.Kind == PlaylistKind && candidate.Playlist != null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task SaveSchedulerStateAsync(
        LibraryRepository repository,
        string? activeSource,
        string? activeSourceId,
        DateTimeOffset? activeStartedUtc,
        DateTimeOffset? lastProgressUtc,
        int zeroQueueStreak,
        CancellationToken cancellationToken)
    {
        await repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                PlaylistWatchType,
                activeSource,
                activeSourceId,
                activeStartedUtc,
                lastProgressUtc,
                zeroQueueStreak),
            cancellationToken);
    }

    private static bool IsCircuitOpen(WatchlistSourceCircuitStateDto? circuitState)
    {
        if (circuitState is not { IsOpen: true })
        {
            return false;
        }

        if (!circuitState.OpenUntilUtc.HasValue)
        {
            return true;
        }

        return DateTimeOffset.UtcNow < circuitState.OpenUntilUtc.Value;
    }

    private static async Task OpenSourceCircuitAsync(
        LibraryRepository repository,
        string source,
        string? fingerprint,
        string? reason,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetWatchlistSourceCircuitStateAsync(PlaylistWatchType, source, cancellationToken);
        var failureCount = Math.Max(0, existing?.FailureCount ?? 0) + 1;
        var isOpen = failureCount >= SourceCircuitFailureThreshold;
        var openUntilUtc = isOpen
            ? DateTimeOffset.UtcNow.AddSeconds(SourceCircuitCooldownSeconds)
            : existing?.OpenUntilUtc;

        await repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                PlaylistWatchType,
                source,
                isOpen,
                openUntilUtc,
                string.IsNullOrWhiteSpace(reason) ? "Systemic source failure." : reason,
                fingerprint,
                failureCount),
            cancellationToken);
    }

    private static bool IsLikelySystemicFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim().ToLowerInvariant();
        return normalized.Contains("captcha", StringComparison.Ordinal)
               || normalized.Contains("forbidden", StringComparison.Ordinal)
               || normalized.Contains("unauthorized", StringComparison.Ordinal)
               || normalized.Contains("login required", StringComparison.Ordinal)
               || normalized.Contains("http 401", StringComparison.Ordinal)
               || normalized.Contains("http 403", StringComparison.Ordinal)
               || normalized.Contains("http 429", StringComparison.Ordinal)
               || normalized.Contains("rate limit", StringComparison.Ordinal)
               || normalized.Contains("too many requests", StringComparison.Ordinal)
               || normalized.Contains("timeout", StringComparison.Ordinal)
               || normalized.Contains("timed out", StringComparison.Ordinal)
               || normalized.Contains("service unavailable", StringComparison.Ordinal)
               || normalized.Contains("gateway timeout", StringComparison.Ordinal)
               || normalized.Contains("http 500", StringComparison.Ordinal)
               || normalized.Contains("http 502", StringComparison.Ordinal)
               || normalized.Contains("http 503", StringComparison.Ordinal)
               || normalized.Contains("http 504", StringComparison.Ordinal);
    }

    private WatchItemEligibility GetEligibility(WatchItem item, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        if (_nextAllowedRun.TryGetValue(item.Key, out var nextAllowedUtc) && DateTimeOffset.UtcNow < nextAllowedUtc)
        {
            return WatchItemEligibility.Backoff;
        }

        if (!_lastRun.TryGetValue(item.Key, out var lastRunUtc))
        {
            return WatchItemEligibility.Eligible;
        }

        var delaySeconds = item.Kind == ArtistKind
            ? settings.WatchDelayBetweenArtistsSeconds
            : settings.WatchDelayBetweenPlaylistsSeconds;
        var delay = TimeSpan.FromSeconds(Math.Max(1, delaySeconds));
        return DateTimeOffset.UtcNow - lastRunUtc >= delay
            ? WatchItemEligibility.Eligible
            : WatchItemEligibility.DelayWindow;
    }

    private void CleanupStaleState(IReadOnlyList<WatchItem> items)
    {
        var activeKeys = new HashSet<string>(items.Select(static item => item.Key), StringComparer.Ordinal);
        CleanupDictionary(_itemLocks, activeKeys, static semaphore =>
        {
            semaphore.Dispose();
            return true;
        });
        CleanupDictionary(_lastRun, activeKeys, static _ => true);
        CleanupDictionary(_consecutiveFailures, activeKeys, static _ => true);
        CleanupDictionary(_nextAllowedRun, activeKeys, static _ => true);
    }

    private static void CleanupDictionary<TValue>(
        ConcurrentDictionary<string, TValue> dictionary,
        HashSet<string> activeKeys,
        Func<TValue, bool> onRemoved)
    {
        foreach (var key in dictionary.Keys)
        {
            if (activeKeys.Contains(key))
            {
                continue;
            }

            if (dictionary.TryRemove(key, out var removedValue))
            {
                _ = onRemoved(removedValue);
            }
        }
    }

    private static async Task<PlaylistWatchService.PlaylistReconciliationResult?> RunItemAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken)
    {
        if (item.Kind == PlaylistKind && item.Playlist != null)
        {
            var watcher = serviceProvider.GetRequiredService<PlaylistWatchService>();
            return await watcher.ReconcilePlaylistAsync(item.Playlist, stoppingToken);
        }

        if (item.Kind == ArtistKind && item.Artist != null)
        {
            var watcher = serviceProvider.GetRequiredService<ArtistWatchService>();
            await watcher.CheckArtistWatchItemAsync(item.Artist, stoppingToken);
        }

        return null;
    }

    private sealed record WatchItem(
        string Kind,
        string Key,
        string Source,
        PlaylistWatchlistDto? Playlist,
        WatchlistArtistDto? Artist);
    private sealed record PlaylistRunResult(bool AbortedRun);
    private sealed record WatchItemExecutionOutcome(
        WatchItemRunOutcome Outcome,
        PlaylistWatchService.PlaylistReconciliationResult? PlaylistResult,
        bool SystemicFailure,
        string? FailureMessage);

    private enum WatchItemRunOutcome
    {
        Success,
        Failure,
        LockBusy
    }

    private enum PlaylistAdvanceDecision
    {
        Advance,
        StopRunKeepActive,
        StopRunClearActive
    }

    private enum WatchItemEligibility
    {
        Eligible,
        Backoff,
        DelayWindow
    }

    private static string NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim().ToLowerInvariant();

    internal static bool ShouldEmitBackoffWarning(int failures)
    {
        if (failures <= 2)
        {
            return true;
        }

        // Keep warnings on exponential milestones while reducing repetitive noise.
        return (failures & (failures - 1)) == 0;
    }
}
