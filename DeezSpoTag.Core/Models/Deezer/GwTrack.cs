using Newtonsoft.Json;

namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// GW Track model (ported from deezspotag GWTrack interface)
/// EXACT PORT: Matches the JSON structure from Deezer GW API
/// </summary>
public class GwAlbumArtistMetadata
{
    [JsonProperty("ALB_ID")]
    public string AlbId { get; set; } = "";

    [JsonProperty("ALB_TITLE")]
    public string AlbTitle { get; set; } = "";

    [JsonProperty("ALB_PICTURE")]
    public string AlbPicture { get; set; } = "";

    [JsonProperty("ART_ID")]
    public long ArtId { get; set; }

    [JsonProperty("ART_NAME")]
    public string ArtName { get; set; } = "";
}

/// <summary>
/// GW Track model (ported from deezspotag GWTrack interface)
/// EXACT PORT: Matches the JSON structure from Deezer GW API
/// </summary>
public class GwTrack : GwAlbumArtistMetadata
{
    [JsonProperty("SNG_ID")]
    public long SngId { get; set; }

    [JsonProperty("SNG_TITLE")]
    public string SngTitle { get; set; } = "";

    [JsonProperty("DURATION")]
    public int Duration { get; set; }

    [JsonProperty("MD5_ORIGIN")]
    [JsonConverter(typeof(Md5OriginConverter))]
    public string Md5Origin { get; set; } = "0";

    [JsonProperty("MEDIA_VERSION")]
    public long MediaVersion { get; set; } = 0;

    [JsonProperty("FILESIZE")]
    public int Filesize { get; set; }

    [JsonProperty("ART_PICTURE")]
    public string ArtPicture { get; set; } = "";

    [JsonProperty("ISRC")]
    public string Isrc { get; set; } = "";

    [JsonProperty("TRACK_TOKEN")]
    public string TrackToken { get; set; } = "";

    [JsonProperty("TRACK_TOKEN_EXPIRE")]
    public int TrackTokenExpire { get; set; }

    [JsonProperty("TOKEN")]
    public string Token { get; set; } = "";

    [JsonProperty("USER_ID")]
    public string UserId { get; set; } = "";

    [JsonProperty("TRACK_NUMBER")]
    public int TrackNumber { get; set; }

    [JsonProperty("DISK_NUMBER")]
    public int DiskNumber { get; set; }

    [JsonProperty("VERSION")]
    public string Version { get; set; } = "";

    [JsonProperty("EXPLICIT_LYRICS")]
    public bool ExplicitLyrics { get; set; }

    [JsonProperty("GAIN")]
    public double Gain { get; set; }

    [JsonProperty("LYRICS_ID")]
    public string? LyricsId { get; set; }

    [JsonProperty("LYRICS")]
    public string? Lyrics { get; set; }

    [JsonProperty("COPYRIGHT")]
    public string? Copyright { get; set; }

    [JsonProperty("PHYSICAL_RELEASE_DATE")]
    public string? PhysicalReleaseDate { get; set; }

    [JsonProperty("DIGITAL_RELEASE_DATE")]
    public string? DigitalReleaseDate { get; set; }

    [JsonProperty("FALLBACK")]
    public object? Fallback { get; set; }

    [JsonProperty("ALBUM_FALLBACK")]
    public object? AlbumFallback { get; set; }

    // File sizes for different formats
    [JsonProperty("FILESIZE_MP3_128")]
    public int? FilesizeMp3128 { get; set; }

    [JsonProperty("FILESIZE_MP3_320")]
    public int? FilesizeMp3320 { get; set; }

    [JsonProperty("FILESIZE_FLAC")]
    public int? FilesizeFlac { get; set; }

    [JsonProperty("FILESIZE_MP4_RA1")]
    public int? FilesizeMp4Ra1 { get; set; }

    [JsonProperty("FILESIZE_MP4_RA2")]
    public int? FilesizeMp4Ra2 { get; set; }

    [JsonProperty("FILESIZE_MP4_RA3")]
    public int? FilesizeMp4Ra3 { get; set; }

    // Additional properties
    public int Position { get; set; }
    public int? FallbackId { get; set; }
}

/// <summary>
/// Custom converter to handle MD5_ORIGIN field that can be either a string (MD5 hash) or a number
/// EXACT PORT: Handles the same issue that deezspotag encounters where MD5_ORIGIN can be different types
/// </summary>
public class Md5OriginConverter : JsonConverter<string>
{
    public override string ReadJson(JsonReader reader, Type objectType, string? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.String)
        {
            // When MD5_ORIGIN is a string (MD5 hash), track IS encoded - return the MD5 string
            return reader.Value?.ToString() ?? "0";
        }
        else if (reader.TokenType == JsonToken.Integer)
        {
            // When MD5_ORIGIN is a number, convert to string
            // If it's 0, track is NOT encoded
            return reader.Value?.ToString() ?? "0";
        }
        else if (reader.TokenType == JsonToken.Null)
        {
            // Null means not encoded
            return "0";
        }

        throw new JsonSerializationException($"Unexpected token type for MD5_ORIGIN: {reader.TokenType}");
    }

    public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer)
    {
        writer.WriteValue(value);
    }
}
