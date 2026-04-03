using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
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
}
