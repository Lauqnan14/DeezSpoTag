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
    private static readonly MethodInfo ApplyTitleLossyOverwriteGuardMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "ApplyTitleLossyOverwriteGuard",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.ApplyTitleLossyOverwriteGuard not found.");
    private static readonly MethodInfo CollectAutoTagTagsMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "CollectAutoTagTags",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.CollectAutoTagTags not found.");

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
    public void BuildTagSettings_MapsConfiguredTagsToExpectedFlags()
    {
        var configType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.AutoTagRunnerConfig not found.");
        var config = Activator.CreateInstance(configType)
            ?? throw new InvalidOperationException("Failed to instantiate AutoTagRunnerConfig.");
        configType.GetProperty("Tags")!.SetValue(
            config,
            new List<string>
            {
                "title", "artist", "artists", "album", "albumArtist",
                "trackNumber", "trackTotal", "discNumber", "discTotal",
                "genre", "label", "bpm", "isrc", "explicit", "duration",
                "releaseDate", "cover", "barcode", "replayGain", "copyright",
                "composer", "involvedPeople", "source", "url", "trackId",
                "releaseId", "rating", "lyrics", "syncedLyrics"
            });

        var runtimeSettings = new DeezSpoTagSettings
        {
            Tags = new TagSettings()
        };

        var tagSettings = Assert.IsType<TagSettings>(
            BuildTagSettingsMethod.Invoke(null, new object?[] { config, runtimeSettings }));

        Assert.True(tagSettings.Title);
        Assert.True(tagSettings.Artist);
        Assert.True(tagSettings.Artists);
        Assert.True(tagSettings.Album);
        Assert.True(tagSettings.AlbumArtist);
        Assert.True(tagSettings.TrackNumber);
        Assert.True(tagSettings.TrackTotal);
        Assert.True(tagSettings.DiscNumber);
        Assert.True(tagSettings.DiscTotal);
        Assert.True(tagSettings.Genre);
        Assert.True(tagSettings.Label);
        Assert.True(tagSettings.Bpm);
        Assert.True(tagSettings.Isrc);
        Assert.True(tagSettings.Explicit);
        Assert.True(tagSettings.Length);
        Assert.True(tagSettings.Date);
        Assert.True(tagSettings.Year);
        Assert.True(tagSettings.Cover);
        Assert.True(tagSettings.Barcode);
        Assert.True(tagSettings.ReplayGain);
        Assert.True(tagSettings.Copyright);
        Assert.True(tagSettings.Composer);
        Assert.True(tagSettings.InvolvedPeople);
        Assert.True(tagSettings.Source);
        Assert.True(tagSettings.Url);
        Assert.True(tagSettings.TrackId);
        Assert.True(tagSettings.ReleaseId);
        Assert.True(tagSettings.Rating);
        Assert.True(tagSettings.Lyrics);
        Assert.True(tagSettings.SyncedLyrics);
    }

    [Fact]
    public void CollectAutoTagTags_IncludesMappedCoreFeatureAndOtherTags()
    {
        var track = new AutoTagTrack
        {
            Title = "Rise Up",
            Artists = new List<string> { "2Baba", "Falz" },
            AlbumArtists = new List<string> { "2Baba" },
            Album = "Ascension",
            Art = "https://example.com/cover.jpg",
            Version = "Extended Mix",
            Remixers = new List<string> { "DJ Test" },
            Genres = new List<string> { "Hip-Hop" },
            Styles = new List<string> { "Rap" },
            Label = "Top Label",
            ReleaseId = "rel-1",
            TrackId = "trk-1",
            Bpm = 124,
            Danceability = 0.6,
            Energy = 0.8,
            Valence = 0.4,
            Acousticness = 0.1,
            Instrumentalness = 0.0,
            Speechiness = 0.2,
            Loudness = -7.0,
            Tempo = 124,
            TimeSignature = 4,
            Liveness = 0.15,
            Key = "C#m",
            Mood = "Energetic",
            CatalogNumber = "CAT-1",
            TrackNumber = 1,
            TrackTotal = 10,
            DiscNumber = 1,
            Duration = TimeSpan.FromMinutes(3),
            Isrc = "USXXX2400001",
            PublishDate = new DateTime(2024, 1, 1),
            ReleaseDate = new DateTime(2024, 1, 2),
            Url = "https://example.com/track",
            Explicit = true,
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["discTotal"] = new() { "1" },
                ["barcode"] = new() { "1234567890" },
                ["replayGain"] = new() { "-8.00 dB" },
                ["copyright"] = new() { "Copyright" },
                ["composer"] = new() { "Composer Name" },
                ["involvedPeople"] = new() { "Producer Name" },
                ["source"] = new() { "spotify" },
                ["rating"] = new() { "5" },
                ["language"] = new() { "en" },
                ["syncedLyrics"] = new() { "[00:01.00]line" },
                ["lyrics"] = new() { "plain lyric" },
                ["ttmlLyrics"] = new() { "<tt></tt>" },
                ["customMeta"] = new() { "value" }
            }
        };

        var tags = Assert.IsType<List<string>>(CollectAutoTagTagsMethod.Invoke(null, new object?[] { track }));

        Assert.Contains(tags, value => value.Equals("title", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("artist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("albumArtist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("album", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("albumArt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("bpm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("danceability", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("catalogNumber", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("duration", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("isrc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("releaseDate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("url", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("explicit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("barcode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("syncedLyrics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("unsyncedLyrics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("ttmlLyrics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, value => value.Equals("otherTags", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void ApplyTitleLossyOverwriteGuard_Boomplay_PreservesExistingDetailedTitle()
    {
        var effective = new TagSettings
        {
            Title = true
        };
        var incoming = new AutoTagTrack
        {
            Title = "All eyes on me"
        };

        ApplyTitleLossyOverwriteGuardMethod.Invoke(
            null,
            new object?[]
            {
                effective,
                incoming,
                "All eyes on me (feat. Burna Boy)",
                "boomplay"
            });

        Assert.False(effective.Title);
        Assert.Equal("All eyes on me (feat. Burna Boy)", incoming.Title);
    }

    [Fact]
    public void ApplyTitleLossyOverwriteGuard_AllPlatforms_PreserveExistingDetailedTitle()
    {
        var effective = new TagSettings
        {
            Title = true
        };
        var incoming = new AutoTagTrack
        {
            Title = "All eyes on me"
        };

        ApplyTitleLossyOverwriteGuardMethod.Invoke(
            null,
            new object?[]
            {
                effective,
                incoming,
                "All eyes on me (feat. Burna Boy)",
                "itunes"
            });

        Assert.False(effective.Title);
        Assert.Equal("All eyes on me (feat. Burna Boy)", incoming.Title);
    }
}
