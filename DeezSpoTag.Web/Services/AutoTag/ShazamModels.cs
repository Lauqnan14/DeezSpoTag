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

    /// <summary>Minimum title similarity, as a percentage (72 = 72%). Stored on the 0-100 scale the
    /// UI exposes; <see cref="ShazamMatcher"/> converts it to the 0-1 fraction it compares against.</summary>
    [JsonPropertyName("min_title_similarity")]
    public double MinTitleSimilarity { get; set; } = 72d;

    /// <summary>Minimum artist similarity, as a percentage (52 = 52%). Same 0-100 scale as the UI.</summary>
    [JsonPropertyName("min_artist_similarity")]
    public double MinArtistSimilarity { get; set; } = 52d;

    [JsonPropertyName("max_duration_delta_seconds")]
    public int MaxDurationDeltaSeconds { get; set; } = 20;
}
