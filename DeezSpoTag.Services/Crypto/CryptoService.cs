using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace DeezSpoTag.Services.Crypto;

/// <summary>
/// Consolidated crypto service merging CryptoService, DecryptionService, and BlowfishService
/// Direct port from deezspotag decryption.ts and crypto.ts with enhanced functionality
/// </summary>
public class CryptoService
{
    private readonly ILogger<CryptoService> _logger;
    
    // Constants from deezspotag
    private const string BinaryEncodingName = "binary";
    private const string Latin1EncodingName = "ISO-8859-1";
    private const string SeedMaterial = "g4el58wc0zvf9na1";
    internal const string EcbTransformKey = "jo6aey6haid2Teih";

    public CryptoService(ILogger<CryptoService>? logger)
    {
        _logger = logger ?? NullLogger<CryptoService>.Instance;
    }

    #region MD5 and Hashing Operations

    /// <summary>
    /// Generate MD5 hash with specific encoding (exact port of _md5 from deezspotag)
    /// </summary>
    public static string GenerateMd5(string data, string encoding = BinaryEncodingName)
    {
        return LegacyMd5.ComputeHexLower(GetEncodedBytes(data, encoding));
    }

    /// <summary>
    /// Compute MD5 hash of input string (legacy compatibility)
    /// </summary>
    public static string ComputeMD5Hash(string input)
    {
        return GenerateMd5(input, BinaryEncodingName);
    }

    #endregion

    #region ECB Encryption/Decryption

    /// <summary>
    /// ECB encrypt (port of _ecbCrypt from deezspotag)
    /// </summary>
    public string EcbEncrypt(string key, string data)
    {
        try
        {
            return EcbEncryptCore(key, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to ECB encrypt data");
            return "";
        }
    }

    /// <summary>
    /// ECB decrypt (port of _ecbDecrypt from deezspotag)
    /// </summary>
    public string EcbDecrypt(string key, string data)
    {
        try
        {
            return EcbDecryptCore(key, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to ECB decrypt data");
            return "";
        }
    }

    #endregion

    #region Blowfish Key Generation

    /// <summary>
    /// Generate Blowfish key for track decryption (exact port of generateBlowfishKey from deezspotag)
    /// </summary>
    public static string GenerateBlowfishKeyString(string trackId)
    {
        // CRITICAL: Use _md5 function exactly like deezspotag (with ASCII encoding)
        var idMd5 = GenerateMd5(trackId, "ascii");
        
        var bfKey = new StringBuilder();

        for (int i = 0; i < 16; i++)
        {
            var char1 = (byte)idMd5[i];
            var char2 = (byte)idMd5[i + 16];
            var secretChar = (byte)SeedMaterial[i];
            
            bfKey.Append((char)(char1 ^ char2 ^ secretChar));
        }

        return bfKey.ToString();
    }

    /// <summary>
    /// Generate Blowfish key as byte array for track decryption
    /// </summary>
    public static byte[] GenerateBlowfishKey(string trackId)
    {
        var keyString = GenerateBlowfishKeyString(trackId);
        return Encoding.GetEncoding(Latin1EncodingName).GetBytes(keyString);
    }

    #endregion

    #region Stream URL Generation

    /// <summary>
    /// Generate stream path (port of generateStreamPath from deezspotag)
    /// </summary>
    public static string GenerateStreamPath(string sngId, string md5, string mediaVersion, string format)
    {
        return GenerateStreamPathCore(sngId, md5, mediaVersion, format);
    }

    /// <summary>
    /// Generate crypted stream URL (port of generateCryptedStreamURL from deezspotag)
    /// </summary>
    public static string GenerateCryptedStreamUrl(string sngId, string md5, string mediaVersion, string format)
    {
        var urlPart = GenerateStreamPathCore(sngId, md5, mediaVersion, format);
        return $"https://e-cdns-proxy-{md5[0]}.dzcdn.net/mobile/1/{urlPart}";
    }

    /// <summary>
    /// Generate stream URL (port of generateStreamURL from deezspotag)
    /// </summary>
    public static string GenerateStreamUrl(string sngId, string md5, string mediaVersion, string format)
    {
        var urlPart = GenerateStreamPathCore(sngId, md5, mediaVersion, format);
        return $"https://cdns-proxy-{md5[0]}.dzcdn.net/api/1/{urlPart}";
    }

    internal static string EcbEncryptCore(string key, string data)
    {
        var dataBytes = Encoding.GetEncoding(Latin1EncodingName).GetBytes(data);
        var encrypted = TransformAesBlocks(key, dataBytes, encrypt: true);
        return Convert.ToHexString(encrypted).ToLower();
    }

    internal static string EcbDecryptCore(string key, string data)
    {
        var dataBytes = Convert.FromHexString(data);
        var decrypted = TransformAesBlocks(key, dataBytes, encrypt: false);
        return Encoding.GetEncoding(Latin1EncodingName).GetString(decrypted);
    }

    internal static string GenerateStreamPathCore(string sngId, string md5, string mediaVersion, string format)
    {
        var urlPart = $"{md5}¤{format}¤{sngId}¤{mediaVersion}";
        var md5Val = GenerateMd5(urlPart);
        var step2 = $"{md5Val}¤{urlPart}¤";
        var padding = 16 - (step2.Length % 16);
        step2 += new string('.', padding);
        return EcbEncryptCore(EcbTransformKey, step2);
    }

    private static byte[] GetEncodedBytes(string data, string encoding)
    {
        return encoding switch
        {
            "ascii" => Encoding.ASCII.GetBytes(data),
            "utf8" => Encoding.UTF8.GetBytes(data),
            BinaryEncodingName => Encoding.GetEncoding(Latin1EncodingName).GetBytes(data),
            _ => Encoding.GetEncoding(Latin1EncodingName).GetBytes(data)
        };
    }

    internal static byte[] TransformAesBlocks(string key, byte[] data, bool encrypt)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length is not (16 or 24 or 32))
        {
            throw new CryptographicException("Invalid AES key length.");
        }

        if (data.Length % 16 != 0)
        {
            throw new CryptographicException("Input must be a multiple of 16 bytes.");
        }

        var engine = new Org.BouncyCastle.Crypto.Engines.AesEngine();
        engine.Init(encrypt, new Org.BouncyCastle.Crypto.Parameters.KeyParameter(keyBytes));

        var output = new byte[data.Length];
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            engine.ProcessBlock(data, offset, output, offset);
        }

