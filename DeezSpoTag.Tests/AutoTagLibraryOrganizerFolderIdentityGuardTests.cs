using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagLibraryOrganizerFolderIdentityGuardTests
{
    private const string MainArtistRole = "Main";

    private static readonly MethodInfo ApplyFolderIdentityGuardsMethod =
        typeof(AutoTagLibraryOrganizer).GetMethod(
            "ApplyFolderIdentityGuards",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutoTagLibraryOrganizer.ApplyFolderIdentityGuards not found.");

    private static readonly MethodInfo CleanTemplateTitleMethod =
        typeof(AutoTagLibraryOrganizer).GetMethod(
            "CleanTemplateTitle",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutoTagLibraryOrganizer.CleanTemplateTitle not found.");

    [Fact]
    public void ApplyFolderIdentityGuards_ReplacesVariousArtistsWithSourceArtist()
    {
        var rootPath = Path.Join(Path.GetTempPath(), "deezspotag-folder-guard");
        var filePath = Path.Join(rootPath, "OneRepublic", "Dreaming Out Loud", "08 - Apologize.flac");
        var track = BuildTrack("Apologize", "Various Artists", "Dreaming Out Loud");

        ApplyFolderIdentityGuards(track, filePath, rootPath);

        Assert.NotNull(track.MainArtist);
        Assert.Equal("OneRepublic", track.MainArtist.Name);
        Assert.Equal("OneRepublic", Assert.Single(track.Artists));
        Assert.Equal("OneRepublic", Assert.Single(track.Artist[MainArtistRole]));
        Assert.NotNull(track.Album);
        var album = track.Album!;
        Assert.NotNull(album.MainArtist);
        Assert.Equal("OneRepublic", album.MainArtist.Name);
        Assert.Equal("OneRepublic", Assert.Single(album.Artists));
        Assert.Equal("Dreaming Out Loud", album.Title);
    }

    [Fact]
    public void ApplyFolderIdentityGuards_BlocksUnrelatedArtistAndAlbumMove()
    {
        var rootPath = Path.Join(Path.GetTempPath(), "deezspotag-folder-guard");
        var filePath = Path.Join(
            rootPath,
            "King Jammy",
            "King Jammy - King Jammy's_ Selector's Choice Vol. 4",
            "CD1",
            "08 - If I Were A Carpenter.flac");
        var track = BuildTrack("Always Remember Us This Way", "John Holt", "Memories By The Score Vol. 5");

        ApplyFolderIdentityGuards(track, filePath, rootPath);

        Assert.NotNull(track.MainArtist);
        Assert.Equal("King Jammy", track.MainArtist.Name);
        Assert.Equal("King Jammy", Assert.Single(track.Artists));
        Assert.NotNull(track.Album);
        var album = track.Album!;
        Assert.Equal("King Jammy - King Jammy's_ Selector's Choice Vol. 4", album.Title);
    }

    [Fact]
    public void ApplyFolderIdentityGuards_PreservesRealVariousArtistsSource()
    {
        var rootPath = Path.Join(Path.GetTempPath(), "deezspotag-folder-guard");
        var filePath = Path.Join(rootPath, "Various Artists", "Dance Collection", "01 - Track.flac");
        var track = BuildTrack("Track", "Various Artists", "Dance Collection");

        ApplyFolderIdentityGuards(track, filePath, rootPath);

        Assert.NotNull(track.MainArtist);
        Assert.Equal("Various Artists", track.MainArtist.Name);
        Assert.Equal("Various Artists", Assert.Single(track.Artists));
        Assert.NotNull(track.Album);
        var album = track.Album!;
        Assert.Equal("Dance Collection", album.Title);
    }

    [Fact]
    public void CleanTemplateTitle_RemovesRepeatedNumbersAndArtistPrefixes()
    {
        var cleanedTitle = Assert.IsType<string>(CleanTemplateTitleMethod.Invoke(
            null,
            new object?[]
            {
                "FEMI ONE - FEMI ONE - 01 - 01 - 01 - 01 - 01 - Form Today",
                new[] { "FEMI ONE" }
            }));

        Assert.Equal("Form Today", cleanedTitle);
    }

    private static Track BuildTrack(string title, string artist, string albumTitle)
    {
        var mainArtist = new Artist(artist);
        var track = new Track
        {
            Title = title,
            MainArtist = mainArtist,
            Artists = new List<string> { artist },
            Album = new Album(albumTitle)
            {
                MainArtist = mainArtist,
                Artists = new List<string> { artist }
            }
        };
        track.Artist[MainArtistRole] = new List<string> { artist };
        track.Album.Artist[MainArtistRole] = new List<string> { artist };
        track.GenerateMainFeatStrings();
        return track;
    }

    private static void ApplyFolderIdentityGuards(Track track, string filePath, string rootPath)
    {
        ApplyFolderIdentityGuardsMethod.Invoke(
            null,
            new object?[] { track, filePath, rootPath, true, null, null });
    }
}
