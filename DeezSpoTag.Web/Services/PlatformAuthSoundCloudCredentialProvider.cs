using DeezSpoTag.Services.Download.SoundCloud;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

/// <summary>
///     Supplies the SoundCloud engine with the OAuth token held in the protected platform-auth store.
/// </summary>
/// <remarks>
///     <para>
///         This is the web host's replacement for the engine's default null provider, registered after
///         <c>AddSoundCloudDownloadEngine</c> so the later registration wins. A host without the protected store
///         keeps the null provider and public-only operation.
///     </para>
///     <para>
///         A read failure returns null rather than throwing. A SoundCloud download failing because the token
///         store had a bad moment would be reported as a provider failure and sent round the fallback ladder,
///         which is a misleading diagnosis; returning null degrades to public-only, which is accurate.
///     </para>
/// </remarks>
public sealed class PlatformAuthSoundCloudCredentialProvider : ISoundCloudCredentialProvider
{
    private readonly PlatformAuthService _platformAuthService;
    private readonly ILogger<PlatformAuthSoundCloudCredentialProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="PlatformAuthSoundCloudCredentialProvider" /> class.</summary>
    public PlatformAuthSoundCloudCredentialProvider(
        PlatformAuthService platformAuthService,
        ILogger<PlatformAuthSoundCloudCredentialProvider> logger)
    {
        _platformAuthService = platformAuthService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetOAuthTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var state = await _platformAuthService.LoadAsync();
            var token = state.SoundCloud?.OAuthToken;
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "Could not read the saved SoundCloud token; continuing with public-only access.");
            }

            return null;
        }
    }
}