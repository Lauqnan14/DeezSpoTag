namespace DeezSpoTag.Web.Services;

public sealed class LibraryRecommendationAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan InitialWarmupDelay = TimeSpan.FromSeconds(45);

    private readonly LibraryRecommendationService _recommendationService;
    private readonly ILogger<LibraryRecommendationAutomationHostedService> _logger;

    public LibraryRecommendationAutomationHostedService(
        LibraryRecommendationService recommendationService,
        ILogger<LibraryRecommendationAutomationHostedService> logger)
    {
        _recommendationService = recommendationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (InitialWarmupDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(InitialWarmupDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await RefreshDailyRecommendationsAsync("startup", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextLocalMidnight(DateTimeOffset.Now);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RefreshDailyRecommendationsAsync("midnight", stoppingToken);
        }
    }

    private async Task RefreshDailyRecommendationsAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Refreshing library recommendations ({Reason}).", reason);
            await _recommendationService.RefreshDailyRecommendationsAsync(cancellationToken);
            _logger.LogInformation("Library recommendations refreshed ({Reason}).", reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library recommendation refresh failed ({Reason}).", reason);
        }
    }

    private static TimeSpan GetDelayUntilNextLocalMidnight(DateTimeOffset nowLocal)
    {
        var nextMidnight = new DateTimeOffset(
            nowLocal.Year,
            nowLocal.Month,
            nowLocal.Day,
            0,
            0,
            0,
            nowLocal.Offset).AddDays(1);

        var delay = nextMidnight - nowLocal;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
    }
}
