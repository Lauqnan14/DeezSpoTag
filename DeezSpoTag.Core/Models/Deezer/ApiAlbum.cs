using Newtonsoft.Json;

namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Deezer API Album model (ported from deezer-sdk APIAlbum interface)
/// </summary>
public class ApiAlbum
{
    [JsonProperty("id")]
    public long Id { get; set; }
    
    [JsonProperty("title")]
    public string Title { get; set; } = "";
    
    [JsonProperty("link")]
    public string Link { get; set; } = "";
    
    [JsonProperty("cover")]
    public string Cover { get; set; } = "";
    
    [JsonProperty("cover_small")]
    public string CoverSmall { get; set; } = "";
    
    [JsonProperty("cover_medium")]
    public string CoverMedium { get; set; } = "";
    
    [JsonProperty("cover_big")]
    public string CoverBig { get; set; } = "";
    
    [JsonProperty("cover_xl")]
    public string CoverXl { get; set; } = "";
    
    [JsonProperty("release_date")]
    public string? ReleaseDate { get; set; }
    
    [JsonProperty("root_artist")]
    public ApiArtist? RootArtist { get; set; }
    
    [JsonProperty("nb_tracks")]
    public int? NbTracks { get; set; }
    
    [JsonProperty("nb_disk")]
    public int? NbDisk { get; set; }
    
    [JsonProperty("tracks")]
    public ApiTrackCollection? Tracks { get; set; }
    
    [JsonProperty("md5_image")]
    public string? Md5Image { get; set; }
    
    [JsonProperty("md5_origin")]
    public string? Md5Origin { get; set; }
    
    [JsonProperty("artist")]
    public ApiArtist? Artist { get; set; }
    
    [JsonProperty("explicit_lyrics")]
    public bool? ExplicitLyrics { get; set; }
    
    [JsonProperty("contributors")]
    public List<ApiContributor>? Contributors { get; set; }
    
    [JsonProperty("record_type")]
    public string? RecordType { get; set; }
    
    [JsonProperty("upc")]
    public string? Upc { get; set; }
    
    [JsonProperty("label")]
    public string? Label { get; set; }
    
    [JsonProperty("copyright")]
    public string? Copyright { get; set; }
    
    [JsonProperty("original_release_date")]
    public string? OriginalReleaseDate { get; set; }
    
    [JsonProperty("genres")]
    public ApiGenreCollection? Genres { get; set; }
    
    [JsonProperty("duration")]
    public int? Duration { get; set; }
    
    [JsonProperty("fans")]
    public int? Fans { get; set; }
    
    [JsonProperty("available")]
    public bool? Available { get; set; }
    
    [JsonProperty("tracklist")]
    public string? Tracklist { get; set; }
    
    [JsonProperty("explicit_content_lyrics")]
    public int? ExplicitContentLyrics { get; set; }
    
    [JsonProperty("explicit_content_cover")]
    public int? ExplicitContentCover { get; set; }
    
    [JsonProperty("genre_id")]
    public int? GenreId { get; set; }
    
    [JsonProperty("share")]
    public string? Share { get; set; }
    
    [JsonProperty("type")]
    public string? Type { get; set; }
}

public class ApiTrackCollection
{
    [JsonProperty("data")]
    public List<ApiTrack>? Data { get; set; }
}

public class ApiGenreCollection
{
    [JsonProperty("data")]
    public List<ApiGenre>? Data { get; set; }
}

public class ApiGenre
{
    [JsonProperty("id")]
    public long? Id { get; set; }
    
    [JsonProperty("name")]
    public string? Name { get; set; }
    
    [JsonProperty("picture")]
    public string? Picture { get; set; }
    
    [JsonProperty("type")]
    public string? Type { get; set; }
}
