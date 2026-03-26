using System.Text;

namespace DeezSpoTag.Services.Crypto;

/// <summary>
/// Decryption service ported from deezspotag decryption.ts
/// </summary>
public static class DecryptionService
{
    private const string BinaryEncodingName = "binary";
    private const string Latin1EncodingName = "ISO-8859-1";

    /// <summary>
    /// Generate MD5 hash with specific encoding (exact port of _md5 from deezspotag)
    /// </summary>
    public static string GenerateMd5(string data, string encoding = BinaryEncodingName)
    {
        return CryptoService.GenerateMd5(data, encoding);
    }

    /// <summary>
    /// ECB encrypt (port of _ecbCrypt)
    /// </summary>
    public static string EcbEncrypt(string key, string data)
    {
        return CryptoService.EcbEncryptCore(key, data);
    }

    /// <summary>
    /// ECB decrypt (port of _ecbDecrypt)
    /// </summary>
    public static string EcbDecrypt(string key, string data)
    {
        return CryptoService.EcbDecryptCore(key, data);
    }

    /// <summary>
    /// Generate Blowfish key for track decryption (exact port of generateBlowfishKey from deezspotag)
    /// </summary>
    public static string GenerateBlowfishKey(string trackId)
    {
        return CryptoService.GenerateBlowfishKeyString(trackId);
    }

    /// <summary>
    /// Generate Blowfish key as byte array for track decryption
    /// </summary>
    public static byte[] GenerateBlowfishKeyBytes(string trackId)
    {
        return CryptoService.GenerateBlowfishKey(trackId);
    }

    /// <summary>
    /// Generate stream path (port of generateStreamPath)
    /// </summary>
    public static string GenerateStreamPath(string sngId, string md5, string mediaVersion, string format)
    {
        return CryptoService.GenerateStreamPathCore(sngId, md5, mediaVersion, format);
    }

    /// <summary>
    /// Generate crypted stream URL (port of generateCryptedStreamURL)
    /// </summary>
    public static string GenerateCryptedStreamUrl(string sngId, string md5, string mediaVersion, string format)
    {
        var urlPart = GenerateStreamPath(sngId, md5, mediaVersion, format);
        return $"https://e-cdns-proxy-{md5[0]}.dzcdn.net/mobile/1/{urlPart}";
    }

    /// <summary>
    /// Generate stream URL (port of generateStreamURL)
    /// </summary>
    public static string GenerateStreamUrl(string sngId, string md5, string mediaVersion, string format)
    {
        var urlPart = GenerateStreamPath(sngId, md5, mediaVersion, format);
        return $"https://cdns-proxy-{md5[0]}.dzcdn.net/api/1/{urlPart}";
    }

    /// <summary>
    /// Decrypt audio chunk using Blowfish CBC mode (exact port from deezspotag decryptChunk)
    /// CRITICAL: Use custom BlowfishService to match deezspotag blowfish.cjs behavior exactly
    /// </summary>
    public static byte[] DecryptChunk(byte[] chunk, string blowfishKey)
    {
        if (chunk == null || chunk.Length == 0)
            return Array.Empty<byte>();

        if (chunk.Length < 2048)
            return chunk;

        try
        {
            // CRITICAL: Use ISO-8859-1 encoding exactly like deezspotag
            var keyBytes = Encoding.GetEncoding(Latin1EncodingName).GetBytes(blowfishKey);
            var iv = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
            
            // Extract exactly 2048 bytes to decrypt (exact port from deezspotag)
            var toDecrypt = new byte[2048];
            Array.Copy(chunk, 0, toDecrypt, 0, 2048);
            
            // Use custom BlowfishService that matches deezspotag blowfish.cjs exactly
            var blowfish = new BlowfishService(keyBytes);
            var decrypted = blowfish.DecryptCBC(toDecrypt, iv);
            
            return DecryptionChunkHelper.MergeDecryptedPrefix(chunk, decrypted, 2048);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If decryption fails, return original chunk (exact port from deezspotag)
            return chunk;
        }
    }
}
