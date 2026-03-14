using Newtonsoft.Json;

namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Deezer API Track model (ported from deezer-sdk APITrack interface)
/// </summary>
public class ApiTrack
{
    [JsonProperty("id")]
    public string Id { get; set; } = "0";
    
    [JsonProperty("readable")]
    public bool Readable { get; set; }
    
    [JsonProperty("title")]
    public string Title { get; set; } = "";
    
    [JsonProperty("title_short")]
    public string TitleShort { get; set; } = "";
    
    [JsonProperty("title_version")]
    public string TitleVersion { get; set; } = "";
    
    [JsonProperty("unseen")]
    public bool Unseen { get; set; }
    
    [JsonProperty("isrc")]
    public string Isrc { get; set; } = "";
    
    [JsonProperty("link")]
    public string Link { get; set; } = "";
    
    [JsonProperty("share")]
    public string Share { get; set; } = "";
    
    [JsonProperty("duration")]
    public int Duration { get; set; }
    
    [JsonProperty("track_position")]
    public int TrackPosition { get; set; }
    
    [JsonProperty("disk_number")]
    public int DiskNumber { get; set; }
    
    [JsonProperty("rank")]
    public int Rank { get; set; }
    
    [JsonProperty("release_date")]
    public string ReleaseDate { get; set; } = "";
    
    [JsonProperty("explicit_lyrics")]
    public bool ExplicitLyrics { get; set; }
    
    [JsonProperty("explicit_content_lyrics")]
    public int ExplicitContentLyrics { get; set; }
    
    [JsonProperty("explicit_content_cover")]
    public int ExplicitContentCover { get; set; }
    
    [JsonProperty("preview")]
    public string Preview { get; set; } = "";
    
    [JsonProperty("bpm")]
    public double Bpm { get; set; }
    
    [JsonProperty("gain")]
    public double Gain { get; set; }
    
    [JsonProperty("available_countries")]
    public List<string> AvailableCountries { get; set; } = new();
    
    [JsonProperty("alternative")]
    public ApiTrack? Alternative { get; set; }
    
    [JsonProperty("alternative_albums")]
    public ApiAlbumCollection? AlternativeAlbums { get; set; }
    
    [JsonProperty("contributors")]
    public List<ApiContributor>? Contributors { get; set; }
    
    [JsonProperty("md5_image")]
    public string Md5Image { get; set; } = "";
    
    [JsonProperty("track_token")]
    public string TrackToken { get; set; } = "";
    
    [JsonProperty("artist")]
    public ApiArtist Artist { get; set; } = new();
    
    [JsonProperty("album")]
    public ApiAlbum Album { get; set; } = new();
    
    [JsonProperty("size")]
    public int? Size { get; set; }
    
    [JsonProperty("lyrics_id")]
    public string? LyricsId { get; set; }
    
    [JsonProperty("lyrics")]
    public string? Lyrics { get; set; }
    
    [JsonProperty("position")]
    public int? Position { get; set; }
    
    [JsonProperty("copyright")]
    public string? Copyright { get; set; }
    
    [JsonProperty("physical_release_date")]
    public string? PhysicalReleaseDate { get; set; }
    
    [JsonProperty("genres")]
    public List<string>? Genres { get; set; }
    
    [JsonProperty("type")]
    public string? Type { get; set; }
}

public class ApiAlbumCollection
{
    [JsonProperty("data")]
    public List<ApiAlbum> Data { get; set; } = new();
}
