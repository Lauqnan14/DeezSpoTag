using Microsoft.Data.Sqlite;

namespace DeezSpoTag.Services.Library;

internal static class SqlitePayloadCacheReader
{
    internal readonly record struct CacheRow(bool Found, string PayloadJson, DateTimeOffset FetchedUtc);

    public static async Task<CacheRow> TryReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CacheRow(false, string.Empty, DateTimeOffset.MinValue);
        }

        var payload = reader.GetString(0);
        var fetchedUtc = CacheTimestampParser.ParseOrMin(reader.GetString(1));
        return new CacheRow(true, payload, fetchedUtc);
    }
}
