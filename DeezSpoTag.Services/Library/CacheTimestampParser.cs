using System.Globalization;

namespace DeezSpoTag.Services.Library;

internal static class CacheTimestampParser
{
    internal static DateTimeOffset ParseOrMin(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsedUtc)
            ? parsedUtc
            : DateTimeOffset.MinValue;
    }
}
