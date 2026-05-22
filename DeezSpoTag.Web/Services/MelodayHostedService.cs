using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services;

public sealed class MelodayHostedService : BackgroundService
{
    private readonly MelodayService _melodayService;
    private readonly MelodayOptions _options;
    private readonly MelodaySettingsStore _settingsStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MelodayHostedService> _logger;
    private string? _lastPeriod;

    public MelodayHostedService(
        MelodayService melodayService,
        IOptions<MelodayOptions> options,
        ILogger<MelodayHostedService> logger,
        MelodaySettingsStore settingsStore,
        IConfiguration configuration)
    {
        _melodayService = melodayService;
        _options = options.Value;
        _logger = logger;
        _settingsStore = settingsStore;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "Meloday"))
        {
            return;
        }

        var effective = await _settingsStore.LoadAsync(_options);
        var loggedDisabledState = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                effective = await _settingsStore.LoadAsync(_options);
                if (!effective.Enabled)
                {
                    loggedDisabledState = LogDisabledStateOnce(loggedDisabledState);
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }
                loggedDisabledState = false;

                await RunCurrentPeriodUpdateIfNeededAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Meloday update failed.");
            }

            var delay = TimeSpan.FromMinutes(Math.Max(5, effective.UpdateIntervalMinutes));
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private bool LogDisabledStateOnce(bool loggedDisabledState)
    {
        if (loggedDisabledState)
        {
            return true;
        }

        _logger.LogInformation("Meloday disabled; hosted service waiting for enable.");
        return true;
    }

    private async Task RunCurrentPeriodUpdateIfNeededAsync(CancellationToken stoppingToken)
    {
        var period = MelodayService.GetCurrentPeriodName();
        if (string.Equals(period, _lastPeriod, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var result = await _melodayService.RunAsync(stoppingToken);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Meloday update: {Message}", result.Message);
        }
        _lastPeriod = period;
    }
}
