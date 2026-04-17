using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

internal static class TaggingProfileSettingsMapper
{
    public static void ApplyProfileToSettings(
        DeezSpoTagSettings settings,
        TaggingProfile profile,
        string defaultTitleCasing = "nothing",
        string defaultArtistCasing = "nothing",
        string defaultArtworkFallbackOrder = "apple,deezer,spotify")
    {
        settings.Tags ??= new TagSettings();
        var technical = profile.Technical ?? new TechnicalTagSettings();
        var folder = profile.FolderStructure ?? new FolderStructureSettings();

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator ?? "default";
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
        settings.DateFormat = technical.DateFormat ?? "Y-M-D";
        settings.FeaturedToTitle = technical.FeaturedToTitle ?? "0";
        settings.TitleCasing = technical.TitleCasing ?? defaultTitleCasing;
        settings.ArtistCasing = technical.ArtistCasing ?? defaultArtistCasing;
        settings.SyncedLyrics = technical.SyncedLyrics;
        settings.SaveLyrics = technical.SaveLyrics;
        settings.LrcType = technical.LrcType ?? "lyrics,syllable-lyrics,unsynced-lyrics";
        settings.LrcFormat = technical.LrcFormat ?? "both";
        settings.LyricsFallbackEnabled = technical.LyricsFallbackEnabled;
        settings.LyricsFallbackOrder = technical.LyricsFallbackOrder ?? "apple,deezer,spotify,lrclib,musixmatch";
        settings.ArtworkFallbackEnabled = technical.ArtworkFallbackEnabled;
        settings.ArtworkFallbackOrder = technical.ArtworkFallbackOrder ?? defaultArtworkFallbackOrder;
        settings.ArtistArtworkFallbackEnabled = technical.ArtistArtworkFallbackEnabled;
        settings.ArtistArtworkFallbackOrder = technical.ArtistArtworkFallbackOrder ?? defaultArtworkFallbackOrder;

        settings.CreateArtistFolder = folder.CreateArtistFolder;
        settings.ArtistNameTemplate = folder.ArtistNameTemplate ?? "%artist%";
        settings.CreateAlbumFolder = folder.CreateAlbumFolder;
        settings.AlbumNameTemplate = folder.AlbumNameTemplate ?? "%album%";
        settings.CreateCDFolder = folder.CreateCDFolder;
        settings.CreateStructurePlaylist = folder.CreateStructurePlaylist;
        settings.CreateSingleFolder = folder.CreateSingleFolder;
        settings.CreatePlaylistFolder = folder.CreatePlaylistFolder;
        settings.PlaylistNameTemplate = folder.PlaylistNameTemplate ?? "%playlist%";
        settings.IllegalCharacterReplacer = folder.IllegalCharacterReplacer ?? "_";
    }
}
