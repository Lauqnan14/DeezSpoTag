using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzStringIdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Unexpected token type for Qobuz ID.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }

    private static string ReadNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var longValue))
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (reader.TryGetDecimal(out var decimalValue))
        {
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        var doubleValue = reader.GetDouble();
        return doubleValue.ToString("R", CultureInfo.InvariantCulture);
    }
}
