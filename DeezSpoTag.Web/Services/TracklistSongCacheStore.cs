using System.Globalization;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Web.Services;

public sealed class TracklistSongCacheStore : SqlitePersistentCacheStoreBase
{
    private static readonly TimeSpan FreshTtl = TimeSpan.FromSeconds(20);
    private const string TracklistTypeParam = "$tracklist_type";
    private const string TracklistIdParam = "$tracklist_id";

    private readonly ILogger<TracklistSongCacheStore> _logger;

    public TracklistSongCacheStore(IConfiguration configuration, ILogger<TracklistSongCacheStore> logger)
        : base(configuration, "deezspotag.db")
    {
        _logger = logger;
    }

    public static bool IsFresh(TracklistSongCacheEntry entry, DateTimeOffset nowUtc)
    {
        return nowUtc - entry.UpdatedUtc <= FreshTtl;
    }

    public async Task<TracklistSongCacheEntry?> TryGetAsync(
        string tracklistType,
        string tracklistId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeKey(tracklistType, tracklistId, out var normalizedType, out var normalizedId)
            || string.IsNullOrWhiteSpace(ConnectionString))
        {
            return null;
        }

        await EnsureSchemaOnceAsync(EnsureSchemaCoreAsync, _logger, "Failed to ensure tracklist cache schema.", cancellationToken);
        await CleanupStaleEntriesIfDueAsync(CleanupCoreAsync, _logger, "Tracklist cache stale cleanup failed.", cancellationToken);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT tracklist_type, tracklist_id, payload_json, payload_hash, track_count, updated_utc, last_used_at
                FROM tracklist_song_cache
                WHERE tracklist_type = {TracklistTypeParam}
                  AND tracklist_id = {TracklistIdParam}
                LIMIT 1;
                """;
            command.Parameters.AddWithValue(TracklistTypeParam, normalizedType);
            command.Parameters.AddWithValue(TracklistIdParam, normalizedId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var payloadJson = await GetStringAsync(reader, 2, cancellationToken);
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                await DeleteByKeyAsync(connection, normalizedType, normalizedId, cancellationToken);
                return null;
            }

            var lastUsedRaw = await GetStringAsync(reader, 6, cancellationToken);
            if (!TryParseTimestamp(lastUsedRaw, out var lastUsedUtc)
                || IsStale(lastUsedUtc, DateTimeOffset.UtcNow))
            {
                await DeleteByKeyAsync(connection, normalizedType, normalizedId, cancellationToken);
                return null;
            }

            var updatedUtcText = await GetStringAsync(reader, 5, cancellationToken);
            if (!TryParseTimestamp(updatedUtcText, out var updatedUtc))
            {
                updatedUtc = DateTimeOffset.UtcNow;
            }

            await TouchByKeyAsync(connection, normalizedType, normalizedId, cancellationToken);

            return new TracklistSongCacheEntry(
                TracklistType: await GetStringOrDefaultAsync(reader, 0, normalizedType, cancellationToken),
                TracklistId: await GetStringOrDefaultAsync(reader, 1, normalizedId, cancellationToken),
                PayloadJson: payloadJson,
                PayloadHash: await GetStringAsync(reader, 3, cancellationToken),
                TrackCount: await reader.IsDBNullAsync(4, cancellationToken) ? 0 : reader.GetInt32(4),
                UpdatedUtc: updatedUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read tracklist cache entry.");
            return null;
        }
    }

    public async Task<TracklistSongCacheUpsertResult> UpsertAsync(
        string tracklistType,
        string tracklistId,
        string payloadJson,
        string payloadHash,
        int trackCount,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeKey(tracklistType, tracklistId, out var normalizedType, out var normalizedId)
            || string.IsNullOrWhiteSpace(payloadJson)
            || string.IsNullOrWhiteSpace(payloadHash)
            || string.IsNullOrWhiteSpace(ConnectionString))
        {
            return TracklistSongCacheUpsertResult.Noop;
        }

        await EnsureSchemaOnceAsync(EnsureSchemaCoreAsync, _logger, "Failed to ensure tracklist cache schema.", cancellationToken);
        await CleanupStaleEntriesIfDueAsync(CleanupCoreAsync, _logger, "Tracklist cache stale cleanup failed.", cancellationToken);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            string? previousHash;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = $"""
                    SELECT payload_hash
                    FROM tracklist_song_cache
                    WHERE tracklist_type = {TracklistTypeParam}
                      AND tracklist_id = {TracklistIdParam}
                    LIMIT 1;
                    """;
                select.Parameters.AddWithValue(TracklistTypeParam, normalizedType);
                select.Parameters.AddWithValue(TracklistIdParam, normalizedId);
                previousHash = (await select.ExecuteScalarAsync(cancellationToken) as string)?.Trim();
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var nowUtcText = FormatTimestamp(nowUtc);

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = (SqliteTransaction)transaction;
                upsert.CommandText = $"""
                    INSERT INTO tracklist_song_cache(
                        tracklist_type,
                        tracklist_id,
                        payload_json,
                        payload_hash,
                        track_count,
                        last_used_at,
                        updated_utc,
                        created_at,
                        updated_at
                    ) VALUES (
                        {TracklistTypeParam},
                        {TracklistIdParam},
                        $payload_json,
                        $payload_hash,
                        $track_count,
                        $last_used_at,
                        $updated_utc,
                        $created_at,
                        $updated_at
                    )
                    ON CONFLICT(tracklist_type, tracklist_id) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        payload_hash = excluded.payload_hash,
                        track_count = excluded.track_count,
                        last_used_at = excluded.last_used_at,
                        updated_utc = excluded.updated_utc,
                        updated_at = excluded.updated_at;
                    """;
                upsert.Parameters.AddWithValue(TracklistTypeParam, normalizedType);
                upsert.Parameters.AddWithValue(TracklistIdParam, normalizedId);
                upsert.Parameters.AddWithValue("$payload_json", payloadJson);
                upsert.Parameters.AddWithValue("$payload_hash", payloadHash);
                upsert.Parameters.AddWithValue("$track_count", Math.Max(trackCount, 0));
                upsert.Parameters.AddWithValue("$last_used_at", nowUtcText);
                upsert.Parameters.AddWithValue("$updated_utc", nowUtcText);
                upsert.Parameters.AddWithValue("$created_at", nowUtcText);
                upsert.Parameters.AddWithValue("$updated_at", nowUtcText);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            var hasChanged = !string.Equals(previousHash, payloadHash, StringComparison.OrdinalIgnoreCase);
            return new TracklistSongCacheUpsertResult(hasChanged, previousHash, payloadHash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to upsert tracklist cache entry.");
            return TracklistSongCacheUpsertResult.Noop;
        }
    }

    private static async Task EnsureSchemaCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS tracklist_song_cache (
                    tracklist_type TEXT NOT NULL,
                    tracklist_id TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    payload_hash TEXT NOT NULL,
                    track_count INTEGER NOT NULL,
                    last_used_at TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    created_at TEXT,
                    updated_at TEXT,
                    PRIMARY KEY (tracklist_type, tracklist_id)
                );
                CREATE INDEX IF NOT EXISTS idx_tracklist_song_cache_last_used
                    ON tracklist_song_cache(last_used_at);
                CREATE INDEX IF NOT EXISTS idx_tracklist_song_cache_updated_utc
                    ON tracklist_song_cache(updated_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureColumnAsync(connection, "last_used_at", cancellationToken);
        await EnsureColumnAsync(connection, "created_at", cancellationToken);
        await EnsureColumnAsync(connection, "updated_at", cancellationToken);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        await using (var normalizeCommand = connection.CreateCommand())
        {
            normalizeCommand.CommandText = """
                UPDATE tracklist_song_cache
                SET last_used_at = COALESCE(NULLIF(last_used_at, ''), updated_utc),
                    created_at = COALESCE(NULLIF(created_at, ''), updated_utc, $now),
                    updated_at = COALESCE(NULLIF(updated_at, ''), updated_utc, $now)
                WHERE last_used_at IS NULL OR TRIM(last_used_at) = ''
                   OR created_at IS NULL OR TRIM(created_at) = ''
                   OR updated_at IS NULL OR TRIM(updated_at) = '';
                """;
            normalizeCommand.Parameters.AddWithValue("$now", now);
            await normalizeCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CleanupCoreAsync(SqliteConnection connection, string cutoff, CancellationToken cancellationToken)
    {
        await using var cleanupCommand = connection.CreateCommand();
        cleanupCommand.CommandText = """
            DELETE FROM tracklist_song_cache
            WHERE COALESCE(NULLIF(last_used_at, ''), updated_utc) < $cutoff;
            """;
        cleanupCommand.Parameters.AddWithValue("$cutoff", cutoff);
        await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchByKeyAsync(
        SqliteConnection connection,
        string tracklistType,
        string tracklistId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tracklist_song_cache
            SET last_used_at = $last_used_at,
                updated_at = $updated_at
            WHERE tracklist_type = $tracklist_type
              AND tracklist_id = $tracklist_id;
            """;
        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("$tracklist_type", tracklistType);
        command.Parameters.AddWithValue("$tracklist_id", tracklistId);
        command.Parameters.AddWithValue("$last_used_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteByKeyAsync(
        SqliteConnection connection,
        string tracklistType,
        string tracklistId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM tracklist_song_cache
            WHERE tracklist_type = $tracklist_type
              AND tracklist_id = $tracklist_id;
            """;
        command.Parameters.AddWithValue("$tracklist_type", tracklistType);
        command.Parameters.AddWithValue("$tracklist_id", tracklistId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(tracklist_song_cache);";
        await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);

        var exists = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            var existingName = await GetStringAsync(reader, 1, cancellationToken);
            if (string.Equals(existingName, columnName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        if (exists)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = columnName switch
        {
            "last_used_at" => "ALTER TABLE tracklist_song_cache ADD COLUMN last_used_at TEXT;",
            "created_at" => "ALTER TABLE tracklist_song_cache ADD COLUMN created_at TEXT;",
            "updated_at" => "ALTER TABLE tracklist_song_cache ADD COLUMN updated_at TEXT;",
            _ => throw new InvalidOperationException($"Unsupported tracklist cache column '{columnName}'.")
        };
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> GetStringAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        return await reader.IsDBNullAsync(ordinal, cancellationToken)
            ? string.Empty
            : reader.GetString(ordinal);
    }

    private static async Task<string> GetStringOrDefaultAsync(
        SqliteDataReader reader,
        int ordinal,
        string fallback,
        CancellationToken cancellationToken)
    {
        var value = await GetStringAsync(reader, ordinal, cancellationToken);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool TryNormalizeKey(
        string tracklistType,
        string tracklistId,
        out string normalizedType,
        out string normalizedId)
    {
        normalizedType = string.IsNullOrWhiteSpace(tracklistType)
            ? string.Empty
            : tracklistType.Trim().ToLowerInvariant();
        normalizedId = string.IsNullOrWhiteSpace(tracklistId)
            ? string.Empty
            : tracklistId.Trim();
        return !string.IsNullOrWhiteSpace(normalizedType) && !string.IsNullOrWhiteSpace(normalizedId);
    }
}

public sealed record TracklistSongCacheEntry(
    string TracklistType,
    string TracklistId,
    string PayloadJson,
    string PayloadHash,
    int TrackCount,
    DateTimeOffset UpdatedUtc);

public sealed record TracklistSongCacheUpsertResult(bool HasChanged, string? PreviousHash, string CurrentHash)
{
    public static readonly TracklistSongCacheUpsertResult Noop = new(false, null, string.Empty);
}
