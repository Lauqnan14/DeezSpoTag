using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackDownloaderMetadataMergeTests
{
    [Fact]
    public void MergePublicTrackMetadata_PreservesExistingRichTrackValues()
    {
        var track = new Track
        {
            Title = "Original Title",
            Duration = 245,
            TrackNumber = 7,
            DiscNumber = 2,
            Explicit = false,
            ISRC = "EXISTING-ISRC",
            Bpm = 124,
            Copyright = "Original Copyright",
            PhysicalReleaseDate = "2022-01-01",
            Rank = 900000,
            Gain = 1.25,
            LyricsId = "lyrics-existing"
        };

        var apiTrack = new ApiTrack
        {
            Title = "Replacement Title",
            Duration = 180,
            TrackPosition = 1,
            DiskNumber = 1,
            ExplicitLyrics = false,
            ExplicitContentLyrics = 0,
            Isrc = "NEW-ISRC",
            Bpm = 98,
            Copyright = "Replacement Copyright",
            PhysicalReleaseDate = "2019-05-05",
            Rank = 1,
            Gain = 0.5,
            LyricsId = "lyrics-new"
        };

        InvokePrivateStatic("MergePublicTrackMetadata", track, apiTrack);

        Assert.Equal("Original Title", track.Title);
        Assert.Equal(245, track.Duration);
        Assert.Equal(7, track.TrackNumber);
        Assert.Equal(2, track.DiscNumber);
        Assert.Equal("EXISTING-ISRC", track.ISRC);
        Assert.Equal(124, track.Bpm);
        Assert.Equal("Original Copyright", track.Copyright);
        Assert.Equal("2022-01-01", track.PhysicalReleaseDate);
        Assert.Equal(900000, track.Rank);
        Assert.Equal(1.25, track.Gain);
        Assert.Equal("lyrics-existing", track.LyricsId);
        Assert.False(track.Explicit);
    }

    [Fact]
    public void AlbumMergeHelpers_DoNotReplaceExistingArtistAndGenreState()
    {
        var album = new Album("42", "Existing Album")
        {
            Label = "Existing Label",
            TrackTotal = 12,
            DiscTotal = 2,
            MainArtist = new Artist("10", "Existing Main Artist"),
            Genre = new List<string> { "House", "Techno" }
        };
        album.Artist["Main"] = new List<string> { "Existing Main Artist", "Featured Artist" };
        album.Artists = new List<string> { "Existing Main Artist", "Featured Artist" };

        var apiAlbum = new ApiAlbum
        {
            Title = "Replacement Album",
            Label = "Replacement Label",
            NbTracks = 1,
            NbDisk = 1,
            Artist = new ApiArtist
            {
                Id = 99,
                Name = "Replacement Main Artist",
                Md5Image = "artist-md5"
            },
            Genres = new ApiGenreCollection
            {
                Data = new List<ApiGenre>
                {
                    new() { Name = "Pop" }
                }
            }
        };

        InvokePrivateStatic("MergePublicAlbumMetadata", album, apiAlbum);
        InvokePrivateStatic("MergeAlbumGenres", album, apiAlbum);
        InvokePrivateStatic("MergeAlbumMainArtist", album, apiAlbum.Artist);

        Assert.Equal("Existing Label", album.Label);
        Assert.Equal(12, album.TrackTotal);
        Assert.Equal(2, album.DiscTotal);
        Assert.Equal("Existing Main Artist", album.MainArtist?.Name);
        Assert.Contains("House", album.Genre);
        Assert.Contains("Techno", album.Genre);
        Assert.Contains("Pop", album.Genre);
        Assert.Contains("Featured Artist", album.Artists);
    }

    private static void InvokePrivateStatic(string methodName, params object?[] args)
    {
        var method = typeof(TrackDownloader).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");
        _ = method.Invoke(null, args);
    }
}
