using DeezSpoTag.Integrations.Tidal;

namespace DeezSpoTag.Web.Services;

public sealed class PlatformAuthTidalCredentialProvider : ITidalCredentialProvider
{
    private readonly PlatformAuthService _platformAuthService;

    public PlatformAuthTidalCredentialProvider(PlatformAuthService platformAuthService)
    {
        _platformAuthService = platformAuthService;
    }

    public async Task<TidalOfficialCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var auth = (await _platformAuthService.LoadAsync()).Tidal;
        return new TidalOfficialCredentials(
            auth?.ClientId?.Trim() ?? string.Empty,
            auth?.ClientSecret?.Trim() ?? string.Empty,
            auth?.AccessToken?.Trim() ?? string.Empty,
            auth?.RefreshToken?.Trim() ?? string.Empty,
            auth?.UserId?.Trim() ?? string.Empty,
            NormalizeCountryCode(auth?.CountryCode));
    }

    private static string NormalizeCountryCode(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 2 ? normalized : "US";
    }
}
