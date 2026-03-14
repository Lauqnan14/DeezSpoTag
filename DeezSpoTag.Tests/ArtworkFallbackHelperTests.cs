using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtworkFallbackHelperTests
{
    [Fact]
    public void ShouldRejectAlbumArtworkCandidate_AllowsTrailingSingleSuffix()
    {
        var rejected = ArtworkFallbackHelper.ShouldRejectAlbumArtworkCandidate(
            "(When You Gonna) Give It Up to Me",
            "(When You Gonna) Give It Up to Me - Single");

        Assert.False(rejected);
    }

    [Fact]
    public void ShouldRejectAlbumArtworkCandidate_AllowsBracketedSingleSuffix()
    {
        var rejected = ArtworkFallbackHelper.ShouldRejectAlbumArtworkCandidate(
            "(When You Gonna) Give It Up to Me",
            "(When You Gonna) Give It Up to Me (Single)");

        Assert.False(rejected);
    }

    [Fact]
    public void ShouldRejectAlbumArtworkCandidate_StillRejectsCompilation()
    {
        var rejected = ArtworkFallbackHelper.ShouldRejectAlbumArtworkCandidate(
            "(When You Gonna) Give It Up to Me",
            "Only Hits");

        Assert.True(rejected);
    }
}
