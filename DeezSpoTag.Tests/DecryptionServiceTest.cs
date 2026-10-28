using System;
using System.Linq;
using System.Text;
using DeezSpoTag.Services.Crypto;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DecryptionServiceTest
{
    private static readonly byte[] DeezerIv = { 0, 1, 2, 3, 4, 5, 6, 7 };

    private static byte[] EncryptBfCbc(byte[] plaintext, byte[] key)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(new Org.BouncyCastle.Crypto.Engines.BlowfishEngine());
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), DeezerIv));
        var output = new byte[plaintext.Length];
        for (var offset = 0; offset < plaintext.Length; offset += 8)
        {
            cipher.ProcessBlock(plaintext, offset, output, offset);
        }

        return output;
    }

    private static byte[] Latin1(string value) => Encoding.GetEncoding("ISO-8859-1").GetBytes(value);

    [Fact]
    public void GenerateMd5_DelegatesToCryptoService()
    {
        Assert.Equal(CryptoService.GenerateMd5("abc"), DecryptionService.GenerateMd5("abc"));
        Assert.Equal(CryptoService.GenerateMd5("abc", "ascii"), DecryptionService.GenerateMd5("abc", "ascii"));
    }

    [Fact]
    public void EcbEncryptDecrypt_RoundTrip()
    {
        const string key = "jo6aey6haid2Teih";
        var encrypted = DecryptionService.EcbEncrypt(key, "0123456789abcdef");

        Assert.Equal(32, encrypted.Length);
        Assert.Equal("0123456789abcdef", DecryptionService.EcbDecrypt(key, encrypted));
    }

    [Fact]
    public void GenerateBlowfishKey_MatchesCryptoService()
    {
        Assert.Equal(CryptoService.GenerateBlowfishKeyString("42"), DecryptionService.GenerateBlowfishKey("42"));
        Assert.Equal(CryptoService.GenerateBlowfishKey("42"), DecryptionService.GenerateBlowfishKeyBytes("42"));
    }

    [Fact]
    public void StreamUrlGenerators_MatchCryptoServiceAndHostPatterns()
    {
        const string md5Origin = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

        Assert.Equal(
            CryptoService.GenerateStreamPath("77", md5Origin, "10", "MP3_128"),
            DecryptionService.GenerateStreamPath("77", md5Origin, "10", "MP3_128"));
        Assert.StartsWith($"https://e-cdns-proxy-{md5Origin[0]}.dzcdn.net/mobile/1/",
            DecryptionService.GenerateCryptedStreamUrl("77", md5Origin, "10", "MP3_128"));
        Assert.StartsWith($"https://cdns-proxy-{md5Origin[0]}.dzcdn.net/api/1/",
            DecryptionService.GenerateStreamUrl("77", md5Origin, "10", "MP3_128"));
    }

    [Fact]
    public void DecryptChunk_ShortChunk_PassesThroughUnchanged()
    {
        var chunk = new byte[512];
        new Random(41).NextBytes(chunk);

        Assert.Equal(chunk, DecryptionService.DecryptChunk(chunk, "key123456789012"));
    }

    [Fact]
    public void DecryptChunk_NullOrEmpty_ReturnsEmptyArray()
    {
        Assert.Empty(DecryptionService.DecryptChunk(null!, "key"));
        Assert.Empty(DecryptionService.DecryptChunk(Array.Empty<byte>(), "key"));
    }

    [Fact]
    public void DecryptChunk_Exact2048ByteChunk_ReturnsOnlyDecryptedPrefix()
    {
        var key = CryptoService.GenerateBlowfishKey("2468");
        var plaintext = new byte[2048];
        new Random(42).NextBytes(plaintext);
        plaintext[0] = 0x55;

        var decrypted = DecryptionService.DecryptChunk(EncryptBfCbc(plaintext, key), Encoding.GetEncoding("ISO-8859-1").GetString(key));

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DecryptChunk_LongChunk_DecryptsPrefixAndKeepsTail()
    {
        var key = CryptoService.GenerateBlowfishKey("1357");
        var plaintext = new byte[2048];
        new Random(43).NextBytes(plaintext);
        plaintext[0] = 0x66;
        var tail = new byte[300];
        new Random(44).NextBytes(tail);

        var chunk = new byte[2348];
        EncryptBfCbc(plaintext, key).CopyTo(chunk, 0);
        tail.CopyTo(chunk, 2048);

        var decrypted = DecryptionService.DecryptChunk(chunk, Encoding.GetEncoding("ISO-8859-1").GetString(key));

        Assert.Equal(plaintext.Concat(tail), decrypted);
    }

    [Fact]
    public void DecryptChunk_MatchesNodeJsBlowfishDecryptorByteForByte()
    {
        // Two independent implementations back the two entry points (custom BlowfishService vs
        // BouncyCastle); they must agree on the same ciphertext.
        var key = CryptoService.GenerateBlowfishKey("8642");
        var plaintext = new byte[2048];
        new Random(45).NextBytes(plaintext);
        plaintext[0] = 0x77;
        var tail = new byte[123];
        new Random(46).NextBytes(tail);
        var chunk = new byte[2171];
        EncryptBfCbc(plaintext, key).CopyTo(chunk, 0);
        tail.CopyTo(chunk, 2048);

        var viaCustomBlowfish = DecryptionService.DecryptChunk(chunk, Encoding.GetEncoding("ISO-8859-1").GetString(key));
        var viaBouncyCastle = NodeJsBlowfishDecryptor.DecryptChunk(chunk, Encoding.GetEncoding("ISO-8859-1").GetString(key));

        Assert.Equal(viaCustomBlowfish, viaBouncyCastle);
    }

    [Fact]
    public void DecryptChunk_UndecryptableChunk_ReturnsOriginal()
    {
        var chunk = new byte[2048];
        new Random(47).NextBytes(chunk);

        Assert.Equal(chunk, DecryptionService.DecryptChunk(chunk, ""));
    }

    [Fact]
    public void NodeJsBlowfishDecryptor_EmptyAndNull_ReturnEmpty()
    {
        Assert.Empty(NodeJsBlowfishDecryptor.DecryptChunk(null!, "key"));
        Assert.Empty(NodeJsBlowfishDecryptor.DecryptChunk(Array.Empty<byte>(), "key"));
    }

    [Fact]
    public void NodeJsBlowfishDecryptor_ShortChunk_PassesThroughUnchanged()
    {
        var chunk = new byte[2047];
        new Random(48).NextBytes(chunk);

        Assert.Equal(chunk, NodeJsBlowfishDecryptor.DecryptChunk(chunk, "key"));
    }
}
