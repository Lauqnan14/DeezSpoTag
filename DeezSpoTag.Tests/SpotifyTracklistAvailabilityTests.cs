using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyTracklistAvailabilityTests
{
    [Fact]
    public void CreateTracklistTrackFromSummary_PreservesSpotifyId_WhenIdIsDeezerMatch()
    {
        var summary = new SpotifyTrackSummary(
            "5sTnOnK4nYx3jzD0aHkV2W",
            "Not Like Us",
            "Kendrick Lamar",
            "Not Like Us",
            274000,
            "https://open.spotify.com/track/5sTnOnK4nYx3jzD0aHkV2W",
            null,
            "USUG12400910");

        var track = SpotifyTracklistService.CreateTracklistTrackFromSummary(
            summary,
            index: 0,
            id: "3021278461",
            preview: "/api/deezer/stream/3021278461");

        Assert.Equal("3021278461", track.Id);
        Assert.Equal("5sTnOnK4nYx3jzD0aHkV2W", track.SpotifyId);
        Assert.Equal("https://open.spotify.com/track/5sTnOnK4nYx3jzD0aHkV2W", track.Link);
    }
}
