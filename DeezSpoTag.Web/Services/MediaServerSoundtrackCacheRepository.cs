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
    soundtrack_kind,
    soundtrack_deezer_id,
    soundtrack_title,
    soundtrack_subtitle,
    soundtrack_url,
    soundtrack_cover_url,
    soundtrack_score,
    last_seen_utc,
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
    $soundtrack_kind,
    $soundtrack_deezer_id,
    $soundtrack_title,
    $soundtrack_subtitle,
    $soundtrack_url,
    $soundtrack_cover_url,
    $soundtrack_score,
    $last_seen_utc,
    $updated_at_utc
)
ON CONFLICT(server_type, library_id, item_id) DO UPDATE SET
    server_label = excluded.server_label,
    library_name = excluded.library_name,
    category = excluded.category,
    title = excluded.title,
    year = excluded.year,
    image_url = excluded.image_url,
    soundtrack_kind = excluded.soundtrack_kind,
    soundtrack_deezer_id = excluded.soundtrack_deezer_id,
    soundtrack_title = excluded.soundtrack_title,
    soundtrack_subtitle = excluded.soundtrack_subtitle,
    soundtrack_url = excluded.soundtrack_url,
    soundtrack_cover_url = excluded.soundtrack_cover_url,
    soundtrack_score = excluded.soundtrack_score,
    last_seen_utc = excluded.last_seen_utc,
    updated_at_utc = excluded.updated_at_utc;
""";

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);

            var nowUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item?.ServerType)
                    || string.IsNullOrWhiteSpace(item.LibraryId)
                    || string.IsNullOrWhiteSpace(item.ItemId)
                    || string.IsNullOrWhiteSpace(item.Title))
                {
                    continue;
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
                command.Parameters.AddWithValue("$soundtrack_kind", NullOrText(item.Soundtrack?.Kind));
                command.Parameters.AddWithValue("$soundtrack_deezer_id", NullOrText(item.Soundtrack?.DeezerId));
                command.Parameters.AddWithValue("$soundtrack_title", NullOrText(item.Soundtrack?.Title));
                command.Parameters.AddWithValue("$soundtrack_subtitle", NullOrText(item.Soundtrack?.Subtitle));
                command.Parameters.AddWithValue("$soundtrack_url", NullOrText(item.Soundtrack?.Url));
                command.Parameters.AddWithValue("$soundtrack_cover_url", NullOrText(item.Soundtrack?.CoverUrl));
                command.Parameters.AddWithValue("$soundtrack_score", item.Soundtrack != null ? item.Soundtrack.Score : DBNull.Value);
                command.Parameters.AddWithValue("$last_seen_utc", nowUtc);
                command.Parameters.AddWithValue("$updated_at_utc", nowUtc);

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
    soundtrack_kind,
    soundtrack_deezer_id,
    soundtrack_title,
    soundtrack_subtitle,
    soundtrack_url,
    soundtrack_cover_url,
    soundtrack_score
FROM soundtrack_media_cache
WHERE category = $category
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
                var soundtrack = BuildSoundtrack(reader);
                rows.Add(new MediaServerSoundtrackItemDto
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
                    Soundtrack = soundtrack
                });
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
    soundtrack_kind,
    soundtrack_deezer_id,
    soundtrack_title,
    soundtrack_subtitle,
    soundtrack_url,
    soundtrack_cover_url,
    soundtrack_score
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
                    var soundtrack = BuildSoundtrack(reader);
                    var row = new MediaServerSoundtrackItemDto
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
                        Soundtrack = soundtrack
                    };

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
    soundtrack_kind TEXT NULL,
    soundtrack_deezer_id TEXT NULL,
    soundtrack_title TEXT NULL,
    soundtrack_subtitle TEXT NULL,
    soundtrack_url TEXT NULL,
    soundtrack_cover_url TEXT NULL,
    soundtrack_score REAL NULL,
    last_seen_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE(server_type, library_id, item_id)
);

CREATE INDEX IF NOT EXISTS idx_soundtrack_media_cache_filters
ON soundtrack_media_cache (category, server_type, library_id, title COLLATE NOCASE);
""";

            await using (var schemaCommand = new SqliteCommand(schemaSql, connection))
            {
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static MediaServerSoundtrackMatchDto? BuildSoundtrack(SqliteDataReader reader)
    {
        var kind = ReadNullableString(reader, 9);
        var deezerId = ReadNullableString(reader, 10);
        var title = ReadNullableString(reader, 11);
        var subtitle = ReadNullableString(reader, 12);
        var url = ReadNullableString(reader, 13);
        var coverUrl = ReadNullableString(reader, 14);
        var score = ReadNullableDouble(reader, 15);

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
            Score = score ?? 0
        };
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
}
