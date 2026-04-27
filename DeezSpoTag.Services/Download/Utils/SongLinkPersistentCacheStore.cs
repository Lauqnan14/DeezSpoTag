using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class SongLinkPersistentCacheStore : SqlitePersistentCacheStoreBase
{
    private const string CacheKeyParameter = "$cache_key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<SongLinkPersistentCacheStore> _logger;

    public SongLinkPersistentCacheStore(
        IConfiguration configuration,
        ILogger<SongLinkPersistentCacheStore> logger)
        : base(configuration, "deezspotag.db")
    {
        _logger = logger;
    }

    public async Task<SongLinkResult?> TryGetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(ConnectionString))
        {
            return null;
        }

        await EnsureSchemaOnceAsync(EnsureSchemaCoreAsync, _logger, "Failed to ensure song-link cache schema.", cancellationToken);
        await CleanupStaleEntriesIfDueAsync(CleanupCoreAsync, _logger, "Song-link cache stale cleanup failed.", cancellationToken);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(@"
SELECT result_json, last_used_at
FROM song_link_cache
WHERE cache_key = $cache_key
LIMIT 1;", connection);
            command.Parameters.AddWithValue(CacheKeyParameter, cacheKey);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var resultJson = await reader.IsDBNullAsync(0, cancellationToken) ? string.Empty : reader.GetString(0);
            var lastUsedRaw = await reader.IsDBNullAsync(1, cancellationToken) ? string.Empty : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(resultJson)
                || !TryParseTimestamp(lastUsedRaw, out var lastUsedUtc)
                || IsStale(lastUsedUtc, DateTimeOffset.UtcNow))
            {
                await DeleteByKeyAsync(connection, cacheKey, cancellationToken);
                return null;
            }

            SongLinkResult? value;
            try
            {
                value = JsonSerializer.Deserialize<SongLinkResult>(resultJson, JsonOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to deserialize song-link cache payload for key {CacheKey}", cacheKey);
                await DeleteByKeyAsync(connection, cacheKey, cancellationToken);
                return null;
            }

            if (value == null)
            {
                await DeleteByKeyAsync(connection, cacheKey, cancellationToken);
                return null;
            }

            await TouchByKeyAsync(connection, cacheKey, cancellationToken);
            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Song-link cache lookup failed for key {CacheKey}", cacheKey);
            return null;
        }
    }

    public async Task UpsertAsync(
        string cacheKey,
        string normalizedUrl,
        string? userCountry,
        SongLinkResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey)
            || string.IsNullOrWhiteSpace(normalizedUrl)
            || result == null
            || string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await EnsureSchemaOnceAsync(EnsureSchemaCoreAsync, _logger, "Failed to ensure song-link cache schema.", cancellationToken);
        await CleanupStaleEntriesIfDueAsync(CleanupCoreAsync, _logger, "Song-link cache stale cleanup failed.", cancellationToken);

        var payload = JsonSerializer.Serialize(result, JsonOptions);
        var now = FormatTimestamp(DateTimeOffset.UtcNow);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(@"
INSERT INTO song_link_cache (
    cache_key,
    normalized_url,
    user_country,
    result_json,
    last_used_at,
    created_at,
    updated_at
)
VALUES (
    $cache_key,
    $normalized_url,
    $user_country,
    $result_json,
    $last_used_at,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
)
ON CONFLICT(cache_key) DO UPDATE SET
    normalized_url = excluded.normalized_url,
    user_country = excluded.user_country,
    result_json = excluded.result_json,
    last_used_at = excluded.last_used_at,
    updated_at = CURRENT_TIMESTAMP;", connection);
            command.Parameters.AddWithValue(CacheKeyParameter, cacheKey);
            command.Parameters.AddWithValue("$normalized_url", normalizedUrl);
            command.Parameters.AddWithValue("$user_country", NormalizeCountry(userCountry));
            command.Parameters.AddWithValue("$result_json", payload);
            command.Parameters.AddWithValue("$last_used_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Song-link cache upsert failed for key {CacheKey}", cacheKey);
        }
    }

    private static async Task EnsureSchemaCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
CREATE TABLE IF NOT EXISTS song_link_cache (
    cache_key TEXT NOT NULL PRIMARY KEY,
    normalized_url TEXT NOT NULL,
    user_country TEXT,
    result_json TEXT NOT NULL,
    last_used_at TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_song_link_cache_last_used
    ON song_link_cache (last_used_at);
CREATE INDEX IF NOT EXISTS idx_song_link_cache_url_country
    ON song_link_cache (normalized_url, user_country);", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupCoreAsync(SqliteConnection connection, string cutoff, CancellationToken cancellationToken)
    {
        await using var cleanupCommand = new SqliteCommand(@"
DELETE FROM song_link_cache
WHERE last_used_at < $cutoff;", connection);
        cleanupCommand.Parameters.AddWithValue("$cutoff", cutoff);
        await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchByKeyAsync(SqliteConnection connection, string cacheKey, CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
UPDATE song_link_cache
SET last_used_at = $last_used_at,
    updated_at = CURRENT_TIMESTAMP
WHERE cache_key = $cache_key;", connection);
        command.Parameters.AddWithValue(CacheKeyParameter, cacheKey);
        command.Parameters.AddWithValue("$last_used_at", FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteByKeyAsync(SqliteConnection connection, string cacheKey, CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
DELETE FROM song_link_cache
WHERE cache_key = $cache_key;", connection);
        command.Parameters.AddWithValue(CacheKeyParameter, cacheKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeCountry(string? userCountry)
    {
        return string.IsNullOrWhiteSpace(userCountry)
            ? string.Empty
            : userCountry.Trim().ToUpperInvariant();
    }
}
