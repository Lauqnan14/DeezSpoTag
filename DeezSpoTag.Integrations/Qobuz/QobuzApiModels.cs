using System.Text.Json;
using System.Text.Json.Serialization;
using DeezSpoTag.Core.Models.Qobuz;

namespace DeezSpoTag.Integrations.Qobuz;

public sealed class QobuzSearchList<T>
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("items")]
    public List<T> Items { get; set; } = new();
}

public sealed class QobuzTrackSearchResponse
{
    [JsonPropertyName("tracks")]
    public QobuzSearchList<QobuzTrack>? Tracks { get; set; }
}

public sealed class QobuzAlbumSearchResponse
{
    [JsonPropertyName("albums")]
    public QobuzSearchList<QobuzAlbum>? Albums { get; set; }
}

public sealed class QobuzArtistSearchResponse
{
    [JsonPropertyName("artists")]
    public QobuzSearchList<QobuzArtist>? Artists { get; set; }
}

public sealed class QobuzAutosuggestResponse
{
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("artists")]
    public JsonElement Artists { get; set; }

    [JsonPropertyName("albums")]
    public JsonElement Albums { get; set; }

    [JsonPropertyName("tracks")]
    public JsonElement Tracks { get; set; }
}
