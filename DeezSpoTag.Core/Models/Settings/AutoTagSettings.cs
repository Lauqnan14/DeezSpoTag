using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Settings;

public sealed class AutoTagSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Data { get; set; } = new();
}
