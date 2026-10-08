namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     Supplies the optional SoundCloud OAuth token to the engine.
/// </summary>
/// <remarks>
///     SoundCloud authentication is entirely optional: public tracks resolve and download without it. A host
///     that owns the protected credential store (the web tier) supplies a real provider; every other host
///     registers <see cref="NullSoundCloudCredentialProvider"/> and keeps public-only operation.
/// </remarks>
public interface ISoundCloudCredentialProvider
{
    /// <summary>
    ///     Returns the saved OAuth token, or <see langword="null"/> when none is configured.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetOAuthTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
///     The default provider: no token, so only public SoundCloud content is reachable.
/// </summary>
public sealed class NullSoundCloudCredentialProvider : ISoundCloudCredentialProvider
{
    /// <summary>The shared instance.</summary>
    public static readonly NullSoundCloudCredentialProvider Instance = new();

    private NullSoundCloudCredentialProvider()
    {
    }

    /// <inheritdoc />
    public Task<string?> GetOAuthTokenAsync(CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}