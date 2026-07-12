using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LastFmConfig
{
    public int MaxTags { get; set; } = 12;
    public int MinTagCount { get; set; } = 10;
    public double MinRelativeWeight { get; set; } = 0.15;
}

public sealed class LastFmTopTagsResponse
{
    [JsonPropertyName("toptags")]
    public LastFmTopTagsContainer? Toptags { get; init; }

    [JsonPropertyName("error")]
    public int? Error { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class LastFmTopTagsContainer
{
    [JsonPropertyName("tag")]
    public List<LastFmTag>? Tag { get; init; }

    [JsonPropertyName("@attr")]
    public LastFmTopTagsAttributes? Attributes { get; init; }
}

public sealed class LastFmTopTagsAttributes
{
    [JsonPropertyName("artist")]
    public string Artist { get; init; } = string.Empty;

    [JsonPropertyName("track")]
    public string Track { get; init; } = string.Empty;
}

public sealed class LastFmTag
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Count { get; init; }
}
