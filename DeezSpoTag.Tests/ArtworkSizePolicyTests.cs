using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtworkSizePolicyTests
{
    [Theory]
    [InlineData("spotify", 640)]
    [InlineData("deezer", 1000)]
    [InlineData("qobuz", 999)]
    [InlineData("lastfm", 500)]
    public void LargeRequestIsClampedToWhatTheProviderCanActuallyServe(string provider, int expected)
    {
        Assert.Equal(expected, ArtworkSizePolicy.ResolveRequestSize(5000, provider));
    }

    [Theory]
    [InlineData("apple")]
    [InlineData("itunes")]
    [InlineData("")]
    public void ProvidersThatCapThemselvesKeepTheRequestedSize(string provider)
    {
        Assert.Equal(5000, ArtworkSizePolicy.ResolveRequestSize(5000, provider));
        Assert.True(ArtworkSizePolicy.ServesBestAvailable(provider));
    }

    [Fact]
    public void SmallRequestIsNeverInflatedToTheProviderCeiling()
    {
        Assert.Equal(300, ArtworkSizePolicy.ResolveRequestSize(300, "deezer"));
        Assert.Equal(300, ArtworkSizePolicy.ResolveRequestSize(300, "spotify"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidSizeFallsBackToTheDefault(int desired)
    {
        Assert.Equal(ArtworkSizePolicy.DefaultRequestSize, ArtworkSizePolicy.ResolveRequestSize(desired, "apple"));
    }

    [Fact]
    public void ProviderMatchingIgnoresCaseAndWhitespace()
    {
        Assert.Equal(640, ArtworkSizePolicy.ResolveRequestSize(5000, "  Spotify  "));
        Assert.Equal(500, ArtworkSizePolicy.ResolveRequestSize(5000, "Last.FM"));
    }
}
