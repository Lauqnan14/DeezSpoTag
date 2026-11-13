namespace DeezSpoTag.Web.Services;

public sealed class LibraryRecommendationAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    /// <summary>
    ///     Upper bound for publication ticks. Activation is cheap, so a DST shift or a suspended
    ///     clock only delays a publication by at most this long, and the current local date is
    ///     read at activation time rather than trusted from a computed boundary.
    /// </summary>
    private static readonly TimeSpan MaximumPublicationTick = TimeSpan.FromMinutes(15);
    private readonly LibraryRecommendationService _recommendationService;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LibraryRecommendationAutomationHostedService> _logger;
    private readonly TimeProvider _timeProvider;

    public LibraryRecommendationAutomationHostedService(
        LibraryRecommendationService recommendationService,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator,
        IConfiguration configuration,
        ILogger<LibraryRecommendationAutomationHostedService> logger,
        TimeProvider? timeProvider = null)
    {
        _recommendationService = recommendationService;
        _workCoordinator = workCoordinator;
        _configuration = configuration;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     Two coordinated loops share this worker and its shutdown cancellation:
    ///     a lightweight publication clock that activates ready prepared selections without ever
    ///     waiting for the heavy work gate, and the scheduling loop that runs weekly reconciliation
    ///     and advance preparation (the only heavy daily work) inside that gate.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "LibraryRecommendations")) return;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var publication = PublicationClockAsync(shutdown.Token);
        try
        {
            await SchedulingLoopAsync(shutdown.Token);
        }
        finally
        {
            shutdown.Cancel();
            try { await publication; }
            catch (OperationCanceledException) { /* Shutdown cancellation is expected. */ }
        }
    }

    private async Task PublicationClockAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _recommendationService.ActivateReadyDailyRecommendationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Daily publication tick failed; prepared data stays staged.");
            }
            try
            {
                var nowLocal = _timeProvider.GetLocalNow();
                var untilMidnight = nowLocal.Date.AddDays(1) - nowLocal.DateTime;
                await Task.Delay(untilMidnight < MaximumPublicationTick ? untilMidnight : MaximumPublicationTick,
                    _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static TimeSpan GetReconciliationDelay(DateTimeOffset nowLocal)
    {
        var untilMidnight = nowLocal.Date.AddDays(1) - nowLocal.DateTime;
        return untilMidnight < PollInterval ? untilMidnight : PollInterval;
    }

    private async Task<bool> RunCalendarDayAsync(
        Func<DateTimeOffset, CancellationToken, Task> reconcile,
        CancellationToken stoppingToken)
    {
        var nowLocal = _timeProvider.GetLocalNow();
        using var dayEnded = new CancellationTokenSource(
            NextScheduleBoundaryUtc(nowLocal) - _timeProvider.GetUtcNow(), _timeProvider);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, dayEnded.Token);
        try
        {
            await _workCoordinator.RunHeavyWorkAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                // Other background work may have held the slot since we queued.
                // Choose the calendar date when reconciliation actually starts.
                var targetTime = _timeProvider.GetLocalNow();
                return reconcile(targetTime, token);
            }, work.Token);
            return dayEnded.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (dayEnded.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // A schedule boundary re-evaluates immediately without a polling delay.
            return true;
        }
    }

    private async Task SchedulingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _recommendationService.IsDailyPreparationDueAsync(_timeProvider.GetLocalNow(), stoppingToken))
                {
                    // Heavy advance preparation; each folder's target date is chosen when the slot
                    // is actually acquired, so a date change never runs stale work. Preparation
                    // enforces its own end-of-target-day deadline and never blocks publication.
                    await _workCoordinator.RunHeavyWorkAsync(token =>
                        _recommendationService.PreparePendingDailyRecommendationsAsync(token), stoppingToken);
                    // A completed selection publishes as soon as it is ready, outside the heavy gate.
                    await _recommendationService.ActivateReadyDailyRecommendationsAsync(stoppingToken);
                    if (await _recommendationService.IsDailyPreparationDueAsync(_timeProvider.GetLocalNow(), stoppingToken))
                    {
                        // Bounded retry cadence while a folder's preparation is still incomplete.
                        await Task.Delay(PollInterval, _timeProvider, stoppingToken);
                    }
                    continue;
                }
                if (await RunCalendarDayAsync(
                    (targetTime, token) => _recommendationService.ReconcileRecommendationGenerationAsync(targetTime, token), stoppingToken))
                {
                    continue;
                }
                await Task.Delay(GetReconciliationDelay(_timeProvider.GetLocalNow()), _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Recommendation scheduling failed; persisted work will be retried.");
                try { await Task.Delay(PollInterval, _timeProvider, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal static DateTimeOffset NextScheduleBoundaryUtc(DateTimeOffset nowLocal)
    {
        var nextMidnightLocal = nowLocal.Date.AddDays(1);
        var preparationStart = TimeOnly.FromDateTime(nowLocal.DateTime) < LibraryRecommendationService.AutomaticPreparationStart
            ? nowLocal.Date.Add(LibraryRecommendationService.AutomaticPreparationStart.ToTimeSpan())
            : nowLocal.Date.AddDays(1).Add(LibraryRecommendationService.AutomaticPreparationStart.ToTimeSpan());
        var next = nextMidnightLocal <= preparationStart ? nextMidnightLocal : preparationStart;
        return new DateTimeOffset(next, nowLocal.Offset).ToUniversalTime();
    }
}
