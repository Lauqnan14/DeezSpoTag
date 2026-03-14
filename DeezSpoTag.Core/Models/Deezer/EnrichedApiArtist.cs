namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Enriched API Artist with additional GW information (ported from deezer-sdk EnrichedAPIArtist interface)
/// </summary>
public class EnrichedApiArtist : ApiArtist
{
    public new long Id { get; set; }
    public new string? Type { get; set; }
}