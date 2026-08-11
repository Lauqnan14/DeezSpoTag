using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerPlaceholderArtworkTests
{
    [Theory]
    [InlineData("https://e-cdns-images.dzcdn.net/images/artist//500x500-000000-80-0-0.jpg")]
    [InlineData("https://e-cdns-images.dzcdn.net/images/artist//1000x1000-000000-80-0-0.jpg")]
    [InlineData("https://cdn-images.dzcdn.net/images/cover//500x500-000000-80-0-0.jpg")]
    public void EmptyHashPlaceholdersAreBlocked(string url)
    {
        Assert.False(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(url));
    }

    [Theory]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("522c7b1de6d02790c348da447d3fd2b7")]
    [InlineData("c34f636093a87af8fd7dda0a10184280")]
    public void KnownPlaceholderHashesAreBlocked(string hash)
    {
        Assert.False(DeezerImageUrlValidator.HasUsableDeezerMd5(hash));
        Assert.False(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(
            $"https://e-cdns-images.dzcdn.net/images/artist/{hash}/1000x1000-000000-80-0-0.jpg"));
    }

    [Fact]
    public void RealArtworkIsStillAllowed()
    {
        Assert.True(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(
            "https://e-cdns-images.dzcdn.net/images/artist/31b52d923da9434d4b9bde58b896fd97/1000x1000-000000-80-0-0.jpg"));
        Assert.True(DeezerImageUrlValidator.HasUsableDeezerMd5("31b52d923da9434d4b9bde58b896fd97"));
    }

    [Fact]
    public void NonDeezerHostsAreNotRejected()
    {
        Assert.True(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(
            "https://is1-ssl.mzstatic.com/image/thumb/Music/v4/aa/bb/cc/file.jpg/1200x1200bb.jpg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingUrlsAreBlocked(string? url)
    {
        Assert.False(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(url));
    }
}
