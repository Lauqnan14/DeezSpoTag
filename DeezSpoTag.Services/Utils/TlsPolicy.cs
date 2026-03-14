using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Security.Authentication;

namespace DeezSpoTag.Services.Utils;

public static class TlsPolicy
{
    private const string ConfigKey = "Download:AllowInsecureTls";
    private const string EnvKey = "DEEZSPOTAG_ALLOW_INSECURE_TLS";
    private const string LegacyConfigKey = "Download:AllowLegacyTls";
    private const string LegacyEnvKey = "DEEZSPOTAG_ALLOW_LEGACY_TLS";

    public static bool AllowInsecure(IConfiguration? configuration)
    {
        if (configuration?.GetValue<bool?>(ConfigKey) == true)
        {
            return true;
        }

        var env = Environment.GetEnvironmentVariable(EnvKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return false;
        }

        return env.Equals("1", StringComparison.OrdinalIgnoreCase)
            || env.Equals("true", StringComparison.OrdinalIgnoreCase)
            || env.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyIfAllowed(HttpClientHandler handler, IConfiguration? configuration)
    {
        _ = configuration;
        handler.CheckCertificateRevocationList = true;
    }

    public static bool AllowLegacy(IConfiguration? configuration)
    {
        if (configuration?.GetValue<bool?>(LegacyConfigKey) == true)
        {
            return true;
        }

        var env = Environment.GetEnvironmentVariable(LegacyEnvKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return false;
        }

        return env.Equals("1", StringComparison.OrdinalIgnoreCase)
            || env.Equals("true", StringComparison.OrdinalIgnoreCase)
            || env.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static SslProtocols GetSslProtocols(IConfiguration? configuration)
    {
        _ = configuration;
        return SslProtocols.Tls12 | SslProtocols.Tls13;
    }
}
