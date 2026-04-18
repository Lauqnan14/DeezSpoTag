using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;
using CoreTrack = DeezSpoTag.Core.Models.Track;

namespace DeezSpoTag.Tests;

public sealed class AutoTagRunnerMultiArtistHandlingTests
{
    private static readonly MethodInfo BuildCoreTrackMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "BuildCoreTrack",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.BuildCoreTrack not found.");

    private static readonly MethodInfo NormalizeTrackArtistsForTaggingMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "NormalizeTrackArtistsForTagging",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.NormalizeTrackArtistsForTagging not found.");

    private static readonly MethodInfo PreserveRicherArtistCreditsFromSourceMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "PreserveRicherArtistCreditsFromSource",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.PreserveRicherArtistCreditsFromSource not found.");

    private static readonly MethodInfo ApplyPreferenceAwareArtistGuardsMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "ApplyPreferenceAwareArtistGuards",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.ApplyPreferenceAwareArtistGuards not found.");

    [Fact]
    public void BuildCoreTrack_SingleAlbumArtist_UsesAlbumArtistPrimary()
    {
        var track = new AutoTagTrack
        {
            Title = "Rise Up",
            Artists = new List<string> { "2Baba & Falz" },
            AlbumArtists = new List<string> { "2Baba" }
        };
        var settings = new DeezSpoTagSettings
        {
            FeaturedToTitle = "2",
            Tags = new TagSettings
            {
                MultiArtistSeparator = "default",
                SingleAlbumArtist = true
            }
        };

        var coreTrack = Assert.IsType<CoreTrack>(BuildCoreTrackMethod.Invoke(null, new object?[] { track, ", ", true, settings }));

        Assert.NotNull(coreTrack.Album?.MainArtist);
        Assert.Equal("2Baba", coreTrack.Album!.MainArtist!.Name);
    }

    [Fact]
    public void NormalizeTrackArtistsForTagging_SplitsCombinedCredits_WhenSingleAlbumArtistEnabled()
    {
        var track = new AutoTagTrack
        {
            Artists = new List<string> { "2Baba & Falz" },
            AlbumArtists = new List<string> { "2Baba & Falz" }
        };

        NormalizeTrackArtistsForTaggingMethod.Invoke(null, new object?[] { track, true });

        Assert.Equal(new[] { "2Baba", "Falz" }, track.Artists);
        Assert.Equal(new[] { "2Baba" }, track.AlbumArtists);
    }

    [Fact]
    public void NormalizeTrackArtistsForTagging_EnablesFeaturedToTitleFlow()
    {
        var track = new AutoTagTrack
        {
            Title = "Rise Up",
            Artists = new List<string> { "2Baba & Falz" },
            AlbumArtists = new List<string> { "2Baba & Falz" }
        };
        var settings = new DeezSpoTagSettings
        {
            FeaturedToTitle = "2",
            Tags = new TagSettings
            {
                MultiArtistSeparator = "default",
                SingleAlbumArtist = true
            }
        };

        NormalizeTrackArtistsForTaggingMethod.Invoke(null, new object?[] { track, true });
        var coreTrack = Assert.IsType<CoreTrack>(BuildCoreTrackMethod.Invoke(null, new object?[] { track, ", ", true, settings }));

        Assert.Equal("Rise Up (feat. Falz)", coreTrack.Title);
        Assert.NotNull(coreTrack.Album?.MainArtist);
        Assert.Equal("2Baba", coreTrack.Album!.MainArtist!.Name);
        Assert.Equal(new[] { "2Baba", "Falz" }, coreTrack.Artists);
    }

    [Fact]
    public void PreserveRicherArtistCreditsFromSource_WhenMatchHasSingleArtist_PreservesRicherSourceCredits()
    {
        var source = new AutoTagAudioInfo
        {
            Artist = "2Baba & Falz",
            Artists = new List<string> { "2Baba & Falz" }
        };
        var track = new AutoTagTrack
        {
            Artists = new List<string> { "2Baba" },
            AlbumArtists = new List<string> { "2Baba" }
        };
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                SingleAlbumArtist = true
            }
        };

        PreserveRicherArtistCreditsFromSourceMethod.Invoke(null, new object?[] { source, track, settings });

        Assert.Equal(new[] { "2Baba", "Falz" }, track.Artists);
        Assert.Equal(new[] { "2Baba" }, track.AlbumArtists);
    }

    [Fact]
    public void PreserveRicherArtistCreditsFromSource_WhenMatchIsAlreadyRicher_KeepsMatchedCredits()
    {
        var source = new AutoTagAudioInfo
        {
            Artist = "2Baba",
            Artists = new List<string> { "2Baba" }
        };
        var track = new AutoTagTrack
        {
            Artists = new List<string> { "2Baba", "Falz" },
            AlbumArtists = new List<string> { "2Baba", "Falz" }
        };
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                SingleAlbumArtist = true
            }
        };

        PreserveRicherArtistCreditsFromSourceMethod.Invoke(null, new object?[] { source, track, settings });

        Assert.Equal(new[] { "2Baba", "Falz" }, track.Artists);
        Assert.Equal(new[] { "2Baba" }, track.AlbumArtists);
    }

    [Fact]
    public void ApplyPreferenceAwareArtistGuards_WhenExistingIsRicher_DisablesArtistOverwrite()
    {
        var effective = new TagSettings
        {
            Title = true,
            Artist = true,
            AlbumArtist = true,
            SingleAlbumArtist = true
        };
        var incoming = new AutoTagTrack
        {
            Title = "Rise Up",
            Artists = new List<string> { "2Baba" },
            AlbumArtists = new List<string> { "2Baba" }
        };
        var settings = new DeezSpoTagSettings
        {
            FeaturedToTitle = "2",
            Tags = new TagSettings
            {
                MultiArtistSeparator = "default",
                SingleAlbumArtist = true
            }
        };

        ApplyPreferenceAwareArtistGuardsMethod.Invoke(
            null,
            new object?[]
            {
                effective,
                incoming,
                settings,
                new List<string> { "2Baba & Falz" },
                new List<string> { "2Baba" },
                "Rise Up (feat. Falz)"
            });

        Assert.False(effective.Artist);
        Assert.False(effective.AlbumArtist);
        Assert.False(effective.Title);
        Assert.Equal(new[] { "2Baba", "Falz" }, incoming.Artists);
        Assert.Equal(new[] { "2Baba" }, incoming.AlbumArtists);
        Assert.Equal("Rise Up (feat. Falz)", incoming.Title);
    }

    [Fact]
    public void ApplyPreferenceAwareArtistGuards_WhenSeparatorIsNothing_DoesNotPreserveRicherArtists()
    {
        var effective = new TagSettings
        {
            Artist = true,
            AlbumArtist = true
        };
        var incoming = new AutoTagTrack
        {
            Artists = new List<string> { "2Baba" },
            AlbumArtists = new List<string> { "2Baba" }
        };
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                MultiArtistSeparator = "nothing",
                SingleAlbumArtist = true
            }
        };

        ApplyPreferenceAwareArtistGuardsMethod.Invoke(
            null,
            new object?[]
            {
                effective,
                incoming,
                settings,
                new List<string> { "2Baba & Falz" },
                new List<string> { "2Baba" },
                "Rise Up (feat. Falz)"
            });

        Assert.True(effective.Artist);
        Assert.True(effective.AlbumArtist);
        Assert.Equal(new[] { "2Baba" }, incoming.Artists);
        Assert.Equal(new[] { "2Baba" }, incoming.AlbumArtists);
    }
}
