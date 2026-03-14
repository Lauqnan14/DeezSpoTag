using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LastFmConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public int MaxTags { get; set; } = 12;
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
}

public sealed class LastFmTag
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public string? Count { get; init; }
}

