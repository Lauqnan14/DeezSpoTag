using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzBiography
{
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
