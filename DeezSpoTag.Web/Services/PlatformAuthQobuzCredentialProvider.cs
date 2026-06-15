using DeezSpoTag.Integrations.Qobuz;

namespace DeezSpoTag.Web.Services;

public sealed class PlatformAuthQobuzCredentialProvider : IQobuzCredentialProvider
{
    private const string DefaultAppId = "712109809";
    private readonly PlatformAuthService _platformAuthService;

    public PlatformAuthQobuzCredentialProvider(PlatformAuthService platformAuthService)
    {
        _platformAuthService = platformAuthService;
    }

    public async Task<QobuzOfficialCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await _platformAuthService.LoadAsync();
        var auth = state.Qobuz;
        return new QobuzOfficialCredentials(
            string.IsNullOrWhiteSpace(auth?.AppId) ? DefaultAppId : auth.AppId.Trim(),
            auth?.AuthToken?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(auth?.AppSecret)
                ? auth?.DownloadSecret?.Trim() ?? string.Empty
                : auth.AppSecret.Trim());
    }
}
