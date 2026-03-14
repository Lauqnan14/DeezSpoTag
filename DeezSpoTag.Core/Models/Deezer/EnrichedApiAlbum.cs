namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Enriched API Album with additional GW information (ported from deezer-sdk EnrichedAPIAlbum interface)
/// </summary>
public class EnrichedApiAlbum : ApiAlbum
{
    public new string? Tracklist { get; set; }
    public new long Id { get; set; }
    public new string? Type { get; set; }
}