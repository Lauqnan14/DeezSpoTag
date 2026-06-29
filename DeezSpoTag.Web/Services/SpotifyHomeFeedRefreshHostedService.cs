using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyHomeFeedRefreshHostedService : BackgroundService
{
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(15);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _repository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpotifyHomeFeedRefreshHostedService> _logger;

    public SpotifyHomeFeedRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        BackgroundWorkCoordinator workCoordinator,
        DeezSpoTag.Services.Library.LibraryRepository repository,
        IConfiguration configuration,
        ILogger<SpotifyHomeFeedRefreshHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _workCoordinator = workCoordinator;
        _repository = repository;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "SpotifyHomeFeedRefresh"))
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
                    delay = TimeSpan.FromHours(Math.Clamp(settings.SpotifyHomeFeedAutoRefreshHours, 2, 24));
                    if (await _repository.TryClaimBackgroundJobAsync(
                            "spotify-home-feed-refresh",
                            delay,
                            DateTimeOffset.UtcNow,
                            stoppingToken))
                    {
                        var refreshService = scope.ServiceProvider.GetRequiredService<SpotifyHomeFeedRuntimeService>();
                        try
                        {
                            await _workCoordinator.RunHeavyWorkAsync(
                                token => refreshService.RefreshAsync(timeZone: null, token),
                                stoppingToken);
                            await _repository.CompleteBackgroundJobAsync(
                                "spotify-home-feed-refresh",
                                delay,
                                DateTimeOffset.UtcNow,
                                stoppingToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await _repository.FailBackgroundJobAsync(
                                "spotify-home-feed-refresh",
                                TimeSpan.FromMinutes(15),
                                DateTimeOffset.UtcNow,
                                CancellationToken.None);
                            throw new InvalidOperationException("Spotify home feed refresh failed.", ex);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
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
