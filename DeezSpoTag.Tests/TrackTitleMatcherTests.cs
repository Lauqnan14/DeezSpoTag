using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackTitleMatcherTests
{
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
}
