using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services;

public sealed class MelodayHostedService : BackgroundService
{
    private readonly MelodayService _melodayService;
    private readonly MelodayOptions _options;
    private readonly MelodaySettingsStore _settingsStore;
    private readonly IConfiguration _configuration;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _repository;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<MelodayHostedService> _logger;
    private string? _lastPeriod;

    public MelodayHostedService(
        MelodayService melodayService,
        IOptions<MelodayOptions> options,
        ILogger<MelodayHostedService> logger,
        MelodaySettingsStore settingsStore,
        DeezSpoTag.Services.Library.LibraryRepository repository,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator,
        IConfiguration configuration)
    {
        _melodayService = melodayService;
        _options = options.Value;
        _logger = logger;
        _settingsStore = settingsStore;
        _repository = repository;
        _workCoordinator = workCoordinator;
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
                    await _settingsStore.WaitForChangeAsync(stoppingToken);
                    continue;
                }
                loggedDisabledState = false;

                await RunCurrentPeriodUpdateIfNeededAsync(effective, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Meloday update failed.");
            }

            try
            {
                await _settingsStore.WaitForChangeAsync(
                    TimeSpan.FromMinutes(Math.Max(5, effective.UpdateIntervalMinutes)),
                    stoppingToken);
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

    private async Task RunCurrentPeriodUpdateIfNeededAsync(MelodayOptions effective, CancellationToken stoppingToken)
    {
        var period = MelodayService.GetCurrentPeriodName();
        if (string.Equals(period, _lastPeriod, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, effective.UpdateIntervalMinutes));
        var jobKey = $"meloday:{period}";
        if (!await _repository.TryClaimBackgroundJobAsync(jobKey, interval, DateTimeOffset.UtcNow, stoppingToken))
        {
            _lastPeriod = period;
            return;
        }

        MelodayRunResult? result = null;
        await _workCoordinator.RunHeavyWorkAsync(
            async token => result = await _melodayService.RunAsync(refreshHistory: true, token),
            stoppingToken);
        if (result?.Success == true)
        {
            await _repository.CompleteBackgroundJobAsync(jobKey, interval, DateTimeOffset.UtcNow, stoppingToken);
        }
        else
        {
            await _repository.FailBackgroundJobAsync(jobKey, TimeSpan.FromMinutes(15), DateTimeOffset.UtcNow, stoppingToken);
        }
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Meloday update: {Message}", result?.Message);
        }
        _lastPeriod = period;
    }
}
