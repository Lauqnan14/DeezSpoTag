using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Web.Services;

internal static class ArtistArtworkTextInspector
{
    public static bool LikelyContainsOverlayText(Image image)
    {
        using var sampled = image.CloneAs<Rgba32>();
        return LikelyContainsOverlayText(sampled);
    }

    public static bool LikelyContainsOverlayText(Image<Rgba32> image)
    {
        const int maxWidth = 384;
        using var sampled = image.CloneAs<Rgba32>();
        if (sampled.Width > maxWidth)
        {
            var resizedHeight = Math.Max(1, (int)Math.Round(sampled.Height * (maxWidth / (double)sampled.Width)));
            sampled.Mutate(ctx => ctx.Resize(maxWidth, resizedHeight));
        }

        if (sampled.Width < 48 || sampled.Height < 48)
        {
            return false;
        }

        var topBandHeight = Math.Max(8, (int)Math.Round(sampled.Height * 0.22));
        var bottomBandStart = Math.Max(0, sampled.Height - topBandHeight);
        var middleStart = topBandHeight;
        var middleHeight = Math.Max(8, bottomBandStart - middleStart);

        var top = AnalyzeBand(sampled, 0, topBandHeight);
        var middle = AnalyzeBand(sampled, middleStart, middleHeight);
        var bottom = AnalyzeBand(sampled, bottomBandStart, sampled.Height - bottomBandStart);

        return IsTextHeavyBand(top, middle)
            || IsTextHeavyBand(bottom, middle)
            || IsLikelyAlbumCoverWithTitle(sampled);
    }

    private static bool IsLikelyAlbumCoverWithTitle(Image<Rgba32> image)
    {
        if (image.Height <= 0)
        {
            return false;
        }

        var aspect = image.Width / (double)image.Height;
        if (aspect < 0.85 || aspect > 1.15)
        {
            return false;
        }

        var start = (int)Math.Round(image.Height * 0.28);
        var height = Math.Max(8, (int)Math.Round(image.Height * 0.44));
        var rates = RowJumpRates(image, start, height);
        if (rates.Count == 0)
        {
            return false;
        }

        var ordered = rates.OrderBy(static rate => rate).ToList();
        var median = ordered[ordered.Count / 2];
        var max = ordered[^1];
        return (median < 0.10 && max >= 0.16)
               || (median < 0.18 && max >= 0.25 && max >= median * 1.8);
    }

    private static List<double> RowJumpRates(Image<Rgba32> image, int startRow, int height)
    {
        const double jumpThreshold = 20d;
        var yStart = Math.Max(0, startRow);
        var yEnd = Math.Min(image.Height, startRow + Math.Max(1, height));
        var rates = new List<double>(Math.Max(0, yEnd - yStart));
        if (yEnd <= yStart || image.Width < 2)
        {
            return rates;
        }

        for (var y = yStart; y < yEnd; y++)
        {
            var jumps = 0;
            var previous = GetLuminance(image[0, y]);
            for (var x = 1; x < image.Width; x++)
            {
                var current = GetLuminance(image[x, y]);
                if (Math.Abs(current - previous) >= jumpThreshold)
                {
                    jumps++;
                    previous = current;
                }
            }

            rates.Add(jumps / (double)image.Width);
        }

        return rates;
    }

    private readonly record struct ArtworkBandAnalysis(double EdgeDensity, double TransitionDensity);

    private static ArtworkBandAnalysis AnalyzeBand(Image<Rgba32> image, int startRow, int height)
    {
        var yStart = Math.Max(1, startRow);
        var yEnd = Math.Min(image.Height - 1, startRow + Math.Max(1, height));
        if (yEnd <= yStart)
        {
            return new ArtworkBandAnalysis(0, 0);
        }

        var totalPixels = 0;
        var edgePixels = 0;
        var transitions = 0;
        var rows = 0;

        for (var y = yStart; y < yEnd; y++)
        {
            rows++;
            var previousEdge = false;
            var rowTransitions = 0;

            for (var x = 1; x < image.Width - 1; x++)
            {
                var current = image[x, y];
                var right = image[x + 1, y];
                var down = image[x, y + 1];
                var edge = Math.Abs(GetLuminance(current) - GetLuminance(right))
                           + Math.Abs(GetLuminance(current) - GetLuminance(down)) >= 95;

                totalPixels++;
                if (edge)
                {
                    edgePixels++;
                }

                if (x > 1 && edge != previousEdge)
                {
                    rowTransitions++;
                }

                previousEdge = edge;
            }

            transitions += rowTransitions;
        }

        if (totalPixels <= 0 || rows <= 0)
        {
            return new ArtworkBandAnalysis(0, 0);
        }

        return new ArtworkBandAnalysis(
            edgePixels / (double)totalPixels,
            transitions / ((double)rows * Math.Max(1, image.Width - 2)));
    }

    private static bool IsTextHeavyBand(ArtworkBandAnalysis band, ArtworkBandAnalysis middle)
        => band.EdgeDensity >= 0.135
            && band.TransitionDensity >= 0.18
            && band.EdgeDensity >= Math.Max(0.04, middle.EdgeDensity) * 1.45
            && band.TransitionDensity >= Math.Max(0.06, middle.TransitionDensity) * 1.35;

    private static double GetLuminance(Rgba32 pixel)
        => (pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114);
}
