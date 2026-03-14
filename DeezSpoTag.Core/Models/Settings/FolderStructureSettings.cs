namespace DeezSpoTag.Core.Models.Settings;

public class FolderStructureSettings
{
    public bool CreateArtistFolder { get; set; } = true;
    public string ArtistNameTemplate { get; set; } = "%artist%";
    public bool CreateAlbumFolder { get; set; } = true;
    public string AlbumNameTemplate { get; set; } = "%album%";
    public bool CreateCDFolder { get; set; }
    public bool CreateStructurePlaylist { get; set; }
    public bool CreateSingleFolder { get; set; }
    public bool CreatePlaylistFolder { get; set; }
    public string PlaylistNameTemplate { get; set; } = "%playlist%";
    public string IllegalCharacterReplacer { get; set; } = "_";

    public bool AutoOrganizeImports { get; set; } = true;
    public string ImportStagingPath { get; set; } = "";
}
