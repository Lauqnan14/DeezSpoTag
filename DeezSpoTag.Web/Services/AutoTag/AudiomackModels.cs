using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>Per-platform tunables for the Audiomack AutoTag matcher (profile `custom` JSON).</summary>
public sealed class AudiomackMatchConfig
{
    [JsonPropertyName("match_by_id")]
    public bool MatchById { get; set; }

    [JsonPropertyName("search_limit")]
    public int SearchLimit { get; set; } = 12;
}
