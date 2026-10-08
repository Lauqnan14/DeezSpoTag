using System.Globalization;
using System.Security.Cryptography;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     AES-128-CBC decryption for SoundCloud HLS segments.
/// </summary>
/// <remarks>
///     <para>
///         Decryption is done with explicit PKCS#7 validation rather than the padding tolerance a permissive
///         implementation would use. Every SoundCloud segment is genuinely padded, so a bad padding byte means
///         the key, the IV, or the ciphertext is wrong - and writing out the mis-decrypted bytes would produce
///         an mp3 that looks fine to a size check and is silent noise to the listener.
///     </para>
/// </remarks>
public static class SoundCloudHlsDecryptor
{
    /// <summary>AES block size in bytes.</summary>
    private const int BlockSizeBytes = 16;

    /// <summary>The key length AES-128 requires, in bytes.</summary>
    private const int KeySizeBytes = 16;

    /// <summary>The IV length AES-CBC requires, in bytes.</summary>
    private const int IvSizeBytes = 16;

    /// <summary>
    ///     Decrypts one segment.
    /// </summary>
    /// <param name="ciphertext">The segment bytes as downloaded.</param>
    /// <param name="key">The 16-byte AES key.</param>
    /// <param name="sequenceNumber">The segment's absolute media sequence number.</param>
    /// <param name="explicitIv">The <c>IV</c> attribute, when the playlist declared one.</param>
    /// <returns>The plaintext segment.</returns>
    /// <exception cref="InvalidDataException">The key, IV, ciphertext, or padding is invalid.</exception>
    public static byte[] Decrypt(byte[] ciphertext, byte[] key, long sequenceNumber, string? explicitIv)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeySizeBytes)
        {
            throw new InvalidDataException(
                $"SoundCloud HLS AES-128 key was {key.Length} bytes; AES-128 requires exactly {KeySizeBytes}.");
        }

        if (ciphertext.Length == 0 || ciphertext.Length % BlockSizeBytes != 0)
        {
            throw new InvalidDataException(
                $"SoundCloud HLS segment was {ciphertext.Length} bytes, which is not a whole number of AES blocks.");
        }

        var iv = ResolveIv(sequenceNumber, explicitIv);

        // NoPadding, so the padding is validated and removed here rather than by the transform silently
        // accepting a corrupt final block.
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var plaintext = new byte[ciphertext.Length];
        using var transform = aes.CreateDecryptor();
        transform.TransformBlock(ciphertext, 0, ciphertext.Length, plaintext, 0);

        return StripPkcs7(plaintext);
    }

    /// <summary>
    ///     Resolves the IV: the playlist's explicit value when it has one, otherwise the sequence number.
    /// </summary>
    /// <remarks>
    ///     The derived form is the segment's absolute media sequence number as a 16-byte big-endian value,
    ///     which is what HLS specifies and what SoundCloud relies on for the segments that omit an IV.
    /// </remarks>
    private static byte[] ResolveIv(long sequenceNumber, string? explicitIv)
    {
        if (string.IsNullOrWhiteSpace(explicitIv))
        {
            var derived = new byte[IvSizeBytes];
            var value = sequenceNumber;
            for (var index = IvSizeBytes - 1; index >= 0 && value > 0; index--)
            {
                derived[index] = (byte)(value & 0xFF);
                value >>= 8;
            }

            return derived;
        }

        var text = explicitIv.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        // Sixteen bytes is thirty-two hex characters, which is wider than any single numeric type. Parsed
        // byte by byte rather than packed, so a full-width IV is accepted instead of rejected for being large.
        if (text.Length != IvSizeBytes * 2)
        {
            throw new InvalidDataException(
                $"SoundCloud HLS IV '{SoundCloudUrlRedactor.Redact(explicitIv)}' is not {IvSizeBytes} bytes of hex.");
        }

        var iv = new byte[IvSizeBytes];
        for (var index = 0; index < IvSizeBytes; index++)
        {
            if (!byte.TryParse(
                    text.AsSpan(index * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out iv[index]))
            {
                throw new InvalidDataException(
                    $"SoundCloud HLS IV '{SoundCloudUrlRedactor.Redact(explicitIv)}' is not {IvSizeBytes} bytes of hex.");
            }
        }

        return iv;
    }

    private static byte[] StripPkcs7(byte[] plaintext)
    {
        var padding = plaintext[^1];
        if (padding == 0 || padding > BlockSizeBytes || padding > plaintext.Length)
        {
            throw new InvalidDataException(
                $"SoundCloud HLS segment had an invalid PKCS#7 padding byte ({padding}); the key or IV is wrong.");
        }

        for (var index = plaintext.Length - padding; index < plaintext.Length; index++)
        {
            if (plaintext[index] != padding)
            {
                throw new InvalidDataException(
                    "SoundCloud HLS segment had inconsistent PKCS#7 padding; the key or IV is wrong.");
            }
        }

        return plaintext[..^padding];
    }
}