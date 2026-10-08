using System.Globalization;
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

    /// <summary>
    /// The paths the queue writes into a payload's "cover" to mean "no artwork was found"
    /// (see <c>QueuePayloadBuilder.DefaultCoverPath</c>). They are presentation placeholders, not
    /// covers, and are never treated as a track's own artwork.
    /// </summary>
    public static readonly string[] CoverPlaceholderPaths =
        ["/images/unavailable/unavailable.jpg", "/images/default-cover.png"];

    /// <summary>
    /// Reads the first key holding a non-blank value, rendered the same way for every value kind.
    /// </summary>
    public static string? ReadValue(
        IReadOnlyDictionary<string, object> payload,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!payload.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var normalized = NormalizeValue(value)?.Trim();
            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the first key holding a track's own artwork. A presentation placeholder is skipped so a
    /// real cover further down the key list still wins, and absent artwork stays absent.
    /// </summary>
    public static string? ReadCoverUrl(IReadOnlyDictionary<string, object> payload, params string[] keys)
    {
        foreach (var value in keys
                     .Select(key => ReadValue(payload, key))
                     .Where(candidate => candidate is not null && !IsCoverPlaceholder(candidate)))
        {
            return value;
        }

        return null;
    }

    public static bool IsCoverPlaceholder(string? value)
        => CoverPlaceholderPaths.Any(path =>
            string.Equals(value?.Trim(), path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The first key holding a whole, positive, int-representable number.
    /// </summary>
    /// <remarks>
    /// An integer written as a JSON string is accepted, because that is how a queue payload
    /// serialises some providers. A fractional value such as 4.7 is NOT accepted: rounding it would
    /// invent a number the source never stated. Zero and negative values are treated as absent so a
    /// placeholder cannot displace a real value further down the key list.
    /// </remarks>
    public static int? ReadPositiveInt32(IReadOnlyDictionary<string, object> payload, params string[] keys)
    {
        var raw = ReadValue(payload, keys);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
    }

    /// <summary>
    /// Accepts a JSON boolean, 0/1, or a boolean string in any casing. Returns null when the payload
    /// says nothing, so an unknown explicit status is never recorded as "false".
    /// </summary>
    public static bool? ReadBoolean(IReadOnlyDictionary<string, object> payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!payload.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is bool flag)
            {
                return flag;
            }

            var normalized = NormalizeValue(value)?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (normalized == "1")
            {
                return true;
            }

            if (normalized == "0")
            {
                return false;
            }

            if (bool.TryParse(normalized, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// A queue row's own duration wins, then a millisecond payload value, then legacy seconds. The
    /// seconds path is bounded BEFORE multiplying, because a silent integer overflow would store a
    /// negative duration on an otherwise valid record.
    /// </summary>
    public static int? ResolveDurationMs(
        IReadOnlyDictionary<string, object> payload,
        int? queueDurationMs,
        string[] millisecondKeys,
        string[] secondKeys)
    {
        if (queueDurationMs is > 0)
        {
            return queueDurationMs;
        }

        var payloadMs = ReadPositiveInt32(payload, millisecondKeys);
        if (payloadMs.HasValue)
        {
            return payloadMs;
        }

        var seconds = ReadPositiveInt32(payload, secondKeys);
        return seconds is > 0 && seconds <= int.MaxValue / 1000 ? seconds.Value * 1000 : null;
    }

    private static string? NormalizeValue(object? value)
        => value switch
        {
            null => null,
            JsonElement element => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.ToString()
            },
            _ => value.ToString()
        };

    private static Dictionary<string, object> ConvertObject(JsonElement element)
    {
        var result = CreateDictionary();
        foreach (var property in element.EnumerateObject())
        {
            result[NormalizePropertyName(property.Name)] = ConvertValue(property.Value)!;
        }

        return result;
    }

    private static string NormalizePropertyName(string propertyName)
    {
        return string.Equals(propertyName, "finalDestinations", StringComparison.OrdinalIgnoreCase)
            ? "finalDestinations"
            : propertyName;
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
