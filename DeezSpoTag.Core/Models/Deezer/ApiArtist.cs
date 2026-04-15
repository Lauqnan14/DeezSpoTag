using Newtonsoft.Json;

namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Deezer API Artist model (ported from deezer-sdk APIArtist interface)
/// </summary>
public class ApiArtist
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("link")]
    public string? Link { get; set; }

    [JsonProperty("share")]
    public string? Share { get; set; }

    [JsonProperty("picture")]
    public string? Picture { get; set; }

    [JsonProperty("picture_small")]
    public string? PictureSmall { get; set; }

    [JsonProperty("picture_medium")]
    public string? PictureMedium { get; set; }

    [JsonProperty("picture_big")]
    public string? PictureBig { get; set; }

    [JsonProperty("picture_xl")]
    public string? PictureXl { get; set; }

    [JsonProperty("nb_album")]
    public int? NbAlbum { get; set; }

    [JsonProperty("nb_fan")]
    public int? NbFan { get; set; }

    [JsonProperty("radio")]
    public bool? Radio { get; set; }

    [JsonProperty("tracklist")]
    public string? Tracklist { get; set; }

    [JsonProperty("role")]
    public string? Role { get; set; }

    [JsonProperty("md5_image")]
    public string? Md5Image { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }
}