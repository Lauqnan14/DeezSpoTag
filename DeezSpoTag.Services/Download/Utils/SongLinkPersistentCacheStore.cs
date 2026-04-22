using System.Text.Json;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class SongLinkPersistentCacheStore
{
    private const string TableName = "song_link_cache";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<SongLinkPersistentCacheStore> _logger;
    private readonly string? _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private volatile bool _schemaEnsured;
    private DateTimeOffset _nextCleanupUtc = DateTimeOffset.MinValue;

    public SongLinkPersistentCacheStore(
        IConfiguration configuration,
        ILogger<SongLinkPersistentCacheStore> logger)
    {
        _logger = logger;
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");
        _connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }

    public async Task<SongLinkResult?> TryGetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(_connectionString))
        {
            return null;
        }

        await EnsureSchemaAsync(cancellationToken);
        await CleanupStaleEntriesIfDueAsync(cancellationToken);

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(@"
SELECT result_json, last_used_at
FROM song_link_cache
WHERE cache_key = $cache_key
LIMIT 1;", connection);
            command.Parameters.AddWithValue("$cache_key", cacheKey);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var resultJson = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var lastUsedRaw = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(resultJson)
                || !DateTimeOffset.TryParse(lastUsedRaw, out var lastUsedUtc)
                || DateTimeOffset.UtcNow - lastUsedUtc > StaleAfter)
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
            || string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await CleanupStaleEntriesIfDueAsync(cancellationToken);

        var payload = JsonSerializer.Serialize(result, JsonOptions);
        var now = DateTimeOffset.UtcNow.ToString("O");

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
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
            command.Parameters.AddWithValue("$cache_key", cacheKey);
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

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured || string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
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
            _schemaEnsured = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to ensure song-link cache schema.");
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async Task CleanupStaleEntriesIfDueAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextCleanupUtc)
        {
            return;
        }

        var hasLock = await _cleanupGate.WaitAsync(0, cancellationToken);
        if (!hasLock)
        {
            return;
        }

        try
        {
            now = DateTimeOffset.UtcNow;
            if (now < _nextCleanupUtc)
            {
                return;
            }

            await EnsureSchemaAsync(cancellationToken);
            var cutoff = now.Subtract(StaleAfter).ToString("O");

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var cleanupCommand = new SqliteCommand(@"
DELETE FROM song_link_cache
WHERE last_used_at < $cutoff;", connection);
            cleanupCommand.Parameters.AddWithValue("$cutoff", cutoff);
            await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
            _nextCleanupUtc = now.Add(CleanupInterval);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Song-link cache stale cleanup failed.");
            _nextCleanupUtc = DateTimeOffset.UtcNow.Add(CleanupInterval);
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    private static async Task TouchByKeyAsync(SqliteConnection connection, string cacheKey, CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
UPDATE song_link_cache
SET last_used_at = $last_used_at,
    updated_at = CURRENT_TIMESTAMP
WHERE cache_key = $cache_key;", connection);
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        command.Parameters.AddWithValue("$last_used_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteByKeyAsync(SqliteConnection connection, string cacheKey, CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
DELETE FROM song_link_cache
WHERE cache_key = $cache_key;", connection);
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeCountry(string? userCountry)
    {
        return string.IsNullOrWhiteSpace(userCountry)
            ? string.Empty
            : userCountry.Trim().ToUpperInvariant();
    }
}
