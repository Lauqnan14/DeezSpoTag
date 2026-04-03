using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

public static class AutoTagOrganizerProfileOverlay
{
    public static void ApplySettingsOverrides(AutoTagOrganizerOptions options, DeezSpoTagSettings? settings)
    {
        if (options == null || settings == null)
        {
            return;
        }

        var technical = new TechnicalTagSettings
        {
            SingleAlbumArtist = settings.Tags?.SingleAlbumArtist ?? true,
            MultiArtistSeparator = settings.Tags?.MultiArtistSeparator ?? "default"
        };

        var folderStructure = new FolderStructureSettings
        {
            CreateArtistFolder = settings.CreateArtistFolder,
            ArtistNameTemplate = settings.ArtistNameTemplate,
            CreateAlbumFolder = settings.CreateAlbumFolder,
            AlbumNameTemplate = settings.AlbumNameTemplate,
            CreateCDFolder = settings.CreateCDFolder,
            CreateStructurePlaylist = settings.CreateStructurePlaylist,
            CreateSingleFolder = settings.CreateSingleFolder,
            CreatePlaylistFolder = settings.CreatePlaylistFolder,
            PlaylistNameTemplate = settings.PlaylistNameTemplate,
            IllegalCharacterReplacer = settings.IllegalCharacterReplacer
        };

        ApplyTechnicalAndFolderStructureOverrides(options, technical, folderStructure);
    }

    public static void ApplyTaggingProfileOverrides(AutoTagOrganizerOptions options, TaggingProfile? profile)
    {
        if (options == null || profile == null)
        {
            return;
        }

        ApplyTechnicalAndFolderStructureOverrides(options, profile.Technical, profile.FolderStructure);
    }

    public static void ApplyTechnicalAndFolderStructureOverrides(
        AutoTagOrganizerOptions options,
        TechnicalTagSettings? technical,
        FolderStructureSettings? folderStructure)
    {
        if (options == null)
        {
            return;
        }

        if (technical != null)
        {
            options.TechnicalSettingsOverride = technical;
            options.UsePrimaryArtistFoldersOverride = technical.SingleAlbumArtist;
            options.MultiArtistSeparatorOverride = string.IsNullOrWhiteSpace(technical.MultiArtistSeparator)
                ? "default"
                : technical.MultiArtistSeparator.Trim();
        }

        if (folderStructure == null)
        {
            return;
        }

        options.CreateArtistFolderOverride = folderStructure.CreateArtistFolder;
        options.ArtistNameTemplateOverride = string.IsNullOrWhiteSpace(folderStructure.ArtistNameTemplate)
            ? "%artist%"
            : folderStructure.ArtistNameTemplate.Trim();
        options.CreateAlbumFolderOverride = folderStructure.CreateAlbumFolder;
        options.AlbumNameTemplateOverride = string.IsNullOrWhiteSpace(folderStructure.AlbumNameTemplate)
            ? "%album%"
            : folderStructure.AlbumNameTemplate.Trim();
        options.CreateCDFolderOverride = folderStructure.CreateCDFolder;
        options.CreateStructurePlaylistOverride = folderStructure.CreateStructurePlaylist;
        options.CreateSingleFolderOverride = folderStructure.CreateSingleFolder;
        options.CreatePlaylistFolderOverride = folderStructure.CreatePlaylistFolder;
        options.PlaylistNameTemplateOverride = string.IsNullOrWhiteSpace(folderStructure.PlaylistNameTemplate)
            ? "%playlist%"
            : folderStructure.PlaylistNameTemplate.Trim();
        options.IllegalCharacterReplacerOverride = string.IsNullOrWhiteSpace(folderStructure.IllegalCharacterReplacer)
            ? "_"
            : folderStructure.IllegalCharacterReplacer.Trim();
    }
}
