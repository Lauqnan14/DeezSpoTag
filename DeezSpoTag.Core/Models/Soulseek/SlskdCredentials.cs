namespace DeezSpoTag.Core.Models.Soulseek;

/// <summary>
///     Connection details for a <c>slskd</c> instance.
/// </summary>
/// <remarks>
///     <para>
///         This type exists so that <c>DeezSpoTag.Integrations</c> (which references only
///         <c>DeezSpoTag.Core</c>) can talk to <c>slskd</c> without taking a dependency on the web-tier
///         credential store. The web tier supplies the values from the encrypted
///         <c>PlatformAuthService</c> state.
///     </para>
///     <para>
///         The API key is deliberately never rendered by <see cref="ToString"/> so that an accidental
///         interpolation into a log message or an exception cannot leak it.
///     </para>
/// </remarks>
public sealed record SlskdCredentials(string BaseUrl, string ApiKey)
{
    /// <summary>
    ///     Gets a value indicating whether an API key was supplied. slskd may run without one.
    /// </summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    ///     Returns a redacted description. Never includes the API key.
    /// </summary>
    public override string ToString()
        => $"slskd:{BaseUrl}";
}
