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
using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Web.Services;

public enum WatchlistTriggerKind
{
    All,
    Artist,
    Playlist
}

public enum WatchlistTriggerStatus
{
    Accepted,
    Coalesced,
    Disabled
}

public sealed record WatchlistTriggerRequest(
    WatchlistTriggerKind Kind,
    string Source,
    string Identifier);

public sealed record WatchlistTriggerResult(WatchlistTriggerStatus Status)
{
    public bool Scheduled => Status is not WatchlistTriggerStatus.Disabled;
}

public sealed record WatchlistRuntimeHealth(
    bool IsRunning,
    bool TriggerPending,
    DateTimeOffset? LastCycleStartedUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    string? LastAdmissionBlockReason,
    int LastRecoveredClaimCount,
    int PendingReconciliationRequests = 0);

public sealed record WatchlistRuntimeResetResult(
    LibraryRepository.WatchlistRuntimeCleanupResult Cleanup,
    WatchlistTriggerStatus TriggerStatus);

public sealed class WatchlistRunCoordinator : BackgroundService
{
    private const string ArtistKind = "artist";
    private const string PlaylistKind = "playlist";
    private const string PlaylistWatchType = "playlist";
    private const int SourceCircuitFailureThreshold = 2;
    private const int SourceCircuitCooldownSeconds = 300;
    private readonly IServiceProvider _serviceProvider;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly WatchlistRunSignal _runSignal;
    private readonly ILogger<WatchlistRunCoordinator> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _itemLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRun = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAllowedRun = new();
    private readonly string _reconciliationLeaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _lastDestinationRepairUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastIdentityIndexRefreshUtc = DateTimeOffset.MinValue;
    private int _artistRoundRobinIndex;
    private readonly object _runtimeHealthGate = new();
    private readonly object _activeCycleGate = new();
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private CancellationTokenSource? _activeCycleCancellation;
    private int _runtimeResetRequested;
    private WatchlistRuntimeHealth _runtimeHealth = new(false, false, null, null, null, 0);

    public WatchlistRunCoordinator(
        IServiceProvider serviceProvider,
        BackgroundWorkCoordinator workCoordinator,
        WatchlistRunSignal runSignal,
        ILogger<WatchlistRunCoordinator> logger)
    {
        _serviceProvider = serviceProvider;
        _workCoordinator = workCoordinator;
        _runSignal = runSignal;
        _logger = logger;
    }

    public WatchlistRunCoordinator(
        IServiceProvider serviceProvider,
        ILogger<WatchlistRunCoordinator> logger)
        : this(
            serviceProvider,
            new BackgroundWorkCoordinator(),
            new WatchlistRunSignal(),
            logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Playlist watch service started.");
        await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);

        var wakeReason = WatchlistWakeReason.ScheduledRefresh;
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunTriggeredOnceAsync(wakeReason, stoppingToken);

