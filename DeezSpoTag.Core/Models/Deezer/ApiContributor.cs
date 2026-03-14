using Newtonsoft.Json;

namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Deezer API Contributor model (ported from deezer-sdk APIContributor interface)
/// </summary>
public class ApiContributor
{
    [JsonProperty("id")]
    public long Id { get; set; }
    
    [JsonProperty("name")]
    public string Name { get; set; } = "";
    
    [JsonProperty("link")]
    public string Link { get; set; } = "";
    
    [JsonProperty("share")]
    public string Share { get; set; } = "";
    
    [JsonProperty("picture")]
    public string Picture { get; set; } = "";
    
    [JsonProperty("picture_small")]
    public string PictureSmall { get; set; } = "";
    
    [JsonProperty("picture_medium")]
    public string PictureMedium { get; set; } = "";
    
    [JsonProperty("picture_big")]
    public string PictureBig { get; set; } = "";
    
    [JsonProperty("picture_xl")]
    public string PictureXl { get; set; } = "";
    
    [JsonProperty("role")]
    public string? Role { get; set; }
    
    [JsonProperty("radio")]
    public bool? Radio { get; set; }
    
    [JsonProperty("tracklist")]
    public string? Tracklist { get; set; }
    
    [JsonProperty("type")]
    public string? Type { get; set; }
}