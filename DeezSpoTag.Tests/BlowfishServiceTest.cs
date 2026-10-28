using System;
using System.Text;
using DeezSpoTag.Services.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BlowfishServiceTest
{
    private static readonly byte[] DeezerIv = { 0, 1, 2, 3, 4, 5, 6, 7 };

    /// <summary>
    /// Independent oracle: BouncyCastle's own Blowfish CBC implementation. Decrypting a
    /// BouncyCastle-produced ciphertext with BlowfishService must reproduce the plaintext,
    /// which validates key schedule, block ops and CBC chaining against a second implementation.
    /// </summary>
    private static byte[] EncryptWithBouncyCastle(byte[] plaintext, byte[] key)
    {
        var cipher = new CbcBlockCipher(new BlowfishEngine());
        cipher.Init(true, new ParametersWithIV(new KeyParameter(key), DeezerIv));
        var output = new byte[plaintext.Length];
        for (var offset = 0; offset < plaintext.Length; offset += 8)
        {
            cipher.ProcessBlock(plaintext, offset, output, offset);
        }

        return output;
    }

    private static byte[] BlowfishKeyForTrack(string trackId)
        => CryptoService.GenerateBlowfishKey(trackId);

    [Fact]
    public void DecryptCBC_KnownBlowfishVector_ZeroKey()
    {
        // Published Blowfish ECB vector: key = 64 zero bits, plaintext = 64 zero bits,
        // ciphertext = 4EF99745 6198DD78. CBC with an all-zero IV reduces to ECB for the
        // first block, so this isolates the core cipher from any chaining questions.
        var key = new byte[8];
        var iv = new byte[8];
        var ciphertext = new byte[]
        {
            0x4E, 0xF9, 0x97, 0x45, 0x61, 0x98, 0xDD, 0x78,
        };

        var decrypted = new BlowfishService(key).DecryptCBC(ciphertext, iv);

        Assert.Equal(new byte[8], decrypted);
    }

    [Fact]
    public void DecryptCBC_KnownBlowfishVector_ClassicKey()
    {
        // Published vector (cross-checked with PyCryptodome and BouncyCastle):
        // key = 0123456789ABCDEFF0E1D2C3B4A59687,
        // plaintext F9FAD597C7DF60B7 -> ciphertext 254E62A0317CEB00.
        var key = Convert.FromHexString("0123456789ABCDEFF0E1D2C3B4A59687");
        var iv = new byte[8];
        var ciphertext = Convert.FromHexString("254E62A0317CEB00");

        var decrypted = new BlowfishService(key).DecryptCBC(ciphertext, iv);

        Assert.Equal(Convert.FromHexString("F9FAD597C7DF60B7"), decrypted);
    }

    [Fact]
    public void DecryptCBC_ReversesBouncyCastleEncryption_SingleBlock()
    {
        var key = BlowfishKeyForTrack("1234567");
        var plaintext = Encoding.ASCII.GetBytes("AAAAAAAA");
        var ciphertext = EncryptWithBouncyCastle(plaintext, key);

        var decrypted = new BlowfishService(key).DecryptCBC(ciphertext, DeezerIv);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DecryptCBC_ReversesBouncyCastleEncryption_MultipleBlocksChained()
    {
        var key = BlowfishKeyForTrack("9876543");
        var plaintext = new byte[8 * 5];
        new Random(20261028).NextBytes(plaintext);
        var ciphertext = EncryptWithBouncyCastle(plaintext, key);

        var decrypted = new BlowfishService(key).DecryptCBC(ciphertext, DeezerIv);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DecryptCBC_IvIsPartOfTheChain_DifferentIvYieldsDifferentPlaintext()
    {
        var key = BlowfishKeyForTrack("5555555");
        var plaintext = new byte[16];
        new Random(7).NextBytes(plaintext);
        var ciphertext = EncryptWithBouncyCastle(plaintext, key);

        var withDeezerIv = new BlowfishService(key).DecryptCBC(ciphertext, DeezerIv);
        var withOtherIv = new BlowfishService(key).DecryptCBC(ciphertext, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 });

        Assert.NotEqual(withDeezerIv, withOtherIv);
        Assert.Equal(plaintext, withDeezerIv);
    }

    [Fact]
    public void DecryptCBC_EmptyData_ReturnsEmptyArray()
    {
        Assert.Empty(new BlowfishService(BlowfishKeyForTrack("1")).DecryptCBC(Array.Empty<byte>(), DeezerIv));
    }

    [Fact]
    public void DecryptCBC_NullData_ReturnsEmptyArray()
    {
        Assert.Empty(new BlowfishService(BlowfishKeyForTrack("1")).DecryptCBC(null!, DeezerIv));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(15)]
    public void DecryptCBC_NonBlockAlignedData_Throws(int length)
    {
        var service = new BlowfishService(BlowfishKeyForTrack("1"));
        Assert.Throws<ArgumentException>(() => service.DecryptCBC(new byte[length], DeezerIv));
    }

    [Fact]
    public void DecryptCBC_WrongIvLength_Throws()
    {
        var service = new BlowfishService(BlowfishKeyForTrack("1"));
        Assert.Throws<ArgumentException>(() => service.DecryptCBC(new byte[8], new byte[4]));
    }
}
