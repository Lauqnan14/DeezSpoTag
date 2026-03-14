using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ShazamMatchConfig
{
    [JsonPropertyName("id_first")]
    public bool IdFirst { get; set; } = true;

    [JsonPropertyName("fingerprint_fallback")]
    public bool FingerprintFallback { get; set; } = true;

    [JsonPropertyName("fallback_missing_core_tags")]
    public bool FallbackMissingCoreTags { get; set; } = true;

    [JsonPropertyName("force_match")]
    public bool ForceMatch { get; set; }

    [JsonPropertyName("prefer_hq_artwork")]
    public bool PreferHqArtwork { get; set; } = true;

    [JsonPropertyName("include_album")]
    public bool IncludeAlbum { get; set; } = true;

    [JsonPropertyName("include_genre")]
    public bool IncludeGenre { get; set; } = true;

    [JsonPropertyName("include_label")]
    public bool IncludeLabel { get; set; } = true;

    [JsonPropertyName("include_release_date")]
    public bool IncludeReleaseDate { get; set; } = true;
}
