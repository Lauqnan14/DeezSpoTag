using System.Text.Json;

namespace DeezSpoTag.Services.Download.Shared;

public static class QueuePayloadJsonParser
{
    public static Dictionary<string, object> Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return CreateDictionary();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CreateDictionary();
            }

            return ConvertObject(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return CreateDictionary();
        }
    }

    private static Dictionary<string, object> CreateDictionary()
        => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object> ConvertObject(JsonElement element)
    {
        var result = CreateDictionary();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ConvertValue(property.Value)!;
        }

        return result;
    }

    private static object? ConvertValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => ConvertArray(element),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ConvertNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    private static List<object?> ConvertArray(JsonElement element)
    {
        var result = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            result.Add(ConvertValue(item));
        }

        return result;
    }

    private static object ConvertNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (element.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return decimalValue;
        }

        return element.GetDouble();
    }
}
