namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class JunoDownloadTrackInfo
{
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string Album { get; set; } = "";
    public long? Bpm { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Label { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Art { get; set; }
    public string Url { get; set; } = "";
    public string? CatalogNumber { get; set; }
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public int? TrackNumber { get; set; }
    public int? TrackTotal { get; set; }
}
