using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Utils;
using System.Globalization;

namespace DeezSpoTag.Services.Library;

public sealed class SpotifyMetadataCacheRepository
{
    private const string TypeParameter = "$type";
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpotifyMetadataCacheRepository> _logger;
    private readonly TimeSpan _ttl = TimeSpan.FromDays(7);
    private readonly TimeSpan _maxStale = TimeSpan.FromDays(30);

    public SpotifyMetadataCacheRepository(IConfiguration configuration, ILogger<SpotifyMetadataCacheRepository> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsFresh(DateTimeOffset fetchedUtc) => DateTimeOffset.UtcNow - fetchedUtc <= _ttl;

    public bool IsUsable(DateTimeOffset fetchedUtc) => DateTimeOffset.UtcNow - fetchedUtc <= _maxStale;

    public async Task<SpotifyMetadataCacheEntry?> TryGetAsync(string type, string sourceId, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        const string sql = @"
SELECT payload_json, fetched_utc
FROM spotify_metadata_cache
WHERE type = $type AND source_id = $source_id
LIMIT 1;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue(TypeParameter, type);
            command.Parameters.AddWithValue("$source_id", sourceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var payload = reader.GetString(0);
            var fetchedText = reader.GetString(1);
            if (!DateTimeOffset.TryParse(
                    fetchedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var fetchedUtc))
            {
                fetchedUtc = DateTimeOffset.MinValue;
            }

            return new SpotifyMetadataCacheEntry(payload, fetchedUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify metadata cache lookup failed (type={Type}, id={Id})", type, sourceId);
            return null;
        }
    }

    public async Task UpsertAsync(string type, string sourceId, string payloadJson, DateTimeOffset fetchedUtc, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        const string sql = @"
INSERT INTO spotify_metadata_cache (type, source_id, payload_json, fetched_utc, created_at, updated_at)
VALUES ($type, $source_id, $payload_json, $fetched_utc, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT(type, source_id) DO UPDATE SET
    payload_json = excluded.payload_json,
    fetched_utc = excluded.fetched_utc,
    updated_at = CURRENT_TIMESTAMP;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue(TypeParameter, type);
            command.Parameters.AddWithValue("$source_id", sourceId);
            command.Parameters.AddWithValue("$payload_json", payloadJson);
            command.Parameters.AddWithValue("$fetched_utc", fetchedUtc.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify metadata cache upsert failed (type={Type}, id={Id})", type, sourceId);
        }
    }

    public async Task ClearAsync(string? type, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var sql = string.IsNullOrWhiteSpace(type)
            ? "DELETE FROM spotify_metadata_cache;"
            : "DELETE FROM spotify_metadata_cache WHERE type = $type;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            if (!string.IsNullOrWhiteSpace(type))
            {
                command.Parameters.AddWithValue(TypeParameter, type);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify metadata cache clear failed (type={Type})", type ?? "all");
        }
    }

    public async Task ClearEntryAsync(string type, string sourceId, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        const string sql = "DELETE FROM spotify_metadata_cache WHERE type = $type AND source_id = $source_id;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue(TypeParameter, type);
            command.Parameters.AddWithValue("$source_id", sourceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify metadata cache clear failed (type={Type}, id={Id})", type, sourceId);
        }
    }

    public async Task<SpotifyMetadataCacheStats?> TryGetStatsAsync(CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        const string sql = @"
SELECT type, COUNT(*)
FROM spotify_metadata_cache
GROUP BY type;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.GetString(0);
                var count = reader.GetInt32(1);
                byType[type] = count;
                total += count;
            }

            return new SpotifyMetadataCacheStats(total, byType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify metadata cache stats failed");
            return null;
        }
    }

    private string? GetConnectionString()
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? _configuration.GetConnectionString("Library");
        return SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }
}

public sealed record SpotifyMetadataCacheEntry(string PayloadJson, DateTimeOffset FetchedUtc);

public sealed record SpotifyMetadataCacheStats(int Total, Dictionary<string, int> ByType);
