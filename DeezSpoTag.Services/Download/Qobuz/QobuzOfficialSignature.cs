using System.Buffers.Binary;
using System.Text;

namespace DeezSpoTag.Services.Download.Qobuz;

public static class QobuzOfficialSignature
{
    private static readonly uint[] RoundConstants =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
        0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
        0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
        0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
        0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
        0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
        0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
        0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
        0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391
    ];

    private static readonly int[] RotationAmounts =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
    ];

    public static string ComputeProtocolDigestHex(string input)
    {
        var payload = PadMessage(Encoding.UTF8.GetBytes(input));
        var state = new DigestState();
        Span<uint> words = stackalloc uint[16];

        for (var offset = 0; offset < payload.Length; offset += 64)
        {
            LoadWords(payload.AsSpan(offset, 64), words);
            ProcessBlock(words, ref state);
        }

        Span<byte> digest = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest[..4], state.A);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.Slice(4, 4), state.B);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.Slice(8, 4), state.C);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.Slice(12, 4), state.D);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static byte[] PadMessage(byte[] input)
    {
        var originalBitLength = (ulong)input.Length * 8UL;
        var paddedLength = input.Length + 1;
        while (paddedLength % 64 != 56)
        {
            paddedLength++;
        }

        var padded = new byte[paddedLength + 8];
        input.CopyTo(padded, 0);
        padded[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(paddedLength, 8), originalBitLength);
        return padded;
    }

    private static void LoadWords(ReadOnlySpan<byte> block, Span<uint> words)
    {
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(index * 4, 4));
        }
    }

    private static void ProcessBlock(ReadOnlySpan<uint> words, ref DigestState state)
    {
        var a = state.A;
        var b = state.B;
        var c = state.C;
        var d = state.D;

        for (var index = 0; index < 64; index++)
        {
            var step = BuildStep(index, b, c, d);
            var next = d;
            d = c;
            c = b;
            b += RotateLeft(a + step.Function + RoundConstants[index] + words[step.WordIndex], RotationAmounts[index]);
            a = next;
        }

        state.A += a;
        state.B += b;
        state.C += c;
        state.D += d;
    }

    private static DigestStep BuildStep(int index, uint b, uint c, uint d)
    {
        if (index < 16)
        {
            return new DigestStep((b & c) | (~b & d), index);
        }

        if (index < 32)
        {
            return new DigestStep((d & b) | (~d & c), (5 * index + 1) % 16);
        }

        if (index < 48)
        {
            return new DigestStep(b ^ c ^ d, (3 * index + 5) % 16);
        }

        return new DigestStep(c ^ (b | ~d), (7 * index) % 16);
    }

    private static uint RotateLeft(uint value, int count)
        => (value << count) | (value >> (32 - count));

    private struct DigestState
    {
        public uint A = 0x67452301;
        public uint B = 0xefcdab89;
        public uint C = 0x98badcfe;
        public uint D = 0x10325476;

        public DigestState()
        {
        }
    }

    private readonly record struct DigestStep(uint Function, int WordIndex);
}
