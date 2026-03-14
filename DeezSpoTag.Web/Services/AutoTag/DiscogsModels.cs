using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CA1846
namespace DeezSpoTag.Web.Services.AutoTag;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscogsReleaseType
{
    Release,
    Master
}

public sealed class DiscogsSearchResult
{
    public long Id { get; set; }
    [JsonPropertyName("type")]
    public DiscogsReleaseType Type { get; set; }
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [JsonPropertyName("year")]
    [JsonConverter(typeof(DiscogsNullableIntConverter))]
    public int? Year { get; set; }
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
    [JsonPropertyName("thumb")]
    public string? Thumb { get; set; }
    [JsonPropertyName("format")]
    public List<string>? Formats { get; set; }
    [JsonPropertyName("label")]
    public List<string>? Labels { get; set; }
    [JsonPropertyName("resource_url")]
    public string Url { get; set; } = "";
}

public sealed class DiscogsArtist
{
    public string Name { get; set; } = "";
    public long Id { get; set; }
}

public sealed class DiscogsExtraArtist
{
    public string Name { get; set; } = "";
    public long Id { get; set; }
    public string Role { get; set; } = "";
}

public sealed class DiscogsTrack
{
    public string Position { get; set; } = "";
    public string Title { get; set; } = "";
    public List<DiscogsExtraArtist>? Artists { get; set; }
    public string Duration { get; set; } = "";
}

public sealed class DiscogsLabel
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Catno { get; set; }
}

public sealed class DiscogsImage
{
    public long Height { get; set; }
    public long Width { get; set; }
    [JsonPropertyName("uri")]
    public string Url { get; set; } = "";
    [JsonPropertyName("type")]
    public string ImageType { get; set; } = "";
}

public sealed class DiscogsReleaseFormat
{
    public string Name { get; set; } = "";
    public string Qty { get; set; } = "";
    public List<string>? Descriptions { get; set; }
}

public sealed class DiscogsRelease
{
    public long Id { get; set; }
    public List<string>? Styles { get; set; }
    public List<string> Genres { get; set; } = new();
    [JsonConverter(typeof(DiscogsNullableIntConverter))]
    public int? Year { get; set; }
    public List<DiscogsArtist> Artists { get; set; } = new();
    [JsonPropertyName("extraartists")]
    public List<DiscogsExtraArtist>? ExtraArtists { get; set; }
    public string? Country { get; set; }
    [JsonPropertyName("uri")]
    public string Url { get; set; } = "";
    public List<DiscogsLabel>? Labels { get; set; }
    public string Title { get; set; } = "";
    public List<DiscogsImage>? Images { get; set; }
    [JsonPropertyName("tracklist")]
    public List<DiscogsTrack> Tracks { get; set; } = new();
    public string? Released { get; set; }
    [JsonPropertyName("main_release")]
    public long? MainRelease { get; set; }
    public List<DiscogsReleaseFormat>? Formats { get; set; }
}

public sealed class DiscogsConfig
{
    public string Token { get; set; } = "";
    public int MaxAlbums { get; set; } = 4;
    public bool TrackNumberInt { get; set; } = false;
    public int? RateLimit { get; set; }
}

public sealed class DiscogsNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var value32))
                {
                    return value32;
                }

                if (reader.TryGetInt64(out var value64) && value64 >= int.MinValue && value64 <= int.MaxValue)
                {
                    return (int)value64;
                }

                return null;
            case JsonTokenType.String:
                return ParseFlexibleInt(reader.GetString());
            default:
                using (JsonDocument.ParseValue(ref reader))
                {
                    return null;
                }
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
            return;
        }

        writer.WriteNullValue();
    }

    private static int? ParseFlexibleInt(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        // Discogs occasionally returns composite date strings (for example "1987-11-12").
        // Use the first 4-digit run when direct parsing fails.
        for (var i = 0; i <= value.Length - 4; i++)
        {
            if (!char.IsDigit(value[i]) ||
                !char.IsDigit(value[i + 1]) ||
                !char.IsDigit(value[i + 2]) ||
                !char.IsDigit(value[i + 3]))
            {
                continue;
            }

            if (int.TryParse(value.Substring(i, 4), out parsed)) // NOSONAR
            {
                return parsed;
            }
        }

        return null;
    }
}
