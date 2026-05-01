namespace DeezSpoTag.Web.Services;

public sealed class SpotifyHomeFeedRefreshHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(15);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SpotifyHomeFeedRefreshHostedService> _logger;

    public SpotifyHomeFeedRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<SpotifyHomeFeedRefreshHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Settings.ISettingsService>();
                var settings = settingsService.LoadSettings();

                if (!settings.SpotifyHomeFeedCacheEnabled || !settings.SpotifyHomeFeedAutoRefreshEnabled)
                {
                    delay = DisabledPollInterval;
                }
                else
                {
                    var refreshService = scope.ServiceProvider.GetRequiredService<SpotifyHomeFeedRuntimeService>();
                    await refreshService.RefreshAsync(timeZone: null, stoppingToken);
                    delay = TimeSpan.FromHours(Math.Clamp(settings.SpotifyHomeFeedAutoRefreshHours, 2, 24));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Spotify home feed background refresh failed.");
                delay = TimeSpan.FromMinutes(15);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
