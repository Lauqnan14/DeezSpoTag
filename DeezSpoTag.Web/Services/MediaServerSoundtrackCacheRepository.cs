using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackCacheRepository
{
    private readonly string _connectionString;
    private readonly ILogger<MediaServerSoundtrackCacheRepository> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public MediaServerSoundtrackCacheRepository(
        IWebHostEnvironment environment,
        ILogger<MediaServerSoundtrackCacheRepository> logger)
    {
        _logger = logger;

        var dataRoot = AppDataPaths.GetDataRoot(environment);
        var cacheDirectory = Path.Join(dataRoot, "media-server");
        Directory.CreateDirectory(cacheDirectory);
        var cacheDbPath = Path.Join(cacheDirectory, "soundtrack-cache.db");
        _connectionString = $"Data Source={cacheDbPath};Cache=Shared";
    }

    public async Task UpsertItemsAsync(IReadOnlyList<MediaServerSoundtrackItemDto> items, CancellationToken cancellationToken)
    {
        if (items == null || items.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken);

        const string sql = """
INSERT INTO soundtrack_media_cache (
    server_type,
    server_label,
    library_id,
    library_name,
    category,
    item_id,
    title,
    year,
    image_url,
    content_hash,
    is_active,
    first_seen_utc,
    last_seen_utc,
    soundtrack_kind,
    soundtrack_deezer_id,
    soundtrack_title,
    soundtrack_subtitle,
    soundtrack_url,
    soundtrack_cover_url,
    soundtrack_score,
    match_provider,
    match_reason,
    match_locked,
    match_retry_count,
    match_resolved_utc,
    updated_at_utc
) VALUES (
    $server_type,
    $server_label,
    $library_id,
    $library_name,
    $category,
    $item_id,
    $title,
    $year,
    $image_url,
    $content_hash,
    $is_active,
    $first_seen_utc,
    $last_seen_utc,
    $soundtrack_kind,
    $soundtrack_deezer_id,
    $soundtrack_title,
    $soundtrack_subtitle,
    $soundtrack_url,
    $soundtrack_cover_url,
    $soundtrack_score,
    $match_provider,
    $match_reason,
    $match_locked,
    $match_retry_count,
    $match_resolved_utc,
    $updated_at_utc
)
ON CONFLICT(server_type, library_id, item_id) DO UPDATE SET
    server_label = excluded.server_label,
    library_name = excluded.library_name,
    category = excluded.category,
    title = excluded.title,
    year = excluded.year,
    image_url = excluded.image_url,
    content_hash = excluded.content_hash,
    is_active = excluded.is_active,
    first_seen_utc = COALESCE(soundtrack_media_cache.first_seen_utc, excluded.first_seen_utc),
    last_seen_utc = excluded.last_seen_utc,
    soundtrack_kind = excluded.soundtrack_kind,
    soundtrack_deezer_id = excluded.soundtrack_deezer_id,
    soundtrack_title = excluded.soundtrack_title,
    soundtrack_subtitle = excluded.soundtrack_subtitle,
    soundtrack_url = excluded.soundtrack_url,
    soundtrack_cover_url = excluded.soundtrack_cover_url,
    soundtrack_score = excluded.soundtrack_score,
    match_provider = excluded.match_provider,
    match_reason = excluded.match_reason,
    match_locked = excluded.match_locked,
    match_retry_count = excluded.match_retry_count,
    match_resolved_utc = excluded.match_resolved_utc,
    updated_at_utc = excluded.updated_at_utc;
""";

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);

            var nowUtc = DateTimeOffset.UtcNow;
            var nowUtcText = nowUtc.ToString("O", CultureInfo.InvariantCulture);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item?.ServerType)
                    || string.IsNullOrWhiteSpace(item.LibraryId)
                    || string.IsNullOrWhiteSpace(item.ItemId)
                    || string.IsNullOrWhiteSpace(item.Title))
                {
                    continue;
                }

                var normalizedContentHash = Normalize(item.ContentHash);
                if (string.IsNullOrWhiteSpace(normalizedContentHash))
                {
                    normalizedContentHash = BuildFallbackContentHash(item);
                }

                var match = item.Soundtrack;
                var matchProvider = NormalizeMatchProvider(match?.Provider, match?.Kind, match?.Url);
                var matchResolvedUtc = match?.ResolvedAtUtc;
                if (matchResolvedUtc == null && HasResolvedSoundtrack(match))
                {
                    matchResolvedUtc = nowUtc;
                }

                command.Parameters.Clear();
                command.Parameters.AddWithValue("$server_type", Normalize(item.ServerType));
                command.Parameters.AddWithValue("$server_label", Normalize(item.ServerLabel));
                command.Parameters.AddWithValue("$library_id", Normalize(item.LibraryId));
                command.Parameters.AddWithValue("$library_name", Normalize(item.LibraryName));
                command.Parameters.AddWithValue("$category", NormalizeCategory(item.Category));
                command.Parameters.AddWithValue("$item_id", Normalize(item.ItemId));
                command.Parameters.AddWithValue("$title", Normalize(item.Title));
                command.Parameters.AddWithValue("$year", item.Year.HasValue ? item.Year.Value : DBNull.Value);
                command.Parameters.AddWithValue("$image_url", NullOrText(item.ImageUrl));
                command.Parameters.AddWithValue("$content_hash", normalizedContentHash);
                command.Parameters.AddWithValue("$is_active", item.IsActive ? 1 : 0);
                command.Parameters.AddWithValue("$first_seen_utc", (item.FirstSeenUtc ?? nowUtc).ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$last_seen_utc", (item.LastSeenUtc ?? nowUtc).ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$soundtrack_kind", NullOrText(match?.Kind));
                command.Parameters.AddWithValue("$soundtrack_deezer_id", NullOrText(match?.DeezerId));
                command.Parameters.AddWithValue("$soundtrack_title", NullOrText(match?.Title));
                command.Parameters.AddWithValue("$soundtrack_subtitle", NullOrText(match?.Subtitle));
                command.Parameters.AddWithValue("$soundtrack_url", NullOrText(match?.Url));
                command.Parameters.AddWithValue("$soundtrack_cover_url", NullOrText(match?.CoverUrl));
                command.Parameters.AddWithValue("$soundtrack_score", match != null ? match.Score : DBNull.Value);
                command.Parameters.AddWithValue("$match_provider", NullOrText(matchProvider));
                command.Parameters.AddWithValue("$match_reason", NullOrText(match?.Reason));
                command.Parameters.AddWithValue("$match_locked", match?.Locked == true ? 1 : 0);
                command.Parameters.AddWithValue("$match_retry_count", Math.Max(match?.RetryCount ?? 0, 0));
                command.Parameters.AddWithValue("$match_resolved_utc", matchResolvedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$updated_at_utc", nowUtcText);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed persisting media server soundtrack cache rows.");
        }
    }

    public async Task<List<MediaServerSoundtrackItemDto>> GetItemsAsync(
        string category,
        string? serverType,
        string? libraryId,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var normalizedCategory = NormalizeCategory(category);
        var normalizedServerType = Normalize(serverType);
        var normalizedLibraryId = Normalize(libraryId);
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit <= 0 ? 120 : limit, 1, 500);

        var sql = """
SELECT
    server_type,
    server_label,
    library_id,
    library_name,
    category,
    item_id,
    title,
    year,
    image_url,
    content_hash,
    is_active,
    first_seen_utc,
    last_seen_utc,
    soundtrack_kind,
    soundtrack_deezer_id,
    soundtrack_title,
    soundtrack_subtitle,
    soundtrack_url,
    soundtrack_cover_url,
    soundtrack_score,
    match_provider,
    match_reason,
    match_locked,
    match_retry_count,
    match_resolved_utc
FROM soundtrack_media_cache
WHERE category = $category
  AND is_active = 1
""";

        if (!string.IsNullOrWhiteSpace(normalizedServerType))
        {
            sql += "\nAND server_type = $server_type";
        }

        if (!string.IsNullOrWhiteSpace(normalizedLibraryId))
        {
            sql += "\nAND library_id = $library_id";
        }

        sql += """

ORDER BY title COLLATE NOCASE, COALESCE(year, 0), item_id
LIMIT $limit OFFSET $offset;
""";

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("$category", normalizedCategory);
            if (!string.IsNullOrWhiteSpace(normalizedServerType))
            {
                command.Parameters.AddWithValue("$server_type", normalizedServerType);
            }
            if (!string.IsNullOrWhiteSpace(normalizedLibraryId))
            {
                command.Parameters.AddWithValue("$library_id", normalizedLibraryId);
            }
            command.Parameters.AddWithValue("$limit", safeLimit);
            command.Parameters.AddWithValue("$offset", safeOffset);

            var rows = new List<MediaServerSoundtrackItemDto>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(BuildItemRow(reader));
            }

            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed loading media server soundtrack cache rows.");
            return new List<MediaServerSoundtrackItemDto>();
        }
    }

    public async Task<Dictionary<string, MediaServerSoundtrackItemDto>> GetItemsByIdsAsync(
        string serverType,
        string libraryId,
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, MediaServerSoundtrackItemDto>(StringComparer.OrdinalIgnoreCase);
        if (itemIds == null || itemIds.Count == 0)
        {
            return rows;
        }

        await EnsureInitializedAsync(cancellationToken);

        var normalizedServerType = Normalize(serverType);
        var normalizedLibraryId = Normalize(libraryId);
        if (string.IsNullOrWhiteSpace(normalizedServerType) || string.IsNullOrWhiteSpace(normalizedLibraryId))
        {
            return rows;
        }

        var normalizedIds = itemIds
            .Select(Normalize)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedIds.Length == 0)
        {
            return rows;
        }

        const int maxBatchSize = 250;
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            for (var offset = 0; offset < normalizedIds.Length; offset += maxBatchSize)
            {
                var batch = normalizedIds.Skip(offset).Take(maxBatchSize).ToArray();
                if (batch.Length == 0)
                {
                    continue;
                }

                var sql = new StringBuilder("""
SELECT
    server_type,
    server_label,
    library_id,
    library_name,
    category,
    item_id,
    title,
    year,
    image_url,
    content_hash,
    is_active,
    first_seen_utc,
    last_seen_utc,
    soundtrack_kind,
    soundtrack_deezer_id,
    soundtrack_title,
    soundtrack_subtitle,
    soundtrack_url,
    soundtrack_cover_url,
    soundtrack_score,
    match_provider,
    match_reason,
    match_locked,
    match_retry_count,
    match_resolved_utc
FROM soundtrack_media_cache
WHERE server_type = $server_type
  AND library_id = $library_id
  AND item_id IN (
""");

                for (var index = 0; index < batch.Length; index++)
                {
                    if (index > 0)
                    {
                        sql.Append(", ");
                    }

                    sql.Append("$item_id_").Append(index);
                }

                sql.Append(");");

                await using var command = new SqliteCommand(sql.ToString(), connection);
                command.Parameters.AddWithValue("$server_type", normalizedServerType);
                command.Parameters.AddWithValue("$library_id", normalizedLibraryId);
                for (var index = 0; index < batch.Length; index++)
                {
                    command.Parameters.AddWithValue($"$item_id_{index}", batch[index]);
                }

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = BuildItemRow(reader);
                    var key = Normalize(row.ItemId);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        rows[key] = row;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed loading media server soundtrack rows by item ids.");
        }

        return rows;
    }

    public async Task DeactivateLibraryItemsNotSeenSinceAsync(
        string serverType,
        string libraryId,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = """
UPDATE soundtrack_media_cache
SET is_active = 0,
    updated_at_utc = $updated_at_utc
WHERE server_type = $server_type
  AND library_id = $library_id
  AND last_seen_utc < $cutoff_utc;
""";
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("$server_type", Normalize(serverType));
            command.Parameters.AddWithValue("$library_id", Normalize(libraryId));
            command.Parameters.AddWithValue("$cutoff_utc", cutoffUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed deactivating stale soundtrack rows for {ServerType}/{LibraryId}.", serverType, libraryId);
        }
    }

    public async Task UpsertLibrarySyncStateAsync(MediaServerSoundtrackLibrarySyncStateDto state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await EnsureInitializedAsync(cancellationToken);

        const string sql = """
INSERT INTO soundtrack_library_sync_state (
    server_type,
    library_id,
    category,
    status,
    last_offset,
    last_batch_count,
    total_processed,
    last_sync_utc,
    last_success_utc,
    last_error,
    updated_at_utc
) VALUES (
    $server_type,
    $library_id,
    $category,
    $status,
    $last_offset,
    $last_batch_count,
    $total_processed,
    $last_sync_utc,
    $last_success_utc,
    $last_error,
    $updated_at_utc
)
ON CONFLICT(server_type, library_id) DO UPDATE SET
    category = excluded.category,
    status = excluded.status,
    last_offset = excluded.last_offset,
    last_batch_count = excluded.last_batch_count,
    total_processed = excluded.total_processed,
    last_sync_utc = excluded.last_sync_utc,
    last_success_utc = excluded.last_success_utc,
    last_error = excluded.last_error,
    updated_at_utc = excluded.updated_at_utc;
""";

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("$server_type", Normalize(state.ServerType));
            command.Parameters.AddWithValue("$library_id", Normalize(state.LibraryId));
            command.Parameters.AddWithValue("$category", NormalizeCategory(state.Category));
            command.Parameters.AddWithValue("$status", Normalize(state.Status));
            command.Parameters.AddWithValue("$last_offset", Math.Max(state.LastOffset, 0));
            command.Parameters.AddWithValue("$last_batch_count", Math.Max(state.LastBatchCount, 0));
            command.Parameters.AddWithValue("$total_processed", Math.Max(state.TotalProcessed, 0));
            command.Parameters.AddWithValue("$last_sync_utc", state.LastSyncUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$last_success_utc", state.LastSuccessUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$last_error", NullOrText(state.LastError));
            command.Parameters.AddWithValue("$updated_at_utc", (state.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : state.UpdatedAtUtc).ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed persisting soundtrack sync state for {ServerType}/{LibraryId}", state.ServerType, state.LibraryId);
        }
    }

    public async Task<MediaServerSoundtrackLibrarySyncStateDto?> GetLibrarySyncStateAsync(
        string serverType,
        string libraryId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalizedServerType = Normalize(serverType);
        var normalizedLibraryId = Normalize(libraryId);
        if (string.IsNullOrWhiteSpace(normalizedServerType) || string.IsNullOrWhiteSpace(normalizedLibraryId))
        {
            return null;
        }

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = """
SELECT
    server_type,
    library_id,
    category,
    status,
    last_offset,
    last_batch_count,
    total_processed,
    last_sync_utc,
    last_success_utc,
    last_error,
    updated_at_utc
FROM soundtrack_library_sync_state
WHERE server_type = $server_type
  AND library_id = $library_id
LIMIT 1;
""";
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("$server_type", normalizedServerType);
            command.Parameters.AddWithValue("$library_id", normalizedLibraryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return ReadSyncState(reader);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed loading soundtrack sync state for {ServerType}/{LibraryId}", serverType, libraryId);
            return null;
        }
    }

    public async Task<List<MediaServerSoundtrackLibrarySyncStateDto>> GetLibrarySyncStatesAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = """
SELECT
    server_type,
    library_id,
    category,
    status,
    last_offset,
    last_batch_count,
    total_processed,
    last_sync_utc,
    last_success_utc,
    last_error,
    updated_at_utc
FROM soundtrack_library_sync_state
ORDER BY updated_at_utc DESC, server_type, library_id;
""";
            await using var command = new SqliteCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<MediaServerSoundtrackLibrarySyncStateDto>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadSyncState(reader));
            }

            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed loading soundtrack sync state list.");
            return new List<MediaServerSoundtrackLibrarySyncStateDto>();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var modeCommand = new SqliteCommand("PRAGMA journal_mode=WAL;", connection))
            {
                await modeCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string schemaSql = """
CREATE TABLE IF NOT EXISTS soundtrack_media_cache (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    server_type TEXT NOT NULL,
    server_label TEXT NOT NULL,
    library_id TEXT NOT NULL,
    library_name TEXT NOT NULL,
    category TEXT NOT NULL,
    item_id TEXT NOT NULL,
    title TEXT NOT NULL,
    year INTEGER NULL,
    image_url TEXT NULL,
    content_hash TEXT NOT NULL DEFAULT '',
    is_active INTEGER NOT NULL DEFAULT 1,
    first_seen_utc TEXT NULL,
    last_seen_utc TEXT NOT NULL,
    soundtrack_kind TEXT NULL,
    soundtrack_deezer_id TEXT NULL,
    soundtrack_title TEXT NULL,
    soundtrack_subtitle TEXT NULL,
    soundtrack_url TEXT NULL,
    soundtrack_cover_url TEXT NULL,
    soundtrack_score REAL NULL,
    match_provider TEXT NULL,
    match_reason TEXT NULL,
    match_locked INTEGER NOT NULL DEFAULT 0,
    match_retry_count INTEGER NOT NULL DEFAULT 0,
    match_resolved_utc TEXT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE(server_type, library_id, item_id)
);

CREATE INDEX IF NOT EXISTS idx_soundtrack_media_cache_filters
ON soundtrack_media_cache (category, is_active, server_type, library_id, title COLLATE NOCASE);

CREATE INDEX IF NOT EXISTS idx_soundtrack_media_cache_updated
ON soundtrack_media_cache (updated_at_utc DESC);

CREATE TABLE IF NOT EXISTS soundtrack_library_sync_state (
    server_type TEXT NOT NULL,
    library_id TEXT NOT NULL,
    category TEXT NOT NULL,
    status TEXT NOT NULL,
    last_offset INTEGER NOT NULL DEFAULT 0,
    last_batch_count INTEGER NOT NULL DEFAULT 0,
    total_processed INTEGER NOT NULL DEFAULT 0,
    last_sync_utc TEXT NULL,
    last_success_utc TEXT NULL,
    last_error TEXT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(server_type, library_id)
);
""";

            await using (var schemaCommand = new SqliteCommand(schemaSql, connection))
            {
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureColumnAsync(connection, "soundtrack_media_cache", "content_hash", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnAsync(connection, "soundtrack_media_cache", "is_active", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "soundtrack_media_cache", "first_seen_utc", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "soundtrack_media_cache", "match_provider", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "soundtrack_media_cache", "match_reason", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "soundtrack_media_cache", "match_locked", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "soundtrack_media_cache", "match_retry_count", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "soundtrack_media_cache", "match_resolved_utc", "TEXT NULL", cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definitionSql,
        CancellationToken cancellationToken)
    {
        var columns = await GetTableColumnsAsync(connection, tableName, cancellationToken);
        if (columns.Contains(columnName))
        {
            return;
        }

        var alterSql = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definitionSql};";
        await using var alterCommand = new SqliteCommand(alterSql, connection);
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragmaSql = $"PRAGMA table_info({tableName});";
        await using var pragmaCommand = new SqliteCommand(pragmaSql, connection);
        await using var reader = await pragmaCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static MediaServerSoundtrackItemDto BuildItemRow(SqliteDataReader reader)
    {
        var soundtrack = BuildSoundtrack(reader, 13);
        return new MediaServerSoundtrackItemDto
        {
            ServerType = ReadString(reader, 0),
            ServerLabel = ReadString(reader, 1),
            LibraryId = ReadString(reader, 2),
            LibraryName = ReadString(reader, 3),
            Category = ReadString(reader, 4),
            ItemId = ReadString(reader, 5),
            Title = ReadString(reader, 6),
            Year = ReadNullableInt(reader, 7),
            ImageUrl = ReadNullableString(reader, 8),
            ContentHash = ReadString(reader, 9),
            IsActive = ReadBool(reader, 10),
            FirstSeenUtc = ReadNullableDateTimeOffset(reader, 11),
            LastSeenUtc = ReadNullableDateTimeOffset(reader, 12),
            Soundtrack = soundtrack
        };
    }

    private static MediaServerSoundtrackMatchDto? BuildSoundtrack(SqliteDataReader reader, int startIndex)
    {
        var kind = ReadNullableString(reader, startIndex);
        var deezerId = ReadNullableString(reader, startIndex + 1);
        var title = ReadNullableString(reader, startIndex + 2);
        var subtitle = ReadNullableString(reader, startIndex + 3);
        var url = ReadNullableString(reader, startIndex + 4);
        var coverUrl = ReadNullableString(reader, startIndex + 5);
        var score = ReadNullableDouble(reader, startIndex + 6);
        var provider = ReadNullableString(reader, startIndex + 7);
        var reason = ReadNullableString(reader, startIndex + 8);
        var locked = ReadBool(reader, startIndex + 9);
        var retryCount = ReadNullableInt(reader, startIndex + 10) ?? 0;
        var resolvedUtc = ReadNullableDateTimeOffset(reader, startIndex + 11);

        if (string.IsNullOrWhiteSpace(kind)
            && string.IsNullOrWhiteSpace(deezerId)
            && string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new MediaServerSoundtrackMatchDto
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? "search" : kind!,
            DeezerId = deezerId,
            Title = string.IsNullOrWhiteSpace(title) ? string.Empty : title!,
            Subtitle = subtitle,
            Url = string.IsNullOrWhiteSpace(url) ? string.Empty : url!,
            CoverUrl = coverUrl,
            Score = score ?? 0,
            Provider = provider,
            Reason = reason,
            Locked = locked,
            RetryCount = Math.Max(retryCount, 0),
            ResolvedAtUtc = resolvedUtc
        };
    }

    private static MediaServerSoundtrackLibrarySyncStateDto ReadSyncState(SqliteDataReader reader)
    {
        return new MediaServerSoundtrackLibrarySyncStateDto
        {
            ServerType = ReadString(reader, 0),
            LibraryId = ReadString(reader, 1),
            Category = ReadString(reader, 2),
            Status = ReadString(reader, 3),
            LastOffset = ReadNullableInt(reader, 4) ?? 0,
            LastBatchCount = ReadNullableInt(reader, 5) ?? 0,
            TotalProcessed = ReadNullableInt(reader, 6) ?? 0,
            LastSyncUtc = ReadNullableDateTimeOffset(reader, 7),
            LastSuccessUtc = ReadNullableDateTimeOffset(reader, 8),
            LastError = ReadNullableString(reader, 9),
            UpdatedAtUtc = ReadNullableDateTimeOffset(reader, 10) ?? DateTimeOffset.UtcNow
        };
    }

    private static bool HasResolvedSoundtrack(MediaServerSoundtrackMatchDto? match)
    {
        if (match == null)
        {
            return false;
        }

        var kind = Normalize(match.Kind).ToLowerInvariant();
        var deezerId = Normalize(match.DeezerId);
        return !string.IsNullOrWhiteSpace(deezerId) && !string.Equals(kind, "search", StringComparison.Ordinal);
    }

    private static string BuildFallbackContentHash(MediaServerSoundtrackItemDto item)
        => $"{Normalize(item.Title)}|{item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}|{Normalize(item.ImageUrl)}|{NormalizeCategory(item.Category)}";

    private static string NormalizeMatchProvider(string? provider, string? kind, string? url)
    {
        var normalizedProvider = Normalize(provider).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedProvider))
        {
            return normalizedProvider;
        }

        var normalizedKind = Normalize(kind).ToLowerInvariant();
        if (normalizedKind.StartsWith("spotify_", StringComparison.Ordinal))
        {
            return "spotify";
        }

        if (normalizedKind is "album" or "playlist" or "track")
        {
            return "deezer";
        }

        var normalizedUrl = Normalize(url).ToLowerInvariant();
        if (normalizedUrl.Contains("spotify.com", StringComparison.Ordinal))
        {
            return "spotify";
        }

        if (normalizedUrl.Contains("deezer.com", StringComparison.Ordinal))
        {
            return "deezer";
        }

        return "search";
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeCategory(string? value)
    {
        var normalized = Normalize(value).ToLowerInvariant();
        return normalized == MediaServerSoundtrackConstants.TvShowCategory
            ? MediaServerSoundtrackConstants.TvShowCategory
            : MediaServerSoundtrackConstants.MovieCategory;
    }

    private static object NullOrText(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string ReadString(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? string.Empty : reader.GetString(index);

    private static string? ReadNullableString(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static int? ReadNullableInt(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetInt32(index);

    private static double? ReadNullableDouble(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetDouble(index);

    private static bool ReadBool(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return false;
        }

        var value = reader.GetValue(index);
        return value switch
        {
            long longValue => longValue != 0,
            int intValue => intValue != 0,
            bool boolValue => boolValue,
            string text => text == "1" || bool.TryParse(text, out var parsed) && parsed,
            _ => false
        };
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return null;
        }

        var text = reader.GetString(index);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
