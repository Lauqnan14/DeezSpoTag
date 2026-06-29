namespace DeezSpoTag.Web.Services;

public sealed class LibraryRecommendationAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan PeriodicCatchupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(15);
    private const string JobKey = "library-recommendations";

    private readonly LibraryRecommendationService _recommendationService;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _repository;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LibraryRecommendationAutomationHostedService> _logger;

    public LibraryRecommendationAutomationHostedService(
        LibraryRecommendationService recommendationService,
        DeezSpoTag.Services.Library.LibraryRepository repository,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator,
        IConfiguration configuration,
        ILogger<LibraryRecommendationAutomationHostedService> logger)
    {
        _recommendationService = recommendationService;
        _repository = repository;
        _workCoordinator = workCoordinator;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "LibraryRecommendations"))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (await _repository.TryClaimBackgroundJobAsync(JobKey, PeriodicCatchupInterval, now, stoppingToken))
            {
                await RefreshDailyRecommendationsAsync("scheduled", stoppingToken);
            }
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshDailyRecommendationsAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Refreshing library recommendations ({Reason}).", reason);
            }
            await _workCoordinator.RunHeavyWorkAsync(
                token => _recommendationService.RefreshDailyRecommendationsAsync(reason, token),
                cancellationToken);
            await _repository.CompleteBackgroundJobAsync(JobKey, PeriodicCatchupInterval, DateTimeOffset.UtcNow, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Library recommendations refreshed ({Reason}).", reason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Library recommendation refresh timed out ({Reason}).", reason);
            await _repository.FailBackgroundJobAsync(JobKey, FailureRetryDelay, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Library recommendation refresh failed ({Reason}).", reason);
            await _repository.FailBackgroundJobAsync(JobKey, FailureRetryDelay, DateTimeOffset.UtcNow, CancellationToken.None);
        }
    }
}
