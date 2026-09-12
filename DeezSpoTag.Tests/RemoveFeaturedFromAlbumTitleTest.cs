using System;
using System.Collections.Generic;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Coverage for the "Remove featured artists from album title" option.
///
/// The album title is not only a tag: the default album folder template is "%album%", so the
/// album title is also the album folder name. This option must work independently of the
/// Featured To Title setting, so it can be combined with any of its options.
/// </summary>
public sealed class RemoveFeaturedFromAlbumTitleTest
{
    private static Track MakeTrack(string title)
        => new()
        {
            Title = title,
            MainArtist = new Artist("2Baba"),
            Album = new Album("album-1", title),
            Artist = new Dictionary<string, List<string>>
            {
                ["Main"] = new List<string> { "2Baba" },
                ["Featured"] = new List<string> { "Falz" }
            },
            Artists = new List<string> { "2Baba", "Falz" }
        };

    private static DeezSpoTagSettings Settings(string featuredToTitle, bool removeFromAlbum)
        => new()
        {
            FeaturedToTitle = featuredToTitle,
            RemoveFeaturedFromAlbumTitle = removeFromAlbum,
            Tags = new TagSettings { MultiArtistSeparator = "default" }
        };

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void Off_LeavesTheAlbumTitleUntouched(string featuredToTitle)
    {
        var track = MakeTrack("Rise Up (feat. Falz)");

        // Modes "1" and "2" do not clean the album themselves, so they isolate this toggle.
        // ("No" / "0" already cleans the album by design, independently of this option.)
        track.ApplySettings(Settings(featuredToTitle, removeFromAlbum: false));

        Assert.Equal("Rise Up (feat. Falz)", track.Album!.Title);
    }

    [Fact]
    public void No_AlreadyCleansTheAlbumWithoutThisOption()
    {
        var track = MakeTrack("Rise Up (feat. Falz)");

        track.ApplySettings(Settings("0", removeFromAlbum: false));

        Assert.Equal("Rise Up", track.Album!.Title);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void On_RemovesTheFeaturedArtistFromTheAlbumTitleInEveryTitleMode(string featuredToTitle)
    {
        var track = MakeTrack("Rise Up (feat. Falz)");

        track.ApplySettings(Settings(featuredToTitle, removeFromAlbum: true));

        Assert.Equal("Rise Up", track.Album!.Title);
    }

    [Fact]
    public void On_CombinesWithCopyToTitle_SoTheTrackKeepsTheCreditButTheAlbumDoesNot()
    {
        var track = MakeTrack("Rise Up (feat. Falz)");

        // FeaturedToTitle "2" normalises the credit into the track title; the album must still
        // come out clean, which is the combination that did not exist before.
        track.ApplySettings(Settings("2", removeFromAlbum: true));

        Assert.Equal("Rise Up (feat. Falz)", track.Title);
        Assert.Equal("Rise Up", track.Album!.Title);
    }

    [Theory]
    [InlineData("Rise Up (feat. Falz)")]
    [InlineData("Rise Up (ft. Falz)")]
    [InlineData("Rise Up (featuring Falz)")]
    [InlineData("Rise Up [feat. Falz]")]
    [InlineData("Rise Up feat. Falz")]
    public void On_HandlesEveryCreditSpellingInTheAlbumTitle(string albumTitle)
    {
        var track = MakeTrack(albumTitle);

        track.ApplySettings(Settings("0", removeFromAlbum: true));

        Assert.Equal("Rise Up", track.Album!.Title);
    }

    [Fact]
    public void On_LeavesANonFeaturedQualifierInTheAlbumTitle()
    {
        var track = MakeTrack("Rise Up (Deluxe Edition) (feat. Falz)");

        track.ApplySettings(Settings("0", removeFromAlbum: true));

        Assert.Equal("Rise Up (Deluxe Edition)", track.Album!.Title);
    }

    [Fact]
    public void On_IsIdempotentAcrossRepeatedRuns()
    {
        var track = MakeTrack("Rise Up (feat. Falz)");

        track.ApplySettings(Settings("0", removeFromAlbum: true));
        var afterFirstRun = track.Album!.Title;
        track.ApplySettings(Settings("0", removeFromAlbum: true));

        Assert.Equal(afterFirstRun, track.Album!.Title);
        Assert.Equal("Rise Up", track.Album.Title);
    }

    [Fact]
    public void On_IsSafeWhenTheTrackHasNoAlbum()
    {
        var track = MakeTrack("Rise Up (feat. Falz)");
        track.Album = null;

        // Must not throw.
        track.ApplySettings(Settings("0", removeFromAlbum: true));

        Assert.Null(track.Album);
    }
}
