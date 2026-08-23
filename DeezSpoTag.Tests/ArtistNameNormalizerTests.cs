using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistNameNormalizerTests
{
    [Theory]
    [InlineData("Artist x Guest", "Artist", "Guest")]
    [InlineData("Artist X Guest", "Artist", "Guest")]
    [InlineData("Artist  x  Guest", "Artist", "Guest")]
    public void ExpandArtistNames_KeepsStandaloneXAsCollaborationSeparator(
        string credit,
        string expectedPrimary,
        string expectedAdditional)
    {
        var artists = ArtistNameNormalizer.ExpandArtistNames([credit]);

        Assert.Equal([expectedPrimary, expectedAdditional], artists);
        Assert.Equal(expectedPrimary, ArtistNameNormalizer.ExtractPrimaryArtist(credit));
    }

    [Theory]
    [InlineData("X.O")]
    [InlineData("X.O;SUNS3T")]
    [InlineData("X.O; SUNS3T")]
    [InlineData("X.O x SUNS3T")]
    public void ExpandArtistNames_DoesNotSplitInitialedArtistNamesAtX(string credit)
    {
        var artists = ArtistNameNormalizer.ExpandArtistNames([credit]);

        Assert.Equal("X.O", artists[0]);
        Assert.Equal("X.O", ArtistNameNormalizer.ExtractPrimaryArtist(credit));
    }
}
