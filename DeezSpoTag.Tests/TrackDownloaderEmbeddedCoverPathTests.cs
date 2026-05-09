using System;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackDownloaderEmbeddedCoverPathTests
{
    [Fact]
    public void ApplyEmbeddedCoverPathForTagging_CopiesContextAlbumCoverOntoTrackAlbum()
    {
        var trackAlbum = new Album("Track Album");
        var contextAlbum = new Album("Context Album");
        var track = new Track
        {
            Title = "All Over You",
            Album = trackAlbum
        };

        InvokePrivateStatic(
            "ApplyEmbeddedCoverPathForTagging",
            track,
            contextAlbum,
            "/tmp/deezspotag-embedded-cover.jpg");

        Assert.Equal("/tmp/deezspotag-embedded-cover.jpg", contextAlbum.EmbeddedCoverPath);
        Assert.Same(trackAlbum, track.Album);
        Assert.Equal("/tmp/deezspotag-embedded-cover.jpg", track.Album.EmbeddedCoverPath);
    }

    [Fact]
    public void EnsureTrackEmbeddedCoverPathForTagging_UsesContextAlbumWhenTrackAlbumIsMissingCover()
    {
        var trackAlbum = new Album("Track Album");
        var contextAlbum = new Album("Context Album")
        {
            EmbeddedCoverPath = "/tmp/deezspotag-context-cover.jpg"
        };
        var track = new Track
        {
            Title = "All Over You",
            Album = trackAlbum
        };

        InvokePrivateStatic(
            "EnsureTrackEmbeddedCoverPathForTagging",
            track,
            contextAlbum);

        Assert.Equal("/tmp/deezspotag-context-cover.jpg", track.Album!.EmbeddedCoverPath);
    }

    [Fact]
    public void EnsureTrackEmbeddedCoverPathForTagging_DoesNotReplaceExistingTrackAlbumCover()
    {
        var trackAlbum = new Album("Track Album")
        {
            EmbeddedCoverPath = "/tmp/deezspotag-existing-cover.jpg"
        };
        var contextAlbum = new Album("Context Album")
        {
            EmbeddedCoverPath = "/tmp/deezspotag-context-cover.jpg"
        };
        var track = new Track
        {
            Title = "All Over You",
            Album = trackAlbum
        };

        InvokePrivateStatic(
            "EnsureTrackEmbeddedCoverPathForTagging",
            track,
            contextAlbum);

        Assert.Equal("/tmp/deezspotag-existing-cover.jpg", track.Album!.EmbeddedCoverPath);
    }

    private static void InvokePrivateStatic(string methodName, params object?[] args)
    {
        var method = typeof(TrackDownloader).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        _ = method.Invoke(null, args);
    }
}
