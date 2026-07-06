using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackTitleMatcherTests
{
    [Theory]
    [InlineData("i am > i was", "i am > i was (Dolby Atmos Version)")]
    [InlineData("LOVE? (Deluxe Edition)", "LOVE? (Deluxe Edition) (Atmos Version)")]
    public void TitlesMatch_IgnoresAtmosEditionMarker(string expected, string actual)
        => Assert.True(TrackTitleMatcher.TitlesMatch(expected, actual));

    [Fact]
    public void RemoveAtmosVersionMarker_RemovesOnlyTrailingAtmosEdition()
        => Assert.Equal(
            "i am > i was",
            TrackTitleMatcher.RemoveAtmosVersionMarker("i am > i was (Dolby Atmos Version)"));

    [Theory]
    [InlineData("Je ?", "Je?")]
    [InlineData("Je ?", "Je _")]
    [InlineData("Purple Pills", "Purple_Pills")]
    [InlineData("P.I.M.P.", "PIMP")]
    public void TitlesMatch_TreatsPunctuationAndFilenameSafeVariantsAsSameTitle(
        string expected,
        string actual)
    {
        Assert.True(TrackTitleMatcher.TitlesMatch(expected, actual));
        Assert.True(TrackTitleMatcher.TitlesMatch(actual, expected));
    }

    [Theory]
    [InlineData("Purple Pills", "Purple Hills")]
    [InlineData("Je ?", "Jenny")]
    public void TitlesMatch_DoesNotMatchDifferentTitlesAfterPunctuationNormalization(
        string expected,
        string actual)
        => Assert.False(TrackTitleMatcher.TitlesMatch(expected, actual));

    [Theory]
    [InlineData("JAŸ-Z", "Jay Z")]
    [InlineData("Mike WiLL Made-It", "Mike Will Made It")]
    public void StrictArtistsMatch_TreatsCrossServicePunctuationAndDiacriticsAsSameArtist(
        string expected,
        string actual)
    {
        Assert.True(TrackTitleMatcher.StrictArtistsMatch(expected, actual));
        Assert.True(TrackTitleMatcher.ArtistsMatch(expected, actual));
    }

    [Fact]
    public void StrictArtistsMatch_DoesNotMatchUnrelatedArtists()
        => Assert.False(TrackTitleMatcher.StrictArtistsMatch("Jay Z", "Sonny Rollins"));
}
