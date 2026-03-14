using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzTrack
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("track_number")]
    public int TrackNumber { get; set; }

    [JsonPropertyName("media_number")]
    public int MediaNumber { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("isrc")]
    public string? ISRC { get; set; }

    [JsonPropertyName("hires")]
    public bool HiRes { get; set; }

    [JsonPropertyName("maximum_bit_depth")]
    public int MaximumBitDepth { get; set; }

    [JsonPropertyName("maximum_sampling_rate")]
    public double MaximumSamplingRate { get; set; }

    [JsonPropertyName("album")]
    public QobuzAlbum? Album { get; set; }

    [JsonPropertyName("performer")]
    public QobuzArtist? Performer { get; set; }

    [JsonPropertyName("composer")]
    public QobuzArtist? Composer { get; set; }

    [JsonPropertyName("parental_warning")]
    public bool ParentalWarning { get; set; }
}
