using DeezSpoTag.Core.Models.Soulseek;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Supplies the stored slskd connection details.
/// </summary>
/// <remarks>
///     The encrypted credential store lives in the web tier, so the implementation is supplied by
///     composition root. This keeps <c>DeezSpoTag.Services</c> free of any dependency on web types while
///     still letting the Soulseek services reach slskd.
/// </remarks>
public interface ISoulseekCredentialProvider
{
    /// <summary>
    ///     Reads the current slskd connection details.
    /// </summary>
    /// <returns>
    ///     The credentials, or <see langword="null"/> when Soulseek has not been configured. The result
    ///     may carry a base URL with no API key, because slskd can run without authentication.
    /// </returns>
    Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     A provider that reports Soulseek as unconfigured.
/// </summary>
/// <remarks>
///     Hosts that do not own the encrypted credential store - the workers host, which has no platform auth -
///     still register the Soulseek engine, because the engine processor is registered for every host. This
///     keeps that registration resolvable instead of throwing, and makes a Soulseek queue item fail with the
///     ordinary "not configured" message rather than taking the whole download queue down.
/// </remarks>
public sealed class NullSoulseekCredentialProvider : ISoulseekCredentialProvider
{
    /// <summary>The shared instance.</summary>
    public static NullSoulseekCredentialProvider Instance { get; } = new();

    /// <inheritdoc />
    public Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SlskdCredentials?>(null);
}
