using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LrclibConfig
{
    [JsonPropertyName("duration_tolerance_seconds")]
    public int DurationToleranceSeconds { get; set; } = 10;

    [JsonPropertyName("use_duration_hint")]
    public bool UseDurationHint { get; set; } = true;

    [JsonPropertyName("search_fallback")]
    public bool SearchFallback { get; set; } = true;

    [JsonPropertyName("prefer_synced")]
    public bool PreferSynced { get; set; } = true;
}
