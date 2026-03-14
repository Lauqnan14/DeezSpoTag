using Org.BouncyCastle.Crypto.Digests;
using System.Text;

namespace DeezSpoTag.Services.Crypto;

internal static class LegacyMd5
{
    internal static string ComputeHexLower(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var digest = new MD5Digest();
        digest.BlockUpdate(input, 0, input.Length);

        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ComputeHexLower(string input, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(encoding);

        return ComputeHexLower(encoding.GetBytes(input));
    }
}
