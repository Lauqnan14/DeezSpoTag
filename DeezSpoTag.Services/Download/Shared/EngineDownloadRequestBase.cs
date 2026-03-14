namespace DeezSpoTag.Services.Download.Shared;

public abstract class EngineDownloadRequestBase
{
    public string OutputDir { get; set; } = "";
    public string FilenameFormat { get; set; } = "";
    public bool IncludeTrackNumber { get; set; }
    public int Position { get; set; }
    public bool UseAlbumTrackNumber { get; set; }
    public string TrackName { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string Isrc { get; set; } = "";
    public int DurationSeconds { get; set; }
    public int SpotifyTrackNumber { get; set; }
    public int SpotifyDiscNumber { get; set; }
    public int SpotifyTotalTracks { get; set; }
    public string SpotifyId { get; set; } = "";
    public string ServiceUrl { get; set; } = "";
}
