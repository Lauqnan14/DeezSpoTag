namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class TraxsourceTrackInfo
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public List<string> Artists { get; set; } = new();
    public long? Bpm { get; set; }
    public string? Key { get; set; }
    public string Url { get; set; } = "";
    public string? Label { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? TrackId { get; set; }
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string? Album { get; set; }
    public string? CatalogNumber { get; set; }
    public List<string> AlbumArtists { get; set; } = new();
    public int? TrackNumber { get; set; }
    public int? TrackTotal { get; set; }
    public string? Art { get; set; }
}
