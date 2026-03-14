using System.Net;
using System.Net.Sockets;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class LocalApiAccess
{
    private const string TrustPrivateNetworkEnv = "DEEZSPOTAG_TRUST_PRIVATE_NETWORK";

    public static bool IsAllowed(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        if (HasForwardedClientHeaders(context))
        {
            return false;
        }

        return IsTrustedLocal(context.Connection.RemoteIpAddress);
    }

    public static bool IsTrustedLocal(IPAddress? remote)
    {
        if (remote is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remote))
        {
            return true;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            return IsTrustedLocal(remote.MapToIPv4());
        }

        if (!AllowPrivateNetworkTrust())
        {
            return false;
        }

        return remote.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsTrustedPrivateIpv4(remote),
            AddressFamily.InterNetworkV6 => IsTrustedPrivateIpv6(remote),
            _ => false
        };
    }

    private static bool IsTrustedPrivateIpv4(IPAddress remote)
    {
        var bytes = remote.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        // RFC1918 private ranges + link-local.
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsTrustedPrivateIpv6(IPAddress remote)
    {
        if (remote.IsIPv6LinkLocal || remote.IsIPv6SiteLocal)
        {
            return true;
        }

        var bytes = remote.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool HasForwardedClientHeaders(HttpContext context)
    {
        var headers = context.Request.Headers;
        return headers.ContainsKey("X-Forwarded-For")
            || headers.ContainsKey("Forwarded")
            || headers.ContainsKey("X-Real-IP");
    }

    private static bool AllowPrivateNetworkTrust()
    {
        var value = Environment.GetEnvironmentVariable(TrustPrivateNetworkEnv);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
