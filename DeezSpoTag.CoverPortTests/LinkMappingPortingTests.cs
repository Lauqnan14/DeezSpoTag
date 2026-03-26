using DeezSpoTag.Web.Services.LinkMapping;
using Xunit;

namespace DeezSpoTag.CoverPortTests;

public sealed class LinkMappingPortingTests
{
    [Theory]
    [InlineData("https://open.spotify.com/track/3AhXZa8sUQht0UEdBJgpGc", ExternalLinkSource.Spotify)]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", ExternalLinkSource.YouTube)]
    [InlineData("https://music.apple.com/us/album/x/123456789?i=123456790", ExternalLinkSource.AppleMusic)]
    [InlineData("https://www.deezer.com/track/917265", ExternalLinkSource.Deezer)]
    [InlineData("https://soundcloud.com/artist-name/track-name", ExternalLinkSource.SoundCloud)]
    [InlineData("https://tidal.com/browse/track/148583382", ExternalLinkSource.Tidal)]
    [InlineData("https://www.qobuz.com/us-en/album/album-title/ab12cd34", ExternalLinkSource.Qobuz)]
    [InlineData("https://artist.bandcamp.com/track/track-name", ExternalLinkSource.Bandcamp)]
    [InlineData("https://www.pandora.com/artist/artist-name/AR123456789", ExternalLinkSource.Pandora)]
    [InlineData("https://www.boomplay.com/songs/123456", ExternalLinkSource.Boomplay)]
    public void Classifier_DetectsKnownSources(string url, ExternalLinkSource expected)
    {
        var actual = ExternalLinkClassifier.Classify(url);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("https://www.deezer.com/track/917265", "track", "917265")]
    [InlineData("https://www.deezer.com/us/album/302127", "album", "302127")]
    [InlineData("https://www.deezer.com/artist/27", "artist", "27")]
    [InlineData("https://www.deezer.com/playlist/908622995", "playlist", "908622995")]
    [InlineData("https://www.deezer.com/show/143", "show", "143")]
    public void DeezerParser_ParsesTypeAndId(string url, string expectedType, string expectedId)
    {
        var parsed = DeezerLinkParser.TryParse(url, out var descriptor);

        Assert.True(parsed);
        Assert.Equal(expectedType, descriptor.Type);
        Assert.Equal(expectedId, descriptor.Id);
    }

    [Fact]
    public void DeezerParser_RejectsNonDeezerHost()
    {
        var parsed = DeezerLinkParser.TryParse("https://open.spotify.com/track/3AhXZa8sUQht0UEdBJgpGc", out _);
        Assert.False(parsed);
    }

    [Fact]
    public void DeezerParser_RejectsNonNumericIds()
    {
        var parsed = DeezerLinkParser.TryParse("https://www.deezer.com/track/pl.d25f5d1181894928af76c85c967f8f31", out _);
        Assert.False(parsed);
    }
}
