namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Enriched API Contributor with additional GW information (ported from deezer-sdk EnrichedAPIContributor interface)
/// </summary>
public class EnrichedApiContributor : ApiContributor
{
    public string Md5Image { get; set; } = "";
    public new string Tracklist { get; set; } = "";
    public new string Type { get; set; } = "";
    public string Order { get; set; } = "";
    public object? Rank { get; set; }
}