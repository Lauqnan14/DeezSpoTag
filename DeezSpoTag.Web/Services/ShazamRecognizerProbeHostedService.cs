namespace DeezSpoTag.Web.Services;

/// <summary>
/// Keeps the Shazam recognizer availability probe warm.
///
/// Probing spawns a Python interpreter and can escalate to a multi-minute dependency
/// bootstrap. That used to run inline in whichever request happened to observe a stale
/// probe, under a lock that blocked every other Shazam request behind it. Doing it here
/// means a live capture reads an already-computed answer instead of paying for one.
/// </summary>
public sealed class ShazamRecognizerProbeHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly ShazamRecognitionService _recognitionService;
    private readonly ILogger<ShazamRecognizerProbeHostedService> _logger;

    public ShazamRecognizerProbeHostedService(
        ShazamRecognitionService recognitionService,
        ILogger<ShazamRecognizerProbeHostedService> logger)
    {
        _recognitionService = recognitionService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        do
        {
            try
            {
                // The service owns the time-to-live policy; this worker only ticks.
                if (_recognitionService.IsRuntimeProbeStale(DateTimeOffset.UtcNow))
                {
                    await _recognitionService.RefreshRuntimeProbeAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Shazam recognizer runtime probe refresh failed.");
            }
        }
        while (await SafeWaitForNextTickAsync(timer, stoppingToken));
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
