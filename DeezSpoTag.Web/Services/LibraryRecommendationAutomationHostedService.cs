namespace DeezSpoTag.Web.Services;

public sealed class LibraryRecommendationAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
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
        var midnightUtc = TimeZoneInfo.ConvertTimeToUtc(
            nowLocal.Date.AddDays(1), _timeProvider.LocalTimeZone);
        using var dayEnded = new CancellationTokenSource(
            midnightUtc - nowLocal.UtcDateTime, _timeProvider);
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
            // Release the old day's work and start the new day without a polling delay.
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "LibraryRecommendations")) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunCalendarDayAsync(
                    (targetTime, token) => _recommendationService.ReconcileRecommendationGenerationAsync(targetTime, token), stoppingToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Recommendation reconciliation failed; persisted work will be retried.");
            }
            try { await Task.Delay(GetReconciliationDelay(_timeProvider.GetLocalNow()), _timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
