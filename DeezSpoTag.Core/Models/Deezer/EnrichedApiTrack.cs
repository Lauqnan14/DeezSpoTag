using Newtonsoft.Json;

namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Enriched API track model that combines data from both API and GW endpoints
/// Used for mapping GwTrack data to a format compatible with ApiTrack
/// </summary>
public class EnrichedApiTrack : ApiTrack
{
    [JsonProperty("TRACK_TOKEN")]
    public new string TrackToken { get; set; } = "";

    [JsonProperty("TRACK_TOKEN_EXPIRE")]
    public int TrackTokenExpire { get; set; }

    [JsonProperty("MD5_ORIGIN")]
    public string Md5Origin { get; set; } = "0";

    [JsonProperty("MEDIA_VERSION")]
    public long MediaVersion { get; set; }

    [JsonProperty("FILESIZES")]
    public Dictionary<string, object> Filesizes { get; set; } = new();

    [JsonProperty("FALLBACK_ID")]
    public int? FallbackId { get; set; }
}