using System.Security.Authentication;

namespace DeezSpoTag.Services.Crypto;

/// <summary>
/// EXACT PORT of DeezSpot.Web working implementation + deezspotag reinforcements
/// Based on DeezSpot.Web's successful approach with additional deezspotag compatibility
/// </summary>
public static class DeezSpoTagHttpClientHandler
{
    private static readonly SslProtocols SecureProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    /// Creates HttpClientHandler using DeezSpot.Web's working approach + deezspotag reinforcements
    /// </summary>
    public static HttpClientHandler Create(bool allowInsecureTls)
    {
        var handler = new HttpClientHandler();

        _ = allowInsecureTls;
        ConfigureCommon(handler);
        handler.SslProtocols = SecureProtocols;
        return handler;
    }

    /// <summary>
    /// Creates fallback HttpClientHandler with specific SSL protocol for problematic endpoints
    /// DEEZSPOTAG REINFORCEMENT: Multiple SSL protocol fallbacks
    /// </summary>
    public static HttpClientHandler CreateWithProtocol(SslProtocols protocol, bool allowInsecureTls)
    {
        var handler = new HttpClientHandler();

        _ = allowInsecureTls;
        ConfigureCommon(handler);
        handler.SslProtocols = NormalizeProtocol(protocol);
        return handler;
    }

    /// <summary>
    /// Creates an HttpClientHandler (legacy fallback) for maximum compatibility
    /// DEEZSPOTAG REINFORCEMENT: Legacy SSL support
    /// </summary>
    public static HttpClientHandler CreateLegacy(bool allowInsecureTls)
    {
        _ = allowInsecureTls;
        var handler = Create(false);
        handler.MaxConnectionsPerServer = 10;
        return handler;
    }

    private static void ConfigureCommon(HttpClientHandler handler)
    {
        handler.CheckCertificateRevocationList = true;
        handler.UseCookies = false;
        handler.AllowAutoRedirect = true;
        handler.MaxAutomaticRedirections = 10;
        handler.ClientCertificateOptions = ClientCertificateOption.Manual;
        handler.PreAuthenticate = false;
        handler.UseDefaultCredentials = false;
        handler.AutomaticDecompression = System.Net.DecompressionMethods.None;
    }

    private static SslProtocols NormalizeProtocol(SslProtocols protocol)
    {
        if (protocol == SslProtocols.Tls13)
        {
            return SslProtocols.Tls13;
        }

        if (protocol == SslProtocols.Tls12)
        {
            return SslProtocols.Tls12;
        }

        if ((protocol & SslProtocols.Tls13) != 0)
        {
            return SecureProtocols;
        }

        if ((protocol & SslProtocols.Tls12) != 0)
        {
            return SslProtocols.Tls12;
        }

        return SecureProtocols;
    }
}
