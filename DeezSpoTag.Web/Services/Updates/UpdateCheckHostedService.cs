namespace DeezSpoTag.Web.Services.Updates;

/// <summary>
/// Polls the configured branch for a newer release on a fixed interval.
/// </summary>
/// <remarks>
/// The poll is deliberately coarse. The sidebar and the manual button already ask
/// <see cref="UpdateCheckService"/> on demand, and that service owns the minimum-interval budget,
/// so this worker's only job is to keep the answer fresh for instances with no open browser and to
/// make sure a new release is announced even when nobody is looking at the app.
/// </remarks>
public sealed class UpdateCheckHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);

    private readonly UpdateCheckService _updateCheckService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UpdateCheckHostedService> _logger;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;

    public UpdateCheckHostedService(
        UpdateCheckService updateCheckService,
        IConfiguration configuration,
        ILogger<UpdateCheckHostedService> logger,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator)
    {
        _updateCheckService = updateCheckService;
        _configuration = configuration;
        _logger = logger;
        _workCoordinator = workCoordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "AppVersionCheck"))
        {
            return;
        }

        try
        {
            await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);
            if (!await SafeWaitForNextTickAsync(timer, stoppingToken))
            {
                return;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _workCoordinator.RunHeavyWorkAsync(
                _updateCheckService.GetStatusAsync,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Background update check failed.");
        }
    }

    private static async Task<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
