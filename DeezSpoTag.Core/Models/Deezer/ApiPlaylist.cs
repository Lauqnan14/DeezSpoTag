namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Deezer API Playlist model (ported from deezer-sdk APIPlaylist interface)
/// </summary>
public class ApiPlaylist
{
    public long Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Duration { get; set; }
    public bool? Public { get; set; }
    public bool? IsLovedTrack { get; set; }
    public bool? Collaborative { get; set; }
    public int? NbTracks { get; set; }
    public int? Fans { get; set; }
    public string? Link { get; set; }
    public string? Share { get; set; }
    public string? Picture { get; set; }
    public string? PictureSmall { get; set; }
    public string? PictureMedium { get; set; }
    public string? PictureBig { get; set; }
    public string? PictureXl { get; set; }
    public string? Checksum { get; set; }
    public string? Tracklist { get; set; }
    public string? CreationDate { get; set; }
    public ApiArtist? Creator { get; set; }
    public string? Type { get; set; }
    public bool? VariousArtist { get; set; }
    public bool? Explicit { get; set; }
    public bool Unseen { get; set; }
}
