using DeezSpoTag.Services.Download.Shared;
using System.IO;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtworkAssetProcessorTest
{
    [Fact]
    public async Task PrepareAsync_ProducesSizedSidecarsAndIndependentEmbeddedVariant()
    {
        await using var source = new MemoryStream();
        using (var image = new Image<Rgba32>(1600, 1200))
        {
            await image.SaveAsJpegAsync(source, new JpegEncoder { Quality = 95 });
        }

        var result = await ArtworkAssetProcessor.PrepareAsync(new ArtworkAssetRequest(
            source.ToArray(), "jpg,png,webp", 1000, 600, false, 72, ".flac"));

        Assert.Equal(3, result.Sidecars.Count);
        Assert.All(result.Sidecars.Values, variant => Assert.Equal((1000, 750), (variant.Width, variant.Height)));
        Assert.Equal((600, 450), (result.Embedded.Width, result.Embedded.Height));
        Assert.Equal("image/jpeg", result.Embedded.MimeType);
        Assert.Equal("image/webp", result.Sidecars["webp"].MimeType);
    }

    [Fact]
    public async Task PrepareAsync_MaxQualityNeverUpscalesSource()
    {
        await using var source = new MemoryStream();
        using (var image = new Image<Rgba32>(320, 320)) await image.SaveAsJpegAsync(source);
        var result = await ArtworkAssetProcessor.PrepareAsync(new ArtworkAssetRequest(
            source.ToArray(), "webp", 1200, 800, true, 90, ".flac"));
        Assert.Equal((320, 320), (result.Embedded.Width, result.Embedded.Height));
        Assert.Equal("webp", result.Embedded.Extension);
    }
}
