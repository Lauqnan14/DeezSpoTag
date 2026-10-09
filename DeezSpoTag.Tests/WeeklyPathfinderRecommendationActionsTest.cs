using System;
using System.Reflection;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Download.Shared.Models;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WeeklyPathfinderRecommendationActionsTest
{
    [Fact]
    public void WeeklyMonitorUsesNativeSpotifyIdentity()
    {
        const string id = "0123456789ABCDEFGHIJKL";
        var candidate = new PlaylistTrackCandidate("spotify:track:" + id, null, "Song", "Artist", "Album", null, 180000, null, Array.Empty<string>(), SourceUrl: "https://open.spotify.com/track/" + id);
        var method = typeof(WatchlistEngine).GetMethod("BuildWatchDownloadIntentFromCandidate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var intent = Assert.IsType<DownloadIntent>(method.Invoke(null, ["recommendations", candidate]));
        Assert.Equal("spotify", intent.SourceService);
        Assert.Equal(id, intent.SpotifyId);
        Assert.True(string.IsNullOrEmpty(intent.DeezerId));
        Assert.Equal(candidate.SourceUrl, intent.SourceUrl);
    }

    [Fact]
    public void WeeklyMonitorRetainsSavedDeezerMatch()
    {
        const string id = "0123456789ABCDEFGHIJKL";
        var candidate = new PlaylistTrackCandidate("spotify:track:" + id, null, "Song", "Artist", "Album", null,
            180000, null, Array.Empty<string>(), DeezerId: "12345", MappingStatus: "matched");
        var method = typeof(WatchlistEngine).GetMethod("BuildWatchDownloadIntentFromCandidate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var intent = Assert.IsType<DownloadIntent>(method.Invoke(null, ["recommendations", candidate]));
        Assert.Equal("spotify", intent.SourceService);
        Assert.Equal(id, intent.SpotifyId);
        Assert.Equal("12345", intent.DeezerId);
    }

    [Theory]
    [InlineData("weekly-rotation:l7:f9:weekly-missing-favourites", true)]
    [InlineData("weekly-rotation:l7:f9:weekly-similar-artists", true)]
    [InlineData("weekly-rotation:l0:f9:weekly-similar-artists", false)]
    [InlineData("weekly-rotation:l7:f9:unknown", false)]
    [InlineData("weekly-rotation:l7:f9:weekly-similar-artists:8", false)]
    public void WeeklyStationRejectsMalformedIdentity(string station, bool valid)
    {
        Assert.Equal(valid, LibraryRecommendationService.TryParseWeeklyStation(station, out var library, out var folder, out _));
        if (valid) { Assert.Equal(7, library); Assert.Equal(9, folder); }
    }
}
