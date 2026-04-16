namespace DeezSpoTag.Core.Models.Settings;

public class TechnicalTagSettings
{
    public bool SavePlaylistAsCompilation { get; set; } = false;
    public bool UseNullSeparator { get; set; } = false;
    public bool SaveID3v1 { get; set; } = true;
    public string MultiArtistSeparator { get; set; } = "default";
    public bool SingleAlbumArtist { get; set; } = true;
    public bool CoverDescriptionUTF8 { get; set; } = true;

    public bool AlbumVariousArtists { get; set; } = true;
    public bool RemoveDuplicateArtists { get; set; } = true;
    public bool RemoveAlbumVersion { get; set; } = false;
    public string DateFormat { get; set; } = "Y-M-D";
    public string FeaturedToTitle { get; set; } = "0";
    public string TitleCasing { get; set; } = "nothing";
    public string ArtistCasing { get; set; } = "nothing";

    public bool SyncedLyrics { get; set; } = true;
    public bool SaveLyrics { get; set; } = false;
    public bool EmbedLyrics { get; set; } = true;
    public string LrcType { get; set; } = "lyrics,syllable-lyrics,unsynced-lyrics";
    public string LrcFormat { get; set; } = "both";
    public bool LyricsFallbackEnabled { get; set; } = true;
    public string LyricsFallbackOrder { get; set; } = "apple,deezer,spotify,lrclib,musixmatch";

    public bool ArtworkFallbackEnabled { get; set; } = true;
    public string ArtworkFallbackOrder { get; set; } = "apple,deezer,spotify";
    public bool ArtistArtworkFallbackEnabled { get; set; } = true;
    public string ArtistArtworkFallbackOrder { get; set; } = "apple,deezer,spotify";
}
