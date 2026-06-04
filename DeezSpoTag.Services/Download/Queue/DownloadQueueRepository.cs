using System.Linq;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Download.Queue;

public enum QueueRequeueOrigin
{
    Manual = 0,
    AutoRetry = 1,
    DuplicateRehydrate = 2,
    QueueUpgradeRecovery = 3,
    FallbackAdvance = 4,
    Unknown = 99
}

public sealed class DownloadQueueRepository
{
    public sealed record QueueStateChangedEvent(string QueueUuid, string Status);
    public static event Action<QueueStateChangedEvent>? QueueStateChanged;

    private static readonly SemaphoreSlim DequeueGate = new(1, 1);
    private const string DownloadTaskTable = "download_task";
    private const string FilesPropertyLower = "files";
    private const string PayloadParameterName = "payload";
    private const string LyricsStatusParameterName = "lyricsStatus";
    private const string MoveStatusPending = "pending";
    private const string MoveStatusRunning = "running";
    private const string MoveStatusMoved = "moved";
    private const string MoveStatusBlocked = "blocked";
    private const string MoveStatusFailed = "failed";
    private const string MoveStatusNotRequired = "not_required";
    private const string EnrichmentStatusPending = "pending";
    private const string EnrichmentStatusRunning = "running";
    private const string EnrichmentStatusCompleted = "completed";
    private const string EnrichmentStatusFailed = "failed";
    private const string EnrichmentStatusCanceled = "canceled";
    private const string EnrichmentStatusInterrupted = "interrupted";
    private const string EnrichmentStatusNotRequired = "not_required";
    private readonly string _connectionString;
    private readonly DownloadStagingCleanupService? _stagingCleanupService;
    private readonly ILogger<DownloadQueueRepository> _logger;
    private bool _schemaEnsured;
    private readonly object _schemaLock = new();

    public DownloadQueueRepository(
        IConfiguration configuration,
        ILogger<DownloadQueueRepository> logger,
        DownloadStagingCleanupService? stagingCleanupService = null)
    {
        _logger = logger;
        _stagingCleanupService = stagingCleanupService;
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
    (queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id, spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id, duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status, status, payload, progress, downloaded, failed, error, created_at, updated_at)
VALUES
    (@queueUuid, @engine, @artistName, @trackTitle, @isrc, @deezerTrackId, @deezerAlbumId, @deezerArtistId, @spotifyTrackId, @spotifyAlbumId, @spotifyArtistId, @appleTrackId, @appleAlbumId, @appleArtistId, @durationMs, @destinationFolderId, @qualityRank, @queueOrder, @contentType, @moveStatus, @enrichmentStatus, @status, @payload, @progress, @downloaded, @failed, @error, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
SELECT CASE
    WHEN changes() = 0 THEN NULL
    ELSE last_insert_rowid()
END;";
        await using var command = new SqliteCommand(sql, connection);
        BindCommonParameters(command, item with { QueueOrder = queueOrder });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var id = result is null or DBNull ? (long?)null : Convert.ToInt64(result);
        if (id.HasValue)
        {
            PublishQueueStateChanged(item.QueueUuid, item.Status);
        }

        return id;
    }

    public async Task<bool> RequeueAsync(
        string queueUuid,
        QueueRequeueOrigin origin,
        CancellationToken cancellationToken = default)
        => await RequeueAsync(queueUuid, origin, requeueToFront: false, newestFirst: false, cancellationToken);

    public async Task<bool> RequeueAsync(
        string queueUuid,
        QueueRequeueOrigin origin,
        bool requeueToFront,
        bool newestFirst,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var currentStatus = await GetStatusAsync(connection, queueUuid, cancellationToken);
        if (IsCanceledStatus(currentStatus) && origin != QueueRequeueOrigin.Manual)
        {
            _logger.LogInformation(
                "Blocked non-manual requeue for cancelled item {QueueUuid} (origin={Origin})",
                LogSanitizer.OneLine(queueUuid),
                origin);
            return false;
        }

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
    move_status = CASE
        WHEN destination_folder_id IS NOT NULL THEN '" + MoveStatusPending + @"'
        ELSE NULL
    END,
    enrichment_status = CASE
        WHEN destination_folder_id IS NOT NULL THEN '" + EnrichmentStatusPending + @"'
        ELSE '" + EnrichmentStatusNotRequired + @"'
    END,
    queue_order = @queueOrder,
    activities_cleared_at = NULL,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("queueOrder", queueOrder);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected > 0)
        {
            PublishQueueStateChanged(queueUuid, "queued");
        }

        return affected > 0;
    }

    private static bool IsCanceledStatus(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "canceled" or "cancelled";
    }

