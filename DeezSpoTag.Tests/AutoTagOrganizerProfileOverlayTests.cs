using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagOrganizerProfileOverlayTests
{
    [Fact]
    public void ApplyTaggingProfileOverrides_AppliesTechnicalAndFolderStructure()
    {
        var options = new AutoTagOrganizerOptions
        {
            MultiArtistSeparatorOverride = ", ",
            CreateArtistFolderOverride = false,
            ArtistNameTemplateOverride = null
        };

        var profile = new TaggingProfile
        {
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["tracknameTemplate"] = JsonSerializer.SerializeToElement("%artist% - %title%")
                }
            },
            Technical = new TechnicalTagSettings
            {
                SingleAlbumArtist = false,
                MultiArtistSeparator = " & "
            },
            FolderStructure = new FolderStructureSettings
            {
                CreateArtistFolder = true,
                ArtistNameTemplate = "%artists%",
                CreateAlbumFolder = true,
                AlbumNameTemplate = "%album% [%year%]",
                CreateCDFolder = true,
                CreatePlaylistFolder = true,
                PlaylistNameTemplate = "%playlist% [mix]",
                IllegalCharacterReplacer = "-"
            }
        };

        AutoTagOrganizerProfileOverlay.ApplyTaggingProfileOverrides(options, profile);

        Assert.NotNull(options.TechnicalSettingsOverride);
        Assert.False(options.UsePrimaryArtistFoldersOverride);
        Assert.Equal("&", options.MultiArtistSeparatorOverride?.Trim());
        Assert.True(options.CreateArtistFolderOverride);
        Assert.Equal("%artists%", options.ArtistNameTemplateOverride);
        Assert.True(options.CreateAlbumFolderOverride);
        Assert.Equal("%album% [%year%]", options.AlbumNameTemplateOverride);
        Assert.True(options.CreateCDFolderOverride);
        Assert.True(options.CreatePlaylistFolderOverride);
        Assert.Equal("%playlist% [mix]", options.PlaylistNameTemplateOverride);
        Assert.Equal("%artist% - %title%", options.TracknameTemplateOverride);
        Assert.Equal("-", options.IllegalCharacterReplacerOverride);
    }

    [Fact]
    public void ApplyTechnicalAndFolderStructureOverrides_UsesSafeDefaultsForBlankTemplates()
    {
        var options = new AutoTagOrganizerOptions();
        var technical = new TechnicalTagSettings
        {
            SingleAlbumArtist = true,
            MultiArtistSeparator = "   "
        };
        var structure = new FolderStructureSettings
        {
            CreateArtistFolder = true,
            ArtistNameTemplate = " ",
            CreateAlbumFolder = true,
            AlbumNameTemplate = "",
            CreatePlaylistFolder = true,
            PlaylistNameTemplate = "",
            IllegalCharacterReplacer = ""
        };

        AutoTagOrganizerProfileOverlay.ApplyTechnicalAndFolderStructureOverrides(options, technical, structure);

        Assert.True(options.UsePrimaryArtistFoldersOverride);
        Assert.Equal("default", options.MultiArtistSeparatorOverride);
        Assert.Equal("%artist%", options.ArtistNameTemplateOverride);
        Assert.Equal("%album%", options.AlbumNameTemplateOverride);
        Assert.Equal("%playlist%", options.PlaylistNameTemplateOverride);
        Assert.Equal("_", options.IllegalCharacterReplacerOverride);
    }

    [Fact]
    public void ApplySettingsOverrides_MapsGlobalSettingsIntoOrganizerOverrides()
    {
        var options = new AutoTagOrganizerOptions();
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                SingleAlbumArtist = false,
                MultiArtistSeparator = " / "
            },
            CreateArtistFolder = true,
            ArtistNameTemplate = " ",
            CreateAlbumFolder = true,
            AlbumNameTemplate = "",
            CreateCDFolder = true,
            CreateStructurePlaylist = true,
            CreateSingleFolder = false,
            CreatePlaylistFolder = true,
            PlaylistNameTemplate = "",
            IllegalCharacterReplacer = ""
        };

        AutoTagOrganizerProfileOverlay.ApplySettingsOverrides(options, settings);

        Assert.False(options.UsePrimaryArtistFoldersOverride);
        Assert.Equal("/", options.MultiArtistSeparatorOverride?.Trim());
        Assert.True(options.CreateArtistFolderOverride);
        Assert.Equal("%artist%", options.ArtistNameTemplateOverride);
        Assert.True(options.CreateAlbumFolderOverride);
        Assert.Equal("%album%", options.AlbumNameTemplateOverride);
        Assert.True(options.CreateCDFolderOverride);
        Assert.True(options.CreateStructurePlaylistOverride);
        Assert.False(options.CreateSingleFolderOverride);
        Assert.True(options.CreatePlaylistFolderOverride);
        Assert.Equal("%playlist%", options.PlaylistNameTemplateOverride);
        Assert.Equal("_", options.IllegalCharacterReplacerOverride);
    }
}
