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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "LibraryRecommendations")) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var targetTime = _timeProvider.GetLocalNow();
                await _workCoordinator.RunHeavyWorkAsync(
                    token => _recommendationService.ReconcileRecommendationGenerationAsync(targetTime, token), stoppingToken);
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
