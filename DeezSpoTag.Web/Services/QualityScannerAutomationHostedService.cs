namespace DeezSpoTag.Web.Services;

public sealed class QualityScannerAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private readonly QualityScannerService _qualityScannerService;
    private readonly ILogger<QualityScannerAutomationHostedService> _logger;

    public QualityScannerAutomationHostedService(
        QualityScannerService qualityScannerService,
        ILogger<QualityScannerAutomationHostedService> logger)
    {
        _qualityScannerService = qualityScannerService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ExecuteAutomationIterationAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task<bool> ExecuteAutomationIterationAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await _qualityScannerService.GetAutomationSettingsAsync(stoppingToken);
            await TryStartAutomationRunAsync(settings, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Quality scanner automation loop failed.");
        }

        return await DelayUntilNextPollAsync(stoppingToken);
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

    private static async Task<bool> DelayUntilNextPollAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(PollInterval, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
