using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services;

public sealed class MelodayHostedService : BackgroundService
{
    private readonly MelodayService _melodayService;
    private readonly MelodayOptions _options;
    private readonly MelodaySettingsStore _settingsStore;
    private readonly ILogger<MelodayHostedService> _logger;
    private string? _lastPeriod;

    public MelodayHostedService(
        MelodayService melodayService,
        IOptions<MelodayOptions> options,
        ILogger<MelodayHostedService> logger,
        MelodaySettingsStore settingsStore)
    {
        _melodayService = melodayService;
        _options = options.Value;
        _logger = logger;
        _settingsStore = settingsStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var effective = await _settingsStore.LoadAsync(_options);
        var loggedDisabledState = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                effective = await _settingsStore.LoadAsync(_options);
                if (!effective.Enabled)
                {
                    if (!loggedDisabledState)
                    {
                        _logger.LogInformation("Meloday disabled; hosted service waiting for enable.");
                        loggedDisabledState = true;
                    }
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }
                loggedDisabledState = false;

                var period = MelodayService.GetCurrentPeriodName();
                if (!string.Equals(period, _lastPeriod, StringComparison.OrdinalIgnoreCase))
                {
                    var result = await _melodayService.RunAsync(stoppingToken);
                    _logger.LogInformation("Meloday update: {Message}", result.Message);
                    _lastPeriod = period;
                }
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
}
