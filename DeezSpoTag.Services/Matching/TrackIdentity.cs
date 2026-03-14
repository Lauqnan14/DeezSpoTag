namespace DeezSpoTag.Services.Matching;

public sealed class TrackIdentity
{
    public string Isrc { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public int? DurationMs { get; init; }
    public int? TrackNumber { get; init; }
}
