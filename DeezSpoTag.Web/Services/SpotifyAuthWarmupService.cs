namespace DeezSpoTag.Web.Services;

public class SpotifyAuthWarmupService : BackgroundService
{
    private readonly SpotifyBlobService _blobService;
    private readonly ILogger<SpotifyAuthWarmupService> _logger;

    public SpotifyAuthWarmupService(SpotifyBlobService blobService, ILogger<SpotifyAuthWarmupService> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        var delaySeconds = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _blobService.EnsureSpotifyAuthEnvironmentAsync(stoppingToken);
                _logger.LogInformation("Spotify auth environment warmup completed.");
                return;
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
