using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagReleaseCategoryTests
{
    [Theory]
    [InlineData("single", "single")]
    [InlineData("Single", "single")]
    [InlineData("album", "album")]
    [InlineData("EP", "album")]
    [InlineData("Compilation", "album")]
    public void Resolve_UsesExplicitReleaseTypeWhenAvailable(string input, string expected)
    {
        Assert.Equal(expected, AutoTagReleaseCategory.Resolve(input, 12));
    }

    [Theory]
    [InlineData(1, "single")]
    [InlineData(2, "album")]
    [InlineData(14, "album")]
    public void Resolve_UsesTrackTotalWhenExplicitReleaseTypeIsMissing(int trackTotal, string expected)
    {
        Assert.Equal(expected, AutoTagReleaseCategory.Resolve(null, trackTotal));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenReleaseShapeIsUnknown()
    {
        Assert.Null(AutoTagReleaseCategory.Resolve(null, null));
    }
}