        return output;
    }

    #endregion

    #region Audio Chunk Decryption

    /// <summary>
    /// Decrypt audio chunk using the Node.js-compatible Blowfish path.
    /// </summary>
    public byte[] DecryptChunk(byte[] chunk, string blowfishKey)
    {
        if (chunk == null || chunk.Length == 0)
            return Array.Empty<byte>();

        if (chunk.Length < 2048)
            return chunk;

        try
        {
            return NodeJsBlowfishDecryptor.DecryptChunk(chunk, blowfishKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error decrypting audio chunk");
            // If decryption fails, return original chunk (exact port from deezspotag)
            return chunk;
        }
    }

    /// <summary>
    /// Decrypt multiple chunks of audio data
    /// </summary>
    public byte[] DecryptChunks(byte[] data, string blowfishKey)
    {
        if (data == null || data.Length == 0)
            return Array.Empty<byte>();

        try
        {
            var result = new List<byte>();
            var offset = 0;

            while (offset < data.Length)
            {
                var chunkSize = Math.Min(2048, data.Length - offset);
                var chunk = new byte[chunkSize];
                Array.Copy(data, offset, chunk, 0, chunkSize);

                var decryptedChunk = DecryptChunk(chunk, blowfishKey);
                result.AddRange(decryptedChunk);

                offset += chunkSize;
            }

            return result.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error decrypting audio chunks");
            return data; // Return original data if decryption fails
        }
    }

    /// <summary>
    /// Decrypt a single chunk of audio data asynchronously
    /// </summary>
    public async Task<byte[]> DecryptChunkAsync(byte[] chunk, byte[] blowfishKey)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Convert byte array key to string for compatibility
                var keyString = Encoding.GetEncoding(Latin1EncodingName).GetString(blowfishKey);
                return DecryptChunk(chunk, keyString);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to decrypt chunk asynchronously");
                return chunk; // Return original chunk if decryption fails
            }
        });
    }

    /// <summary>
    /// Decrypt a single chunk of audio data asynchronously with string key
    /// </summary>
    public async Task<byte[]> DecryptChunkAsync(byte[] chunk, string blowfishKey)
    {
        return await Task.Run(() =>
        {
            try
            {
                return DecryptChunk(chunk, blowfishKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to decrypt chunk asynchronously");
                return chunk; // Return original chunk if decryption fails
            }
        });
    }

    #endregion

    #region Static Methods for Backward Compatibility

    public static string GenerateMd5Static(string data, string encoding = BinaryEncodingName)
    {
        return GenerateMd5(data, encoding);
    }

    public static string EcbEncryptStatic(string key, string data)
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        return service.EcbEncrypt(key, data);
    }

    public static string EcbDecryptStatic(string key, string data)
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        return service.EcbDecrypt(key, data);
    }

    public static string GenerateBlowfishKeyStatic(string trackId)
    {
        return GenerateBlowfishKeyString(trackId);
    }

    public static byte[] GenerateBlowfishKeyBytesStatic(string trackId)
    {
        return GenerateBlowfishKey(trackId);
    }

    public static string GenerateStreamPathStatic(string sngId, string md5, string mediaVersion, string format)
    {
        return GenerateStreamPath(sngId, md5, mediaVersion, format);
    }

    public static byte[] DecryptChunkStatic(byte[] chunk, string blowfishKey)
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        return service.DecryptChunk(chunk, blowfishKey);
    }

    #endregion
}
