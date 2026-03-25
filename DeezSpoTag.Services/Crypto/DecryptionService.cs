using System.Text;

namespace DeezSpoTag.Services.Crypto;

/// <summary>
/// Decryption service ported from deezspotag decryption.ts
/// </summary>
public static class DecryptionService
{
    private const string BinaryEncodingName = "binary";
    private const string Latin1EncodingName = "ISO-8859-1";
    private const string EcbTransformKey = "jo6aey6haid2Teih";

    /// <summary>
    /// Generate MD5 hash with specific encoding (exact port of _md5 from deezspotag)
    /// </summary>
    public static string GenerateMd5(string data, string encoding = BinaryEncodingName)
    {
        // Protocol compatibility with upstream deezspotag crypto flow.

        // Use the same encoding as deezspotag
        var dataBytes = encoding switch
        {
            "ascii" => Encoding.ASCII.GetBytes(data),
            "utf8" => Encoding.UTF8.GetBytes(data),
            BinaryEncodingName => Encoding.GetEncoding(Latin1EncodingName).GetBytes(data),
            _ => Encoding.GetEncoding(Latin1EncodingName).GetBytes(data)
        };

        return LegacyMd5.ComputeHexLower(dataBytes);
    }

    /// <summary>
    /// ECB encrypt (port of _ecbCrypt)
    /// </summary>
    public static string EcbEncrypt(string key, string data)
    {
        // Use binary encoding like deezspotag (Latin1/ISO-8859-1)
        var dataBytes = Encoding.GetEncoding(Latin1EncodingName).GetBytes(data);
        var encrypted = CryptoService.TransformAesBlocks(key, dataBytes, encrypt: true);
        return Convert.ToHexString(encrypted).ToLower();
    }

    /// <summary>
    /// ECB decrypt (port of _ecbDecrypt)
    /// </summary>
    public static string EcbDecrypt(string key, string data)
    {
        var dataBytes = Convert.FromHexString(data);
        var decrypted = CryptoService.TransformAesBlocks(key, dataBytes, encrypt: false);
        // Use binary encoding like deezspotag (Latin1/ISO-8859-1)
        return Encoding.GetEncoding(Latin1EncodingName).GetString(decrypted);
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
        var urlPart = $"{md5}¤{format}¤{sngId}¤{mediaVersion}";
        var md5Val = GenerateMd5(urlPart);
        var step2 = $"{md5Val}¤{urlPart}¤";
        
        // Pad to 16-byte boundary
        var padding = 16 - (step2.Length % 16);
        step2 += new string('.', padding);
        
        return EcbEncrypt(EcbTransformKey, step2);
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
            
            // Handle result exactly like deezspotag
            if (chunk.Length == 2048)
            {
                return decrypted;
            }
            
            // Combine decrypted part with remaining unencrypted data
            var result = new byte[chunk.Length];
            Array.Copy(decrypted, 0, result, 0, decrypted.Length);
            Array.Copy(chunk, 2048, result, decrypted.Length, chunk.Length - 2048);
            
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If decryption fails, return original chunk (exact port from deezspotag)
            return chunk;
        }
    }
}
