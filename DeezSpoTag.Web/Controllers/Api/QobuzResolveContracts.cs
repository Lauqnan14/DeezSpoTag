using DeezSpoTag.Core.Models.Qobuz;

namespace DeezSpoTag.Web.Controllers.Api;

public sealed class QobuzResolveRequest
{
    public string? ISRC { get; set; }
    public string? UPC { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public int? TrackNumber { get; set; }
}

public sealed class QobuzResolveResult
{
    public string? MatchMethod { get; set; }
    public double Confidence { get; set; }
    public QobuzTrack? Track { get; set; }
    public QobuzAlbum? Album { get; set; }
}
