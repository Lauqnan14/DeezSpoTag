using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatportOAuth
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";
    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }
}

public sealed class BeatportTrackResults
{
    public List<BeatportTrackResult> Data { get; set; } = new();
}

public sealed class BeatportTrackResult
{
    [JsonPropertyName("track_id")]
    public long TrackId { get; set; }
    [JsonPropertyName("track_name")]
    public string TrackName { get; set; } = "";
    public List<BeatportArtist>? Artists { get; set; }
    public string? Isrc { get; set; }
    public long? Length { get; set; }
    [JsonPropertyName("mix_name")]
    public string? MixName { get; set; }
}

public sealed class BeatportArtist
{
    [JsonPropertyName("artist_id")]
    public long ArtistId { get; set; }
    [JsonPropertyName("artist_name")]
    public string ArtistName { get; set; } = "";
    [JsonPropertyName("artist_type_name")]
    public string ArtistTypeName { get; set; } = "";
}

public sealed class BeatportTrack
{
    public List<BeatportGeneric> Artists { get; set; } = new();
    public long? Bpm { get; set; }
    [JsonPropertyName("catalog_number")]
    public string? CatalogNumber { get; set; }
    public bool Exclusive { get; set; }
    public BeatportGeneric Genre { get; set; } = new();
    public long Id { get; set; }
    public BeatportImage? Image { get; set; }
    public string? Isrc { get; set; }
    public BeatportGeneric? Key { get; set; }
    [JsonPropertyName("length_ms")]
    public long? LengthMs { get; set; }
    [JsonPropertyName("mix_name")]
    public string MixName { get; set; } = "";
    public string Name { get; set; } = "";
    public long? Number { get; set; }
    [JsonPropertyName("publish_date")]
    public string? PublishDate { get; set; }
    public BeatportRelease Release { get; set; } = new();
    public List<BeatportGeneric> Remixers { get; set; } = new();
    public string Slug { get; set; } = "";
    [JsonPropertyName("sub_genre")]
    public BeatportGeneric? SubGenre { get; set; }
    [JsonPropertyName("new_release_date")]
    public string? NewReleaseDate { get; set; }
}

public sealed class BeatportGeneric
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class BeatportImage
{
    public long Id { get; set; }
    [JsonPropertyName("dynamic_uri")]
    public string DynamicUri { get; set; } = "";
}

public sealed class BeatportRelease
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public BeatportGeneric Label { get; set; } = new();
    public BeatportImage Image { get; set; } = new();
    public string? Upc { get; set; }
    [JsonPropertyName("track_count")]
    public ushort? TrackCount { get; set; }
    public List<BeatportGeneric>? Artists { get; set; }
}

public sealed class BeatportMatchConfig
{
    public int ArtResolution { get; set; } = 500;
    public int MaxPages { get; set; } = 1;
    public bool IgnoreVersion { get; set; } = false;
}

public sealed class BeatportTrackInfo
{
    public string Platform { get; set; } = "beatport";
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string? Album { get; set; }
    public string? Key { get; set; }
    public long? Bpm { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Styles { get; set; } = new();
    public string? Art { get; set; }
    public string Url { get; set; } = "";
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public string TrackId { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public List<string> Remixers { get; set; } = new();
    public int? TrackNumber { get; set; }
    public string? Isrc { get; set; }
    public int? ReleaseYear { get; set; }
    public int? PublishYear { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public DateTime? PublishDate { get; set; }
    public int? TrackTotal { get; set; }
    public List<(string Key, List<string> Values)> Other { get; set; } = new();
}
