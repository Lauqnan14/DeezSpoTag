using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Services.Download.Soulseek;

namespace DeezSpoTag.Web.Services;

/// <summary>
///     Reads the stored slskd connection details from the encrypted platform auth state.
/// </summary>
/// <remarks>
///     This mirrors the existing <c>PlatformAuthTidalCredentialProvider</c> and
///     <c>PlatformAuthQobuzCredentialProvider</c> pattern: the credential store lives in the web tier, and
///     a small provider bridges it to a service-layer contract so the Soulseek services never depend on
///     web types.
/// </remarks>
public sealed class PlatformAuthSoulseekCredentialProvider : ISoulseekCredentialProvider
{
    private readonly PlatformAuthService _platformAuthService;

    /// <summary>Initializes a new instance of the <see cref="PlatformAuthSoulseekCredentialProvider"/> class.</summary>
    public PlatformAuthSoulseekCredentialProvider(PlatformAuthService platformAuthService)
    {
        _platformAuthService = platformAuthService;
    }

    /// <inheritdoc />
    public async Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var auth = (await _platformAuthService.LoadAsync()).Soulseek;
        var baseUrl = auth?.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return new SlskdCredentials(baseUrl, auth!.ApiKey?.Trim() ?? string.Empty);
    }
}
