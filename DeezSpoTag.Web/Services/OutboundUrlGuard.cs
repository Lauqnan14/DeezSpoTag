using System.Net;
using System.Net.Sockets;
using DeezSpoTag.Core.Security;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Blocks outbound requests to loopback, private, link-local and other non-public addresses.
/// Any feature that fetches from or posts to a user-supplied URL (playlist covers, notification
/// webhooks) should validate through here first to prevent the server being used as an SSRF proxy
/// into its own network.
/// </summary>
public static class OutboundUrlGuard
{
    public static bool IsAllowedScheme(Uri uri)
        => uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
           || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public static Task<bool> IsAllowedRemoteUriAsync(Uri uri, ILogger logger, CancellationToken cancellationToken)
        => IsAllowedRemoteUriAsync(uri, logger, cancellationToken, allowPrivateLan: false);

    public static async Task<bool> IsAllowedRemoteUriAsync(
        Uri uri,
        ILogger logger,
        CancellationToken cancellationToken,
        bool allowPrivateLan)
    {
        if (!IsAllowedScheme(uri))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.DnsSafeHost))
        {
            return false;
        }

        var host = uri.DnsSafeHost.Trim().TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var directIp))
        {
            return !IsBlockedAddress(directIp, allowPrivateLan);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0)
            {
                return false;
            }

            if (addresses.Any(address => IsBlockedAddress(address, allowPrivateLan)))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve host for outbound URL validation: {Host}", LogSanitizer.OneLine(host));
            return false;
        }

        return true;
    }

    private static bool IsBlockedAddress(IPAddress address, bool allowPrivateLan = false)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedAddress(address.MapToIPv4(), allowPrivateLan);
        }

        if (IsLoopbackOrWildcard(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedIpv4Address(address, allowPrivateLan),
            AddressFamily.InterNetworkV6 => IsBlockedIpv6Address(address, allowPrivateLan),
            _ => false
        };
    }

    private static bool IsLoopbackOrWildcard(IPAddress address)
        => IPAddress.IsLoopback(address)
           || address.Equals(IPAddress.Any)
           || address.Equals(IPAddress.IPv6Any);

    private static bool IsBlockedIpv4Address(IPAddress address, bool allowPrivateLan)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length < 2)
        {
            return true;
        }

        if (bytes[0] == 0 || bytes[0] == 127)
        {
            return true;
        }

        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        if (IsPrivateLanIpv4(bytes))
        {
            return !allowPrivateLan;
        }

        return bytes[0] >= 224;
    }

    private static bool IsPrivateLanIpv4(byte[] bytes)
        => bytes[0] == 10
           || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
           || (bytes[0] == 192 && bytes[1] == 168)
           || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);

    private static bool IsBlockedIpv6Address(IPAddress address, bool allowPrivateLan = false)
    {
        if (address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal
            || address.IsIPv6Teredo)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        var isUniqueLocal = bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
        return isUniqueLocal && !allowPrivateLan;
    }
}
