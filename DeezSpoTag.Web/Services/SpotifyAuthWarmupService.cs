using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Services;

public class SpotifyAuthWarmupService : BackgroundService
{
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(15);
    private readonly SpotifyBlobService _blobService;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<SpotifyAuthWarmupService> _logger;

    public SpotifyAuthWarmupService(
        SpotifyBlobService blobService,
        BackgroundWorkCoordinator workCoordinator,
        ILogger<SpotifyAuthWarmupService> logger)
    {
        _blobService = blobService;
        _workCoordinator = workCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);

        var attempt = 0;
        var delaySeconds = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(WarmupTimeout);
                await _blobService.EnsureSpotifyAuthEnvironmentAsync(timeout.Token);
                _logger.LogInformation("Spotify auth environment warmup completed.");
                return;
            }
            catch (OperationCanceledException ex) when (!stoppingToken.IsCancellationRequested)
            {
                attempt++;
                _logger.LogWarning(ex, "Spotify auth warmup timed out after {TimeoutSeconds}s (attempt {Attempt}). Retrying in {Delay}s.", WarmupTimeout.TotalSeconds, attempt, delaySeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempt++;
                _logger.LogWarning(ex, "Spotify auth warmup failed (attempt {Attempt}). Retrying in {Delay}s.", attempt, delaySeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            delaySeconds = Math.Min(delaySeconds * 2, 300);
        }
    }
}
