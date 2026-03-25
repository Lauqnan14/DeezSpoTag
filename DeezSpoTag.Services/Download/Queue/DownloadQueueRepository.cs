using System.Linq;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Download.Queue;
public sealed class DownloadQueueRepository
{
    private static readonly SemaphoreSlim DequeueGate = new(1, 1);
    private const string DownloadTaskTable = "download_task";
    private readonly string _connectionString;
    private bool _schemaEnsured;
    private readonly object _schemaLock = new();

    public DownloadQueueRepository(IConfiguration configuration, ILogger<DownloadQueueRepository> logger)
    {
        _ = logger;
        var rawConnection =
            Environment.GetEnvironmentVariable("QUEUE_DB")
            ?? configuration.GetConnectionString("Queue")
            ?? Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");

        _connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "queue.db")
            ?? throw new InvalidOperationException("Queue database connection string is not configured.");
    }

    public static bool IsConfigured => true;

    public async Task<long?> EnqueueAsync(DownloadQueueItem item, CancellationToken cancellationToken = default)
        => await EnqueueAsync(item, skipDuplicateCheck: false, cancellationToken);

    public async Task<long?> EnqueueAsync(
        DownloadQueueItem item,
        bool skipDuplicateCheck,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (!skipDuplicateCheck && await ExistsDuplicateAsync(
                DuplicateLookupRequest.FromQueueItem(item),
                cancellationToken))
        {
            return null;
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var queueOrder = item.QueueOrder ?? await GetNextQueueOrderAsync(connection, cancellationToken);
        const string sql = @"
INSERT OR IGNORE INTO " + DownloadTaskTable + @"
    (queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id, spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id, duration_ms, destination_folder_id, quality_rank, queue_order, content_type, status, payload, progress, downloaded, failed, error, created_at, updated_at)
VALUES
    (@queueUuid, @engine, @artistName, @trackTitle, @isrc, @deezerTrackId, @deezerAlbumId, @deezerArtistId, @spotifyTrackId, @spotifyAlbumId, @spotifyArtistId, @appleTrackId, @appleAlbumId, @appleArtistId, @durationMs, @destinationFolderId, @qualityRank, @queueOrder, @contentType, @status, @payload, @progress, @downloaded, @failed, @error, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
SELECT last_insert_rowid();";
        await using var command = new SqliteCommand(sql, connection);
        BindCommonParameters(command, item with { QueueOrder = queueOrder });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task<bool> RequeueAsync(string queueUuid, CancellationToken cancellationToken = default)
        => await RequeueAsync(queueUuid, requeueToFront: false, newestFirst: false, cancellationToken);

    public async Task<bool> RequeueAsync(
        string queueUuid,
        bool requeueToFront,
        bool newestFirst,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var queueOrder = requeueToFront
            ? await GetFrontQueueOrderAsync(connection, newestFirst, cancellationToken)
            : await GetNextQueueOrderAsync(connection, cancellationToken);
        const string sql = @"
UPDATE " + DownloadTaskTable + @"
SET status = 'queued',
    error = NULL,
    progress = 0,
    downloaded = 0,
    failed = 0,
    queue_order = @queueOrder,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("queueOrder", queueOrder);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task UpdateProgressAsync(
        string queueUuid,
        double? progress,
        int? downloaded,
        int? failed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET progress = COALESCE(@progress, progress),
    downloaded = COALESCE(@downloaded, downloaded),
    failed = COALESCE(@failed, failed),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("progress", progress ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("downloaded", downloaded ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("failed", failed ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task UpdateProgressAsync(string queueUuid, double progress, CancellationToken cancellationToken = default)
    {
        return UpdateProgressAsync(queueUuid, progress, downloaded: null, failed: null, cancellationToken);
    }

    public async Task<DownloadQueueItem?> GetByUuidAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return null;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type,
       status, payload, progress, downloaded, failed, error, created_at, updated_at
FROM download_task
WHERE queue_uuid = @queueUuid
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadItem(reader);
    }

    public async Task<DownloadQueueItem?> DequeueNextAsync(string engine, bool newestFirst, CancellationToken cancellationToken = default)
    {
        return await DequeueNextCoreAsync(
            newestFirst,
            command => command.Parameters.AddWithValue("engine", engine),
            "AND engine = @engine",
            cancellationToken);
    }

    public async Task<DownloadQueueItem?> DequeueNextAnyAsync(bool newestFirst, CancellationToken cancellationToken = default)
    {
        return await DequeueNextCoreAsync(newestFirst, null, string.Empty, cancellationToken);
    }

    public async Task<DownloadQueueItem?> DequeueNextAnyExceptAsync(
        IReadOnlyCollection<string> excludedEngines,
        bool newestFirst,
        CancellationToken cancellationToken = default)
    {
        if (excludedEngines.Count == 0)
        {
            return await DequeueNextAnyAsync(newestFirst, cancellationToken);
        }

        var placeholders = string.Join(", ", excludedEngines.Select((_, index) => $"@exclude{index}"));
        return await DequeueNextCoreAsync(
            newestFirst,
            command =>
            {
                var indexer = 0;
                foreach (var engine in excludedEngines)
                {
                    command.Parameters.AddWithValue($"exclude{indexer}", engine);
                    indexer++;
                }
            },
            $"AND engine NOT IN ({placeholders})",
            cancellationToken);
    }

    public async Task<IReadOnlyList<DownloadQueueItem>> GetTasksAsync(string? engine = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type,
       status, payload, progress, downloaded, failed, error, created_at, updated_at
FROM download_task
WHERE (@engine IS NULL OR engine = @engine)
ORDER BY (queue_order IS NULL), queue_order ASC, created_at;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", (object?)engine ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<DownloadQueueItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<int> GetQueuedCountAsync(string engine, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT COUNT(*)
FROM download_task
WHERE status = 'queued'
  AND engine = @engine;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", engine);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<int> GetQueuedCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT COUNT(*)
FROM download_task
WHERE status = 'queued';";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<bool> HasActiveDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT 1
FROM download_task
WHERE status IN ('queued', 'running', 'paused')
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task UpdateStatusAsync(string queueUuid, string status, string? error = null, int? downloaded = null, int? failed = null, double? progress = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET status = @status,
    error = @error,
    downloaded = @downloaded,
    failed = @failed,
    progress = @progress,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("downloaded", (object?)downloaded ?? DBNull.Value);
        command.Parameters.AddWithValue("failed", (object?)failed ?? DBNull.Value);
        command.Parameters.AddWithValue("progress", (object?)progress ?? DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PauseQueuedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET status = 'paused',
    updated_at = CURRENT_TIMESTAMP
WHERE status = 'queued';";
        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
    }

    public async Task ResumePausedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET status = 'queued',
    updated_at = CURRENT_TIMESTAMP
WHERE status = 'paused';";
        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
    }

    private async Task<DownloadQueueItem?> DequeueNextCoreAsync(
        bool newestFirst,
        Action<SqliteCommand>? bindParameters,
        string extraWhereClause,
        CancellationToken cancellationToken)
    {
        await DequeueGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var sql = BuildDequeueSelectSql(newestFirst, extraWhereClause);
            await using var selectCommand = new SqliteCommand(sql, connection, transaction);
            bindParameters?.Invoke(selectCommand);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var item = ReadItem(reader);
            await UpdateDequeuedItemStatusAsync(connection, transaction, item.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return item with { Status = "running" };
        }
        finally
        {
            DequeueGate.Release();
        }
    }

    private static string BuildDequeueSelectSql(bool newestFirst, string extraWhereClause)
    {
        var orderBy = newestFirst ? "DESC" : "ASC";
        return $@"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type,
       status, payload, progress, downloaded, failed, error, created_at, updated_at
FROM download_task
WHERE status = 'queued'
  {extraWhereClause}
ORDER BY (queue_order IS NULL), queue_order ASC, created_at {orderBy}
LIMIT 1;";
    }

    private static async Task UpdateDequeuedItemStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long itemId,
        CancellationToken cancellationToken)
    {
        const string updateSql = @"
UPDATE download_task
SET status = 'running',
    updated_at = CURRENT_TIMESTAMP
WHERE id = @id;";
        await using var updateCommand = new SqliteCommand(updateSql, connection, transaction);
        updateCommand.Parameters.AddWithValue("id", itemId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdatePayloadAsync(string queueUuid, string payloadJson, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET payload = @payload,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("payload", payloadJson);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateFinalDestinationsAsync(
        string queueUuid,
        string? finalDestinationsJson,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET final_destinations_json = @finalDestinationsJson,
    payload = COALESCE(@payload, payload),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("finalDestinationsJson", (object?)finalDestinationsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", (object?)payloadJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateEngineAsync(string queueUuid, string engine, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET engine = @engine,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", engine);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateQueueMetadataAsync(
        string queueUuid,
        int? qualityRank,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET quality_rank = @qualityRank,
    content_type = COALESCE(@contentType, content_type),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("qualityRank", (object?)qualityRank ?? DBNull.Value);
        command.Parameters.AddWithValue("contentType", NormalizeId(contentType) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM download_task WHERE status = @status;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteByStatusAsync(string engine, string status, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM download_task WHERE status = @status AND engine = @engine;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("engine", engine);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteByUuidAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM download_task WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM download_task;";
        await using var command = new SqliteCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteByEngineAsync(string engine, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM download_task WHERE engine = @engine;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", engine);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"SELECT EXISTS(SELECT 1 FROM download_task WHERE queue_uuid = @queueUuid);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<bool> ExistsByMetadataAsync(string artistName, string trackTitle, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT EXISTS(
    SELECT 1
    FROM download_task
    WHERE lower(artist_name) = lower(@artistName)
      AND lower(track_title) = lower(@trackTitle)
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<bool> ExistsByMetadataAsync(string engine, string artistName, string trackTitle, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT EXISTS(
    SELECT 1
    FROM download_task
    WHERE lower(engine) = lower(@engine)
      AND lower(artist_name) = lower(@artistName)
      AND lower(track_title) = lower(@trackTitle)
);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", engine);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<bool> ExistsDuplicateAsync(
        DuplicateLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT EXISTS(
    SELECT 1
    FROM download_task
    WHERE (
        (
            @isrc IS NOT NULL
            AND @isrc <> ''
            AND upper(isrc) = upper(@isrc)
        )
        OR (
            @deezerTrackId IS NOT NULL
            AND @deezerTrackId <> ''
            AND lower(deezer_track_id) = lower(@deezerTrackId)
        )
        OR (
            @deezerAlbumId IS NOT NULL
            AND @deezerAlbumId <> ''
            AND lower(deezer_album_id) = lower(@deezerAlbumId)
        )
        OR (
            @deezerArtistId IS NOT NULL
            AND @deezerArtistId <> ''
            AND lower(deezer_artist_id) = lower(@deezerArtistId)
        )
        OR (
            @spotifyTrackId IS NOT NULL
            AND @spotifyTrackId <> ''
            AND lower(spotify_track_id) = lower(@spotifyTrackId)
        )
        OR (
            @spotifyAlbumId IS NOT NULL
            AND @spotifyAlbumId <> ''
            AND lower(spotify_album_id) = lower(@spotifyAlbumId)
        )
        OR (
            @spotifyArtistId IS NOT NULL
            AND @spotifyArtistId <> ''
            AND lower(spotify_artist_id) = lower(@spotifyArtistId)
        )
        OR (
            @appleTrackId IS NOT NULL
            AND @appleTrackId <> ''
            AND lower(apple_track_id) = lower(@appleTrackId)
        )
        OR (
            @appleAlbumId IS NOT NULL
            AND @appleAlbumId <> ''
            AND lower(apple_album_id) = lower(@appleAlbumId)
        )
        OR (
            @appleArtistId IS NOT NULL
            AND @appleArtistId <> ''
            AND lower(apple_artist_id) = lower(@appleArtistId)
        )
        OR (
            @durationMs IS NOT NULL
            AND @durationMs > 0
            AND (
                lower(artist_name) = lower(@artistName)
                OR (
                    @artistPrimaryName IS NOT NULL
                    AND @artistPrimaryName <> ''
                    AND lower(artist_name) = lower(@artistPrimaryName)
                )
            )
            AND lower(track_title) = lower(@trackTitle)
            AND duration_ms = @durationMs
        )
    )
    AND (
        (@destinationFolderId IS NULL AND destination_folder_id IS NULL)
        OR destination_folder_id = @destinationFolderId
    )
    AND (
        @contentType IS NULL
        OR lower(content_type) = lower(@contentType)
    )
    AND (
        status NOT IN ('completed', 'complete')
        OR @cooldownMinutes IS NULL
        OR @cooldownMinutes <= 0
        OR updated_at >= datetime('now', '-' || @cooldownMinutes || ' minutes')
    )
        );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("isrc", NormalizeIsrc(request.Isrc) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("deezerTrackId", NormalizeId(request.DeezerTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("deezerAlbumId", NormalizeId(request.DeezerAlbumId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("deezerArtistId", NormalizeId(request.DeezerArtistId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("spotifyTrackId", NormalizeId(request.SpotifyTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("spotifyAlbumId", NormalizeId(request.SpotifyAlbumId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("spotifyArtistId", NormalizeId(request.SpotifyArtistId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("appleTrackId", NormalizeId(request.AppleTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("appleAlbumId", NormalizeId(request.AppleAlbumId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("appleArtistId", NormalizeId(request.AppleArtistId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("artistName", request.ArtistName);
        command.Parameters.AddWithValue("artistPrimaryName", NormalizeId(request.ArtistPrimaryName) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("trackTitle", request.TrackTitle);
        command.Parameters.AddWithValue("durationMs", (object?)request.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("destinationFolderId", (object?)request.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("contentType", NormalizeId(request.ContentType) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("cooldownMinutes", (object?)request.RedownloadCooldownMinutes ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task<DownloadQueueItem?> GetByMetadataAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type,
       status, payload, progress, downloaded, failed, error, created_at, updated_at
FROM download_task
WHERE (
        lower(artist_name) = lower(@artistName)
        OR (
            @artistPrimaryName IS NOT NULL
            AND @artistPrimaryName <> ''
            AND lower(artist_name) = lower(@artistPrimaryName)
        )
      )
  AND lower(track_title) = lower(@trackTitle)
  AND (@contentType IS NULL OR lower(content_type) = lower(@contentType))
ORDER BY updated_at DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistName", request.ArtistName);
        command.Parameters.AddWithValue("artistPrimaryName", NormalizeId(request.ArtistPrimaryName) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("trackTitle", request.TrackTitle);
        command.Parameters.AddWithValue("contentType", NormalizeId(request.ContentType) ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadItem(reader);
    }

    public async Task<DownloadQueueItem?> GetByMetadataAsync(
        string engine,
        string artistName,
        string trackTitle,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type,
       status, payload, progress, downloaded, failed, error, created_at, updated_at
FROM download_task
WHERE lower(engine) = lower(@engine)
  AND lower(artist_name) = lower(@artistName)
  AND lower(track_title) = lower(@trackTitle)
  AND (@contentType IS NULL OR lower(content_type) = lower(@contentType))
ORDER BY updated_at DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", engine);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        command.Parameters.AddWithValue("contentType", NormalizeId(contentType) ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadItem(reader);
    }

    public async Task<DownloadQueueItem?> GetByDeezerTrackIdAsync(
        string engine,
        string deezerTrackId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            return null;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type,
       status, payload, progress, downloaded, failed, error, created_at, updated_at
FROM download_task
WHERE lower(engine) = lower(@engine)
  AND lower(deezer_track_id) = lower(@deezerTrackId)
ORDER BY updated_at DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", engine);
        command.Parameters.AddWithValue("deezerTrackId", deezerTrackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadItem(reader);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        lock (_schemaLock)
        {
            if (_schemaEnsured)
            {
                return;
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
CREATE TABLE IF NOT EXISTS " + DownloadTaskTable + @" (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    queue_uuid TEXT UNIQUE,
    engine TEXT NOT NULL DEFAULT 'deezer',
    artist_name TEXT NOT NULL,
    track_title TEXT NOT NULL,
    isrc TEXT,
    deezer_track_id TEXT,
    deezer_album_id TEXT,
    deezer_artist_id TEXT,
    spotify_track_id TEXT,
    spotify_album_id TEXT,
    spotify_artist_id TEXT,
    apple_track_id TEXT,
    apple_album_id TEXT,
    apple_artist_id TEXT,
    duration_ms INTEGER,
    destination_folder_id INTEGER,
    quality_rank INTEGER,
    queue_order INTEGER,
    content_type TEXT,
    lyrics_status TEXT,
    file_extension TEXT,
    bitrate_kbps INTEGER,
    status TEXT NOT NULL DEFAULT 'queued',
    payload TEXT,
    final_destinations_json TEXT,
    progress REAL,
    downloaded INTEGER,
    failed INTEGER,
    error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "isrc", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "deezer_track_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "deezer_album_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "deezer_artist_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "spotify_track_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "spotify_album_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "spotify_artist_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "apple_track_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "apple_album_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "apple_artist_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "lyrics_status", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "file_extension", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "bitrate_kbps", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "destination_folder_id", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "queue_order", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "content_type", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "final_destinations_json", "TEXT", cancellationToken);
        await EnsureIndexesAsync(connection, cancellationToken);
        await NormalizeLegacyPlaceholderIdsAsync(connection, cancellationToken);
        await NormalizeLegacyAtmosContentTypesAsync(connection, cancellationToken);

        lock (_schemaLock)
        {
            _schemaEnsured = true;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource))
        {
            var directory = Path.GetDirectoryName(builder.DataSource);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string pragmas = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;";
        await using var command = new SqliteCommand(pragmas, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureIndexesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
CREATE INDEX IF NOT EXISTS idx_download_task_status ON " + DownloadTaskTable + @" (status);
CREATE INDEX IF NOT EXISTS idx_download_task_created_at ON " + DownloadTaskTable + @" (created_at);
CREATE INDEX IF NOT EXISTS idx_download_task_isrc ON " + DownloadTaskTable + @" (isrc);
CREATE INDEX IF NOT EXISTS idx_download_task_deezer_track ON " + DownloadTaskTable + @" (deezer_track_id);
CREATE INDEX IF NOT EXISTS idx_download_task_deezer_album ON " + DownloadTaskTable + @" (deezer_album_id);
CREATE INDEX IF NOT EXISTS idx_download_task_deezer_artist ON " + DownloadTaskTable + @" (deezer_artist_id);
CREATE INDEX IF NOT EXISTS idx_download_task_spotify_track ON " + DownloadTaskTable + @" (spotify_track_id);
CREATE INDEX IF NOT EXISTS idx_download_task_spotify_album ON " + DownloadTaskTable + @" (spotify_album_id);
CREATE INDEX IF NOT EXISTS idx_download_task_spotify_artist ON " + DownloadTaskTable + @" (spotify_artist_id);
CREATE INDEX IF NOT EXISTS idx_download_task_apple_track ON " + DownloadTaskTable + @" (apple_track_id);
CREATE INDEX IF NOT EXISTS idx_download_task_apple_album ON " + DownloadTaskTable + @" (apple_album_id);
CREATE INDEX IF NOT EXISTS idx_download_task_apple_artist ON " + DownloadTaskTable + @" (apple_artist_id);
CREATE INDEX IF NOT EXISTS idx_download_task_destination_folder ON " + DownloadTaskTable + @" (destination_folder_id);
CREATE INDEX IF NOT EXISTS idx_download_task_artist_title_duration ON " + DownloadTaskTable + @" (artist_name, track_title, duration_ms);
CREATE UNIQUE INDEX IF NOT EXISTS idx_download_task_queue_uuid ON " + DownloadTaskTable + @" (queue_uuid);";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task NormalizeLegacyAtmosContentTypesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE " + DownloadTaskTable + @"
SET content_type = 'atmos',
    updated_at = CURRENT_TIMESTAMP
WHERE lower(COALESCE(engine, '')) = 'apple'
  AND lower(COALESCE(content_type, '')) = 'stereo'
  AND (
        lower(COALESCE(json_extract(payload, '$.QualityBucket'), '')) = 'atmos'
        OR lower(COALESCE(json_extract(payload, '$.qualityBucket'), '')) = 'atmos'
        OR lower(COALESCE(json_extract(payload, '$.Quality'), '')) LIKE '%atmos%'
        OR lower(COALESCE(json_extract(payload, '$.quality'), '')) LIKE '%atmos%'
      );";

        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task NormalizeLegacyPlaceholderIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE " + DownloadTaskTable + @"
SET deezer_track_id = NULL
WHERE lower(trim(COALESCE(deezer_track_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET deezer_album_id = NULL
WHERE lower(trim(COALESCE(deezer_album_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET deezer_artist_id = NULL
WHERE lower(trim(COALESCE(deezer_artist_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET spotify_track_id = NULL
WHERE lower(trim(COALESCE(spotify_track_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET spotify_album_id = NULL
WHERE lower(trim(COALESCE(spotify_album_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET spotify_artist_id = NULL
WHERE lower(trim(COALESCE(spotify_artist_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET apple_track_id = NULL
WHERE lower(trim(COALESCE(apple_track_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET apple_album_id = NULL
WHERE lower(trim(COALESCE(apple_album_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE download_task
SET apple_artist_id = NULL
WHERE lower(trim(COALESCE(apple_artist_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');";

        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string type,
        CancellationToken cancellationToken)
        => await SqliteSchemaUtils.EnsureColumnAsync(connection, table, column, type, cancellationToken);

    private static void BindCommonParameters(SqliteCommand command, DownloadQueueItem item)
    {
        command.Parameters.AddWithValue("queueUuid", item.QueueUuid);
        command.Parameters.AddWithValue("engine", item.Engine);
        command.Parameters.AddWithValue("artistName", item.ArtistName);
        command.Parameters.AddWithValue("trackTitle", item.TrackTitle);
        command.Parameters.AddWithValue("isrc", (object?)NormalizeIsrc(item.Isrc) ?? DBNull.Value);
        command.Parameters.AddWithValue("deezerTrackId", (object?)NormalizeId(item.DeezerTrackId) ?? DBNull.Value);
        command.Parameters.AddWithValue("deezerAlbumId", (object?)NormalizeId(item.DeezerAlbumId) ?? DBNull.Value);
        command.Parameters.AddWithValue("deezerArtistId", (object?)NormalizeId(item.DeezerArtistId) ?? DBNull.Value);
        command.Parameters.AddWithValue("spotifyTrackId", (object?)NormalizeId(item.SpotifyTrackId) ?? DBNull.Value);
        command.Parameters.AddWithValue("spotifyAlbumId", (object?)NormalizeId(item.SpotifyAlbumId) ?? DBNull.Value);
        command.Parameters.AddWithValue("spotifyArtistId", (object?)NormalizeId(item.SpotifyArtistId) ?? DBNull.Value);
        command.Parameters.AddWithValue("appleTrackId", (object?)NormalizeId(item.AppleTrackId) ?? DBNull.Value);
        command.Parameters.AddWithValue("appleAlbumId", (object?)NormalizeId(item.AppleAlbumId) ?? DBNull.Value);
        command.Parameters.AddWithValue("appleArtistId", (object?)NormalizeId(item.AppleArtistId) ?? DBNull.Value);
        command.Parameters.AddWithValue("durationMs", (object?)item.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("destinationFolderId", (object?)item.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("qualityRank", (object?)item.QualityRank ?? DBNull.Value);
        command.Parameters.AddWithValue("queueOrder", (object?)item.QueueOrder ?? DBNull.Value);
        command.Parameters.AddWithValue("contentType", (object?)NormalizeId(item.ContentType) ?? DBNull.Value);
        command.Parameters.AddWithValue("status", item.Status);
        command.Parameters.AddWithValue("payload", (object?)item.PayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("progress", (object?)item.Progress ?? DBNull.Value);
        command.Parameters.AddWithValue("downloaded", (object?)item.Downloaded ?? DBNull.Value);
        command.Parameters.AddWithValue("failed", (object?)item.Failed ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)item.Error ?? DBNull.Value);
    }

    private static async Task<int> GetNextQueueOrderAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT COALESCE(MAX(queue_order), 0) + 1
FROM " + DownloadTaskTable + @";";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 1 : Convert.ToInt32(result);
    }

    private static async Task<int> GetFrontQueueOrderAsync(
        SqliteConnection connection,
        bool newestFirst,
        CancellationToken cancellationToken)
    {
        if (newestFirst)
        {
            return await GetNextQueueOrderAsync(connection, cancellationToken);
        }

        const string sql = @"
SELECT COALESCE(MIN(queue_order), 0) - 1
FROM " + DownloadTaskTable + @"
WHERE queue_order IS NOT NULL;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static DownloadQueueItem ReadItem(SqliteDataReader reader)
    {
        var payloadJson = GetNullableString(reader, 21);
        var createdAt = ParseTimestampOrUtcNow(GetNullableString(reader, 26));
        var updatedAt = ParseTimestampOrUtcNow(GetNullableString(reader, 27));
        return new DownloadQueueItem(
            reader.GetInt64(0),
            GetNullableString(reader, 1) ?? string.Empty,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            GetNullableString(reader, 5),
            GetNullableString(reader, 6),
            GetNullableString(reader, 7),
            GetNullableString(reader, 8),
            GetNullableString(reader, 9),
            GetNullableString(reader, 10),
            GetNullableString(reader, 11),
            GetNullableString(reader, 12),
            GetNullableString(reader, 13),
            GetNullableString(reader, 14),
            GetNullableInt32(reader, 15),
            GetNullableInt64(reader, 16),
            GetNullableInt32(reader, 17),
            GetNullableInt32(reader, 18),
            GetNullableString(reader, 19),
            reader.GetString(20),
            payloadJson,
            GetNullableDouble(reader, 22),
            GetNullableInt32(reader, 23),
            GetNullableInt32(reader, 24),
            GetNullableString(reader, 25),
            createdAt,
            updatedAt
        );
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? GetNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static DateTimeOffset ParseTimestampOrUtcNow(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DateTimeOffset.UtcNow;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private static string? NormalizeIsrc(string? isrc)
    {
        var trimmed = isrc?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    private static string? NormalizeId(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var normalized = trimmed.ToLowerInvariant();
        return normalized is "0" or "-" or "unknown" or "n/a" or "none" or "null" or "nil"
            ? null
            : normalized;
    }
}

public sealed class DuplicateLookupRequest
{
    public string? Isrc { get; init; }
    public string? DeezerTrackId { get; init; }
    public string? DeezerAlbumId { get; init; }
    public string? DeezerArtistId { get; init; }
    public string? SpotifyTrackId { get; init; }
    public string? SpotifyAlbumId { get; init; }
    public string? SpotifyArtistId { get; init; }
    public string? AppleTrackId { get; init; }
    public string? AppleAlbumId { get; init; }
    public string? AppleArtistId { get; init; }
    public string ArtistName { get; init; } = string.Empty;
    public string TrackTitle { get; init; } = string.Empty;
    public int? DurationMs { get; init; }
    public long? DestinationFolderId { get; init; }
    public string? ContentType { get; init; }
    public int? RedownloadCooldownMinutes { get; init; }
    public string? ArtistPrimaryName { get; init; }

    public static DuplicateLookupRequest FromQueueItem(DownloadQueueItem item)
        => new()
        {
            Isrc = item.Isrc,
            DeezerTrackId = item.DeezerTrackId,
            DeezerAlbumId = item.DeezerAlbumId,
            DeezerArtistId = item.DeezerArtistId,
            SpotifyTrackId = item.SpotifyTrackId,
            SpotifyAlbumId = item.SpotifyAlbumId,
            SpotifyArtistId = item.SpotifyArtistId,
            AppleTrackId = item.AppleTrackId,
            AppleAlbumId = item.AppleAlbumId,
            AppleArtistId = item.AppleArtistId,
            ArtistName = item.ArtistName,
            TrackTitle = item.TrackTitle,
            DurationMs = item.DurationMs,
            DestinationFolderId = item.DestinationFolderId,
            ContentType = item.ContentType
        };
}

public sealed class MetadataLookupRequest
{
    public required string ArtistName { get; init; }
    public required string TrackTitle { get; init; }
    public string? ContentType { get; init; }
    public string? ArtistPrimaryName { get; init; }
}

public sealed record DownloadQueueItem(
    long Id,
    string QueueUuid,
    string Engine,
    string ArtistName,
    string TrackTitle,
    string? Isrc,
    string? DeezerTrackId,
    string? DeezerAlbumId,
    string? DeezerArtistId,
    string? SpotifyTrackId,
    string? SpotifyAlbumId,
    string? SpotifyArtistId,
    string? AppleTrackId,
    string? AppleAlbumId,
    string? AppleArtistId,
    int? DurationMs,
    long? DestinationFolderId,
    int? QualityRank,
    int? QueueOrder,
    string? ContentType,
    string Status,
    string? PayloadJson,
    double? Progress,
    int? Downloaded,
    int? Failed,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public DownloadQueueItem(
        long Id,
        string QueueUuid,
        string Engine,
        string ArtistName,
        string TrackTitle,
        string? Isrc,
        string? DeezerTrackId,
        string? DeezerAlbumId,
        string? DeezerArtistId,
        string? SpotifyTrackId,
        string? SpotifyAlbumId,
        string? SpotifyArtistId,
        string? AppleTrackId,
        string? AppleAlbumId,
        string? AppleArtistId,
        int? DurationMs,
        long? DestinationFolderId,
        int? QualityRank,
        int? QueueOrder,
        string Status,
        string? PayloadJson,
        double? Progress,
        int? Downloaded,
        int? Failed,
        string? Error,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
        : this(
            Id,
            QueueUuid,
            Engine,
            ArtistName,
            TrackTitle,
            Isrc,
            DeezerTrackId,
            DeezerAlbumId,
            DeezerArtistId,
            SpotifyTrackId,
            SpotifyAlbumId,
            SpotifyArtistId,
            AppleTrackId,
            AppleAlbumId,
            AppleArtistId,
            DurationMs,
            DestinationFolderId,
            QualityRank,
            QueueOrder,
            ContentType: null,
            Status,
            PayloadJson,
            Progress,
            Downloaded,
            Failed,
            Error,
            CreatedAt,
            UpdatedAt)
    {
    }
}
