using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DeezerConfig
{
    [JsonPropertyName("art_resolution")]
    public int ArtResolution { get; set; } = 1200;

    [JsonPropertyName("arl")]
    public string? Arl { get; set; }

    [JsonPropertyName("max_bitrate")]
    public int? MaxBitrate { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("match_by_id")]
    public bool MatchById { get; set; }
}

public sealed class DeezerResponse<T>
{
    public T? Data { get; set; }
    public DeezerError? Error { get; set; }

    public bool IsError => Error != null;
}

public sealed class DeezerError
{
    public int Code { get; set; }
    [JsonPropertyName("type")]
    public string ErrorType { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class DeezerSearchResults<T>
{
    public List<T> Data { get; set; } = new();
    public int Total { get; set; }
    public string? Next { get; set; }
}

public sealed class DeezerTrack
{
    public long Id { get; set; }
    public bool Readable { get; set; }
    public string Title { get; set; } = "";
    [JsonPropertyName("title_short")]
    public string TitleShort { get; set; } = "";
    [JsonPropertyName("title_version")]
    public string? TitleVersion { get; set; }
    public string Link { get; set; } = "";
    public int Duration { get; set; }
    public long Rank { get; set; }
    public DeezerArtist Artist { get; set; } = new();
    public DeezerAlbum Album { get; set; } = new();
    [JsonPropertyName("explicit_lyrics")]
    public bool? ExplicitLyrics { get; set; }
    [JsonPropertyName("explicit_content_lyrics")]
    public int? ExplicitContentLyrics { get; set; }

    public DeezerTrackInfo ToTrackInfo()
    {
        return new DeezerTrackInfo
        {
            Title = TitleShort,
            Version = TitleVersion,
            Artists = new List<string> { Artist.Name },
            Album = Album.Title,
            ArtHash = Album.Md5Image,
            Url = Link,
            CatalogNumber = Id.ToString(),
            TrackId = Id.ToString(),
            ReleaseId = Album.Id.ToString(),
            Duration = TimeSpan.FromSeconds(Duration),
            Explicit = ExplicitLyrics ?? (ExplicitContentLyrics.HasValue ? ExplicitContentLyrics.Value == 1 : null)
        };
    }
}

public sealed class DeezerArtist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Picture { get; set; } = "";
    [JsonPropertyName("picture_small")]
    public string PictureSmall { get; set; } = "";
    [JsonPropertyName("picture_medium")]
    public string PictureMedium { get; set; } = "";
    [JsonPropertyName("picture_big")]
    public string PictureBig { get; set; } = "";
    [JsonPropertyName("picture_xl")]
    public string PictureXl { get; set; } = "";
}

public sealed class DeezerAlbum
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    [JsonPropertyName("md5_image")]
    public string Md5Image { get; set; } = "";
}

public sealed class DeezerTrackFull
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    [JsonPropertyName("title_short")]
    public string TitleShort { get; set; } = "";
    [JsonPropertyName("title_version")]
    public string? TitleVersion { get; set; }
    public string? Isrc { get; set; }
    public string Link { get; set; } = "";
    public int Duration { get; set; }
    [JsonPropertyName("track_position")]
    public int? TrackPosition { get; set; }
    [JsonPropertyName("disk_number")]
    public ushort? DiskNumber { get; set; }
    [JsonPropertyName("release_date")]
    public string ReleaseDate { get; set; } = "";
    public double? Bpm { get; set; }
    public double? Gain { get; set; }
    public List<DeezerArtist> Contributors { get; set; } = new();
    [JsonPropertyName("md5_image")]
    public string Md5Image { get; set; } = "";
    public DeezerArtist Artist { get; set; } = new();
    public DeezerAlbum Album { get; set; } = new();
}

public sealed class DeezerAlbumFull
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Upc { get; set; }
    public string Link { get; set; } = "";
    public string Share { get; set; } = "";
    [JsonPropertyName("md5_image")]
    public string Md5Image { get; set; } = "";
    [JsonPropertyName("genre_id")]
    public long GenreId { get; set; }
    public DeezerGenres Genres { get; set; } = new();
    public string Label { get; set; } = "";
    [JsonPropertyName("nb_tracks")]
    public ushort NbTracks { get; set; }
    public int Duration { get; set; }
    [JsonPropertyName("release_date")]
    public string ReleaseDate { get; set; } = "";
    public List<DeezerArtist> Contributors { get; set; } = new();
    public DeezerArtist Artist { get; set; } = new();
}

public sealed class DeezerGenre
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DeezerGenres
{
    public List<DeezerGenre> Data { get; set; } = new();
}

public sealed class DeezerTrackInfo
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string? Album { get; set; }
    public string? ArtHash { get; set; }
    public string? ArtUrl { get; set; }
    public string Url { get; set; } = "";
    public string? CatalogNumber { get; set; }
    public string TrackId { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Label { get; set; }
    public string? Isrc { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? TrackTotal { get; set; }
    public long? Bpm { get; set; }
    public bool? Explicit { get; set; }
    public string? UnsyncedLyrics { get; set; }
    public List<string> SyncedLyrics { get; set; } = new();
}

public sealed class DeezerLyricsPayload
{
    public string? UnsyncedLyrics { get; set; }
    public List<string> SyncedLyrics { get; set; } = new();
}
