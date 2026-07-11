using Microsoft.Extensions.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagStuckRecoveryHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultStaleWindow = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoTagStuckRecoveryHostedService> _logger;

    public AutoTagStuckRecoveryHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<AutoTagStuckRecoveryHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunRecoveryPassAsync(stoppingToken);

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunRecoveryPassAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("AutoTag:StuckRecovery:Enabled", true))
        {
            return;
        }

        var timeoutMinutes = Math.Clamp(
            _configuration.GetValue("AutoTag:StuckRecovery:TimeoutMinutes", (int)DefaultStaleWindow.TotalMinutes),
            2,
            24 * 60);
        var restart = _configuration.GetValue("AutoTag:StuckRecovery:AutoResume", true);

        try
        {
            await _serviceProvider.GetRequiredService<AutoTagService>().RecoverStuckJobsAsync(
                TimeSpan.FromMinutes(timeoutMinutes),
                restart,
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag stuck recovery pass failed.");
        }
    }
}
