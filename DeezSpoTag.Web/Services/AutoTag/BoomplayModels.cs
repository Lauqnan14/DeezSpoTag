using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BoomplayConfig
{
    [JsonPropertyName("match_by_id")]
    public bool MatchById { get; set; } = true;

    [JsonPropertyName("search_limit")]
    public int SearchLimit { get; set; } = 12;
}
