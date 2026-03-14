using System.Text;
using DeezSpoTag.Services.Download.Shared.Utils;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleKeyService
{
    private AppleKeyService()
    {
    }

    public static string BuildPssh(string kidBase64, string? appleId)
    {
        if (string.IsNullOrWhiteSpace(kidBase64))
        {
            return string.Empty;
        }

        var kidBytes = Base64UrlDecoder.TryDecode(kidBase64);
        if (kidBytes is not { Length: > 0 })
        {
            return string.Empty;
        }

        var contentIdBytes = BuildContentIdBytes(appleId);
        var widevine = AppleWidevineProto.BuildWidevineCencHeader(kidBytes, contentIdBytes);
        var prefix = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var payload = new byte[prefix.Length + widevine.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(widevine, 0, payload, prefix.Length, widevine.Length);
        return Convert.ToBase64String(payload);
    }

    private static byte[] BuildContentIdBytes(string? appleId)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return Array.Empty<byte>();
        }

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(appleId));
        return Encoding.ASCII.GetBytes(base64);
    }
}
