using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class CoverPerceptualHashService
{
    private const int SacadMaxHammingDeltaExclusive = 2;
    private readonly int _hashWidth = 9;
    private readonly int _hashHeight = 8;
    private readonly int _maxHashBits = sizeof(ulong) * 8;

    public ulong? TryComputeHash(byte[] imageBytes)
    {
        if (imageBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            image.Mutate(ctx => ctx.Resize(new Size(_hashWidth, _hashHeight)).Grayscale());

            ulong hash = 0;
            var bitIndex = 0;
            for (var y = 0; y < _hashHeight; y++)
            {
                for (var x = 0; x < _hashWidth - 1; x++)
                {
                    var left = image[x, y].R;
                    var right = image[x + 1, y].R;
                    if (left > right)
                    {
                        hash |= 1UL << bitIndex;
                    }

                    bitIndex++;
                }
            }

            return hash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public double Similarity(ulong hashA, ulong hashB)
    {
        var distance = HammingDistance(hashA, hashB);
        var normalized = 1d - (distance / 64d);
        if (normalized < 0d)
        {
            return 0d;
        }

        if (normalized > 1d)
        {
            return 1d;
        }

        return normalized;
    }

    public int HammingDistance(ulong hashA, ulong hashB)
    {
        return Math.Min(BitOperations.PopCount(hashA ^ hashB), _maxHashBits);
    }

    public bool IsSacadSimilar(ulong hashA, ulong hashB)
    {
        return HammingDistance(hashA, hashB) < SacadMaxHammingDeltaExclusive;
    }
}
