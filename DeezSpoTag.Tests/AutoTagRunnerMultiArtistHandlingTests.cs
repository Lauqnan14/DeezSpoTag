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
    private static readonly string[] Artists2BabaAndFalz = ["2Baba", "Falz"];
    private static readonly string[] Artist2BabaOnly = ["2Baba"];
    private static readonly string[] UnknownArtistOnly = ["Unknown Artist"];

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

    private static readonly MethodInfo BuildAlbumArtworkBaseFileNameMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "BuildAlbumArtworkBaseFileName",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.BuildAlbumArtworkBaseFileName not found.");

    private static readonly MethodInfo BuildTagSettingsMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "BuildTagSettings",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.BuildTagSettings not found.");

    private static readonly MethodInfo ResolveArtistValuesMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "ResolveArtistValues",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.ResolveArtistValues not found.");

    private static readonly MethodInfo ResolveAlbumArtistValuesMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "ResolveAlbumArtistValues",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.ResolveAlbumArtistValues not found.");

    private static readonly MethodInfo ApplySingleAlbumArtistGuardMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "ApplySingleAlbumArtistGuard",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.ApplySingleAlbumArtistGuard not found.");

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

        Assert.Equal(Artists2BabaAndFalz, track.Artists);
        Assert.Equal(Artist2BabaOnly, track.AlbumArtists);
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
        Assert.Equal(Artists2BabaAndFalz, coreTrack.Artists);
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

        Assert.Equal(Artists2BabaAndFalz, track.Artists);
        Assert.Equal(Artist2BabaOnly, track.AlbumArtists);
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

        Assert.Equal(Artists2BabaAndFalz, track.Artists);
        Assert.Equal(Artist2BabaOnly, track.AlbumArtists);
    }

    [Fact]
    public void PreserveRicherArtistCreditsFromSource_UsesArtistField_WhenArtistsCollectionIsEmpty()
    {
        var source = new AutoTagAudioInfo
        {
            Artist = "2Baba & Falz",
            Artists = new List<string>()
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

        Assert.Equal(Artists2BabaAndFalz, track.Artists);
        Assert.Equal(Artist2BabaOnly, track.AlbumArtists);
    }

    [Fact]
    public void PreserveRicherArtistCreditsFromSource_LeavesTrackUnchanged_WhenSourceHasNoArtistCredits()
    {
        var source = new AutoTagAudioInfo
        {
            Artist = "   ",
            Artists = new List<string>()
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

        Assert.Equal(Artist2BabaOnly, track.Artists);
        Assert.Equal(Artist2BabaOnly, track.AlbumArtists);
    }

    [Fact]
    public void NormalizeTrackArtistsForTagging_UsesUnknownArtist_WhenIncomingArtistsAreMissing()
    {
        var track = new AutoTagTrack
        {
            Artists = new List<string>(),
            AlbumArtists = new List<string>()
        };

        NormalizeTrackArtistsForTaggingMethod.Invoke(null, new object?[] { track, true });

        Assert.Equal(UnknownArtistOnly, track.Artists);
        Assert.Equal(UnknownArtistOnly, track.AlbumArtists);
    }

    [Fact]
    public void BuildCoreTrack_UsesUnknownArtist_WhenArtistsAreMissing()
    {
        var track = new AutoTagTrack
        {
            Title = "Untitled",
            Artists = new List<string>(),
            AlbumArtists = new List<string>()
        };
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                MultiArtistSeparator = "default",
                SingleAlbumArtist = true
            }
        };

        var coreTrack = Assert.IsType<CoreTrack>(BuildCoreTrackMethod.Invoke(null, new object?[] { track, ", ", true, settings }));

        Assert.NotNull(coreTrack.Album?.MainArtist);
        Assert.Equal("Unknown Artist", coreTrack.Album!.MainArtist!.Name);
    }

    [Fact]
    public void BuildAlbumArtworkBaseFileName_UsesUnknownArtist_WhenArtistsAreMissing()
    {
        var track = new AutoTagTrack
        {
            Album = "The Album",
            Artists = new List<string>()
        };

        var fileName = Assert.IsType<string>(
            BuildAlbumArtworkBaseFileNameMethod.Invoke(
                null,
                new object?[]
                {
                    track,
                    new DeezSpoTagSettings
                    {
                        CoverImageTemplate = "%artist% - %album%"
                    }
                }));

        Assert.Contains("Unknown Artist", fileName);
    }

    [Fact]
    public void BuildTagSettings_UsesDefaultMultiArtistSeparator_WhenRuntimeSettingIsMissing()
    {
        var configType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.AutoTagRunnerConfig not found.");
        var config = Activator.CreateInstance(configType)
            ?? throw new InvalidOperationException("Failed to instantiate AutoTagRunnerConfig.");
        configType.GetProperty("Tags")!.SetValue(config, new List<string>());

        var runtimeSettings = new DeezSpoTagSettings
        {
            Tags = null!
        };

        var tagSettings = Assert.IsType<TagSettings>(
            BuildTagSettingsMethod.Invoke(null, new object?[] { config, runtimeSettings }));

        Assert.Equal("default", tagSettings.MultiArtistSeparator);
    }

    [Fact]
    public void ResolveArtistValues_ReturnsExpandedArtists_WhenSeparatorIsDefault()
    {
        var coreTrack = new CoreTrack
        {
            Artists = new List<string> { "2Baba", "Falz" }
        };
        var tagSettings = new TagSettings
        {
            MultiArtistSeparator = "default"
        };

        var values = Assert.IsType<List<string>>(ResolveArtistValuesMethod.Invoke(null, new object?[] { coreTrack, tagSettings }));

        Assert.Equal(Artists2BabaAndFalz, values);
    }

    [Fact]
    public void ResolveArtistValues_ReturnsPrimaryArtist_WhenSeparatorIsNothing()
    {
        var coreTrack = new CoreTrack
        {
            Artists = new List<string> { "2Baba", "Falz" },
            MainArtist = new DeezSpoTag.Core.Models.Artist("2Baba")
        };
        var tagSettings = new TagSettings
        {
            MultiArtistSeparator = "nothing"
        };

        var values = Assert.IsType<List<string>>(ResolveArtistValuesMethod.Invoke(null, new object?[] { coreTrack, tagSettings }));

        Assert.Equal(Artist2BabaOnly, values);
    }

    [Fact]
    public void ResolveAlbumArtistValues_ReturnsUnknownArtist_WhenNoPrimaryOrMainArtistExists()
    {
        var coreTrack = new CoreTrack
        {
            Artist = new Dictionary<string, List<string>>()
        };

        var values = Assert.IsType<List<string>>(ResolveAlbumArtistValuesMethod.Invoke(null, new object?[] { coreTrack }));

        Assert.Equal(UnknownArtistOnly, values);
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
        Assert.Equal(Artists2BabaAndFalz, incoming.Artists);
        Assert.Equal(Artist2BabaOnly, incoming.AlbumArtists);
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
        Assert.Equal(Artist2BabaOnly, incoming.Artists);
        Assert.Equal(Artist2BabaOnly, incoming.AlbumArtists);
    }

    [Fact]
    public void ApplyPreferenceAwareArtistGuards_SingleAlbumArtist_UsesExistingArtistFallbackWhenAlbumArtistsEmpty()
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
                new List<string> { "   " },
                "Rise Up"
            });

        Assert.False(effective.Artist);
        Assert.True(effective.AlbumArtist);
        Assert.Equal(Artists2BabaAndFalz, incoming.Artists);
        Assert.Equal(Artist2BabaOnly, incoming.AlbumArtists);
    }

    [Fact]
    public void ApplyPreferenceAwareArtistGuards_DoesNotOverwriteTitle_WhenNoFeaturedMarkerInExistingTitle()
    {
        var effective = new TagSettings
        {
            Title = true,
            Artist = true,
            AlbumArtist = true
        };
        var incoming = new AutoTagTrack
        {
            Title = "Incoming Title",
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
                "Rise Up"
            });

        Assert.True(effective.Title);
        Assert.Equal("Incoming Title", incoming.Title);
    }

    [Fact]
    public void ApplyPreferenceAwareArtistGuards_MultiAlbumArtist_WhenExistingAlbumArtistsRicher_DisablesAlbumArtistOverwrite()
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
                MultiArtistSeparator = "default",
                SingleAlbumArtist = false
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
                new List<string> { "2Baba & Falz" },
                "Rise Up"
            });

        Assert.False(effective.AlbumArtist);
        Assert.Equal(Artists2BabaAndFalz, incoming.AlbumArtists);
    }

    [Fact]
    public void ApplyPreferenceAwareArtistGuards_MultiAlbumArtist_WhenAlbumArtistsDoNotMatch_KeepsAlbumArtistOverwriteEnabled()
    {
        var effective = new TagSettings
        {
            Artist = true,
            AlbumArtist = true
        };
        var incoming = new AutoTagTrack
        {
            Artists = new List<string> { "2Baba" },
            AlbumArtists = new List<string> { "Kendrick Lamar" }
        };
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                MultiArtistSeparator = "default",
                SingleAlbumArtist = false
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
                new List<string> { "Drake" },
                "Rise Up"
            });

        Assert.True(effective.AlbumArtist);
        Assert.Equal("Kendrick Lamar", Assert.Single(incoming.AlbumArtists));
    }

    [Fact]
    public void ApplyPreferenceAwareArtistGuards_SingleAlbumArtist_ReturnsWithoutAlbumArtistChange_WhenNoPreferredArtistExists()
    {
        var effective = new TagSettings
        {
            Artist = true,
            AlbumArtist = true
        };
        var incoming = new AutoTagTrack
        {
            Artists = new List<string>(),
            AlbumArtists = new List<string> { "Incoming Album Artist" }
        };
        var settings = new DeezSpoTagSettings
        {
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
                new List<string>(),
                new List<string> { "   " },
                "Rise Up"
            });

        Assert.True(effective.AlbumArtist);
        Assert.Equal("Incoming Album Artist", Assert.Single(incoming.AlbumArtists));
    }

    [Fact]
    public void ApplySingleAlbumArtistGuard_ReturnsImmediately_WhenNoPreferredArtistCanBeDerived()
    {
        var effective = new TagSettings
        {
            AlbumArtist = true
        };
        var incoming = new AutoTagTrack
        {
            AlbumArtists = new List<string> { "Incoming Album Artist" }
        };

        ApplySingleAlbumArtistGuardMethod.Invoke(
            null,
            new object?[]
            {
                effective,
                incoming,
                new List<string>(),
                new List<string>()
            });

        Assert.True(effective.AlbumArtist);
        Assert.Equal("Incoming Album Artist", Assert.Single(incoming.AlbumArtists));
    }
}
