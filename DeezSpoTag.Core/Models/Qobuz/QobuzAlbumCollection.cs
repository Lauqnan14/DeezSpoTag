using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzAlbumCollection
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("items")]
    public List<QobuzAlbum> Items { get; set; } = new();
}
