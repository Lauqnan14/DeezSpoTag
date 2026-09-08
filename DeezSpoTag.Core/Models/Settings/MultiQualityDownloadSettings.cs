using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// Allows downloading multiple quality variants (e.g., Atmos + stereo) for the same items
/// by enqueuing two tasks with different source/quality/destination settings.
/// </summary>
[JsonConverter(typeof(MultiQualityDownloadSettingsJsonConverter))]
public sealed class MultiQualityDownloadSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When enabled, the app may enqueue a second download task for the same track.
    /// </summary>
    public bool SecondaryEnabled { get; set; } = false;

    /// <summary>
    /// Optional destination folder for Atmos/profile A downloads.
    /// If null, uses the request destination folder (or default download root).
    /// </summary>
    public long? PrimaryDestinationFolderId { get; set; }

    /// <summary>
    /// Optional destination folder for stereo/profile B downloads.
    /// If null, uses the request destination folder (or default download root).
    /// </summary>
    public long? SecondaryDestinationFolderId { get; set; }

    /// <summary>
    /// Primary engine used for the Atmos/profile A download branch.
    /// </summary>
    public string AtmosEngine { get; set; } = "apple";

    /// <summary>
    /// Allows Atmos discovery and downloading to use the other enabled Atmos-capable providers.
    /// </summary>
    public bool AtmosFallbackEnabled { get; set; } = false;
}

public sealed class MultiQualityDownloadSettingsJsonConverter : JsonConverter<MultiQualityDownloadSettings>
{
    public override MultiQualityDownloadSettings Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Multi-quality settings must be a JSON object.");
        }

        var settings = new MultiQualityDownloadSettings();
        var canonicalFallbackSeen = false;
        var canonicalFallback = false;
        var legacyFallback = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                settings.AtmosFallbackEnabled = canonicalFallbackSeen
                    ? canonicalFallback
                    : legacyFallback;
                return settings;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Invalid multi-quality settings property.");
            }

            var propertyName = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                throw new JsonException("Missing multi-quality settings value.");
            }

            switch (propertyName.ToLowerInvariant())
            {
                case "enabled":
                    settings.Enabled = reader.GetBoolean();
                    break;
                case "secondaryenabled":
                    settings.SecondaryEnabled = reader.GetBoolean();
                    break;
                case "primarydestinationfolderid":
                    settings.PrimaryDestinationFolderId = ReadNullableInt64(ref reader);
                    break;
                case "secondarydestinationfolderid":
                    settings.SecondaryDestinationFolderId = ReadNullableInt64(ref reader);
                    break;
                case "atmosengine":
                    settings.AtmosEngine = reader.TokenType == JsonTokenType.Null
                        ? "apple"
                        : reader.GetString() ?? "apple";
                    break;
                case "atmosfallbackenabled":
                    canonicalFallbackSeen = true;
                    canonicalFallback = reader.GetBoolean();
                    break;
                case "atmossearchfallback":
                case "atmosdownloadfallback":
                    legacyFallback |= reader.GetBoolean();
                    break;
                default:
                    using (JsonDocument.ParseValue(ref reader))
                    {
                    }
                    break;
            }
        }

        throw new JsonException("Incomplete multi-quality settings object.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        MultiQualityDownloadSettings value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WriteBoolean("secondaryEnabled", value.SecondaryEnabled);
        WriteNullableInt64(writer, "primaryDestinationFolderId", value.PrimaryDestinationFolderId);
        WriteNullableInt64(writer, "secondaryDestinationFolderId", value.SecondaryDestinationFolderId);
        writer.WriteString("atmosEngine", value.AtmosEngine);
        writer.WriteBoolean("atmosFallbackEnabled", value.AtmosFallbackEnabled);
        writer.WriteEndObject();
    }

    private static long? ReadNullableInt64(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetInt64();

    private static void WriteNullableInt64(Utf8JsonWriter writer, string propertyName, long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }
}
