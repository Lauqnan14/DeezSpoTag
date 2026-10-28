using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BandcampGenresTest
{
    [Theory]
    [InlineData("house")]
    [InlineData("pop rock")]
    [InlineData("drum n bass")]
    [InlineData("rhythm & blues")]
    [InlineData("hip hop")]
    [InlineData("lo-fi")]
    [InlineData("synth-pop")]
    public void IsKnownGenre_AcceptsCataloguedGenres(string genre)
    {
        Assert.True(BandcampGenres.IsKnownGenre(genre));
    }

    [Theory]
    [InlineData("HOUSE")]
    [InlineData("Pop Rock")]
    [InlineData("HEAVY METAL")]
    public void IsKnownGenre_IsCaseInsensitive(string genre)
    {
        Assert.True(BandcampGenres.IsKnownGenre(genre));
    }

    [Theory]
    [InlineData("notagenre")]
    [InlineData("hyper polka fusion")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsKnownGenre_RejectsUnknownOrBlankValues(string genre)
    {
        Assert.False(BandcampGenres.IsKnownGenre(genre));
    }

    [Fact]
    public void IsKnownGenre_NullValue_ReturnsFalse()
    {
        Assert.False(BandcampGenres.IsKnownGenre(null));
    }

    [Fact]
    public void IsKnownGenre_TrimsSurroundingWhitespace()
    {
        Assert.True(BandcampGenres.IsKnownGenre(" house "));
        Assert.True(BandcampGenres.IsKnownGenre("\thouse\t"));
    }
}
