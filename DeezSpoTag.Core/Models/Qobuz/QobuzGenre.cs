using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzGenre
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }
}