            try
            {
                wakeReason = await _runSignal.WaitAsync(GetWatchInterval(), stoppingToken);
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

    public Task<WatchlistTriggerResult> TriggerRunOnceAsync(CancellationToken cancellationToken = default)
        => TriggerAsync(new WatchlistTriggerRequest(WatchlistTriggerKind.All, string.Empty, string.Empty), cancellationToken);

    public Task<WatchlistTriggerResult> TriggerArtistOnceAsync(long artistId, CancellationToken cancellationToken = default)
        => TriggerAsync(
            new WatchlistTriggerRequest(WatchlistTriggerKind.Artist, ArtistKind, artistId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);

    public Task<WatchlistTriggerResult> TriggerPlaylistOnceAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
        => TriggerAsync(new WatchlistTriggerRequest(WatchlistTriggerKind.Playlist, source, sourceId), cancellationToken);

    public async Task<WatchlistTriggerResult> TriggerAsync(
        WatchlistTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsWatchlistEnabled())
        {
            return new WatchlistTriggerResult(WatchlistTriggerStatus.Disabled);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return new WatchlistTriggerResult(WatchlistTriggerStatus.Disabled);
        }
        var requestAccepted = await repository.EnqueueWatchlistReconciliationRequestAsync(
            request.Kind switch
            {
                WatchlistTriggerKind.Playlist => PlaylistKind,
                WatchlistTriggerKind.Artist => ArtistKind,
                _ => "all"
            },
            request.Source,
            request.Identifier,
            cancellationToken);
        _runSignal.Request();
        return new WatchlistTriggerResult(
            requestAccepted ? WatchlistTriggerStatus.Accepted : WatchlistTriggerStatus.Coalesced);
    }

    private bool IsWatchlistEnabled()
    {
        using var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>().LoadSettings().WatchEnabled;
    }

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

    public WatchlistRuntimeHealth GetRuntimeHealth()
    {
        lock (_runtimeHealthGate)
        {
            return _runtimeHealth with { TriggerPending = _runSignal.IsPending };
        }
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

    public async Task ResetSchedulerStateAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        await SaveSchedulerStateAsync(
            repository,
            activeSource: null,
            activeSourceId: null,
            activeStartedUtc: null,
            lastProgressUtc: DateTimeOffset.UtcNow,
            zeroQueueStreak: 0,
            cancellationToken);
    }

    public async Task ResetSourceCircuitAsync(string source, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        await repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                PlaylistWatchType,
                NormalizeSource(source),
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                Fingerprint: null,
                FailureCount: 0),
            cancellationToken);
    }

    public async Task<WatchlistRuntimeResetResult> ResetRuntimeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _runtimeResetRequested, 1);
        lock (_activeCycleGate)
        {
            _activeCycleCancellation?.Cancel();
        }

        var acquired = false;
        try
        {
            await _cycleGate.WaitAsync(cancellationToken);
            acquired = true;
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            var cleanup = await repository.ClearWatchlistRuntimeAsync(cancellationToken);

            _consecutiveFailures.Clear();
            _nextAllowedRun.Clear();
            _lastRun.Clear();
            _artistRoundRobinIndex = 0;
            _lastDestinationRepairUtc = DateTimeOffset.MinValue;
            _lastIdentityIndexRefreshUtc = DateTimeOffset.MinValue;
            UpdateRuntimeHealth(_ => new WatchlistRuntimeHealth(false, false, null, DateTimeOffset.UtcNow, null, 0));

            var triggerStatus = WatchlistTriggerStatus.Disabled;
            if (IsWatchlistEnabled())
            {
                var accepted = await repository.EnqueueWatchlistReconciliationRequestAsync(
                    "all",
                    string.Empty,
                    string.Empty,
                    cancellationToken);
                _runSignal.Request();
                triggerStatus = accepted ? WatchlistTriggerStatus.Accepted : WatchlistTriggerStatus.Coalesced;
            }

            return new WatchlistRuntimeResetResult(cleanup, triggerStatus);
        }
        finally
        {
            Interlocked.Exchange(ref _runtimeResetRequested, 0);
            if (acquired)
            {
                _cycleGate.Release();
            }
        }
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Watch cycle entrypoint intentionally centralizes lock, failure handling, and lifecycle semantics.")]
    private Task RunOnceAsync(CancellationToken stoppingToken)
        => RunTriggeredOnceAsync(WatchlistWakeReason.ScheduledRefresh, stoppingToken);

    private async Task RunTriggeredOnceAsync(
        WatchlistWakeReason wakeReason,
        CancellationToken stoppingToken)
    {
        await ExecuteRunAsync(wakeReason, stoppingToken);
    }

    private async Task ExecuteRunAsync(WatchlistWakeReason wakeReason, CancellationToken stoppingToken)
    {
        await _cycleGate.WaitAsync(stoppingToken);
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_activeCycleGate)
        {
            _activeCycleCancellation = cycleCancellation;
        }
        if (Volatile.Read(ref _runtimeResetRequested) != 0)
        {
            cycleCancellation.Cancel();
        }
        UpdateRuntimeHealth(health => health with
        {
            IsRunning = true,
            LastCycleStartedUtc = DateTimeOffset.UtcNow,
            LastAdmissionBlockReason = null,
            LastRecoveredClaimCount = 0
        });
        try
        {
            await RunOneWatchCycleAsync(wakeReason, cycleCancellation.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Watchlist cycle canceled for runtime reset.");
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
            lock (_activeCycleGate)
            {
                if (ReferenceEquals(_activeCycleCancellation, cycleCancellation))
                {
                    _activeCycleCancellation = null;
                }
            }
            UpdateRuntimeHealth(health => health with
            {
                IsRunning = false,
                LastCycleCompletedUtc = DateTimeOffset.UtcNow
            });
            _cycleGate.Release();
        }
    }

    private async Task RunOneWatchCycleAsync(
        WatchlistWakeReason wakeReason,
        CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var settings = settingsService.LoadSettings();
        if (!settings.WatchEnabled)
        {
            var disabledRepository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (disabledRepository.IsConfigured)
            {
                var cleanup = await disabledRepository.ClearWatchlistRuntimeAsync(stoppingToken);
                _consecutiveFailures.Clear();
                _nextAllowedRun.Clear();
                _lastRun.Clear();
                if (_logger.IsEnabled(LogLevel.Information)
                    && (cleanup.ReconciliationRequestsDeleted > 0
                        || cleanup.SyncJobsDeleted > 0
                        || cleanup.FinalizationOutboxDeleted > 0
                        || cleanup.ClaimsDeleted > 0
                        || cleanup.SchedulerRowsDeleted > 0
                        || cleanup.SourceCircuitsDeleted > 0
                        || cleanup.PlaylistStatesDeleted > 0
                        || cleanup.ArtistStatesDeleted > 0))
                {
                    _logger.LogInformation(
                        "Watchlist disabled cleanup applied: reconciliationRequests={ReconciliationRequests}, syncJobs={SyncJobs}, finalizationOutbox={FinalizationOutbox}, claims={Claims}, schedulerRows={SchedulerRows}, sourceCircuits={SourceCircuits}, playlistStates={PlaylistStates}, artistStates={ArtistStates}.",
                        cleanup.ReconciliationRequestsDeleted,
                        cleanup.SyncJobsDeleted,
                        cleanup.FinalizationOutboxDeleted,
                        cleanup.ClaimsDeleted,
                        cleanup.SchedulerRowsDeleted,
                        cleanup.SourceCircuitsDeleted,
                        cleanup.PlaylistStatesDeleted,
                        cleanup.ArtistStatesDeleted);
                }
            }
            _logger.LogDebug("Watchlist disabled in settings.");
            return;
        }

        var queueAdmission = scope.ServiceProvider.GetRequiredService<WatchlistQueueAdmissionService>();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            _logger.LogDebug("Watchlist skipped - library DB not configured.");
            return;
        }
        var staleWorkRecovered = await repository.RecoverStaleWatchlistWorkAsync(stoppingToken);
        if (staleWorkRecovered > 0)
        {
            _logger.LogWarning("Recovered {Count} Watchlist items whose persisted execution deadlines expired.", staleWorkRecovered);
        }
        var playlistReconciler = scope.ServiceProvider.GetRequiredService<PlaylistWatchReconciler>();
        var recoveredClaims = await playlistReconciler.RecoverInvalidPendingWatchClaimsAsync(stoppingToken);
        UpdateRuntimeHealth(health => health with { LastRecoveredClaimCount = recoveredClaims });
        var coordinatorWork = scope.ServiceProvider.GetService<WatchlistPostDownloadSyncService>();
        if (coordinatorWork != null)
        {
            await coordinatorWork.ProcessFinalizationWorkAsync(finalizationLimit: 25, stoppingToken);
        }
        var pendingRequestCount = await repository.GetWatchlistReconciliationRequestCountAsync(stoppingToken);
        UpdateRuntimeHealth(health => health with { PendingReconciliationRequests = pendingRequestCount });

        var shouldRunSourceRefresh =
            wakeReason.HasFlag(WatchlistWakeReason.ScheduledRefresh)
            || pendingRequestCount > 0;
        if (shouldRunSourceRefresh)
        {
            var queueBudget = Math.Max(1, settings.WatchMaxItemsPerRun);
            var queueAdmissionToken = queueAdmission.BeginRun(queueBudget);
            try
            {
                var reconciliationRequests = await repository.ClaimDueWatchlistReconciliationRequestsAsync(
                    1000,
                    TimeSpan.FromMinutes(15),
                    _reconciliationLeaseOwner,
                    stoppingToken);
                using var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var leaseRenewal = RenewReconciliationLeasesAsync(repository, leaseRenewalCancellation.Token);
                try
                {
                    await RunWatchCycleCoreAsync(
                        scope.ServiceProvider,
                        settings,
                        queueAdmission,
                        reconciliationRequests,
                        stoppingToken);
                }
                finally
                {
                    leaseRenewalCancellation.Cancel();
                    try
                    {
                        await leaseRenewal;
                    }
                    catch (OperationCanceledException) when (leaseRenewalCancellation.IsCancellationRequested)
                    {
                        // Expected after the claimed reconciliation batch leaves processing.
                    }
                }
            }
            finally
            {
                if (queueAdmissionToken != 0)
                {
                    queueAdmission.EndRun(queueAdmissionToken);
                }
            }
        }

        if (coordinatorWork != null)
        {
            await coordinatorWork.ProcessTargetSyncWorkAsync(
                syncJobLimit: 15,
                stoppingToken);
        }
    }

    private async Task RepairLegacyApplePlaylistStorefrontsAsync(
        LibraryRepository repository,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var storefront = NormalizeAppleStorefront(settings.AppleMusic?.Storefront);
        if (storefront is null)
        {
            return;
        }

        var repairedSourceIds = await repository.BackfillLegacyApplePlaylistStorefrontAsync(
            storefront,
            cancellationToken);
        if (repairedSourceIds.Count == 0)
        {
            return;
        }

        await repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                PlaylistWatchType,
                "apple",
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                Fingerprint: null,
                FailureCount: 0),
            cancellationToken);

        foreach (var sourceId in repairedSourceIds)
        {
            var key = $"playlist:apple:{sourceId}";
            _nextAllowedRun.TryRemove(key, out _);
            _consecutiveFailures.TryRemove(key, out _);
            await repository.EnqueueWatchlistReconciliationRequestAsync(
                PlaylistKind,
                "apple",
                sourceId,
                cancellationToken);
        }

        _logger.LogInformation(
            "Repaired persisted Apple storefront for {PlaylistCount} legacy monitored playlist(s).",
            repairedSourceIds.Count);
    }

    private static string? NormalizeAppleStorefront(string? storefront)
    {
        var normalized = (storefront ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length is >= 2 and <= 5
               && normalized.All(character => char.IsAsciiLetter(character) || character == '-')
            ? normalized
            : null;
    }

    private async Task RenewReconciliationLeasesAsync(
        LibraryRepository repository,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            if (await repository.RenewClaimedWatchlistReconciliationRequestsAsync(
                    _reconciliationLeaseOwner,
                    TimeSpan.FromMinutes(15),
                    cancellationToken) == 0)
            {
                return;
            }
        }
    }

    private void UpdateRuntimeHealth(Func<WatchlistRuntimeHealth, WatchlistRuntimeHealth> update)
    {
        lock (_runtimeHealthGate)
        {
            _runtimeHealth = update(_runtimeHealth);
        }
    }

    private async Task RunWatchCycleCoreAsync(
        IServiceProvider serviceProvider,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        WatchlistQueueAdmissionService queueAdmission,
        IReadOnlyList<WatchlistReconciliationRequestDto> reconciliationRequests,
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
        await RepairLegacyApplePlaylistStorefrontsAsync(repository, settings, stoppingToken);
        await RefreshWatchlistIdentityIndexAsync(
            serviceProvider,
            profileResolutionService,
            stoppingToken);
        var playlistItems = BuildPlaylistWatchItems(await repository.GetPlaylistWatchlistAsync(stoppingToken));
        var artistItems = BuildArtistWatchItems(await repository.GetWatchlistAsync(stoppingToken));
        var hasGlobalRequest = reconciliationRequests.Any(request => request.Kind == "all");
        var requestedPlaylistKeys = hasGlobalRequest
            ? playlistItems.Select(item => item.Key).ToHashSet(StringComparer.Ordinal)
            : reconciliationRequests
                .Where(request => request.Kind == PlaylistKind)
                .Select(request => $"playlist:{NormalizeSource(request.Source)}:{request.Identifier}")
                .ToHashSet(StringComparer.Ordinal);
        var requestedArtistIds = hasGlobalRequest
            ? artistItems
                .Where(item => item.Artist is not null)
                .Select(item => item.Artist!.ArtistId)
                .ToHashSet()
            : reconciliationRequests
                .Where(request => request.Kind == ArtistKind)
                .Select(request => long.TryParse(request.Identifier, out var artistId) ? artistId : 0)
                .Where(static artistId => artistId > 0)
                .ToHashSet();
        var allItems = BuildCombinedWatchItems(playlistItems, artistItems);
        if (allItems.Count == 0)
        {
            CleanupStaleState(Array.Empty<WatchItem>());
            await repository.CompleteClaimedWatchlistReconciliationRequestsAsync(
                reconciliationRequests,
                _reconciliationLeaseOwner,
                stoppingToken);
            var remainingWhenEmpty = await repository.GetWatchlistReconciliationRequestCountAsync(stoppingToken);
            UpdateRuntimeHealth(health => health with { PendingReconciliationRequests = remainingWhenEmpty });
            return;
        }

        CleanupStaleState(allItems);
        await SeedPersistedLastRunsAsync(allItems, repository, stoppingToken);
        var queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        var orchestrationService = serviceProvider.GetService<DownloadOrchestrationService>();
        var queueGate = orchestrationService is null
            ? await queueAdmission.EvaluateQueueGateAsync(queueRepository, stoppingToken)
            : await queueAdmission.EvaluateQueueGateAsync(
                queueRepository,
                orchestrationService,
                stoppingToken);
        if (!queueGate.Allowed)
        {
            UpdateRuntimeHealth(health => health with { LastAdmissionBlockReason = queueGate.Message });
            _logger.LogInformation(
                "Watchlist download admission deferred while source metadata and target synchronization continue: {Reason}",
                queueGate.Message);
            await ProcessPlaylistWatchItemsAsync(
                playlistItems,
                settings,
                repository,
                serviceProvider,
                queueAdmission,
                requestedPlaylistKeys,
                PlaylistReconciliationMode.SyncOnly,
                stoppingToken);
            await repository.CompleteClaimedWatchlistReconciliationRequestsAsync(
                reconciliationRequests,
                _reconciliationLeaseOwner,
                stoppingToken);
            var remainingWhenDeferred = await repository.GetWatchlistReconciliationRequestCountAsync(stoppingToken);
            UpdateRuntimeHealth(health => health with { PendingReconciliationRequests = remainingWhenDeferred });
            return;
        }

        var playlistRunResult = await ProcessPlaylistWatchItemsAsync(
            playlistItems,
            settings,
            repository,
            serviceProvider,
            queueAdmission,
            requestedPlaylistKeys,
            PlaylistReconciliationMode.SyncAndQueue,
            stoppingToken);
        if (playlistRunResult.AbortedRun)
        {
            return;
        }

        var processedArtistIds = await ProcessArtistWatchItemsAsync(
            artistItems,
            settings,
            serviceProvider,
            queueAdmission,
            requestedArtistIds,
            stoppingToken);
        if (hasGlobalRequest)
        {
            foreach (var item in playlistItems.Where(item => !playlistRunResult.ProcessedKeys.Contains(item.Key)))
            {
                await repository.EnqueueWatchlistReconciliationRequestAsync(
                    PlaylistKind,
                    item.Playlist?.Source,
                    item.Playlist?.SourceId,
                    stoppingToken);
            }

            foreach (var item in artistItems.Where(item =>
                         item.Artist is not null
                         && !processedArtistIds.Contains(item.Artist.ArtistId)))
            {
                await repository.EnqueueWatchlistReconciliationRequestAsync(
                    ArtistKind,
                    "artist",
                    item.Artist!.ArtistId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    stoppingToken);
            }
        }

        var completedRequests = reconciliationRequests.Where(request =>
                request.Kind == "all"
                || (request.Kind == PlaylistKind
                    ? !playlistItems.Any(item => string.Equals(
                          item.Key,
                          $"playlist:{NormalizeSource(request.Source)}:{request.Identifier}",
                          StringComparison.Ordinal))
                      || playlistRunResult.ProcessedKeys.Contains($"playlist:{NormalizeSource(request.Source)}:{request.Identifier}")
                    : request.Kind != ArtistKind
                      || !long.TryParse(request.Identifier, out var artistId)
                      || !artistItems.Any(item => item.Artist?.ArtistId == artistId)
                      || processedArtistIds.Contains(artistId))).ToList();
        await repository.CompleteClaimedWatchlistReconciliationRequestsAsync(
            completedRequests,
            _reconciliationLeaseOwner,
            stoppingToken);
        var retryRequests = reconciliationRequests.Except(completedRequests).ToList();
        await repository.RetryClaimedWatchlistReconciliationRequestsAsync(
            retryRequests,
            _reconciliationLeaseOwner,
            "Reconciliation did not reach a successful terminal outcome.",
            stoppingToken);
        var remainingRequests = await repository.GetWatchlistReconciliationRequestCountAsync(stoppingToken);
        UpdateRuntimeHealth(health => health with { PendingReconciliationRequests = remainingRequests });
    }

    private async Task RefreshWatchlistIdentityIndexAsync(
        IServiceProvider serviceProvider,
        AutoTagProfileResolutionService profileResolutionService,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastIdentityIndexRefreshUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        var ingestionService = serviceProvider.GetService<KnownLibraryFileIngestionService>();
        if (ingestionService is null)
        {
            return;
        }

        var folderIds = await ResolveWatchlistDestinationFolderIdsAsync(
            profileResolutionService,
            cancellationToken);
        if (folderIds.Count == 0)
        {
            return;
        }

        var repository = serviceProvider.GetRequiredService<LibraryRepository>();
        var folders = (await repository.GetFoldersAsync(cancellationToken))
            .Where(folder => folder.Enabled && folderIds.Contains(folder.Id) && Directory.Exists(folder.RootPath))
            .ToList();
        var missingFilesByFolder = new Dictionary<long, List<string>>();
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexedFiles = await repository.GetLocalScanFileStatesAsync(folder.Id, cancellationToken);
            try
            {
                var missingFiles = Directory
                    .EnumerateFiles(folder.RootPath, "*", SearchOption.AllDirectories)
                    .Where(KnownLibraryFilePathSet.IsExistingAudioFile)
                    .Select(Path.GetFullPath)
                    .Where(path => !indexedFiles.ContainsKey(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (missingFiles.Count > 0)
                {
                    missingFilesByFolder[folder.Id] = missingFiles;
                }
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Watchlist identity index inspection failed for folder {FolderId}.", folder.Id);
            }
        }

        if (missingFilesByFolder.Count > 0)
        {
            await ingestionService.IngestAndVerifyAsync(missingFilesByFolder, cancellationToken);
        }
        _lastIdentityIndexRefreshUtc = DateTimeOffset.UtcNow;
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
        WatchlistQueueAdmissionService? queueAdmission,
        IReadOnlySet<string> requestedPlaylistKeys,
        PlaylistReconciliationMode mode,
        CancellationToken stoppingToken)
    {
        var runStartedUtc = DateTimeOffset.UtcNow;
        if (playlistItems.Count == 0)
        {
            return new PlaylistRunResult(AbortedRun: false, new HashSet<string>(StringComparer.Ordinal));
        }

        var targetedItems = mode == PlaylistReconciliationMode.SyncAndQueue
            ? playlistItems
                .Where(item => requestedPlaylistKeys.Contains(item.Key))
                .ToList()
            : new List<WatchItem>();
        var targetedRun = targetedItems.Count > 0;
        var scheduledItems = targetedRun ? targetedItems : playlistItems;
        if (scheduledItems.Count == 0)
        {
            return new PlaylistRunResult(AbortedRun: false, new HashSet<string>(StringComparer.Ordinal));
        }

        var processedKeys = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;
        var succeeded = 0;
        var failed = 0;
        var skippedByBackoff = 0;
        var skippedByDelayWindow = 0;
        var skippedByLockBusy = 0;
        foreach (var activeItem in scheduledItems)
        {
            stoppingToken.ThrowIfCancellationRequested();
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
                    WatchlistPlaylistState.CircuitOpen,
                    string.IsNullOrWhiteSpace(openCircuit.Reason) ? "Source circuit breaker open." : openCircuit.Reason,
                    openUntilUtc,
                    _consecutiveFailures.TryGetValue(activeItem.Key, out var circuitFailures) ? circuitFailures : 0,
                    stoppingToken);
                continue;
            }

            var eligibility = targetedRun ? WatchItemEligibility.Eligible : GetEligibility(activeItem, settings);
            switch (eligibility)
            {
                case WatchItemEligibility.Backoff:
                    await PersistPlaylistSchedulerStateAsync(
                        activeItem,
                        serviceProvider,
                        WatchlistPlaylistState.Backoff,
                        "Waiting for backoff window before retry.",
                        _nextAllowedRun.TryGetValue(activeItem.Key, out var nextAllowedUtc) ? nextAllowedUtc : null,
                        _consecutiveFailures.TryGetValue(activeItem.Key, out var backoffFailures) ? backoffFailures : null,
                        stoppingToken);
                    skippedByBackoff++;
                    continue;
                case WatchItemEligibility.DelayWindow:
                    await PersistPlaylistSchedulerStateAsync(
                        activeItem,
                        serviceProvider,
                        WatchlistPlaylistState.Pending,
                        "Waiting for next scheduled interval.",
                        null,
                        _consecutiveFailures.TryGetValue(activeItem.Key, out var pendingFailures) ? pendingFailures : 0,
                        stoppingToken);
                    skippedByDelayWindow++;
                    continue;
            }

            var execution = await TryProcessItemAsync(activeItem, settings, serviceProvider, stoppingToken, mode);
            if (execution.Outcome == WatchItemRunOutcome.LockBusy)
            {
                skippedByLockBusy++;
                continue;
            }

            var targetSync = serviceProvider.GetService<WatchlistPostDownloadSyncService>();
            if (targetSync is not null)
            {
                await targetSync.ProcessTargetSyncWorkAsync(
                    syncJobLimit: 1,
                    stoppingToken);
            }

            processed++;
            if (execution.Outcome == WatchItemRunOutcome.Success)
            {
                processedKeys.Add(activeItem.Key);
                succeeded++;
            }
            else
            {
                failed++;
            }

            var remainingBudget = queueAdmission?.GetRemaining() ?? int.MaxValue;
            var playlistResult = execution.PlaylistResult;
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
                    activeSource: null,
                    activeSourceId: null,
                    activeStartedUtc: null,
                    lastProgressUtc: DateTimeOffset.UtcNow,
                    zeroQueueStreak: 0,
                    stoppingToken);
                continue;
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
                    activeSource: null,
                    activeSourceId: null,
                    activeStartedUtc: null,
                    lastProgressUtc: DateTimeOffset.UtcNow,
                    zeroQueueStreak: 0,
                    stoppingToken);
                continue;
            }

            var decision = ResolvePlaylistAdvanceDecision(playlistResult, remainingBudget);
            var queueProgressed = queuedThisAttempt > 0;
            var zeroQueueStreak = 0;
            DateTimeOffset? lastProgressUtc = null;
            if (!queueProgressed)
            {
                var schedulerState = await repository.GetWatchlistSchedulerStateAsync(PlaylistWatchType, stoppingToken);
                zeroQueueStreak = (schedulerState?.ZeroQueueStreak ?? 0) + 1;
                lastProgressUtc = schedulerState?.LastProgressUtc;
                await SaveSchedulerStateAsync(
                    repository,
                    activeSource: null,
                    activeSourceId: null,
                    activeStartedUtc: null,
                    lastProgressUtc,
                    zeroQueueStreak,
                    stoppingToken);
            }

            await SaveSchedulerStateAsync(
                repository,
                activeSource: null,
                activeSourceId: null,
                activeStartedUtc: null,
                queueProgressed ? DateTimeOffset.UtcNow : lastProgressUtc,
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

            if (decision == PlaylistAdvanceDecision.StopRunClearActive)
            {
                break;
            }
        }

        var elapsedMs = (DateTimeOffset.UtcNow - runStartedUtc).TotalMilliseconds;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist playlist run summary: total={TotalItems}, processed={Processed}, ok={Succeeded}, failed={Failed}, skipBackoff={SkippedBackoff}, skipCooldown={SkippedCooldown}, skipLock={SkippedLock}, elapsedMs={ElapsedMs:0}",
                playlistItems.Count,
                processed,
                succeeded,
                failed,
                skippedByBackoff,
                skippedByDelayWindow,
                skippedByLockBusy,
                elapsedMs);
        }

        return new PlaylistRunResult(AbortedRun: false, processedKeys);
    }

    private static PlaylistAdvanceDecision ResolvePlaylistAdvanceDecision(
        PlaylistReconciliationResult? result,
        int remainingRunBudget)
    {
        if (result == null)
        {
            return PlaylistAdvanceDecision.Advance;
        }

        if (remainingRunBudget <= 0)
        {
            return PlaylistAdvanceDecision.StopRunClearActive;
        }

        if (IsBlockingPlaylistStopReason(result.QueueStopReason))
        {
            return PlaylistAdvanceDecision.StopRunClearActive;
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
                WatchQueueStopReason.DownloadGate.ToString(),
                StringComparison.Ordinal)
            || string.Equals(
                queueStopReason,
                WatchQueueStopReason.TrackDeferred.ToString(),
                StringComparison.Ordinal);

    private async Task<IReadOnlySet<long>> ProcessArtistWatchItemsAsync(
        IReadOnlyList<WatchItem> artistItems,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        WatchlistQueueAdmissionService? queueAdmission,
        IReadOnlySet<long> requestedArtistIds,
        CancellationToken stoppingToken)
    {
        if (artistItems.Count == 0)
        {
            return new HashSet<long>();
        }

        var targetedItems = artistItems
            .Where(item => item.Artist != null && requestedArtistIds.Contains(item.Artist.ArtistId))
            .ToList();
        var targetedRun = targetedItems.Count > 0;
        var scheduledItems = targetedRun ? targetedItems : artistItems;
        var startIndex = targetedRun ? 0 : _artistRoundRobinIndex % scheduledItems.Count;
        var visited = 0;
        var processedArtistIds = new HashSet<long>();
        var lastVisitedIndex = -1;
        while (visited < scheduledItems.Count)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if ((queueAdmission?.GetRemaining() ?? int.MaxValue) <= 0)
            {
                break;
            }

            var index = (startIndex + visited) % scheduledItems.Count;
            visited++;
            lastVisitedIndex = index;
            var item = scheduledItems[index];
            var eligibility = targetedRun ? WatchItemEligibility.Eligible : GetEligibility(item, settings);
            if (eligibility != WatchItemEligibility.Eligible)
            {
                continue;
            }

            var execution = await TryProcessItemAsync(item, settings, serviceProvider, stoppingToken);
            if (execution.Outcome == WatchItemRunOutcome.Success && item.Artist != null)
            {
                processedArtistIds.Add(item.Artist.ArtistId);
            }
        }

        if (!targetedRun && artistItems.Count > 0)
        {
            _artistRoundRobinIndex = lastVisitedIndex >= 0
                ? (lastVisitedIndex + 1) % artistItems.Count
                : (startIndex + 1) % artistItems.Count;
        }
        return processedArtistIds;
    }

    private async Task<WatchItemExecutionOutcome> TryProcessItemAsync(
        WatchItem item,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken,
        PlaylistReconciliationMode mode = PlaylistReconciliationMode.SyncAndQueue)
    {
        var itemLock = _itemLocks.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
        if (!await itemLock.WaitAsync(0, stoppingToken))
        {
            return new WatchItemExecutionOutcome(WatchItemRunOutcome.LockBusy, null, false, null);
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var itemTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        itemTimeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await PersistArtistRunStateAsync(
                item,
                serviceProvider,
                "processing",
                null,
                null,
                _consecutiveFailures.GetValueOrDefault(item.Key),
                "reconciling",
                startedUtc.AddMinutes(15),
                stoppingToken);
            var playlistResult = await RunItemAsync(item, serviceProvider, itemTimeout.Token, mode);
            _lastRun[item.Key] = DateTimeOffset.UtcNow;
            _consecutiveFailures.TryRemove(item.Key, out _);
            _nextAllowedRun.TryRemove(item.Key, out _);
            await PersistArtistRunStateAsync(
                item,
                serviceProvider,
                "completed",
                null,
                null,
                0,
                "completed",
                null,
                stoppingToken);
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
        await PersistPlaylistSchedulerStateAsync(item, serviceProvider, WatchlistPlaylistState.Backoff, ex.Message, nextRunUtc, failures, cancellationToken);
        await PersistArtistRunStateAsync(
            item,
            serviceProvider,
            "backoff",
            ex.Message,
            nextRunUtc,
            failures,
            "backoff",
            null,
            cancellationToken);
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
        WatchlistPlaylistState status,
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
        var playlistReconciler = serviceProvider.GetService<PlaylistWatchReconciler>();
        if (repository == null || playlistReconciler == null || !repository.IsConfigured)
        {
            return;
        }

        var state = await repository.GetPlaylistWatchStateAsync(item.Playlist.Source, item.Playlist.SourceId, cancellationToken);
        if (state != null
            && string.Equals(state.LastRunStatus, WatchlistStateService.ToPersistedStatus(status), StringComparison.Ordinal)
            && string.Equals(state.LastRunMessage, message, StringComparison.Ordinal)
            && state.ConsecutiveFailures == consecutiveFailures
            && Nullable.Equals(state.NextAttemptUtc, nextAttemptUtc))
        {
            return;
        }

        await playlistReconciler.UpdatePlaylistStateAsync(
            item.Playlist.Source,
            item.Playlist.SourceId,
            state?.TrackCount ?? item.Playlist.TrackCount,
            state?.SnapshotId ?? item.Playlist.SnapshotId,
            status,
            message,
            nextAttemptUtc,
            consecutiveFailures,
            cancellationToken,
            touchLastChecked: false);
    }

    private static async Task PersistArtistRunStateAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        string status,
        string? message,
        DateTimeOffset? nextAttemptUtc,
        int consecutiveFailures,
        string phase,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken)
    {
        if (item.Kind != ArtistKind || item.Artist is null)
        {
            return;
        }

        var repository = serviceProvider.GetService<LibraryRepository>();
        if (repository?.IsConfigured != true)
        {
            return;
        }

        await repository.UpdateArtistWatchRunStateAsync(
            item.Artist.ArtistId,
            status,
            message,
            nextAttemptUtc,
            consecutiveFailures,
            phase,
            deadlineUtc,
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

            if (item.Kind == PlaylistKind && item.Playlist != null)
            {
                var state = await repository.GetPlaylistWatchStateAsync(
                    item.Playlist.Source,
                    item.Playlist.SourceId,
                    cancellationToken);
                if (state?.NextAttemptUtc is { } nextAttemptUtc && nextAttemptUtc > DateTimeOffset.UtcNow)
                {
                    _nextAllowedRun[item.Key] = nextAttemptUtc;
                }
                if (state?.ConsecutiveFailures is > 0)
                {
                    _consecutiveFailures[item.Key] = state.ConsecutiveFailures.Value;
                }
            }
            else if (item.Kind == ArtistKind && item.Artist != null)
            {
                var state = await repository.GetArtistWatchStateAsync(item.Artist.ArtistId, cancellationToken);
                if (state?.NextAttemptUtc is { } nextAttemptUtc && nextAttemptUtc > DateTimeOffset.UtcNow)
                {
                    _nextAllowedRun[item.Key] = nextAttemptUtc;
                }
                if (state?.ConsecutiveFailures is > 0)
                {
                    _consecutiveFailures[item.Key] = state.ConsecutiveFailures.Value;
                }
            }
        }
    }

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

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

    private static async Task<PlaylistReconciliationResult?> RunItemAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken,
        PlaylistReconciliationMode mode)
    {
        if (item.Kind == PlaylistKind && item.Playlist != null)
        {
            var watcher = serviceProvider.GetRequiredService<PlaylistWatchReconciler>();
            return await watcher.ReconcilePlaylistAsync(item.Playlist, stoppingToken, mode: mode);
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
    private sealed record PlaylistRunResult(bool AbortedRun, IReadOnlySet<string> ProcessedKeys);
    private sealed record WatchItemExecutionOutcome(
        WatchItemRunOutcome Outcome,
        PlaylistReconciliationResult? PlaylistResult,
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
