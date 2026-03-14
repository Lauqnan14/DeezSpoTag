using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzArtist
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("image")]
    public QobuzImage? Image { get; set; }

    [JsonPropertyName("biography")]
    public QobuzBiography? Biography { get; set; }

    [JsonPropertyName("albums_count")]
    public int AlbumsCount { get; set; }

    [JsonPropertyName("albums_as_primary_artist_count")]
    public int AlbumsAsPrimaryArtistCount { get; set; }

    [JsonPropertyName("albums_as_primary_composer_count")]
    public int AlbumsAsPrimaryComposerCount { get; set; }

    [JsonPropertyName("similar_artist_ids")]
    public List<int> SimilarArtistIds { get; set; } = new();

    [JsonPropertyName("albums_without_last_release")]
    public QobuzAlbumCollection? Albums { get; set; }
}
