using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtworkFormatPolicyTest
{
    [Theory]
    [InlineData("both", "jpg,png")]
    [InlineData(".WEBP,jpeg,png,jpg", "jpg,png,webp")]
    [InlineData("unknown", "jpg")]
    [InlineData("", "jpg")]
    public void Normalize_ReturnsCanonicalArtworkFormats(string raw, string expected)
        => Assert.Equal(expected, ArtworkFormatPolicy.Normalize(raw));

    [Fact]
    public void ResolveEmbeddedFormat_PrefersJpegWhenSeveralFormatsAreSelected()
        => Assert.Equal("jpg", ArtworkFormatPolicy.ResolveEmbeddedFormat("png,webp,jpg", ".m4a"));

    [Theory]
    [InlineData("webp", ".flac", "webp")]
    [InlineData("png", ".mp3", "png")]
    public void ResolveEmbeddedFormat_PreservesSingleSupportedSelection(string raw, string extension, string expected)
        => Assert.Equal(expected, ArtworkFormatPolicy.ResolveEmbeddedFormat(raw, extension));

    [Fact]
    public void AppleArtwork_WebpSelectionUsesWebpUrlAndJpegFallback()
    {
        const string source = "https://is1-ssl.mzstatic.com/image/thumb/Music/cover/{w}x{h}bb.jpg";
        var settings = new DeezSpoTagSettings { LocalArtworkFormat = "webp" };

        Assert.Equal("webp", AppleQueueHelpers.GetAppleArtworkFormat(settings));
        Assert.EndsWith(".webp", AppleQueueHelpers.BuildAppleArtworkUrl(source, "1200x1200", 1200, 1200, "webp"));
        Assert.EndsWith(".jpg", AppleQueueHelpers.BuildAppleArtworkFallbackUrl(source, "1200x1200", 1200, 1200, "webp"));
    }
}
