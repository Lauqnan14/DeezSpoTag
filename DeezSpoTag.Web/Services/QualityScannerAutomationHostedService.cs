using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class QualityScannerAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan ActiveCheckMaximum = TimeSpan.FromMinutes(15);
    private readonly QualityScannerService _qualityScannerService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QualityScannerAutomationHostedService> _logger;

    public QualityScannerAutomationHostedService(
        QualityScannerService qualityScannerService,
        IConfiguration configuration,
        ILogger<QualityScannerAutomationHostedService> logger)
    {
        _qualityScannerService = qualityScannerService;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "QualityScanner"))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            QualityScannerAutomationSettingsDto settings;
            try
            {
                settings = await _qualityScannerService.GetAutomationSettingsAsync(stoppingToken);
                if (!settings.Enabled)
                {
                    await _qualityScannerService.WaitForAutomationSettingsChangeAsync(stoppingToken);
                    continue;
                }

                await TryStartAutomationRunAsync(settings, stoppingToken);
                await _qualityScannerService.WaitForAutomationSettingsChangeAsync(
                    GetNextCheckDelay(settings),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Quality scanner automation loop failed.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private static TimeSpan GetNextCheckDelay(QualityScannerAutomationSettingsDto settings)
    {
        var baseline = settings.LastFinishedUtc ?? settings.LastStartedUtc;
        if (baseline is null)
        {
            return TimeSpan.FromMinutes(1);
        }
        var due = baseline.Value.AddMinutes(Math.Clamp(settings.IntervalMinutes, 15, 10080));
        var remaining = due - DateTimeOffset.UtcNow;
        return remaining <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(1)
            : remaining < ActiveCheckMaximum ? remaining : ActiveCheckMaximum;
    }

    private async Task TryStartAutomationRunAsync(
        DeezSpoTag.Services.Library.QualityScannerAutomationSettingsDto settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled || !ShouldRunNow(settings))
        {
            return;
        }

        var state = _qualityScannerService.GetState();
        if (string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var started = await _qualityScannerService.StartAsync(
            new QualityScannerStartRequest
            {
                Scope = settings.Scope,
                FolderId = settings.FolderId,
                QueueAtmosAlternatives = settings.QueueAtmosAlternatives,
                CooldownMinutes = settings.CooldownMinutes,
                Trigger = "automation",
                MarkAutomationWindow = true
            },
            cancellationToken);
        if (!started)
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Quality scanner automation run started (scope={Scope}, folderId={FolderId}, queueAtmos={QueueAtmos}).",
                settings.Scope,
                settings.FolderId,
                settings.QueueAtmosAlternatives);
        }
    }

    private static bool ShouldRunNow(DeezSpoTag.Services.Library.QualityScannerAutomationSettingsDto settings)
    {
        var intervalMinutes = Math.Clamp(settings.IntervalMinutes, 15, 10080);
        var now = DateTimeOffset.UtcNow;
        var baseline = settings.LastFinishedUtc ?? settings.LastStartedUtc;
        if (baseline is null)
        {
            return true;
        }

        return now >= baseline.Value.AddMinutes(intervalMinutes);
    }
}
