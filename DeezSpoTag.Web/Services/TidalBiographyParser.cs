using System.Text.Json;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Single shared parser for the Tidal OpenAPI v2 artist biography payload.
/// Handles both single-reference objects and arrays in
/// data.relationships.biography.data, and matches the included resource by id with
/// type tolerance ("artistBiographies"/"biographies"/"biography").
/// </summary>
public static class TidalBiographyParser
{
    public static string? TryReadBiographyText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !TryGetRelationshipId(data, "biography", out var biographyId)
            || biographyId.Length == 0
            || !root.TryGetProperty("included", out var included)
            || included.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in included.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryGetString(item, "id").Equals(biographyId, StringComparison.OrdinalIgnoreCase)
                || !IsBiographyResourceType(TryGetString(item, "type"))
                || !item.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = TryGetString(attributes, "text");
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static bool IsBiographyResourceType(string? type)
        => type is not null && type.Contains("biograph", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetRelationshipId(JsonElement artistData, string name, out string id)
    {
        id = string.Empty;
        if (!artistData.TryGetProperty("relationships", out var relationships)
            || relationships.ValueKind != JsonValueKind.Object
            || !relationships.TryGetProperty(name, out var relationship)
            || relationship.ValueKind != JsonValueKind.Object
            || !relationship.TryGetProperty("data", out var data))
        {
            return false;
        }

        var value = data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().FirstOrDefault()
            : data;
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return false;
        }

        id = TryGetString(value, "id") ?? string.Empty;
        return id.Length > 0;
    }

    private static string TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
}
