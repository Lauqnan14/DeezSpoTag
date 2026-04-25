using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerMetadataResolverTitleMappingTests
{
    [Fact]
    public void ApplyTrackFields_PrefersFullTitle_OverTitleShort()
    {
        var method = typeof(DeezerMetadataResolver).GetMethod(
            "ApplyTrackFields",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var track = new Track();
        var deezerTrack = new ApiTrack
        {
            Id = "8081684",
            Title = "What's My Name? (feat. Drake)",
            TitleShort = "What's My Name?"
        };

        method!.Invoke(null, new object[] { track, deezerTrack });

        Assert.Equal("What's My Name? (feat. Drake)", track.Title);
    }
}
