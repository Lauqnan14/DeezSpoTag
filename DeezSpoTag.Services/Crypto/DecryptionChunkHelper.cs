namespace DeezSpoTag.Services.Crypto;

internal static class DecryptionChunkHelper
{
    internal static byte[] MergeDecryptedPrefix(byte[] originalChunk, byte[] decryptedPrefix, int prefixLength)
    {
        if (originalChunk.Length <= prefixLength)
        {
            return decryptedPrefix;
        }

        var result = new byte[originalChunk.Length];
        Array.Copy(decryptedPrefix, 0, result, 0, decryptedPrefix.Length);
        Array.Copy(originalChunk, prefixLength, result, decryptedPrefix.Length, originalChunk.Length - prefixLength);
        return result;
    }
}
