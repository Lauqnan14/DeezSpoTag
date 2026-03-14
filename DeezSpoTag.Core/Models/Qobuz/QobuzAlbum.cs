using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzAlbum
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(QobuzStringIdConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("artists")]
    public List<QobuzArtist> Artists { get; set; } = new();

    [JsonPropertyName("image")]
    public QobuzImage? Image { get; set; }

    [JsonPropertyName("released_at")]
    public long ReleasedAt { get; set; }

    [JsonPropertyName("release_date_original")]
    public string? ReleaseDateOriginal { get; set; }

    [JsonPropertyName("release_date_download")]
    public string? ReleaseDateDownload { get; set; }

    [JsonPropertyName("release_date_stream")]
    public string? ReleaseDateStream { get; set; }

    [JsonPropertyName("upc")]
    public string? UPC { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("maximum_bit_depth")]
    public int MaximumBitDepth { get; set; }

    [JsonPropertyName("maximum_sampling_rate")]
    public double MaximumSamplingRate { get; set; }

    [JsonPropertyName("hires")]
    public bool HiRes { get; set; }

    [JsonPropertyName("hires_streamable")]
    public bool HiResStreamable { get; set; }

    [JsonPropertyName("maximum_channel_count")]
    public int MaximumChannelCount { get; set; }

    [JsonPropertyName("streamable")]
    public bool Streamable { get; set; }

    [JsonPropertyName("purchasable")]
    public bool Purchasable { get; set; }

    [JsonPropertyName("downloadable")]
    public bool Downloadable { get; set; }

    [JsonPropertyName("label")]
    public QobuzLabel? Label { get; set; }

    [JsonPropertyName("genre")]
    public QobuzGenre? Genre { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("tracks_count")]
    public int TracksCount { get; set; }

    [JsonPropertyName("parental_warning")]
    public bool ParentalWarning { get; set; }

    [JsonPropertyName("popularity")]
    public int Popularity { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
