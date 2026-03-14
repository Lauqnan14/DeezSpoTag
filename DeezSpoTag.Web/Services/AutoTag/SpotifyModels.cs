using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class SpotifyTrackInfo : AudioFeaturesBase
{
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string Url { get; set; } = "";
    public string TrackId { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string? Art { get; set; }
    public string? Isrc { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public bool? Explicit { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? TrackTotal { get; set; }
    public string? Label { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Key { get; set; }
}
