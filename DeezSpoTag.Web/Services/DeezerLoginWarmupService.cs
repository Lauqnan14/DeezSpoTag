using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;

namespace DeezSpoTag.Web.Services;

public sealed class DeezerLoginWarmupService : IHostedService
{
    private readonly ILogger<DeezerLoginWarmupService> _logger;
    private readonly DeezerClient _deezerClient;
    private readonly ILoginStorageService _loginStorage;

    public DeezerLoginWarmupService(
        ILogger<DeezerLoginWarmupService> logger,
        DeezerClient deezerClient,
        ILoginStorageService loginStorage)
    {
        _logger = logger;
        _deezerClient = deezerClient;
        _loginStorage = loginStorage;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_deezerClient.LoggedIn)
        {
            return;
        }

        try
        {
            var credentials = await _loginStorage.LoadLoginCredentialsAsync();
            if (string.IsNullOrWhiteSpace(credentials?.Arl))
            {
                return;
            }

            _logger.LogInformation("Warming up Deezer session from stored credentials.");
            DeezSpoTag.Web.Controllers.Api.DeezerStreamApiController.ClearPlaybackContextCache();
            await _deezerClient.LoginViaArlAsync(credentials.Arl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deezer session warmup failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
