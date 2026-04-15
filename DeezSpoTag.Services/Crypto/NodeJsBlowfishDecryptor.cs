using System.Text;

namespace DeezSpoTag.Services.Crypto;

/// <summary>
/// Blowfish decryptor that mimics Node.js createDecipheriv("bf-cbc") behavior exactly
/// </summary>
public static class NodeJsBlowfishDecryptor
{
    /// <summary>
    /// Decrypt chunk exactly like Node.js createDecipheriv("bf-cbc") with setAutoPadding(false)
    /// </summary>
    public static byte[] DecryptChunk(byte[] chunk, string blowfishKey)
    {
        if (chunk == null || chunk.Length == 0)
            return Array.Empty<byte>();

        if (chunk.Length < 2048)
            return chunk;

        try
        {
            // CRITICAL: Use BouncyCastle Blowfish to match Node.js behavior exactly
            var keyBytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(blowfishKey);
            var iv = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };

            // Extract exactly 2048 bytes to decrypt
            var toDecrypt = new byte[2048];
            Array.Copy(chunk, 0, toDecrypt, 0, 2048);

            // Use BouncyCastle Blowfish engine for exact Node.js compatibility
            var engine = new Org.BouncyCastle.Crypto.Engines.BlowfishEngine();
            var cipher = new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(engine);
            var parameters = new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(
                new Org.BouncyCastle.Crypto.Parameters.KeyParameter(keyBytes), iv);

            cipher.Init(false, parameters); // false = decrypt

            var decrypted = new byte[2048];
            var blockSize = cipher.GetBlockSize();

            for (int i = 0; i < 2048; i += blockSize)
            {
                cipher.ProcessBlock(toDecrypt, i, decrypted, i);
            }

            return DecryptionChunkHelper.MergeDecryptedPrefix(chunk, decrypted, 2048);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If decryption fails, return original chunk
            return chunk;
        }
    }
}
