using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtworkFallbackHelperTests
{
    [Fact]
    public void TrackDownloaderArtistArtworkOrder_RespectsConfiguredSpotifyFirstOrder()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "spotify,apple,deezer"
        };

        var method = typeof(TrackDownloader).GetMethod(
            "ResolveArtistArtworkFallbackOrder",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var order = Assert.IsAssignableFrom<List<string>>(method!.Invoke(null, new object[] { settings }));

        Assert.Equal(["spotify", "apple", "deezer"], order);
    }

    [Fact]
    public void LibraryArtistImageQueueOrder_RespectsConfiguredSpotifyFirstOrder()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "spotify,apple,deezer"
        };

        var order = LibraryArtistImageQueueService.ResolveArtistArtworkOrder(settings);

        Assert.Equal(["spotify", "apple", "deezer"], order);
    }

    [Fact]
    public void AlbumArtworkOrder_DoesNotIncludeLastFm()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = true,
            ArtworkFallbackOrder = "lastfm,apple,deezer"
        };

        var order = ArtworkFallbackHelper.ResolveOrder(settings);

        Assert.Equal(["apple", "deezer"], order);
    }

    [Fact]
    public void ArtistArtworkOrder_AllowsLastFm()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "last.fm,spotify,apple"
        };

        var order = ArtworkFallbackHelper.ResolveArtistOrder(settings);

        Assert.Equal(["lastfm", "spotify", "apple"], order);
    }

    [Fact]
    public void LibraryArtistImageQueueOrder_IncludesConfiguredLastFm()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "lastfm,spotify,apple"
        };

        var order = LibraryArtistImageQueueService.ResolveArtistArtworkOrder(settings);

        Assert.Equal(["lastfm", "spotify", "apple"], order);
    }

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