    private static async Task<string?> GetStatusAsync(
        SqliteConnection connection,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT status
FROM download_task
WHERE queue_uuid = @queueUuid
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
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
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
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
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
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

    public async Task<IReadOnlyList<DownloadQueueItem>> GetActivitiesTasksAsync(
        int terminalItemLimit,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = BuildActivitiesQueueSql();
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("terminalLimit", Math.Max(0, terminalItemLimit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<DownloadQueueItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<DownloadQueueItem>> GetRunningTasksOlderThanAsync(
        TimeSpan age,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
	SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
	       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
	       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
	       status, payload, progress, downloaded, failed, error, created_at, updated_at
	FROM download_task
	WHERE status = 'running'
  AND updated_at <= datetime('now', '-' || @ageSeconds || ' seconds')
ORDER BY updated_at ASC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("ageSeconds", Math.Max(1, (int)Math.Ceiling(age.TotalSeconds)));
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
WHERE status IN ('queued', 'resolving')
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
WHERE status IN ('queued', 'resolving');";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<int> GetActiveDownloadCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT COUNT(*)
FROM download_task
WHERE lower(status) IN ('queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying');";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<int> GetRunnableDownloadCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT COUNT(*)
FROM download_task
WHERE lower(status) IN ('queued', 'inqueue', 'running', 'downloading', 'retrying');";
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
WHERE lower(status) IN ('queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying')
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task<bool> HasRunnableDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT 1
FROM download_task
WHERE lower(status) IN ('queued', 'inqueue', 'running', 'downloading', 'retrying')
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task<int> GetUnfinishedWatchlistDownloadCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT COUNT(*)
FROM download_task
WHERE json_valid(payload)
AND (
    lower(COALESCE(json_extract(payload, '$.WatchlistOrigin'), json_extract(payload, '$.watchlistOrigin'), '')) <> ''
    OR lower(COALESCE(json_extract(payload, '$.WatchlistSource'), json_extract(payload, '$.watchlistSource'), '')) <> ''
    OR lower(COALESCE(json_extract(payload, '$.WatchlistPlaylistId'), json_extract(payload, '$.watchlistPlaylistId'), '')) <> ''
    OR lower(COALESCE(json_extract(payload, '$.WatchlistTrackId'), json_extract(payload, '$.watchlistTrackId'), '')) <> ''
)
AND (
    lower(status) IN ('queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying')
    OR (
        lower(status) IN ('completed', 'complete')
        AND lower(COALESCE(move_status, '')) NOT IN ('" + MoveStatusMoved + @"', '" + MoveStatusNotRequired + @"')
    )
);";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<int> GetActiveWatchlistDownloadCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT COUNT(*)
FROM download_task
WHERE json_valid(payload)
AND (
    lower(COALESCE(json_extract(payload, '$.WatchlistOrigin'), json_extract(payload, '$.watchlistOrigin'), '')) <> ''
    OR lower(COALESCE(json_extract(payload, '$.WatchlistSource'), json_extract(payload, '$.watchlistSource'), '')) <> ''
    OR lower(COALESCE(json_extract(payload, '$.WatchlistPlaylistId'), json_extract(payload, '$.watchlistPlaylistId'), '')) <> ''
    OR lower(COALESCE(json_extract(payload, '$.WatchlistTrackId'), json_extract(payload, '$.watchlistTrackId'), '')) <> ''
)
AND lower(status) IN ('queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying');";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task UpdateStatusAsync(string queueUuid, string status, string? error = null, int? downloaded = null, int? failed = null, double? progress = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET status = @status,
    error = @error,
    downloaded = COALESCE(@downloaded, downloaded),
    failed = COALESCE(@failed, failed),
    progress = CASE
        WHEN @progress IS NOT NULL
            THEN @progress
        WHEN lower(@status) IN ('queued', 'inqueue', 'retrying')
            THEN 0
        WHEN lower(@status) IN ('completed', 'complete')
            THEN 100
        ELSE progress
    END,
    move_status = CASE
        WHEN lower(@status) IN ('completed', 'complete')
             AND destination_folder_id IS NOT NULL
            THEN '" + MoveStatusPending + @"'
        WHEN lower(@status) IN ('completed', 'complete')
             AND destination_folder_id IS NULL
            THEN '" + MoveStatusNotRequired + @"'
        ELSE move_status
    END,
    enrichment_status = CASE
        WHEN lower(@status) IN ('queued', 'inqueue', 'running', 'downloading', 'retrying')
             AND destination_folder_id IS NOT NULL
            THEN '" + EnrichmentStatusPending + @"'
        WHEN lower(@status) IN ('queued', 'inqueue', 'running', 'downloading', 'retrying')
             AND destination_folder_id IS NULL
            THEN '" + EnrichmentStatusNotRequired + @"'
        WHEN lower(@status) IN ('completed', 'complete')
             AND destination_folder_id IS NOT NULL
            THEN '" + EnrichmentStatusPending + @"'
        WHEN lower(@status) IN ('completed', 'complete')
             AND destination_folder_id IS NULL
            THEN '" + EnrichmentStatusNotRequired + @"'
        ELSE enrichment_status
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("downloaded", (object?)downloaded ?? DBNull.Value);
        command.Parameters.AddWithValue("failed", (object?)failed ?? DBNull.Value);
        command.Parameters.AddWithValue("progress", (object?)progress ?? DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated > 0)
        {
            await TryCleanupStagingForTerminalStatusAsync(connection, queueUuid, status, cancellationToken);
            PublishQueueStateChanged(queueUuid, status);
        }
    }

    private async Task TryCleanupStagingForTerminalStatusAsync(
        SqliteConnection connection,
        string queueUuid,
        string status,
        CancellationToken cancellationToken)
    {
        if (_stagingCleanupService == null || !IsFailedOrCanceledStatus(status))
        {
            return;
        }

        var payloadJson = await GetPayloadJsonAsync(connection, queueUuid, cancellationToken);
        var protectedPaths = await GetActivePayloadPathsExceptAsync(connection, queueUuid, cancellationToken);
        DownloadStagingCleanupResult result;
        try
        {
            result = await _stagingCleanupService.CleanupAsync(queueUuid, payloadJson, protectedPaths, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result = DownloadStagingCleanupResult.Failed(ex.Message, 0, 0, 0);
            _logger.LogWarning(
                ex,
                "Staging cleanup failed for queue item {QueueUuid}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(queueUuid));
        }

        await UpdateStagingCleanupStatusAsync(connection, queueUuid, result, cancellationToken);
    }

    private static async Task<List<string>> GetActivePayloadPathsExceptAsync(
        SqliteConnection connection,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT payload
FROM download_task
WHERE queue_uuid <> @queueUuid
  AND lower(status) IN ('resolving', 'queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying');";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            AddPayloadPaths(GetNullableString(reader, 0), paths);
        }

        return paths.ToList();
    }

    private static bool IsFailedOrCanceledStatus(string status)
    {
        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "failed" or "canceled" or "cancelled";
    }

    private static async Task UpdateStagingCleanupStatusAsync(
        SqliteConnection connection,
        string queueUuid,
        DownloadStagingCleanupResult result,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE download_task
SET staging_cleanup_status = @status,
    staging_cleanup_error = @error,
    staging_cleanup_at = CURRENT_TIMESTAMP,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", result.Status);
        command.Parameters.AddWithValue("error", (object?)result.Error ?? DBNull.Value);
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
WHERE lower(status) IN ('queued', 'inqueue', 'resolving', 'retrying');";
        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
        PublishQueueStateChanged(string.Empty, "paused");
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
        PublishQueueStateChanged(string.Empty, "queued");
    }

    private static void PublishQueueStateChanged(string queueUuid, string status)
    {
        var handler = QueueStateChanged;
        if (handler == null || string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        try
        {
            handler(new QueueStateChangedEvent(queueUuid ?? string.Empty, status));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Queue state notifications are best-effort and must never impact queue persistence.
        }
    }

    public async Task<bool> TryClaimStaleRunningAsync(
        string queueUuid,
        TimeSpan age,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return false;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND status = 'running'
  AND updated_at <= datetime('now', '-' || @ageSeconds || ' seconds');";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("ageSeconds", Math.Max(1, (int)Math.Ceiling(age.TotalSeconds)));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
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
        var queueOrderBy = newestFirst ? "DESC" : "ASC";
        return $@"
	SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
	       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
	       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
	       status, payload, progress, downloaded, failed, error, created_at, updated_at
	FROM download_task
	WHERE status IN ('queued', 'resolving')
  {extraWhereClause}
ORDER BY (queue_order IS NULL), queue_order {queueOrderBy}, created_at {orderBy}, id {orderBy}
LIMIT 1;";
    }

    private static string BuildActivitiesQueueSql()
    {
        const string selectedColumns = @"
	id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
	spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
	duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
	status, payload, progress, downloaded, failed, error, created_at, updated_at";

        const string activeStatuses = "'resolving', 'queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying'";
        return @"
WITH active_items AS (
	SELECT " + selectedColumns + @"
	FROM download_task
	WHERE lower(status) IN (" + activeStatuses + @")
	  AND activities_cleared_at IS NULL
),
terminal_items AS (
	SELECT " + selectedColumns + @"
	FROM download_task
	WHERE lower(status) NOT IN (" + activeStatuses + @")
	  AND activities_cleared_at IS NULL
	ORDER BY updated_at DESC, id DESC
	LIMIT @terminalLimit
)
SELECT " + selectedColumns + @"
FROM (
	SELECT " + selectedColumns + @" FROM active_items
	UNION ALL
	SELECT " + selectedColumns + @" FROM terminal_items
)
ORDER BY (queue_order IS NULL), queue_order ASC, created_at ASC, id ASC;";
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
        var lyricsStatus = ResolveLyricsStatusFromOutputs(finalDestinationsJson: null, payloadJson);
        const string sql = @"
UPDATE download_task
SET payload = @payload,
    lyrics_status = COALESCE(@lyricsStatus, lyrics_status),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(PayloadParameterName, payloadJson);
        command.Parameters.AddWithValue(LyricsStatusParameterName, (object?)lyricsStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryUpdateQueuedPayloadIfCurrentAsync(
        string queueUuid,
        string? expectedPayloadJson,
        string payloadJson,
        string? engine = null,
        string? status = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var lyricsStatus = ResolveLyricsStatusFromOutputs(finalDestinationsJson: null, payloadJson);
        const string sql = @"
UPDATE download_task
SET payload = @payload,
    engine = COALESCE(@engine, engine),
    status = COALESCE(@status, status),
    error = CASE
        WHEN @status = 'failed' THEN COALESCE(@error, error)
        WHEN @status = 'queued' THEN NULL
        ELSE error
    END,
    lyrics_status = COALESCE(@lyricsStatus, lyrics_status),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND status IN ('queued', 'resolving')
  AND ((payload IS NULL AND @expectedPayload IS NULL) OR payload = @expectedPayload);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(PayloadParameterName, payloadJson);
        command.Parameters.AddWithValue("engine", (object?)engine ?? DBNull.Value);
        command.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue(LyricsStatusParameterName, (object?)lyricsStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("expectedPayload", (object?)expectedPayloadJson ?? DBNull.Value);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated && !string.IsNullOrWhiteSpace(status))
        {
            PublishQueueStateChanged(queueUuid, status);
        }

        return updated;
    }

    public async Task<bool> TryUpdateQueuedIdentityIfCurrentAsync(
        DownloadQueueItem item,
        string? expectedPayloadJson,
        string? status = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var lyricsStatus = ResolveLyricsStatusFromOutputs(finalDestinationsJson: null, item.PayloadJson);
        const string sql = @"
UPDATE download_task
SET payload = @payload,
    engine = @engine,
    artist_name = @artistName,
    track_title = @trackTitle,
    isrc = COALESCE(NULLIF(@isrc, ''), isrc),
    deezer_track_id = COALESCE(NULLIF(@deezerTrackId, ''), deezer_track_id),
    deezer_album_id = COALESCE(NULLIF(@deezerAlbumId, ''), deezer_album_id),
    deezer_artist_id = COALESCE(NULLIF(@deezerArtistId, ''), deezer_artist_id),
    spotify_track_id = COALESCE(NULLIF(@spotifyTrackId, ''), spotify_track_id),
    spotify_album_id = COALESCE(NULLIF(@spotifyAlbumId, ''), spotify_album_id),
    spotify_artist_id = COALESCE(NULLIF(@spotifyArtistId, ''), spotify_artist_id),
    apple_track_id = COALESCE(NULLIF(@appleTrackId, ''), apple_track_id),
    apple_album_id = COALESCE(NULLIF(@appleAlbumId, ''), apple_album_id),
    apple_artist_id = COALESCE(NULLIF(@appleArtistId, ''), apple_artist_id),
    duration_ms = COALESCE(@durationMs, duration_ms),
    destination_folder_id = @destinationFolderId,
    quality_rank = COALESCE(@qualityRank, quality_rank),
    content_type = COALESCE(NULLIF(@contentType, ''), content_type),
    status = COALESCE(@status, status),
    error = CASE
        WHEN @status = 'failed' THEN COALESCE(@error, error)
        WHEN @status = 'queued' THEN NULL
        ELSE error
    END,
    lyrics_status = COALESCE(@lyricsStatus, lyrics_status),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND status IN ('queued', 'resolving')
  AND ((payload IS NULL AND @expectedPayload IS NULL) OR payload = @expectedPayload);";
        await using var command = new SqliteCommand(sql, connection);
        BindCommonParameters(command, item);
        command.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue(LyricsStatusParameterName, (object?)lyricsStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("expectedPayload", (object?)expectedPayloadJson ?? DBNull.Value);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated && !string.IsNullOrWhiteSpace(status))
        {
            PublishQueueStateChanged(item.QueueUuid, status);
        }

        return updated;
    }

    public async Task UpdateFinalDestinationsAsync(
        string queueUuid,
        string? finalDestinationsJson,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var effectivePayloadJson = string.IsNullOrWhiteSpace(payloadJson)
            ? await GetPayloadJsonAsync(connection, queueUuid, cancellationToken)
            : payloadJson;
        var lyricsStatus = ResolveLyricsStatusFromOutputs(finalDestinationsJson, effectivePayloadJson);
        const string sql = @"
UPDATE download_task
SET final_destinations_json = @finalDestinationsJson,
    payload = COALESCE(@payload, payload),
    lyrics_status = COALESCE(@lyricsStatus, lyrics_status),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("finalDestinationsJson", (object?)finalDestinationsJson ?? DBNull.Value);
        command.Parameters.AddWithValue(PayloadParameterName, (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue(LyricsStatusParameterName, (object?)lyricsStatus ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkMovePendingAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await UpdateMoveStatusAsync(queueUuid, MoveStatusPending, cancellationToken);
    }

    public async Task MarkMoveRunningAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await UpdateMoveStatusAsync(queueUuid, MoveStatusRunning, cancellationToken);
    }

    public async Task MarkMoveSucceededAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await UpdateMoveStatusAsync(queueUuid, MoveStatusMoved, cancellationToken);
    }

    public async Task MarkMoveBlockedAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await UpdateMoveStatusAsync(queueUuid, MoveStatusBlocked, cancellationToken);
    }

    public async Task MarkMoveFailedAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await UpdateMoveStatusAsync(queueUuid, MoveStatusFailed, cancellationToken);
    }

    public async Task MarkMoveNotRequiredAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await UpdateMoveStatusAsync(queueUuid, MoveStatusNotRequired, cancellationToken);
    }

    public async Task SetEnrichmentStatusAsync(
        string queueUuid,
        string enrichmentStatus,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpdateEnrichmentStatusAsync(connection, queueUuid, enrichmentStatus, cancellationToken);
    }

    public async Task SetEnrichmentStatusAsync(
        IReadOnlyCollection<string> queueUuids,
        string enrichmentStatus,
        CancellationToken cancellationToken = default)
    {
        if (queueUuids.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var queueUuid in queueUuids)
        {
            if (string.IsNullOrWhiteSpace(queueUuid))
            {
                continue;
            }

            await UpdateEnrichmentStatusAsync(connection, queueUuid, enrichmentStatus, cancellationToken, transaction);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Dictionary<string, string?>> GetEnrichmentStatusesAsync(
        IReadOnlyCollection<string> queueUuids,
        CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (queueUuids.Count == 0)
        {
            return map;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var normalized = queueUuids
            .Where(queueUuid => !string.IsNullOrWhiteSpace(queueUuid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return map;
        }

        var placeholders = normalized.Select((_, index) => $"@queueUuid{index}").ToArray();
        var sql = $@"
SELECT queue_uuid, enrichment_status
FROM download_task
WHERE queue_uuid IN ({string.Join(", ", placeholders)});";
        await using var command = new SqliteCommand(sql, connection);
        for (var index = 0; index < normalized.Count; index++)
        {
            command.Parameters.AddWithValue($"queueUuid{index}", normalized[index]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var queueUuid = GetNullableString(reader, 0);
            if (string.IsNullOrWhiteSpace(queueUuid))
            {
                continue;
            }

            map[queueUuid] = GetNullableString(reader, 1);
        }

        return map;
    }

    private async Task UpdateMoveStatusAsync(string queueUuid, string moveStatus, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET move_status = @moveStatus,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("moveStatus", moveStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateEnrichmentStatusAsync(
        SqliteConnection connection,
        string queueUuid,
        string enrichmentStatus,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        const string sql = @"
UPDATE download_task
SET enrichment_status = @enrichmentStatus,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("enrichmentStatus", NormalizeEnrichmentStatus(enrichmentStatus));
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

    public async Task ClearRetryArtifactsAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET final_destinations_json = NULL,
    lyrics_status = NULL,
    staging_cleanup_status = NULL,
    staging_cleanup_error = NULL,
    staging_cleanup_at = NULL,
    activities_cleared_at = NULL,
    move_status = CASE
        WHEN destination_folder_id IS NOT NULL THEN '" + MoveStatusPending + @"'
        ELSE NULL
    END,
    enrichment_status = CASE
        WHEN destination_folder_id IS NOT NULL THEN '" + EnrichmentStatusPending + @"'
        ELSE '" + EnrichmentStatusNotRequired + @"'
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateQueueMetadataAsync(
        string queueUuid,
        int? qualityRank,
        string? contentType,
        long? destinationFolderId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET quality_rank = @qualityRank,
    content_type = COALESCE(@contentType, content_type),
    destination_folder_id = @destinationFolderId,
    move_status = CASE
        WHEN @destinationFolderId IS NOT NULL AND lower(status) IN ('completed', 'complete') THEN '" + MoveStatusPending + @"'
        WHEN @destinationFolderId IS NULL AND lower(status) IN ('completed', 'complete') THEN '" + MoveStatusNotRequired + @"'
        ELSE move_status
    END,
    enrichment_status = CASE
        WHEN @destinationFolderId IS NOT NULL AND lower(status) IN ('completed', 'complete') THEN '" + EnrichmentStatusPending + @"'
        WHEN @destinationFolderId IS NULL AND lower(status) IN ('completed', 'complete') THEN '" + EnrichmentStatusNotRequired + @"'
        ELSE enrichment_status
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("qualityRank", (object?)qualityRank ?? DBNull.Value);
        command.Parameters.AddWithValue("contentType", NormalizeId(contentType) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("destinationFolderId", (object?)destinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateQueueIdentityAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var lyricsStatus = ResolveLyricsStatusFromOutputs(finalDestinationsJson: null, item.PayloadJson);
        const string sql = @"
UPDATE download_task
SET engine = @engine,
    artist_name = @artistName,
    track_title = @trackTitle,
    isrc = @isrc,
    deezer_track_id = @deezerTrackId,
    deezer_album_id = @deezerAlbumId,
    deezer_artist_id = @deezerArtistId,
    spotify_track_id = @spotifyTrackId,
    spotify_album_id = @spotifyAlbumId,
    spotify_artist_id = @spotifyArtistId,
    apple_track_id = @appleTrackId,
    apple_album_id = @appleAlbumId,
    apple_artist_id = @appleArtistId,
    duration_ms = @durationMs,
    destination_folder_id = @destinationFolderId,
    quality_rank = @qualityRank,
    content_type = @contentType,
    payload = @payload,
    lyrics_status = COALESCE(@lyricsStatus, lyrics_status),
    move_status = CASE
        WHEN @destinationFolderId IS NOT NULL AND lower(status) IN ('completed', 'complete') THEN '" + MoveStatusPending + @"'
        WHEN @destinationFolderId IS NULL AND lower(status) IN ('completed', 'complete') THEN '" + MoveStatusNotRequired + @"'
        ELSE move_status
    END,
    enrichment_status = CASE
        WHEN @destinationFolderId IS NOT NULL AND lower(status) IN ('completed', 'complete') THEN '" + EnrichmentStatusPending + @"'
        WHEN @destinationFolderId IS NULL AND lower(status) IN ('completed', 'complete') THEN '" + EnrichmentStatusNotRequired + @"'
        ELSE enrichment_status
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        BindCommonParameters(command, item);
        command.Parameters.AddWithValue(LyricsStatusParameterName, (object?)lyricsStatus ?? DBNull.Value);
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

    public async Task<int> MarkActivitiesClearedByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CleanupTerminalRowsByStatusAsync(connection, status, cancellationToken);
        const string sql = @"
UPDATE download_task
SET activities_cleared_at = CURRENT_TIMESTAMP,
    updated_at = CURRENT_TIMESTAMP
WHERE lower(status) = lower(@status)
  AND activities_cleared_at IS NULL;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> MarkActivitiesClearedByUuidAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CleanupTerminalRowByUuidAsync(connection, queueUuid, cancellationToken);
        const string sql = @"
UPDATE download_task
SET activities_cleared_at = CURRENT_TIMESTAMP,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND activities_cleared_at IS NULL;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> MarkTerminalActivitiesClearedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CleanupAllTerminalFailureRowsAsync(connection, cancellationToken);
        const string activeStatuses = "'queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying'";
        const string sql = @"
UPDATE download_task
SET activities_cleared_at = CURRENT_TIMESTAMP,
    updated_at = CURRENT_TIMESTAMP
WHERE lower(status) NOT IN (" + activeStatuses + @")
  AND activities_cleared_at IS NULL;";
        await using var command = new SqliteCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteClearableByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CleanupTerminalRowsByStatusAsync(connection, status, cancellationToken);
        const string sql = @"
DELETE FROM download_task
WHERE status = @status
  AND (
    destination_folder_id IS NULL
    OR move_status = '" + MoveStatusMoved + @"'
    OR move_status = '" + MoveStatusNotRequired + @"'
    OR (lower(status) IN ('failed', 'canceled', 'cancelled') AND staging_cleanup_status IN ('completed', 'skipped'))
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await CleanupOrphanSidecarDirectoriesAsync(connection, cancellationToken);
        return deleted;
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

    public async Task<int> DeleteClearableByUuidAsync(string queueUuid, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CleanupTerminalRowByUuidAsync(connection, queueUuid, cancellationToken);
        const string sql = @"
DELETE FROM download_task
WHERE queue_uuid = @queueUuid
  AND (
    destination_folder_id IS NULL
    OR move_status = '" + MoveStatusMoved + @"'
    OR move_status = '" + MoveStatusNotRequired + @"'
    OR (lower(status) IN ('failed', 'canceled', 'cancelled') AND staging_cleanup_status IN ('completed', 'skipped'))
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await CleanupOrphanSidecarDirectoriesAsync(connection, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"DELETE FROM download_task;";
        await using var command = new SqliteCommand(sql, connection);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await CleanupOrphanSidecarDirectoriesAsync(connection, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteClearableAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await CleanupAllTerminalFailureRowsAsync(connection, cancellationToken);
        const string sql = @"
DELETE FROM download_task
WHERE destination_folder_id IS NULL
   OR move_status = '" + MoveStatusMoved + @"'
   OR move_status = '" + MoveStatusNotRequired + @"'
   OR (lower(status) IN ('failed', 'canceled', 'cancelled') AND staging_cleanup_status IN ('completed', 'skipped'));";
        await using var command = new SqliteCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CleanupTerminalRowsByStatusAsync(
        SqliteConnection connection,
        string status,
        CancellationToken cancellationToken)
    {
        if (_stagingCleanupService == null || !IsFailedOrCanceledStatus(status))
        {
            return;
        }

        const string sql = @"
SELECT queue_uuid
FROM download_task
WHERE lower(status) = lower(@status);";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        await CleanupSelectedTerminalRowsAsync(connection, command, cancellationToken);
    }

    private async Task CleanupTerminalRowByUuidAsync(
        SqliteConnection connection,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        if (_stagingCleanupService == null || string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        const string sql = @"
SELECT queue_uuid
FROM download_task
WHERE queue_uuid = @queueUuid
  AND lower(status) IN ('failed', 'canceled', 'cancelled');";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await CleanupSelectedTerminalRowsAsync(connection, command, cancellationToken);
    }

    private async Task CleanupAllTerminalFailureRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (_stagingCleanupService == null)
        {
            return;
        }

        const string sql = @"
SELECT queue_uuid
FROM download_task
WHERE lower(status) IN ('failed', 'canceled', 'cancelled');";
        await using var command = new SqliteCommand(sql, connection);
        await CleanupSelectedTerminalRowsAsync(connection, command, cancellationToken);
    }

    private async Task CleanupSelectedTerminalRowsAsync(
        SqliteConnection connection,
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var queueUuids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var queueUuid = GetNullableString(reader, 0);
                if (!string.IsNullOrWhiteSpace(queueUuid))
                {
                    queueUuids.Add(queueUuid);
                }
            }
        }

        foreach (var queueUuid in queueUuids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await TryCleanupStagingForTerminalStatusAsync(connection, queueUuid, "failed", cancellationToken);
        }
    }

    private async Task CleanupOrphanSidecarDirectoriesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (_stagingCleanupService == null)
        {
            return;
        }

        var protectedPaths = await GetActivePayloadPathsExceptAsync(connection, string.Empty, cancellationToken);
        try
        {
            await _stagingCleanupService.CleanupOrphanSidecarDirectoriesAsync(protectedPaths, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Orphan staging sidecar cleanup failed.");
        }
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
        => await GetDuplicateAsync(request, cancellationToken) != null;

    public async Task<DownloadQueueItem?> GetDuplicateAsync(
        DuplicateLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // Queue dedupe must remain track-granular; album/artist IDs are intentionally excluded
        // so different tracks from the same release can be queued independently.
        const string sql = @"
	SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
	       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
	       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
	       status, payload, progress, downloaded, failed, error, created_at, updated_at
	FROM download_task
WHERE (
        (
            @isrc IS NOT NULL
            AND @isrc <> ''
            AND (
                upper(isrc) = upper(@isrc)
                OR (
                    json_valid(payload)
                    AND (
                        upper(json_extract(payload, '$.Isrc')) = upper(@isrc)
                        OR upper(json_extract(payload, '$.isrc')) = upper(@isrc)
                    )
                )
            )
        )
        OR (
            @deezerTrackId IS NOT NULL
            AND @deezerTrackId <> ''
            AND (
                lower(deezer_track_id) = lower(@deezerTrackId)
                OR (
                    json_valid(payload)
                    AND (
                        lower(json_extract(payload, '$.DeezerId')) = lower(@deezerTrackId)
                        OR lower(json_extract(payload, '$.deezerId')) = lower(@deezerTrackId)
                    )
                )
            )
        )
        OR (
            @spotifyTrackId IS NOT NULL
            AND @spotifyTrackId <> ''
            AND (
                lower(spotify_track_id) = lower(@spotifyTrackId)
                OR (
                    json_valid(payload)
                    AND (
                        lower(json_extract(payload, '$.SpotifyId')) = lower(@spotifyTrackId)
                        OR lower(json_extract(payload, '$.spotifyId')) = lower(@spotifyTrackId)
                    )
                )
            )
        )
        OR (
            @appleTrackId IS NOT NULL
            AND @appleTrackId <> ''
            AND (
                lower(apple_track_id) = lower(@appleTrackId)
                OR (
                    json_valid(payload)
                    AND (
                        lower(json_extract(payload, '$.AppleId')) = lower(@appleTrackId)
                        OR lower(json_extract(payload, '$.appleId')) = lower(@appleTrackId)
                    )
                )
            )
        )
        OR (
            @durationMs IS NOT NULL
            AND @durationMs > 0
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
ORDER BY
    CASE
        WHEN lower(status) IN ('queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying') THEN 0
        WHEN lower(status) IN ('failed', 'canceled', 'cancelled') THEN 1
        WHEN lower(status) IN ('completed', 'complete') THEN 2
        ELSE 3
    END,
    updated_at DESC;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("isrc", NormalizeIsrc(request.Isrc) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("deezerTrackId", NormalizeId(request.DeezerTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("spotifyTrackId", NormalizeId(request.SpotifyTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("appleTrackId", NormalizeId(request.AppleTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("artistName", request.ArtistName);
        command.Parameters.AddWithValue("artistPrimaryName", NormalizeId(request.ArtistPrimaryName) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("trackTitle", request.TrackTitle);
        command.Parameters.AddWithValue("durationMs", (object?)request.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("destinationFolderId", (object?)request.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("contentType", NormalizeId(request.ContentType) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("cooldownMinutes", (object?)request.RedownloadCooldownMinutes ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadItem(reader);
            if (!MatchesDuplicateRequest(request, item))
            {
                continue;
            }

            return item;
        }

        return null;
    }

    private static bool MatchesDuplicateRequest(DuplicateLookupRequest request, DownloadQueueItem item)
    {
        if (IsCompletedStatus(item.Status) && !HasExistingMaterializedFile(item))
        {
            return false;
        }

        return HasStrongIdentityMatch(request, item)
            || HasPayloadStrongIdentityMatch(request, item)
            || HasMetadataMatch(request, item);
    }

    private static bool HasStrongIdentityMatch(DuplicateLookupRequest request, DownloadQueueItem item)
    {
        return EqualsNormalizedIsrc(request.Isrc, item.Isrc)
            || EqualsNormalizedId(request.DeezerTrackId, item.DeezerTrackId)
            || EqualsNormalizedId(request.SpotifyTrackId, item.SpotifyTrackId)
            || EqualsNormalizedId(request.AppleTrackId, item.AppleTrackId);
    }

    private static bool HasPayloadStrongIdentityMatch(DuplicateLookupRequest request, DownloadQueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var root = document.RootElement;
            return EqualsNormalizedIsrc(request.Isrc, ReadPayloadString(root, "Isrc", "isrc"))
                || EqualsNormalizedId(request.DeezerTrackId, ReadPayloadString(root, "DeezerId", "deezerId"))
                || EqualsNormalizedId(request.SpotifyTrackId, ReadPayloadString(root, "SpotifyId", "spotifyId"))
                || EqualsNormalizedId(request.AppleTrackId, ReadPayloadString(root, "AppleId", "appleId"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadPayloadString(JsonElement root, string pascalName, string camelName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty(pascalName, out var pascalValue) && pascalValue.ValueKind == JsonValueKind.String)
        {
            return pascalValue.GetString();
        }

        return root.TryGetProperty(camelName, out var camelValue) && camelValue.ValueKind == JsonValueKind.String
            ? camelValue.GetString()
            : null;
    }

    private static bool HasMetadataMatch(DuplicateLookupRequest request, DownloadQueueItem item)
    {
        if (!request.DurationMs.HasValue || request.DurationMs.Value <= 0)
        {
            return false;
        }

        if (!item.DurationMs.HasValue || item.DurationMs.Value != request.DurationMs.Value)
        {
            return false;
        }

        if (!TrackTitleMatcher.TitlesMatch(request.TrackTitle, item.TrackTitle))
        {
            return false;
        }

        if (TrackTitleMatcher.ArtistsMatch(request.ArtistName, item.ArtistName))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(request.ArtistPrimaryName)
            && TrackTitleMatcher.ArtistsMatch(request.ArtistPrimaryName, item.ArtistName);
    }

    private static bool EqualsNormalizedIsrc(string? left, string? right)
    {
        var normalizedLeft = NormalizeIsrc(left);
        var normalizedRight = NormalizeIsrc(right);
        return normalizedLeft is not null
            && normalizedRight is not null
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static bool EqualsNormalizedId(string? left, string? right)
    {
        var normalizedLeft = NormalizeId(left);
        var normalizedRight = NormalizeId(right);
        return normalizedLeft is not null
            && normalizedRight is not null
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
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
	       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
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
  AND (
        (@destinationFolderId IS NULL AND destination_folder_id IS NULL)
        OR destination_folder_id = @destinationFolderId
      )
  AND (@contentType IS NULL OR lower(content_type) = lower(@contentType))
ORDER BY updated_at DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("artistName", request.ArtistName);
        command.Parameters.AddWithValue("artistPrimaryName", NormalizeId(request.ArtistPrimaryName) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("trackTitle", request.TrackTitle);
        command.Parameters.AddWithValue("destinationFolderId", (object?)request.DestinationFolderId ?? DBNull.Value);
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
        long? destinationFolderId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
	SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
	       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
	       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
	       status, payload, progress, downloaded, failed, error, created_at, updated_at
	FROM download_task
WHERE lower(engine) = lower(@engine)
  AND lower(artist_name) = lower(@artistName)
  AND lower(track_title) = lower(@trackTitle)
  AND (
        (@destinationFolderId IS NULL AND destination_folder_id IS NULL)
        OR destination_folder_id = @destinationFolderId
      )
  AND (@contentType IS NULL OR lower(content_type) = lower(@contentType))
ORDER BY updated_at DESC
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("engine", engine);
        command.Parameters.AddWithValue("artistName", artistName);
        command.Parameters.AddWithValue("trackTitle", trackTitle);
        command.Parameters.AddWithValue("destinationFolderId", (object?)destinationFolderId ?? DBNull.Value);
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
	       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
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
    move_status TEXT,
    enrichment_status TEXT,
    quality_rank INTEGER,
    queue_order INTEGER,
    content_type TEXT,
    lyrics_status TEXT,
    file_extension TEXT,
    bitrate_kbps INTEGER,
    status TEXT NOT NULL DEFAULT 'queued',
    payload TEXT,
    final_destinations_json TEXT,
    staging_cleanup_status TEXT,
    staging_cleanup_error TEXT,
    staging_cleanup_at TEXT,
    activities_cleared_at TEXT,
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
        await EnsureColumnAsync(connection, DownloadTaskTable, "move_status", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "enrichment_status", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "queue_order", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "content_type", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "final_destinations_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "staging_cleanup_status", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "staging_cleanup_error", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "staging_cleanup_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "activities_cleared_at", "TEXT", cancellationToken);
        await EnsureIndexesAsync(connection, cancellationToken);
        await NormalizeLegacyPlaceholderIdsAsync(connection, cancellationToken);
        await NormalizeLegacyAtmosContentTypesAsync(connection, cancellationToken);
        await NormalizeLegacyEnrichmentStatusesAsync(connection, cancellationToken);
        await NormalizeCompletedFinalizationStatusesAsync(connection, cancellationToken);
        await BackfillMissingIdentityFromPayloadAsync(connection, cancellationToken);

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

    private static async Task BackfillMissingIdentityFromPayloadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE " + DownloadTaskTable + @"
SET isrc = CASE
        WHEN NULLIF(trim(COALESCE(isrc, '')), '') IS NULL THEN upper(NULLIF(trim(COALESCE(json_extract(payload, '$.Isrc'), json_extract(payload, '$.isrc'), '')), ''))
        ELSE isrc
    END,
    deezer_track_id = CASE
        WHEN NULLIF(trim(COALESCE(deezer_track_id, '')), '') IS NULL THEN lower(NULLIF(trim(COALESCE(json_extract(payload, '$.DeezerId'), json_extract(payload, '$.deezerId'), '')), ''))
        ELSE deezer_track_id
    END,
    spotify_track_id = CASE
        WHEN NULLIF(trim(COALESCE(spotify_track_id, '')), '') IS NULL THEN lower(NULLIF(trim(COALESCE(json_extract(payload, '$.SpotifyId'), json_extract(payload, '$.spotifyId'), '')), ''))
        ELSE spotify_track_id
    END,
    apple_track_id = CASE
        WHEN NULLIF(trim(COALESCE(apple_track_id, '')), '') IS NULL THEN lower(NULLIF(trim(COALESCE(json_extract(payload, '$.AppleId'), json_extract(payload, '$.appleId'), '')), ''))
        ELSE apple_track_id
    END,
    duration_ms = CASE
        WHEN duration_ms IS NULL AND COALESCE(json_extract(payload, '$.DurationMs'), json_extract(payload, '$.durationMs'), 0) > 0
            THEN CAST(COALESCE(json_extract(payload, '$.DurationMs'), json_extract(payload, '$.durationMs')) AS INTEGER)
        WHEN duration_ms IS NULL AND COALESCE(json_extract(payload, '$.DurationSeconds'), json_extract(payload, '$.durationSeconds'), 0) > 0
            THEN CAST(COALESCE(json_extract(payload, '$.DurationSeconds'), json_extract(payload, '$.durationSeconds')) AS INTEGER) * 1000
        ELSE duration_ms
    END,
    destination_folder_id = CASE
        WHEN destination_folder_id IS NULL AND COALESCE(json_extract(payload, '$.DestinationFolderId'), json_extract(payload, '$.destinationFolderId'), 0) > 0
            THEN CAST(COALESCE(json_extract(payload, '$.DestinationFolderId'), json_extract(payload, '$.destinationFolderId')) AS INTEGER)
        ELSE destination_folder_id
    END,
    content_type = CASE
        WHEN NULLIF(trim(COALESCE(content_type, '')), '') IS NULL THEN lower(NULLIF(trim(COALESCE(json_extract(payload, '$.ContentType'), json_extract(payload, '$.contentType'), '')), ''))
        ELSE content_type
    END
WHERE json_valid(payload)
  AND (
        NULLIF(trim(COALESCE(isrc, '')), '') IS NULL
        OR NULLIF(trim(COALESCE(deezer_track_id, '')), '') IS NULL
        OR NULLIF(trim(COALESCE(spotify_track_id, '')), '') IS NULL
        OR NULLIF(trim(COALESCE(apple_track_id, '')), '') IS NULL
        OR duration_ms IS NULL
        OR destination_folder_id IS NULL
        OR NULLIF(trim(COALESCE(content_type, '')), '') IS NULL
      );";
        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
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

    private static async Task NormalizeLegacyEnrichmentStatusesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE download_task
SET enrichment_status = CASE
        WHEN lower(status) IN ('completed', 'complete') AND destination_folder_id IS NOT NULL THEN '" + EnrichmentStatusPending + @"'
        WHEN lower(status) IN ('completed', 'complete') AND destination_folder_id IS NULL THEN '" + EnrichmentStatusNotRequired + @"'
        WHEN destination_folder_id IS NULL THEN '" + EnrichmentStatusNotRequired + @"'
        ELSE '" + EnrichmentStatusPending + @"'
    END
WHERE enrichment_status IS NULL OR trim(enrichment_status) = '';

UPDATE download_task
SET enrichment_status = '" + EnrichmentStatusCanceled + @"'
WHERE lower(trim(COALESCE(enrichment_status, ''))) = 'cancelled';";
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task NormalizeCompletedFinalizationStatusesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE download_task
SET move_status = '" + MoveStatusMoved + @"'
WHERE lower(status) IN ('completed', 'complete')
  AND final_destinations_json IS NOT NULL
  AND trim(final_destinations_json) <> ''
  AND lower(COALESCE(move_status, '')) NOT IN ('" + MoveStatusMoved + @"', '" + MoveStatusNotRequired + @"');

UPDATE download_task
SET move_status = '" + MoveStatusNotRequired + @"',
    enrichment_status = CASE
        WHEN lower(COALESCE(enrichment_status, '')) IN ('" + EnrichmentStatusCompleted + @"', '" + EnrichmentStatusNotRequired + @"') THEN enrichment_status
        ELSE '" + EnrichmentStatusNotRequired + @"'
    END
WHERE lower(status) NOT IN ('completed', 'complete')
  AND lower(COALESCE(move_status, '')) IN ('" + MoveStatusPending + @"', '" + MoveStatusRunning + @"');";
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

    private static async Task<string?> GetPayloadJsonAsync(
        SqliteConnection connection,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT payload
FROM download_task
WHERE queue_uuid = @queueUuid
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static string? ResolveLyricsStatusFromOutputs(
        string? finalDestinationsJson,
        string? payloadJson)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFinalDestinationPaths(finalDestinationsJson, paths);
        AddPayloadPaths(payloadJson, paths);

        var lyricsStatus = default(LyricsStatusFlags);
        ApplyPayloadLyricsStatus(payloadJson, ref lyricsStatus);

        foreach (var path in paths)
        {
            TryMarkLyricsStatus(path, ref lyricsStatus);
        }

        var statuses = new List<string>(capacity: 3);
        if (lyricsStatus.HasTimeSynced)
        {
            statuses.Add("time-synced");
        }

        if (lyricsStatus.HasSynced)
        {
            statuses.Add("synced");
        }

        if (lyricsStatus.HasUnsynced)
        {
            statuses.Add("unsynced");
        }

        if (statuses.Count > 0)
        {
            return string.Join(",", statuses);
        }

        return null;
    }

    private static void TryMarkLyricsStatus(
        string? path,
        ref LyricsStatusFlags status)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var ioPath = DownloadPathResolver.ResolveIoPath(path);
            switch (Path.GetExtension(ioPath))
            {
                case ".ttml":
                case ".TTML":
                    status.HasTimeSynced = true;
                    break;
                case ".lrc":
                case ".LRC":
                    status.HasSynced = true;
                    break;
                case ".txt":
                case ".TXT":
                    status.HasUnsynced = true;
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort lyrics status persistence; ignore unreadable paths.
        }
    }

    private static void ApplyPayloadLyricsStatus(
        string? payloadJson,
        ref LyricsStatusFlags status)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            ApplyPayloadLyricsStatusProperty(document.RootElement, "LyricsStatus", ref status);
            ApplyPayloadLyricsStatusProperty(document.RootElement, "lyricsStatus", ref status);
            ApplyPayloadLyricsStatusProperty(document.RootElement, "lyrics_status", ref status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore malformed JSON payloads and continue with best effort.
        }
    }

    private static void ApplyPayloadLyricsStatusProperty(
        JsonElement root,
        string propertyName,
        ref LyricsStatusFlags status)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in value.EnumerateArray().Where(static token => token.ValueKind == JsonValueKind.String))
            {
                MarkLyricsStatusToken(token.GetString(), ref status);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            MarkLyricsStatusToken(value.GetString(), ref status);
        }
    }

    private static void MarkLyricsStatusToken(
        string? rawToken,
        ref LyricsStatusFlags status)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        foreach (var token in rawToken.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "time-synced":
                case "timesynced":
                case "ttml":
                    status.HasTimeSynced = true;
                    break;
                case "synced":
                case "lrc":
                    status.HasSynced = true;
                    break;
                case "unsynced":
                case "txt":
                    status.HasUnsynced = true;
                    break;
            }
        }
    }

    private struct LyricsStatusFlags
    {
        public bool HasTimeSynced { get; set; }

        public bool HasSynced { get; set; }

        public bool HasUnsynced { get; set; }
    }

    private static void AddFinalDestinationPaths(string? finalDestinationsJson, ISet<string> target)
    {
        if (string.IsNullOrWhiteSpace(finalDestinationsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(finalDestinationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                AddPath(property.Name, target);
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    AddPath(property.Value.GetString(), target);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore malformed JSON payloads and continue with best effort.
        }
    }

    private static void AddPayloadPaths(string? payloadJson, ISet<string> target)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            AddPayloadPathProperty(document.RootElement, "FilePath", target);
            AddPayloadPathProperty(document.RootElement, "filePath", target);

            AddPayloadFilePathsProperty(document.RootElement, "Files", target);
            AddPayloadFilePathsProperty(document.RootElement, FilesPropertyLower, target);
            AddPayloadMapPathsProperty(document.RootElement, "FinalDestinations", target);
            AddPayloadMapPathsProperty(document.RootElement, "finalDestinations", target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore malformed JSON payloads and continue with best effort.
        }
    }

    private static void AddPayloadPathProperty(JsonElement root, string propertyName, ISet<string> target)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        AddPath(value.GetString(), target);
    }

    private static void AddPayloadFilePathsProperty(JsonElement root, string propertyName, ISet<string> target)
    {
        if (!root.TryGetProperty(propertyName, out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind == JsonValueKind.String)
            {
                AddPath(file.GetString(), target);
                continue;
            }

            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            AddPayloadPathProperty(file, "path", target);
            AddPayloadPathProperty(file, "Path", target);
            AddPayloadPathProperty(file, "filename", target);
            AddPayloadPathProperty(file, "Filename", target);
        }
    }

    private static void AddPayloadMapPathsProperty(JsonElement root, string propertyName, ISet<string> target)
    {
        if (!root.TryGetProperty(propertyName, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in map.EnumerateObject())
        {
            AddPath(property.Name, target);
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                AddPath(property.Value.GetString(), target);
            }
        }
    }

    public static bool HasExistingMaterializedFile(DownloadQueueItem item)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPayloadPaths(item.PayloadJson, paths);
        return paths.Any(PathExists);
    }

    private static bool PathExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(path);
        return !string.IsNullOrWhiteSpace(ioPath) && File.Exists(ioPath);
    }

    private static void AddPath(string? raw, ISet<string> target)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var normalized = DownloadPathResolver.NormalizeDisplayPath(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        target.Add(normalized);
    }

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
        command.Parameters.AddWithValue("moveStatus", ResolveInitialMoveStatus(item));
        command.Parameters.AddWithValue("enrichmentStatus", ResolveInitialEnrichmentStatus(item));
        command.Parameters.AddWithValue("status", item.Status);
        command.Parameters.AddWithValue(PayloadParameterName, (object?)item.PayloadJson ?? DBNull.Value);
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
        var payloadJson = GetNullableString(reader, 23);
        var createdAt = ParseTimestampOrUtcNow(GetNullableString(reader, 28));
        var updatedAt = ParseTimestampOrUtcNow(GetNullableString(reader, 29));
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
            GetNullableString(reader, 20),
            GetNullableString(reader, 21),
            reader.GetString(22),
            payloadJson,
            GetNullableDouble(reader, 24),
            GetNullableInt32(reader, 25),
            GetNullableInt32(reader, 26),
            GetNullableString(reader, 27),
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

    private static object ResolveInitialMoveStatus(DownloadQueueItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.FinalizationStatus))
        {
            return item.FinalizationStatus.Trim();
        }

        if (item.DestinationFolderId.HasValue)
        {
            return IsCompletedStatus(item.Status) ? MoveStatusPending : DBNull.Value;
        }

        return IsCompletedStatus(item.Status) ? MoveStatusNotRequired : DBNull.Value;
    }

    private static object ResolveInitialEnrichmentStatus(DownloadQueueItem item)
    {
        var normalized = NormalizeEnrichmentStatus(item.EnrichmentStatus);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        if (item.DestinationFolderId.HasValue)
        {
            return IsCompletedStatus(item.Status) ? EnrichmentStatusPending : EnrichmentStatusPending;
        }

        return EnrichmentStatusNotRequired;
    }

    private static string NormalizeEnrichmentStatus(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return normalized switch
        {
            EnrichmentStatusPending => EnrichmentStatusPending,
            EnrichmentStatusRunning => EnrichmentStatusRunning,
            EnrichmentStatusCompleted => EnrichmentStatusCompleted,
            EnrichmentStatusFailed => EnrichmentStatusFailed,
            "cancelled" => EnrichmentStatusCanceled,
            EnrichmentStatusCanceled => EnrichmentStatusCanceled,
            EnrichmentStatusInterrupted => EnrichmentStatusInterrupted,
            EnrichmentStatusNotRequired => EnrichmentStatusNotRequired,
            _ => EnrichmentStatusPending
        };
    }

    private static bool IsCompletedStatus(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return normalized is "completed" or "complete";
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
    public long? DestinationFolderId { get; init; }
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
    string? FinalizationStatus,
    string? EnrichmentStatus,
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
        string? ContentType,
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
            ContentType,
            FinalizationStatus: null,
            EnrichmentStatus: null,
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
            FinalizationStatus: null,
            EnrichmentStatus: null,
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
