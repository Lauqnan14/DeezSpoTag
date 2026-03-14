using System.Text;
using OtpNet;

namespace DeezSpoTag.Services.Utils;

public static class SpotifyWebPlayerTotp
{
    public static (string Totp, int Version) Generate()
    {
        var (version, secretList) = GetSecret();
        if (secretList == null || secretList.Length == 0)
        {
            return (string.Empty, version);
        }

        var transformed = new byte[secretList.Length];
        for (var i = 0; i < secretList.Length; i++)
        {
            transformed[i] = (byte)(secretList[i] ^ (byte)((i % 33) + 9));
        }

        var joined = new StringBuilder(transformed.Length * 3);
        foreach (var b in transformed)
        {
            joined.Append(b);
        }

        var hexString = Convert.ToHexString(Encoding.ASCII.GetBytes(joined.ToString())).ToLowerInvariant();
        var hexBytes = new byte[hexString.Length / 2];
        for (var i = 0; i < hexBytes.Length; i++)
        {
            hexBytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
        }

        return (GenerateCode(hexBytes), version);
    }

    private static (int Version, byte[]? Secret) GetSecret()
    {
        var secrets = new Dictionary<int, byte[]>
        {
            [59] =
            [
                123, 105, 79, 70, 110, 59, 52, 125, 60, 49, 80, 70, 89, 75, 80, 86,
                63, 53, 123, 37, 117, 49, 52, 93, 77, 62, 47, 86, 48, 104, 68, 72
            ],
            [60] =
            [
                79, 109, 69, 123, 90, 65, 46, 74, 94, 34, 58, 48, 70, 71, 92, 85,
                122, 63, 91, 64, 87, 87
            ],
            [61] =
            [
                44, 55, 47, 42, 70, 40, 34, 114, 76, 74, 50, 111, 120, 97, 75, 76,
                94, 102, 43, 69, 49, 120, 118, 80, 64, 78
            ]
        };

        const int version = 61;
        return secrets.TryGetValue(version, out var secret) ? (version, secret) : (version, null);
    }

    private static string GenerateCode(byte[] secretBytes)
    {
        var totp = new Totp(secretBytes, step: 30, totpSize: 6, mode: OtpHashMode.Sha1);
        return totp.ComputeTotp(DateTime.UtcNow);
    }
}
