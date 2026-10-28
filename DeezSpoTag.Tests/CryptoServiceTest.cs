using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DeezSpoTag.Services.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class CryptoServiceTest
{
    private const string EcbKey = "jo6aey6haid2Teih"; // must stay in sync with the production constant

    private static string Md5Hex(byte[] input)
        => Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();

    private static string Md5Hex(string input, Encoding encoding)
        => Md5Hex(encoding.GetBytes(input));

    #region MD5

    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("hello", "5d41402abc4b2a76b9719d911017c592")]
    [InlineData("1234567", "fcea920f7412b5da7be0cf42b8c93759")]
    public void GenerateMd5_BinaryEncoding_MatchesReferenceVectors(string input, string expected)
    {
        Assert.Equal(expected, CryptoService.GenerateMd5(input));
    }

    [Fact]
    public void GenerateMd5_Encodings_RouteToTheRightEncoder()
    {
        // Fixed vectors from independently hashing the three distinct encodings of café.
        const string input = "café";
        const string ascii = "333027b148fc48b3494f9b895f9c857a";
        const string utf8 = "07117fe4a1ebd544965dc19573183da2";
        const string binary = "961f50f6282239d09e48f812c1ca7276";

        Assert.Equal(ascii, CryptoService.GenerateMd5(input, "ascii"));
        Assert.Equal(utf8, CryptoService.GenerateMd5(input, "utf8"));
        Assert.Equal(binary, CryptoService.GenerateMd5(input));
        // Unknown encodings fall back to the binary (Latin-1) encoder.
        Assert.Equal(binary, CryptoService.GenerateMd5(input, "unknown-encoding"));
    }

    [Fact]
    public void ComputeMD5Hash_MatchesGenerateMd5()
    {
        Assert.Equal(CryptoService.GenerateMd5("track-id-42"), CryptoService.ComputeMD5Hash("track-id-42"));
    }

    #endregion

    #region Blowfish key generation

    [Fact]
    public void GenerateBlowfishKeyString_IsSixteenCharactersDerivedFromTrackMd5()
    {
        var trackId = "1234567890";
        var idMd5 = Md5Hex("1234567890", Encoding.ASCII);
        var seed = "g4el58wc0zvf9na1";

        var expected = new StringBuilder();
        for (var i = 0; i < 16; i++)
        {
            expected.Append((char)(idMd5[i] ^ idMd5[i + 16] ^ seed[i]));
        }

        var actual = CryptoService.GenerateBlowfishKeyString(trackId);

        Assert.Equal(expected.ToString(), actual);
        Assert.Equal(16, actual.Length);
    }

    [Fact]
    public void GenerateBlowfishKeyString_IsDeterministicAndTrackSpecific()
    {
        Assert.Equal(
            CryptoService.GenerateBlowfishKeyString("42"),
            CryptoService.GenerateBlowfishKeyString("42"));
        Assert.NotEqual(
            CryptoService.GenerateBlowfishKeyString("42"),
            CryptoService.GenerateBlowfishKeyString("43"));
    }

    [Fact]
    public void GenerateBlowfishKey_BytesMatchLatin1KeyString()
    {
        var keyString = CryptoService.GenerateBlowfishKeyString("777");
        var keyBytes = CryptoService.GenerateBlowfishKey("777");

        Assert.Equal(16, keyBytes.Length);
        Assert.Equal(keyString, Encoding.GetEncoding("ISO-8859-1").GetString(keyBytes));
    }

    #endregion

    #region ECB

    [Fact]
    public void EcbEncrypt_ProducesLowercaseHexOfAesEcbBlocks()
    {
        var data = "0123456789abcdef"; // 16 bytes, single AES block
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(EcbKey);
        var expected = Convert.ToHexString(aes.EncryptEcb(Encoding.GetEncoding("ISO-8859-1").GetBytes(data), PaddingMode.None)).ToLower();

        var actual = new CryptoService(NullLogger<CryptoService>.Instance).EcbEncrypt(EcbKey, data);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EcbEncryptThenDecrypt_RestoresInput()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var data = new string('x', 48); // three full AES blocks; ECB core rejects other sizes

        var encrypted = service.EcbEncrypt(EcbKey, data);

        Assert.NotEqual(data, encrypted);
        Assert.Equal(data, service.EcbDecrypt(EcbKey, encrypted));
    }

    [Fact]
    public void EcbEncrypt_InvalidKeyLength_ReturnsEmptyString()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        Assert.Equal("", service.EcbEncrypt("short-key", "0123456789abcdef"));
    }

    [Fact]
    public void EcbDecrypt_MalformedHex_ReturnsEmptyString()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        Assert.Equal("", service.EcbDecrypt(EcbKey, "not-hex!"));
    }

    #endregion

    #region Stream URL generation

    [Fact]
    public void GenerateStreamPath_IsEcbEncryptedUrlPartWithPadding()
    {
        const string sngId = "12345678";
        const string md5Origin = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
        const string mediaVersion = "10";
        const string format = "MP3_320";

        var urlPart = $"{md5Origin}¤{format}¤{sngId}¤{mediaVersion}";
        var md5Val = CryptoService.GenerateMd5(urlPart);
        var step2 = $"{md5Val}¤{urlPart}¤";
        var padding = 16 - (step2.Length % 16);
        step2 += new string('.', padding);

        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var streamPath = CryptoService.GenerateStreamPath(sngId, md5Origin, mediaVersion, format);

        Assert.Equal(0, streamPath.Length % 32);
        Assert.Equal(step2, service.EcbDecrypt(EcbKey, streamPath));
    }

    [Fact]
    public void GenerateCryptedStreamUrl_UsesFirstMd5CharAndMobileHost()
    {
        const string md5Origin = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

        var url = CryptoService.GenerateCryptedStreamUrl("42", md5Origin, "10", "FLAC");

        Assert.StartsWith($"https://e-cdns-proxy-{md5Origin[0]}.dzcdn.net/mobile/1/", url);
        Assert.EndsWith(CryptoService.GenerateStreamPath("42", md5Origin, "10", "FLAC"), url);
    }

    [Fact]
    public void GenerateStreamUrl_UsesApiHost()
    {
        const string md5Origin = "b1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

        var url = CryptoService.GenerateStreamUrl("42", md5Origin, "10", "FLAC");

        Assert.StartsWith($"https://cdns-proxy-{md5Origin[0]}.dzcdn.net/api/1/", url);
    }

    #endregion

    #region Chunk decryption

    private static readonly byte[] BfCbcIv = { 0, 1, 2, 3, 4, 5, 6, 7 };

    private static byte[] EncryptBfCbcChunk(byte[] plaintext, byte[] key)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(new Org.BouncyCastle.Crypto.Engines.BlowfishEngine());
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), BfCbcIv));
        var output = new byte[plaintext.Length];
        for (var offset = 0; offset < plaintext.Length; offset += 8)
        {
            cipher.ProcessBlock(plaintext, offset, output, offset);
        }

        return output;
    }

    [Fact]
    public void DecryptChunk_ShortChunk_PassesThroughUnchanged()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var chunk = new byte[2047];
        new Random(3).NextBytes(chunk);

        Assert.Equal(chunk, service.DecryptChunk(chunk, "anykey0123456789"));
    }

    [Fact]
    public void DecryptChunk_NullOrEmpty_ReturnsEmptyArray()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        Assert.Empty(service.DecryptChunk(null!, "anykey0123456789"));
        Assert.Empty(service.DecryptChunk(Array.Empty<byte>(), "anykey0123456789"));
    }

    [Fact]
    public void DecryptChunk_DecryptsFirst2048BytesAndKeepsTail()
    {
        var key = CryptoService.GenerateBlowfishKey("1234567");
        var plaintext = new byte[2048];
        new Random(11).NextBytes(plaintext);
        // Make the first byte non-zero; plaintext content is irrelevant here.
        plaintext[0] = 0x42;
        var tail = new byte[97];
        new Random(12).NextBytes(tail);

        var chunk = new byte[2048 + tail.Length];
        EncryptBfCbcChunk(plaintext, key).CopyTo(chunk, 0);
        tail.CopyTo(chunk, 2048);

        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var decrypted = service.DecryptChunk(chunk, Encoding.GetEncoding("ISO-8859-1").GetString(key));

        Assert.Equal(plaintext.Concat(tail), decrypted);
    }

    [Fact]
    public void DecryptChunk_UndecryptableChunk_ReturnsOriginal()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var chunk = new byte[2048];
        new Random(13).NextBytes(chunk);

        // Empty key makes the Blowfish engine throw; the service must fall back to the input.
        Assert.Equal(chunk, service.DecryptChunk(chunk, ""));
    }

    [Fact]
    public void DecryptChunks_EveryFullChunkIsDecryptedAndTailIsKept()
    {
        var key = CryptoService.GenerateBlowfishKey("7654321");
        var keyString = Encoding.GetEncoding("ISO-8859-1").GetString(key);
        var first = new byte[2048];
        var second = new byte[2048];
        var tail = new byte[1500];
        new Random(21).NextBytes(first);
        new Random(22).NextBytes(second);
        new Random(23).NextBytes(tail);
        first[0] = 0x11;
        second[0] = 0x22;

        var data = new byte[first.Length + second.Length + tail.Length];
        EncryptBfCbcChunk(first, key).CopyTo(data, 0);
        EncryptBfCbcChunk(second, key).CopyTo(data, 2048);
        tail.CopyTo(data, 4096);

        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var decrypted = service.DecryptChunks(data, keyString);

        Assert.Equal(first.Concat(second).Concat(tail), decrypted);
    }

    [Fact]
    public void DecryptChunks_NullOrEmpty_ReturnsEmptyArray()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        Assert.Empty(service.DecryptChunks(null!, "key"));
        Assert.Empty(service.DecryptChunks(Array.Empty<byte>(), "key"));
    }

    [Fact]
    public async Task DecryptChunkAsync_ByteKeyOverload_MatchesStringOverload()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var key = CryptoService.GenerateBlowfishKey("9999999");
        var plaintext = new byte[2048];
        new Random(31).NextBytes(plaintext);
        plaintext[0] = 0x7F;
        var chunk = EncryptBfCbcChunk(plaintext, key);

        var byString = await service.DecryptChunkAsync(chunk, Encoding.GetEncoding("ISO-8859-1").GetString(key));
        var byBytes = await service.DecryptChunkAsync(chunk, key);

        Assert.Equal(plaintext, byString);
        Assert.Equal(plaintext, byBytes);
    }

    [Fact]
    public async Task DecryptChunkAsync_ShortChunk_PassesThrough()
    {
        var service = new CryptoService(NullLogger<CryptoService>.Instance);
        var chunk = new byte[1024];
        new Random(32).NextBytes(chunk);

        Assert.Equal(chunk, await service.DecryptChunkAsync(chunk, "key"));
    }

    #endregion

    #region Backward-compatibility statics

    [Fact]
    public void BackwardCompatibleStatics_DelegateToInstanceBehavior()
    {
        Assert.Equal(CryptoService.GenerateMd5("abc"), CryptoService.GenerateMd5Static("abc"));
        Assert.Equal(CryptoService.GenerateBlowfishKeyString("42"), CryptoService.GenerateBlowfishKeyStatic("42"));
        Assert.Equal(CryptoService.GenerateBlowfishKey("42"), CryptoService.GenerateBlowfishKeyBytesStatic("42"));
        Assert.Equal(
            CryptoService.GenerateStreamPath("1", "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", "10", "MP3_128"),
            CryptoService.GenerateStreamPathStatic("1", "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", "10", "MP3_128"));

        var encrypted = CryptoService.EcbEncryptStatic(EcbKey, "0123456789abcdef");
        Assert.Equal("0123456789abcdef", CryptoService.EcbDecryptStatic(EcbKey, encrypted));
    }

    #endregion
}
