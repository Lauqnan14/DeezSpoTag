using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Download.Queue;

public enum QueueRequeueOrigin
{
    Manual = 0,
    AutoRetry = 1,
    QueueUpgradeRecovery = 3,
    FallbackAdvance = 4,
    Unknown = 99
}

public sealed record DownloadQueueStatusCounts(int ActiveDownloads, int CompletedDownloads);

public sealed class DownloadQueueRepository
{
    public sealed record QueueStateChangedEvent(string QueueUuid, string Status);
    public static event Action<QueueStateChangedEvent>? QueueStateChanged;

    private static readonly SemaphoreSlim DequeueGate = new(1, 1);
    private static readonly SemaphoreSlim EnqueueAdmissionGate = new(1, 1);
    private const string DownloadTaskTable = "download_task";
    private const string FilesPropertyLower = "files";
    private const string PayloadParameterName = "payload";
    private const string MoveStatusPending = "pending";
    private const string MoveStatusRunning = "running";
    private const string MoveStatusMoved = "moved";
    private const string MoveStatusBlocked = "blocked";
    private const string StatusFailed = "failed";
    private const string MoveStatusFailed = StatusFailed;
    private const string StereoContentType = "stereo";
    private const string MoveStatusNotRequired = "not_required";
    private const string EnrichmentStatusPending = "pending";
    private const string EnrichmentStatusRunning = "running";
    private const string EnrichmentStatusCompleted = "completed";
    private const string EnrichmentStatusFailed = StatusFailed;
    private const string EnrichmentStatusCanceled = "canceled";
    private const string EnrichmentStatusInterrupted = "interrupted";
    private const string EnrichmentStatusNotRequired = "not_required";
    private const string CompletedQueueStatusSqlCondition = "lower(status) IN ('completed', 'complete')";
    private const string ResolutionStatusSql = "lower(CASE WHEN json_valid(payload) THEN COALESCE(CAST(json_extract(payload, '$.ResolutionStatus') AS TEXT), CAST(json_extract(payload, '$.resolutionStatus') AS TEXT), '') ELSE '' END)";
    private const string QueuedItemReadyForDownloadSqlCondition =
        "(" + ResolutionStatusSql + " = '' OR " + ResolutionStatusSql + " IN ('pending', 'failed', 'resolved'))";
    private const string UpdateDownloadTaskSqlPrefix = "\nUPDATE " + DownloadTaskTable;
    private readonly string _connectionString;
    private readonly DownloadStagingCleanupService? _stagingCleanupService;
    private readonly DownloadQueueWakeSignal? _queueWakeSignal;
    private readonly ILogger<DownloadQueueRepository> _logger;
    private bool _schemaEnsured;
    private readonly object _schemaLock = new();

    public DownloadQueueRepository(
        IConfiguration configuration,
        ILogger<DownloadQueueRepository> logger,
        DownloadStagingCleanupService? stagingCleanupService = null,
        DownloadQueueWakeSignal? queueWakeSignal = null)
    {
        _logger = logger;
        _stagingCleanupService = stagingCleanupService;
        _queueWakeSignal = queueWakeSignal;
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
        await EnqueueAdmissionGate.WaitAsync(cancellationToken);
        try
        {
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
	    (queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id, spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id, qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id, duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status, status, payload, progress, downloaded, failed, error, created_at, updated_at)
	VALUES
	    (@queueUuid, @engine, @artistName, @trackTitle, @isrc, @deezerTrackId, @deezerAlbumId, @deezerArtistId, @spotifyTrackId, @spotifyAlbumId, @spotifyArtistId, @appleTrackId, @appleAlbumId, @appleArtistId, @qobuzTrackId, @qobuzAlbumId, @qobuzArtistId, @tidalTrackId, @tidalAlbumId, @tidalArtistId, @amazonTrackId, @amazonAlbumId, @amazonArtistId, @durationMs, @destinationFolderId, @qualityRank, @queueOrder, @contentType, @moveStatus, @enrichmentStatus, @status, @payload, @progress, @downloaded, @failed, @error, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
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
        finally
        {
            EnqueueAdmissionGate.Release();
        }
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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Blocked non-manual requeue for cancelled item {QueueUuid} (origin={Origin})",
                    LogSanitizer.OneLine(queueUuid),
                    origin);
            }

            return false;
        }

        var queueOrder = requeueToFront
            ? await GetFrontQueueOrderAsync(connection, newestFirst, cancellationToken)
            : await GetExistingQueueOrderAsync(connection, queueUuid, cancellationToken);
        const string sql = UpdateDownloadTaskSqlPrefix + @"
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
    retry_next_at = NULL,
    retry_reason = NULL,
    retry_engine = NULL,
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
       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
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
                    command.Parameters.AddWithValue($"exclude{indexer}", engine.Trim().ToLowerInvariant());
                    indexer++;
                }
            },
            $"AND lower(engine) NOT IN ({placeholders})",
            cancellationToken);
    }

    public async Task<DownloadQueueItem?> DequeueNextWithPublicEngineLimitAsync(
        IReadOnlyCollection<string> publicEngines,
        bool newestFirst,
        CancellationToken cancellationToken = default)
    {
        if (publicEngines.Count == 0)
        {
            return await DequeueNextAnyAsync(newestFirst, cancellationToken);
        }

        await DequeueGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var normalizedEngines = publicEngines
                .Where(engine => !string.IsNullOrWhiteSpace(engine))
                .Select(engine => engine.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedEngines.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var publicEnginePlaceholders = string.Join(", ", normalizedEngines.Select((_, index) => $"@publicEngine{index}"));
            var hasRunningPublicEngine = await HasRunningPublicEngineAsync(
                connection,
                transaction,
                normalizedEngines,
                publicEnginePlaceholders,
                cancellationToken);
            var extraWhereClause = hasRunningPublicEngine
                ? $"AND lower(engine) NOT IN ({publicEnginePlaceholders})"
                : string.Empty;

            var sql = BuildDequeueSelectSql(newestFirst, extraWhereClause);
            await using var selectCommand = new SqliteCommand(sql, connection, transaction);
            BindPublicEngineParameters(selectCommand, normalizedEngines);

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

    public async Task<IReadOnlyList<DownloadQueueItem>> GetTasksAsync(string? engine = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
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

    public async Task<IReadOnlyList<string>> GetPipelineOwnedPayloadPathsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT payload
FROM download_task
WHERE lower(status) IN ('resolving', 'queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying')
   OR (
        lower(status) IN ('completed', 'complete')
        AND (
            lower(COALESCE(enrichment_status, '')) NOT IN ('completed', 'not_required')
            OR lower(COALESCE(move_status, '')) NOT IN ('moved', 'not_required')
        )
   );";
        await using var command = new SqliteCommand(sql, connection);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            AddPayloadPaths(GetNullableString(reader, 0), paths);
        }

        return paths.ToList();
    }

    public async Task<IReadOnlyList<DownloadQueueItem>> GetPreResolutionWindowAsync(
        bool newestFirst,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var direction = newestFirst ? "DESC" : "ASC";
        var sql = $@"
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
FROM download_task
WHERE lower(status) IN ('queued', 'resolving')
ORDER BY (queue_order IS NULL), queue_order {direction}, created_at {direction}, id {direction}
LIMIT @limit;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 25));
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
	       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
	       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
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

    public async Task<bool> ScheduleRetryAsync(
        string queueUuid,
        string engine,
        string reason,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET status = 'retry_waiting',
    progress = 0,
    downloaded = 0,
    failed = 0,
    retry_next_at = strftime(
        '%Y-%m-%dT%H:%M:%fZ',
        'now',
        '+' || MIN(300, 15 * (1 << retry_attempt_count)) || ' seconds'),
    retry_attempt_count = retry_attempt_count + 1,
    retry_reason = @reason,
    retry_engine = @engine,
    error = @reason,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND retry_attempt_count < @maxAttempts
  AND lower(status) NOT IN ('completed', 'complete', 'canceled', 'cancelled');";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("maxAttempts", Math.Max(0, maxAttempts));
        command.Parameters.AddWithValue("reason", reason ?? string.Empty);
        command.Parameters.AddWithValue("engine", engine ?? string.Empty);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected > 0)
        {
            PublishQueueStateChanged(queueUuid, "retry_waiting");
        }
        return affected > 0;
    }

    public async Task<bool> HasScheduledRetriesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqliteCommand(
            "SELECT 1 FROM download_task WHERE status = 'retry_waiting' LIMIT 1;",
            connection);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<string>> GetDueRetryQueueUuidsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT queue_uuid
FROM download_task
WHERE status = 'retry_waiting'
  AND retry_next_at IS NOT NULL
  AND datetime(retry_next_at) <= datetime('now')
ORDER BY datetime(retry_next_at), id;";
        var result = new List<string>();
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public async Task ClearRetryScheduleAsync(
        string queueUuid,
        bool resetAttempts,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET retry_attempt_count = CASE WHEN @resetAttempts = 1 THEN 0 ELSE retry_attempt_count END,
    retry_next_at = NULL,
    retry_reason = NULL,
    retry_engine = NULL
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("resetAttempts", resetAttempts ? 1 : 0);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<int> GetRunnableDownloadCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT COUNT(*)
FROM download_task
WHERE lower(status) IN ('inqueue', 'running', 'downloading', 'retrying')
   OR (lower(status) = 'queued' AND " + QueuedItemReadyForDownloadSqlCondition + @");";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<DownloadQueueStatusCounts> GetStatusCountsAsync(
        DateTimeOffset completedSinceUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT
    SUM(CASE WHEN lower(status) IN ('running', 'downloading', 'inprogress', 'retrying') THEN 1 ELSE 0 END),
    SUM(CASE WHEN lower(status) IN ('completed', 'complete', 'finished')
              AND updated_at >= @completedSinceUtc THEN 1 ELSE 0 END)
FROM download_task;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("completedSinceUtc", completedSinceUtc.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new DownloadQueueStatusCounts(0, 0);
        }
        return new DownloadQueueStatusCounts(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    public async Task<bool> HasActiveDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT 1
FROM download_task
	WHERE lower(status) IN ('queued', 'resolving', 'preparing', 'prepared', 'inqueue', 'running', 'downloading', 'paused', 'retrying')
	LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task<bool> HasActiveDownloadPipelineAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT 1
FROM download_task
WHERE lower(status) IN ('queued', 'resolving', 'preparing', 'prepared', 'inqueue', 'running', 'downloading', 'paused', 'retrying')
   OR (
       lower(status) IN ('completed', 'complete')
       AND destination_folder_id IS NOT NULL
       AND (
           lower(COALESCE(move_status, '')) = 'running'
           OR lower(COALESCE(enrichment_status, '')) = 'running'
           OR (
               datetime(updated_at) >= datetime(@postDownloadLeaseUtc)
               AND (
                   lower(COALESCE(move_status, '')) IN ('', 'pending')
                   OR lower(COALESCE(enrichment_status, '')) IN ('', 'pending')
               )
           )
       )
   )
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(
            "postDownloadLeaseUtc",
            (DateTimeOffset.UtcNow - DownloadQueueRecoveryPolicy.PostDownloadPendingLease).ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task<bool> HasActiveWatchlistDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT 1
FROM download_task
WHERE (
      lower(status) IN ('queued', 'resolving', 'preparing', 'prepared', 'inqueue', 'running', 'downloading', 'paused', 'retrying')
      OR (
          lower(status) IN ('completed', 'complete')
          AND destination_folder_id IS NOT NULL
          AND (
              lower(COALESCE(move_status, '')) = 'running'
              OR lower(COALESCE(enrichment_status, '')) = 'running'
              OR (
                  datetime(updated_at) >= datetime(@postDownloadLeaseUtc)
                  AND (
                      lower(COALESCE(move_status, '')) IN ('', 'pending')
                      OR lower(COALESCE(enrichment_status, '')) IN ('', 'pending')
                  )
              )
          )
      )
  )
  AND json_valid(payload)
  AND (
      COALESCE(CAST(json_extract(payload, '$.WatchlistOrigin') AS TEXT), '') <> ''
      OR COALESCE(CAST(json_extract(payload, '$.watchlistOrigin') AS TEXT), '') <> ''
      OR COALESCE(CAST(json_extract(payload, '$.WatchlistSource') AS TEXT), '') <> ''
      OR COALESCE(CAST(json_extract(payload, '$.watchlistSource') AS TEXT), '') <> ''
      OR COALESCE(CAST(json_extract(payload, '$.WatchlistPlaylistId') AS TEXT), '') <> ''
      OR COALESCE(CAST(json_extract(payload, '$.watchlistPlaylistId') AS TEXT), '') <> ''
      OR COALESCE(CAST(json_extract(payload, '$.WatchlistTrackId') AS TEXT), '') <> ''
      OR COALESCE(CAST(json_extract(payload, '$.watchlistTrackId') AS TEXT), '') <> ''
  )
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(
            "postDownloadLeaseUtc",
            (DateTimeOffset.UtcNow - DownloadQueueRecoveryPolicy.PostDownloadPendingLease).ToString("O"));
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
WHERE lower(status) IN ('inqueue', 'running', 'downloading', 'retrying')
   OR (lower(status) = 'queued' AND " + QueuedItemReadyForDownloadSqlCondition + @")
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task UpdateStatusAsync(string queueUuid, string status, string? error = null, int? downloaded = null, int? failed = null, double? progress = null, CancellationToken cancellationToken = default)
    {
        var completed = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);
        if (progress.HasValue)
        {
            progress = completed
                ? 100d
                : Math.Clamp(progress.Value, 0d, 95d);
        }

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
        return normalized is "failed" or "error" or "canceled" or "cancelled";
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

    public async Task<int> RecoverInterruptedPreResolutionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET status = 'queued',
    payload = CASE
        WHEN json_valid(payload) THEN json_set(
            payload,
            '$.ResolutionStatus', 'pending',
            '$.resolutionStatus', 'pending',
            '$.ResolutionError', '',
            '$.resolutionError', ''
        )
        ELSE payload
    END,
    error = NULL,
    updated_at = CURRENT_TIMESTAMP
WHERE status = 'resolving'
   OR (status = 'queued' AND " + ResolutionStatusSql + @" = 'resolving');";
        await using var command = new SqliteCommand(sql, connection);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected > 0)
        {
            PublishQueueStateChanged(string.Empty, "queued");
        }

        return affected;
    }

    private void PublishQueueStateChanged(string queueUuid, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        _queueWakeSignal?.Pulse();
        var handler = QueueStateChanged;
        if (handler == null)
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

    private static async Task<bool> HasRunningPublicEngineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> publicEngines,
        string placeholders,
        CancellationToken cancellationToken)
    {
        var sql = $@"
SELECT 1
FROM download_task
WHERE status = 'running'
  AND lower(engine) IN ({placeholders})
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        BindPublicEngineParameters(command, publicEngines);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private static void BindPublicEngineParameters(
        SqliteCommand command,
        IReadOnlyList<string> publicEngines)
    {
        for (var index = 0; index < publicEngines.Count; index++)
        {
            command.Parameters.AddWithValue($"publicEngine{index}", publicEngines[index]);
        }
    }

    private static string NormalizeEngine(string? engine)
        => string.IsNullOrWhiteSpace(engine) ? string.Empty : engine.Trim().ToLowerInvariant();

    private static string BuildDequeueSelectSql(bool newestFirst, string extraWhereClause)
    {
        var orderBy = newestFirst ? "DESC" : "ASC";
        var queueOrderBy = newestFirst ? "DESC" : "ASC";
        return $@"
WITH queue_head AS (
	SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
	       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
	       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
	       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
	       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
	FROM download_task
	WHERE status = 'queued'
      AND {QueuedItemReadyForDownloadSqlCondition}
ORDER BY (queue_order IS NULL), queue_order {queueOrderBy}, created_at {orderBy}, id {orderBy}
LIMIT 1
)
SELECT id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
       spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
       duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
FROM queue_head
WHERE 1 = 1
  {extraWhereClause};";
    }

    private static string BuildActivitiesQueueSql()
    {
        const string selectedColumns = @"
	id, queue_uuid, engine, artist_name, track_title, isrc, deezer_track_id, deezer_album_id, deezer_artist_id,
	spotify_track_id, spotify_album_id, spotify_artist_id, apple_track_id, apple_album_id, apple_artist_id,
	duration_ms, destination_folder_id, quality_rank, queue_order, content_type, move_status, enrichment_status,
	status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
	qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id";

        return @"
SELECT " + selectedColumns + @"
FROM download_task
WHERE activities_cleared_at IS NULL
ORDER BY CASE WHEN queue_order IS NULL THEN id ELSE queue_order END ASC,
         created_at ASC,
         id ASC;";
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        payloadJson = await MergeCurrentPrefetchStateAsync(
            connection,
            transaction,
            queueUuid,
            payloadJson,
            cancellationToken);
        const string sql = @"
UPDATE download_task
SET payload = @payload,
    qobuz_track_id = CASE
        WHEN json_valid(@payload) THEN COALESCE(NULLIF(trim(COALESCE(json_extract(@payload, '$.QobuzId'), json_extract(@payload, '$.qobuzId'))), ''), qobuz_track_id)
        ELSE qobuz_track_id
    END,
    tidal_track_id = CASE
        WHEN json_valid(@payload) THEN COALESCE(NULLIF(trim(COALESCE(json_extract(@payload, '$.TidalId'), json_extract(@payload, '$.tidalId'))), ''), tidal_track_id)
        ELSE tidal_track_id
    END,
    amazon_track_id = CASE
        WHEN json_valid(@payload) THEN COALESCE(NULLIF(trim(COALESCE(json_extract(@payload, '$.AmazonId'), json_extract(@payload, '$.amazonId'))), ''), amazon_track_id)
        ELSE amazon_track_id
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(PayloadParameterName, payloadJson);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> MergeCurrentPrefetchStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueUuid,
        string incomingPayloadJson,
        CancellationToken cancellationToken)
    {
        const string selectSql = "SELECT payload FROM download_task WHERE queue_uuid = @queueUuid LIMIT 1;";
        await using var selectCommand = new SqliteCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("queueUuid", queueUuid);
        var currentPayloadJson = await selectCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(currentPayloadJson))
        {
            return incomingPayloadJson;
        }

        try
        {
            var current = JsonNode.Parse(currentPayloadJson) as JsonObject;
            var incoming = JsonNode.Parse(incomingPayloadJson) as JsonObject;
            if (current == null || incoming == null)
            {
                return incomingPayloadJson;
            }

            PreserveNonEmptyProperty(current, incoming, "ArtworkStatus");
            PreserveNonEmptyProperty(current, incoming, "ArtworkError");
            PreserveNonEmptyPropertyUnlessSpecified(current, incoming, "PrefetchArtworkStatus");
            PreserveCanonicalLyricsArtifacts(current, incoming);
            MergeFileArrays(current, incoming);
            return incoming.ToJsonString();
        }
        catch (JsonException)
        {
            return incomingPayloadJson;
        }
    }

    private static void PreserveCanonicalLyricsArtifacts(JsonObject current, JsonObject incoming)
    {
        var currentState = current["lyricsArtifacts"] ?? current["LyricsArtifacts"];
        var incomingState = incoming["lyricsArtifacts"] ?? incoming["LyricsArtifacts"];
        if (currentState == null)
        {
            return;
        }

        var currentRevision = (currentState as JsonObject)?["revision"]?.GetValue<long?>() ?? 0;
        var incomingRevision = (incomingState as JsonObject)?["revision"]?.GetValue<long?>() ?? -1;
        if (incomingState == null || incomingRevision < currentRevision)
        {
            incoming.Remove("LyricsArtifacts");
            incoming["lyricsArtifacts"] = currentState.DeepClone();
        }
    }

    public async Task<bool> UpdateLyricsArtifactsAsync(
        string queueUuid,
        LyricsArtifactState state,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var stateJson = JsonSerializer.Serialize(state);
        const string sql = @"
UPDATE download_task
SET payload = CASE
        WHEN json_valid(payload) THEN json_set(
            json_remove(payload,
                '$.LyricsArtifacts',
                '$.PrefetchLyricsStatus',
                '$.PrefetchLyricsType',
                '$.prefetchLyricsStatus',
                '$.prefetchLyricsType'),
            '$.lyricsArtifacts', json(@state))
        ELSE payload
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND (
      json_extract(payload, '$.lyricsArtifacts.revision') IS NULL
      OR CAST(json_extract(payload, '$.lyricsArtifacts.revision') AS INTEGER) <= @revision
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("state", stateJson);
        command.Parameters.AddWithValue("revision", state.Revision);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<LyricsArtifactState?> GetLyricsArtifactsAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var payloadJson = await GetPayloadJsonAsync(connection, queueUuid, cancellationToken);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }
        try
        {
            var payload = JsonNode.Parse(payloadJson) as JsonObject;
            return (payload?["lyricsArtifacts"] ?? payload?["LyricsArtifacts"])
                ?.Deserialize<LyricsArtifactState>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void PreserveNonEmptyProperty(JsonObject current, JsonObject incoming, string propertyName)
    {
        if (current[propertyName] is not JsonNode currentValue
            || string.IsNullOrWhiteSpace(currentValue.ToString()))
        {
            return;
        }

        incoming[propertyName] = currentValue.DeepClone();
    }

    private static void PreserveNonEmptyPropertyUnlessSpecified(
        JsonObject current,
        JsonObject incoming,
        string propertyName)
    {
        if (incoming.ContainsKey(propertyName))
        {
            return;
        }

        PreserveNonEmptyProperty(current, incoming, propertyName);
    }

    private static void MergeFileArrays(JsonObject current, JsonObject incoming)
    {
        if (current["Files"] is not JsonArray currentFiles || currentFiles.Count == 0)
        {
            return;
        }

        var merged = incoming["Files"] is JsonArray incomingFiles
            ? (JsonArray)incomingFiles.DeepClone()
            : new JsonArray();
        var serialized = new HashSet<string>(
            merged.Where(node => node != null).Select(node => node!.ToJsonString()),
            StringComparer.Ordinal);
        foreach (var file in currentFiles)
        {
            if (file == null || !serialized.Add(file.ToJsonString()))
            {
                continue;
            }

            merged.Add(file.DeepClone());
        }

        incoming["Files"] = merged;
    }

    public async Task UpdatePrefetchFilesAndArtworkAsync(
        string queueUuid,
        string filesJson,
        string artworkStatus,
        string? artworkError,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET payload = CASE
        WHEN json_valid(payload) THEN json_set(
            payload,
            '$.files', json(@files),
            '$.ArtworkStatus', @artworkStatus,
            '$.ArtworkError', @artworkError)
        ELSE payload
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("files", filesJson);
        command.Parameters.AddWithValue("artworkStatus", artworkStatus);
        command.Parameters.AddWithValue("artworkError", (object?)artworkError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateArtworkPrefetchProgressAsync(
        string queueUuid,
        string artworkStatus,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE download_task
SET payload = CASE
        WHEN json_valid(payload) THEN json_set(
            payload,
            '$.PrefetchArtworkStatus', @artworkStatus)
        ELSE payload
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("artworkStatus", artworkStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdatePayloadAndEngineAsync(
        string queueUuid,
        string engine,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        payloadJson = await MergeCurrentPrefetchStateAsync(
            connection,
            transaction,
            queueUuid,
            payloadJson,
            cancellationToken);
        const string sql = @"
UPDATE download_task
SET engine = @engine,
    payload = @payload,
    qobuz_track_id = CASE
        WHEN json_valid(@payload) THEN COALESCE(NULLIF(trim(COALESCE(json_extract(@payload, '$.QobuzId'), json_extract(@payload, '$.qobuzId'))), ''), qobuz_track_id)
        ELSE qobuz_track_id
    END,
    tidal_track_id = CASE
        WHEN json_valid(@payload) THEN COALESCE(NULLIF(trim(COALESCE(json_extract(@payload, '$.TidalId'), json_extract(@payload, '$.tidalId'))), ''), tidal_track_id)
        ELSE tidal_track_id
    END,
    amazon_track_id = CASE
        WHEN json_valid(@payload) THEN COALESCE(NULLIF(trim(COALESCE(json_extract(@payload, '$.AmazonId'), json_extract(@payload, '$.amazonId'))), ''), amazon_track_id)
        ELSE amazon_track_id
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("engine", engine);
        command.Parameters.AddWithValue(PayloadParameterName, payloadJson);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        payloadJson = await MergeCurrentPrefetchStateAsync(
            connection,
            transaction,
            queueUuid,
            payloadJson,
            cancellationToken);
        const string sql = @"
UPDATE download_task
SET payload = @payload,
    engine = COALESCE(@engine, engine),
    status = COALESCE(@status, status),
    error = CASE
        WHEN @status = '" + StatusFailed + @"' THEN COALESCE(@error, error)
        WHEN @status = 'queued' THEN NULL
        ELSE error
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND status IN ('queued', 'resolving', 'running')
  AND ((payload IS NULL AND @expectedPayload IS NULL) OR payload = @expectedPayload);";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(PayloadParameterName, payloadJson);
        command.Parameters.AddWithValue("engine", (object?)engine ?? DBNull.Value);
        command.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("expectedPayload", (object?)expectedPayloadJson ?? DBNull.Value);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var mergedPayloadJson = await MergeCurrentPrefetchStateAsync(
            connection,
            transaction,
            item.QueueUuid,
            item.PayloadJson ?? "{}",
            cancellationToken);
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
    qobuz_track_id = COALESCE(NULLIF(@qobuzTrackId, ''), qobuz_track_id),
    qobuz_album_id = COALESCE(NULLIF(@qobuzAlbumId, ''), qobuz_album_id),
    qobuz_artist_id = COALESCE(NULLIF(@qobuzArtistId, ''), qobuz_artist_id),
    tidal_track_id = COALESCE(NULLIF(@tidalTrackId, ''), tidal_track_id),
    tidal_album_id = COALESCE(NULLIF(@tidalAlbumId, ''), tidal_album_id),
    tidal_artist_id = COALESCE(NULLIF(@tidalArtistId, ''), tidal_artist_id),
    amazon_track_id = COALESCE(NULLIF(@amazonTrackId, ''), amazon_track_id),
    amazon_album_id = COALESCE(NULLIF(@amazonAlbumId, ''), amazon_album_id),
    amazon_artist_id = COALESCE(NULLIF(@amazonArtistId, ''), amazon_artist_id),
    duration_ms = COALESCE(@durationMs, duration_ms),
    destination_folder_id = @destinationFolderId,
    quality_rank = COALESCE(@qualityRank, quality_rank),
    content_type = COALESCE(NULLIF(@contentType, ''), content_type),
    status = COALESCE(@status, status),
    error = CASE
        WHEN @status = '" + StatusFailed + @"' THEN COALESCE(@error, error)
        WHEN @status = 'queued' THEN NULL
        ELSE error
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid
  AND status IN ('queued', 'resolving')
  AND ((payload IS NULL AND @expectedPayload IS NULL) OR payload = @expectedPayload);";
        await using var command = new SqliteCommand(sql, connection, transaction);
        BindCurrentIdentityParameters(command, item, mergedPayloadJson);
        command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@expectedPayload", (object?)expectedPayloadJson ?? DBNull.Value);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        if (updated && !string.IsNullOrWhiteSpace(status))
        {
            PublishQueueStateChanged(item.QueueUuid, status);
        }

        return updated;
    }

    private static void BindCurrentIdentityParameters(
        SqliteCommand command,
        DownloadQueueItem item,
        string? payloadJson = null)
    {
        BindQueueIdentityParameters(command, item, "@");
        command.Parameters.AddWithValue("@payload", (object?)payloadJson ?? (object?)item.PayloadJson ?? DBNull.Value);
    }

    public async Task UpdateFinalDestinationsAsync(
        string queueUuid,
        string? finalDestinationsJson,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var currentPayloadJson = await GetPayloadJsonAsync(connection, queueUuid, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var effectivePayloadJson = string.IsNullOrWhiteSpace(payloadJson)
            ? currentPayloadJson
            : await MergeCurrentPrefetchStateAsync(connection, transaction, queueUuid, payloadJson, cancellationToken);
        effectivePayloadJson = RebaseLyricsArtifacts(effectivePayloadJson, finalDestinationsJson);
        const string sql = @"
UPDATE download_task
SET final_destinations_json = @finalDestinationsJson,
    payload = COALESCE(@payload, payload),
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("finalDestinationsJson", (object?)finalDestinationsJson ?? DBNull.Value);
        command.Parameters.AddWithValue(PayloadParameterName, (object?)effectivePayloadJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string? RebaseLyricsArtifacts(string? payloadJson, string? finalDestinationsJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(finalDestinationsJson))
        {
            return payloadJson;
        }
        try
        {
            var payload = JsonNode.Parse(payloadJson) as JsonObject;
            var artifactsNode = payload?["lyricsArtifacts"] ?? payload?["LyricsArtifacts"];
            var state = artifactsNode?.Deserialize<LyricsArtifactState>();
            var destinations = JsonSerializer.Deserialize<Dictionary<string, string>>(finalDestinationsJson);
            if (payload == null || state == null || destinations == null || destinations.Count == 0)
            {
                return payloadJson;
            }

            var changed = false;
            foreach (var format in state.FilesByFormat.Keys.ToArray())
            {
                var source = state.FilesByFormat[format];
                var destination = destinations
                    .FirstOrDefault(pair => string.Equals(pair.Key, source, StringComparison.OrdinalIgnoreCase)).Value;
                destination ??= destinations.Values.FirstOrDefault(path =>
                    string.Equals(Path.GetExtension(path), "." + format, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(destination)
                    || string.Equals(destination, source, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                state.FilesByFormat[format] = destination;
                changed = true;
            }
            if (!changed)
            {
                return payloadJson;
            }

            state.Revision++;
            payload.Remove("LyricsArtifacts");
            payload["lyricsArtifacts"] = JsonSerializer.SerializeToNode(state);
            return payload.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return payloadJson;
        }
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

        const string sql = @"
	SELECT queue_uuid, enrichment_status
	FROM download_task
	WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        var queueUuidParameter = command.Parameters.Add("queueUuid", SqliteType.Text);
        foreach (var normalizedQueueUuid in normalized)
        {
            queueUuidParameter.Value = normalizedQueueUuid;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                continue;
            }

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
        string sql = @$"
	UPDATE download_task
	SET quality_rank = @qualityRank,
	    content_type = COALESCE(@contentType, content_type),
	    destination_folder_id = @destinationFolderId,
	    move_status = CASE
	        WHEN @destinationFolderId IS NOT NULL AND {CompletedQueueStatusSqlCondition} THEN '{MoveStatusPending}'
	        WHEN @destinationFolderId IS NULL AND {CompletedQueueStatusSqlCondition} THEN '{MoveStatusNotRequired}'
	        ELSE move_status
	    END,
	    enrichment_status = CASE
	        WHEN @destinationFolderId IS NOT NULL AND {CompletedQueueStatusSqlCondition} THEN '{EnrichmentStatusPending}'
	        WHEN @destinationFolderId IS NULL AND {CompletedQueueStatusSqlCondition} THEN '{EnrichmentStatusNotRequired}'
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
        string sql = @$"
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
    qobuz_track_id = @qobuzTrackId,
    qobuz_album_id = @qobuzAlbumId,
    qobuz_artist_id = @qobuzArtistId,
    tidal_track_id = @tidalTrackId,
    tidal_album_id = @tidalAlbumId,
    tidal_artist_id = @tidalArtistId,
    amazon_track_id = @amazonTrackId,
    amazon_album_id = @amazonAlbumId,
    amazon_artist_id = @amazonArtistId,
    duration_ms = @durationMs,
    destination_folder_id = @destinationFolderId,
    quality_rank = @qualityRank,
    content_type = @contentType,
    payload = @payload,
    final_destinations_json = NULL,
    staging_cleanup_status = NULL,
	    staging_cleanup_error = NULL,
	    staging_cleanup_at = NULL,
	    activities_cleared_at = NULL,
	    move_status = CASE
	        WHEN @destinationFolderId IS NOT NULL AND {CompletedQueueStatusSqlCondition} THEN '{MoveStatusPending}'
	        WHEN @destinationFolderId IS NULL AND {CompletedQueueStatusSqlCondition} THEN '{MoveStatusNotRequired}'
	        ELSE move_status
	    END,
	    enrichment_status = CASE
	        WHEN @destinationFolderId IS NOT NULL AND {CompletedQueueStatusSqlCondition} THEN '{EnrichmentStatusPending}'
	        WHEN @destinationFolderId IS NULL AND {CompletedQueueStatusSqlCondition} THEN '{EnrichmentStatusNotRequired}'
	        ELSE enrichment_status
	    END,
    updated_at = CURRENT_TIMESTAMP
WHERE queue_uuid = @queueUuid;";
        await using var command = new SqliteCommand(sql, connection);
        BindCommonParameters(command, item);
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

    public async Task<int> MarkActivitiesClearedByStatusesAsync(
        IReadOnlyCollection<string> statuses,
        CancellationToken cancellationToken = default)
    {
        if (statuses.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var status in statuses)
        {
            total += await MarkActivitiesClearedByStatusAsync(status, cancellationToken);
        }

        return total;
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
        const string activeStatuses = "'resolving', 'queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying'";
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
WHERE lower(status) = lower(@status)
  AND (
    destination_folder_id IS NULL
    OR move_status = '" + MoveStatusMoved + @"'
    OR move_status = '" + MoveStatusNotRequired + @"'
    OR (lower(status) IN ('failed', 'error', 'canceled', 'cancelled') AND staging_cleanup_status IN ('completed', 'skipped'))
  );";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await CleanupOrphanSidecarDirectoriesAsync(connection, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteClearableByStatusesAsync(
        IReadOnlyCollection<string> statuses,
        CancellationToken cancellationToken = default)
    {
        if (statuses.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var status in statuses)
        {
            total += await DeleteClearableByStatusAsync(status, cancellationToken);
        }

        return total;
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
    OR (lower(status) IN ('failed', 'error', 'canceled', 'cancelled') AND staging_cleanup_status IN ('completed', 'skipped'))
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
        const string activeStatuses = "'resolving', 'queued', 'inqueue', 'running', 'downloading', 'paused', 'retrying'";
        const string sql = @"
DELETE FROM download_task
WHERE lower(status) NOT IN (" + activeStatuses + @")
  AND (
    destination_folder_id IS NULL
    OR move_status = '" + MoveStatusMoved + @"'
    OR move_status = '" + MoveStatusNotRequired + @"'
    OR (lower(status) IN ('failed', 'error', 'canceled', 'cancelled') AND staging_cleanup_status IN ('completed', 'skipped'))
  );";
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
  AND lower(status) IN ('failed', 'error', 'canceled', 'cancelled');";
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
WHERE lower(status) IN ('failed', 'error', 'canceled', 'cancelled');";
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
            await TryCleanupStagingForTerminalStatusAsync(connection, queueUuid, StatusFailed, cancellationToken);
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
		       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
		       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
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
            @qobuzTrackId IS NOT NULL
            AND @qobuzTrackId <> ''
            AND (
                lower(qobuz_track_id) = lower(@qobuzTrackId)
                OR (
                    json_valid(payload)
                    AND (
                        lower(json_extract(payload, '$.QobuzId')) = lower(@qobuzTrackId)
                        OR lower(json_extract(payload, '$.qobuzId')) = lower(@qobuzTrackId)
                    )
                )
            )
        )
        OR (
            @tidalTrackId IS NOT NULL
            AND @tidalTrackId <> ''
            AND (
                lower(tidal_track_id) = lower(@tidalTrackId)
                OR (
                    json_valid(payload)
                    AND (
                        lower(json_extract(payload, '$.TidalId')) = lower(@tidalTrackId)
                        OR lower(json_extract(payload, '$.tidalId')) = lower(@tidalTrackId)
                    )
                )
            )
        )
        OR (
            @amazonTrackId IS NOT NULL
            AND @amazonTrackId <> ''
            AND (
                lower(amazon_track_id) = lower(@amazonTrackId)
                OR (
                    json_valid(payload)
                    AND (
                        lower(json_extract(payload, '$.AmazonId')) = lower(@amazonTrackId)
                        OR lower(json_extract(payload, '$.amazonId')) = lower(@amazonTrackId)
                    )
                )
            )
        )
        OR (
            lower(track_title) = lower(@trackTitle)
        )
    )
    AND (
        (@destinationFolderId IS NULL AND destination_folder_id IS NULL)
        OR destination_folder_id = @destinationFolderId
    )
    AND (
        @contentType IS NULL
        OR lower(content_type) = lower(@contentType)
        OR (
            lower(@contentType) = '" + StereoContentType + @"'
            AND NULLIF(trim(COALESCE(content_type, '')), '') IS NULL
        )
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
        command.Parameters.AddWithValue("qobuzTrackId", EngineLinkParser.NormalizeNumericTrackId(request.QobuzTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("tidalTrackId", EngineLinkParser.NormalizeNumericTrackId(request.TidalTrackId) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("amazonTrackId", EngineLinkParser.NormalizeAmazonTrackId(request.AmazonTrackId) ?? (object)DBNull.Value);
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

            if (IsStaleCompletedDuplicate(item))
            {
                continue;
            }

            return item;
        }

        return null;
    }

    private static bool IsStaleCompletedDuplicate(DownloadQueueItem item)
        => IsCompletedStatus(item.Status) && !HasExistingMaterializedFile(item);

    private static bool MatchesDuplicateRequest(DuplicateLookupRequest request, DownloadQueueItem item)
    {
        return HasStrongIdentityMatch(request, item)
            || HasPayloadStrongIdentityMatch(request, item)
            || HasMetadataMatch(request, item);
    }

    private static bool HasStrongIdentityMatch(DuplicateLookupRequest request, DownloadQueueItem item)
    {
        return EqualsNormalizedIsrc(request.Isrc, item.Isrc)
            || EqualsNormalizedId(request.DeezerTrackId, item.DeezerTrackId)
            || EqualsNormalizedId(request.SpotifyTrackId, item.SpotifyTrackId)
            || EqualsNormalizedId(request.AppleTrackId, item.AppleTrackId)
            || EqualsNormalizedId(request.QobuzTrackId, item.QobuzTrackId)
            || EqualsNormalizedId(request.TidalTrackId, item.TidalTrackId)
            || EqualsNormalizedId(request.AmazonTrackId, item.AmazonTrackId);
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
                || EqualsNormalizedId(request.AppleTrackId, ReadPayloadString(root, "AppleId", "appleId"))
                || EqualsNormalizedId(request.QobuzTrackId, ReadPayloadString(root, "QobuzId", "qobuzId"))
                || EqualsNormalizedId(request.TidalTrackId, ReadPayloadString(root, "TidalId", "tidalId"))
                || EqualsNormalizedId(request.AmazonTrackId, ReadPayloadString(root, "AmazonId", "amazonId"));
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
        if (!TrackTitleMatcher.TitlesMatch(request.TrackTitle, item.TrackTitle))
        {
            return false;
        }

        var artistMatches = TrackTitleMatcher.ArtistsMatch(request.ArtistName, item.ArtistName)
            || (!string.IsNullOrWhiteSpace(request.ArtistPrimaryName)
                && TrackTitleMatcher.ArtistsMatch(request.ArtistPrimaryName, item.ArtistName));
        if (!artistMatches)
        {
            return false;
        }

        return !request.DurationMs.HasValue
            || request.DurationMs.Value <= 0
            || !item.DurationMs.HasValue
            || Math.Abs(item.DurationMs.Value - request.DurationMs.Value) <= 2000;
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
	       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
	       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
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
  AND (
        @contentType IS NULL
        OR lower(content_type) = lower(@contentType)
        OR (
            lower(@contentType) = '" + StereoContentType + @"'
            AND NULLIF(trim(COALESCE(content_type, '')), '') IS NULL
        )
      )
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
	       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
	       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
	FROM download_task
WHERE lower(engine) = lower(@engine)
  AND lower(artist_name) = lower(@artistName)
  AND lower(track_title) = lower(@trackTitle)
  AND (
        (@destinationFolderId IS NULL AND destination_folder_id IS NULL)
        OR destination_folder_id = @destinationFolderId
      )
  AND (
        @contentType IS NULL
        OR lower(content_type) = lower(@contentType)
        OR (
            lower(@contentType) = '" + StereoContentType + @"'
            AND NULLIF(trim(COALESCE(content_type, '')), '') IS NULL
        )
      )
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
	       status, payload, progress, downloaded, failed, error, created_at, updated_at, final_destinations_json,
	       qobuz_track_id, qobuz_album_id, qobuz_artist_id, tidal_track_id, tidal_album_id, tidal_artist_id, amazon_track_id, amazon_album_id, amazon_artist_id
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
    qobuz_track_id TEXT,
    qobuz_album_id TEXT,
    qobuz_artist_id TEXT,
    tidal_track_id TEXT,
    tidal_album_id TEXT,
    tidal_artist_id TEXT,
    amazon_track_id TEXT,
    amazon_album_id TEXT,
    amazon_artist_id TEXT,
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
        await EnsureColumnAsync(connection, DownloadTaskTable, "qobuz_track_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "qobuz_album_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "qobuz_artist_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "tidal_track_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "tidal_album_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "tidal_artist_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "amazon_track_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "amazon_album_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "amazon_artist_id", "TEXT", cancellationToken);
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
        await EnsureColumnAsync(connection, DownloadTaskTable, "retry_attempt_count", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "retry_next_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "retry_reason", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, DownloadTaskTable, "retry_engine", "TEXT", cancellationToken);
        await EnsureIndexesAsync(connection, cancellationToken);
        await NormalizeLegacyPlaceholderIdsAsync(connection, cancellationToken);
        await NormalizeLegacyAtmosContentTypesAsync(connection, cancellationToken);
        await NormalizeLegacyEnrichmentStatusesAsync(connection, cancellationToken);
        await NormalizeCompletedFinalizationStatusesAsync(connection, cancellationToken);
        await BackfillMissingIdentityFromPayloadAsync(connection, cancellationToken);
        await NormalizeLegacyExecutionPlansAsync(connection, cancellationToken);
        await MigrateLegacyLyricsArtifactsAsync(connection, cancellationToken);

        lock (_schemaLock)
        {
            _schemaEnsured = true;
        }
    }

    private static async Task MigrateLegacyLyricsArtifactsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string selectSql = @"
SELECT id, payload, lyrics_status
FROM download_task
WHERE json_valid(payload)
  AND json_extract(payload, '$.lyricsArtifacts') IS NULL;";
        var rows = new List<(long Id, string Payload, string? LyricsStatus)>();
        await using (var select = new SqliteCommand(selectSql, connection))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var row in rows)
        {
            var payload = JsonNode.Parse(row.Payload) as JsonObject;
            if (payload == null)
            {
                continue;
            }

            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddLegacyLyricsFormats(row.LyricsStatus, resolved);
            AddLegacyLyricsFormats(payload["PrefetchLyricsType"]?.ToString(), resolved);
            AddLegacyLyricsFormats(payload["prefetchLyricsType"]?.ToString(), resolved);
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filesByFormat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CollectLegacyLyricsFiles(payload["files"] ?? payload["Files"], downloaded, filesByFormat);
            resolved.UnionWith(downloaded);
            if (resolved.Contains("ttml") || resolved.Contains("lrc"))
            {
                resolved.Remove("txt");
                downloaded.Remove("txt");
                filesByFormat.Remove("txt");
            }

            var state = new LyricsArtifactState
            {
                Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Status = downloaded.Count > 0 ? "completed" : (resolved.Count > 0 ? "resolved" : "unavailable"),
                ResolvedFormats = resolved.ToList(),
                DownloadedFormats = downloaded.ToList(),
                FilesByFormat = filesByFormat
            };
            payload.Remove("PrefetchLyricsStatus");
            payload.Remove("PrefetchLyricsType");
            payload.Remove("prefetchLyricsStatus");
            payload.Remove("prefetchLyricsType");
            payload.Remove("LyricsStatus");
            payload.Remove("lyricsStatus");
            payload.Remove("lyrics_status");
            payload["lyricsArtifacts"] = JsonSerializer.SerializeToNode(state);

            await using var update = new SqliteCommand(
                "UPDATE download_task SET payload = @payload WHERE id = @id;",
                connection);
            update.Parameters.AddWithValue("payload", payload.ToJsonString());
            update.Parameters.AddWithValue("id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var clearLegacyStatus = new SqliteCommand(
            "UPDATE download_task SET lyrics_status = NULL WHERE lyrics_status IS NOT NULL;",
            connection);
        await clearLegacyStatus.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddLegacyLyricsFormats(string? raw, ISet<string> formats)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.Trim().ToLowerInvariant();
            if (normalized is "time-synced" or "timesynced" or "ttml" or "syllable-lyrics") formats.Add("ttml");
            else if (normalized is "synced" or "lrc" or "lyrics") formats.Add("lrc");
            else if (normalized is "unsynced" or "txt" or "unsynced-lyrics") formats.Add("txt");
            else if (normalized is "both" or "richlyrics" or "rich-lyrics")
            {
                formats.Add("lrc");
                formats.Add("elrc");
                formats.Add("txt");
                formats.Add("ttml");
            }
        }
    }

    private static void CollectLegacyLyricsFiles(
        JsonNode? filesNode,
        ISet<string> downloaded,
        IDictionary<string, string> filesByFormat)
    {
        if (filesNode is not JsonArray files)
        {
            return;
        }
        foreach (var file in files)
        {
            var path = file is JsonObject obj
                ? (obj["path"] ?? obj["Path"])?.ToString()
                : file?.ToString();
            var format = Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
            {
                ".ttml" => "ttml",
                ".elrc" => "elrc",
                ".lrc" => "lrc",
                ".txt" => "txt",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(format))
            {
                continue;
            }
            downloaded.Add(format);
            filesByFormat[format] = path!;
        }
    }

    private static async Task NormalizeLegacyExecutionPlansAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string selectSql = @"
SELECT queue_uuid, payload
FROM download_task
WHERE payload IS NOT NULL AND trim(payload) <> '';";
        var updates = new List<(string QueueUuid, string Payload)>();
        await using (var select = new SqliteCommand(selectSql, connection))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var queueUuid = reader.GetString(0);
                var raw = reader.IsDBNull(1) ? null : reader.GetString(1);
                JsonObject? payload;
                try
                {
                    payload = JsonNode.Parse(raw ?? string.Empty) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (payload == null)
                {
                    continue;
                }

                var changed = false;
                var plan = DownloadExecutionPlan.Read(payload);
                var legacySources = payload["AutoSources"] as JsonArray
                    ?? payload["autoSources"] as JsonArray;
                if (plan.Count == 0 && legacySources != null)
                {
                    var encoded = legacySources
                        .Select(static node => node?.ToString())
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(static value => value!)
                        .ToList();
                    plan = DownloadExecutionPlan.FromEncodedSources(encoded);
                    if (plan.Count > 0)
                    {
                        payload["FallbackPlan"] = JsonSerializer.SerializeToNode(plan);
                        payload["fallbackPlan"] = JsonSerializer.SerializeToNode(plan);
                        changed = true;
                    }
                }

                changed |= payload.Remove("AutoSources");
                changed |= payload.Remove("autoSources");
                changed |= payload.Remove("FallbackQueuedExternally");
                changed |= payload.Remove("fallbackQueuedExternally");
                if (changed)
                {
                    updates.Add((queueUuid, payload.ToJsonString()));
                }
            }
        }

        const string updateSql = "UPDATE download_task SET payload = @payload WHERE queue_uuid = @queueUuid;";
        foreach (var update in updates)
        {
            await using var command = new SqliteCommand(updateSql, connection);
            command.Parameters.AddWithValue("payload", update.Payload);
            command.Parameters.AddWithValue("queueUuid", update.QueueUuid);
            await command.ExecuteNonQueryAsync(cancellationToken);
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
        const string sql = UpdateDownloadTaskSqlPrefix + @"
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
    qobuz_track_id = CASE
        WHEN NULLIF(trim(COALESCE(qobuz_track_id, '')), '') IS NULL THEN lower(NULLIF(trim(COALESCE(json_extract(payload, '$.QobuzId'), json_extract(payload, '$.qobuzId'), '')), ''))
        ELSE qobuz_track_id
    END,
    tidal_track_id = CASE
        WHEN NULLIF(trim(COALESCE(tidal_track_id, '')), '') IS NULL THEN lower(NULLIF(trim(COALESCE(json_extract(payload, '$.TidalId'), json_extract(payload, '$.tidalId'), '')), ''))
        ELSE tidal_track_id
    END,
    amazon_track_id = CASE
        WHEN NULLIF(trim(COALESCE(amazon_track_id, '')), '') IS NULL THEN lower(NULLIF(trim(COALESCE(json_extract(payload, '$.AmazonId'), json_extract(payload, '$.amazonId'), '')), ''))
        ELSE amazon_track_id
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
        OR NULLIF(trim(COALESCE(qobuz_track_id, '')), '') IS NULL
        OR NULLIF(trim(COALESCE(tidal_track_id, '')), '') IS NULL
        OR NULLIF(trim(COALESCE(amazon_track_id, '')), '') IS NULL
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
CREATE INDEX IF NOT EXISTS idx_download_task_qobuz_track ON " + DownloadTaskTable + @" (qobuz_track_id);
CREATE INDEX IF NOT EXISTS idx_download_task_tidal_track ON " + DownloadTaskTable + @" (tidal_track_id);
CREATE INDEX IF NOT EXISTS idx_download_task_amazon_track ON " + DownloadTaskTable + @" (amazon_track_id);
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
        const string sql = UpdateDownloadTaskSqlPrefix + @"
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
        const string sql = UpdateDownloadTaskSqlPrefix + @"
SET deezer_track_id = NULL
WHERE lower(trim(COALESCE(deezer_track_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
SET deezer_album_id = NULL
WHERE lower(trim(COALESCE(deezer_album_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
SET deezer_artist_id = NULL
WHERE lower(trim(COALESCE(deezer_artist_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
SET spotify_track_id = NULL
WHERE lower(trim(COALESCE(spotify_track_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
SET spotify_album_id = NULL
WHERE lower(trim(COALESCE(spotify_album_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
SET spotify_artist_id = NULL
WHERE lower(trim(COALESCE(spotify_artist_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
SET apple_track_id = NULL
WHERE lower(trim(COALESCE(apple_track_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
SET apple_album_id = NULL
WHERE lower(trim(COALESCE(apple_album_id, ''))) IN ('0', '-', 'unknown', 'n/a', 'none', 'null', 'nil');

UPDATE " + DownloadTaskTable + @"
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
SET move_status = '" + MoveStatusPending + @"',
    enrichment_status = CASE
        WHEN lower(COALESCE(enrichment_status, '')) IN ('" + EnrichmentStatusCompleted + @"', '" + EnrichmentStatusNotRequired + @"') THEN enrichment_status
        ELSE '" + EnrichmentStatusPending + @"'
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE lower(status) IN ('completed', 'complete')
  AND destination_folder_id IS NOT NULL
  AND lower(COALESCE(move_status, '')) = '" + MoveStatusMoved + @"'
  AND json_valid(final_destinations_json)
  AND EXISTS (SELECT 1 FROM json_each(final_destinations_json))
  AND NOT EXISTS (
      SELECT 1
      FROM json_each(final_destinations_json)
      WHERE lower(trim(key)) <> lower(trim(CAST(value AS TEXT)))
  );

UPDATE download_task
SET move_status = CASE
        WHEN destination_folder_id IS NULL THEN '" + MoveStatusNotRequired + @"'
        ELSE '" + MoveStatusPending + @"'
    END
WHERE lower(status) IN ('completed', 'complete')
  AND (move_status IS NULL OR trim(move_status) = '');

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

            foreach (var property in document.RootElement.EnumerateObject()
                         .Where(static property => property.Value.ValueKind == JsonValueKind.String))
            {
                AddPath(property.Value.GetString(), target);
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

    public static bool HasExistingMaterializedFile(DownloadQueueItem item)
        => GetExistingMaterializedFilePaths(item).Count > 0;

    public static IReadOnlyList<string> GetExistingMaterializedFilePaths(DownloadQueueItem item)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFinalDestinationPaths(item.FinalDestinationsJson, paths);
        AddPayloadPaths(item.PayloadJson, paths);
        return paths.Where(PathExists).ToList();
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
        BindQueueIdentityParameters(command, item, string.Empty);
        command.Parameters.AddWithValue("queueOrder", (object?)item.QueueOrder ?? DBNull.Value);
        command.Parameters.AddWithValue("moveStatus", ResolveInitialMoveStatus(item));
        command.Parameters.AddWithValue("enrichmentStatus", ResolveInitialEnrichmentStatus(item));
        command.Parameters.AddWithValue("status", item.Status);
        command.Parameters.AddWithValue(PayloadParameterName, (object?)item.PayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("progress", (object?)item.Progress ?? DBNull.Value);
        command.Parameters.AddWithValue("downloaded", (object?)item.Downloaded ?? DBNull.Value);
        command.Parameters.AddWithValue("failed", (object?)item.Failed ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)item.Error ?? DBNull.Value);
    }

    private static void BindQueueIdentityParameters(SqliteCommand command, DownloadQueueItem item, string prefix)
    {
        command.Parameters.AddWithValue(prefix + "queueUuid", item.QueueUuid);
        command.Parameters.AddWithValue(prefix + "engine", item.Engine);
        command.Parameters.AddWithValue(prefix + "artistName", item.ArtistName);
        command.Parameters.AddWithValue(prefix + "trackTitle", item.TrackTitle);
        command.Parameters.AddWithValue(prefix + "isrc", (object?)NormalizeIsrc(item.Isrc) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "deezerTrackId", (object?)NormalizeId(item.DeezerTrackId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "deezerAlbumId", (object?)NormalizeId(item.DeezerAlbumId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "deezerArtistId", (object?)NormalizeId(item.DeezerArtistId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "spotifyTrackId", (object?)NormalizeId(item.SpotifyTrackId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "spotifyAlbumId", (object?)NormalizeId(item.SpotifyAlbumId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "spotifyArtistId", (object?)NormalizeId(item.SpotifyArtistId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "appleTrackId", (object?)NormalizeId(item.AppleTrackId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "appleAlbumId", (object?)NormalizeId(item.AppleAlbumId) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "appleArtistId", (object?)NormalizeId(item.AppleArtistId) ?? DBNull.Value);
        var qobuzTrackId = EngineLinkParser.NormalizeNumericTrackId(item.QobuzTrackId)
            ?? EngineLinkParser.NormalizeNumericTrackId(
                ResolvePayloadIdentity(item.PayloadJson, "QobuzId", "qobuzId", "QobuzTrackId", "qobuzTrackId"));
        command.Parameters.AddWithValue(prefix + "qobuzTrackId", (object?)qobuzTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "qobuzAlbumId", (object?)(NormalizeId(item.QobuzAlbumId) ?? ResolvePayloadIdentity(item.PayloadJson, "QobuzAlbumId", "qobuzAlbumId")) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "qobuzArtistId", (object?)(NormalizeId(item.QobuzArtistId) ?? ResolvePayloadIdentity(item.PayloadJson, "QobuzArtistId", "qobuzArtistId")) ?? DBNull.Value);
        var tidalTrackId = EngineLinkParser.NormalizeNumericTrackId(item.TidalTrackId)
            ?? EngineLinkParser.NormalizeNumericTrackId(
                ResolvePayloadIdentity(item.PayloadJson, "TidalId", "tidalId", "TidalTrackId", "tidalTrackId"));
        command.Parameters.AddWithValue(prefix + "tidalTrackId", (object?)tidalTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "tidalAlbumId", (object?)(NormalizeId(item.TidalAlbumId) ?? ResolvePayloadIdentity(item.PayloadJson, "TidalAlbumId", "tidalAlbumId")) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "tidalArtistId", (object?)(NormalizeId(item.TidalArtistId) ?? ResolvePayloadIdentity(item.PayloadJson, "TidalArtistId", "tidalArtistId")) ?? DBNull.Value);
        var amazonTrackId = EngineLinkParser.NormalizeAmazonTrackId(item.AmazonTrackId)
            ?? EngineLinkParser.NormalizeAmazonTrackId(
                ResolvePayloadIdentity(item.PayloadJson, "AmazonId", "amazonId", "AmazonTrackId", "amazonTrackId"));
        command.Parameters.AddWithValue(prefix + "amazonTrackId", (object?)amazonTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "amazonAlbumId", (object?)(NormalizeId(item.AmazonAlbumId) ?? ResolvePayloadIdentity(item.PayloadJson, "AmazonAlbumId", "amazonAlbumId")) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "amazonArtistId", (object?)(NormalizeId(item.AmazonArtistId) ?? ResolvePayloadIdentity(item.PayloadJson, "AmazonArtistId", "amazonArtistId")) ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "durationMs", (object?)item.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "destinationFolderId", (object?)item.DestinationFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "qualityRank", (object?)item.QualityRank ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "contentType", (object?)NormalizeId(item.ContentType) ?? DBNull.Value);
    }

    private static string? ResolvePayloadIdentity(string? payloadJson, params string[] keys)
    {
        var payload = QueuePreResolutionPayload.ParseOrEmpty(payloadJson);
        foreach (var key in keys)
        {
            var value = NormalizeId(payload[key]?.ToString());
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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

    private static async Task<int> GetExistingQueueOrderAsync(
        SqliteConnection connection,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT queue_order
FROM download_task
WHERE queue_uuid = @queueUuid
LIMIT 1;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull
            ? await GetNextQueueOrderAsync(connection, cancellationToken)
            : Convert.ToInt32(result);
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
        var item = new DownloadQueueItem(
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
            updatedAt,
            GetNullableString(reader, 30)
        );

        return reader.FieldCount < 40
            ? item
            : item with
            {
                QobuzTrackId = GetNullableString(reader, 31),
                QobuzAlbumId = GetNullableString(reader, 32),
                QobuzArtistId = GetNullableString(reader, 33),
                TidalTrackId = GetNullableString(reader, 34),
                TidalAlbumId = GetNullableString(reader, 35),
                TidalArtistId = GetNullableString(reader, 36),
                AmazonTrackId = EngineLinkParser.NormalizeAmazonTrackId(GetNullableString(reader, 37)),
                AmazonAlbumId = GetNullableString(reader, 38),
                AmazonArtistId = GetNullableString(reader, 39)
            };
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

    private static string ResolveInitialEnrichmentStatus(DownloadQueueItem item)
    {
        var normalized = NormalizeEnrichmentStatus(item.EnrichmentStatus);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return item.DestinationFolderId.HasValue
            ? EnrichmentStatusPending
            : EnrichmentStatusNotRequired;
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

public sealed class DuplicateLookupRequest : DownloadIdentityLookupRequest
{
    public string ArtistName { get; init; } = string.Empty;
    public int? RedownloadCooldownMinutes { get; init; }
    public string? ArtistPrimaryName { get; init; }

    public static DuplicateLookupRequest FromQueueItem(DownloadQueueItem item)
    {
        using var payloadDocument = TryParsePayload(item.PayloadJson);
        var payload = payloadDocument?.RootElement;
        return new DuplicateLookupRequest
        {
            Isrc = FirstNonEmpty(item.Isrc, ReadPayloadString(payload, "Isrc", "isrc")),
            DeezerTrackId = FirstNonEmpty(item.DeezerTrackId, ReadPayloadString(payload, "DeezerId", "deezerId", "DeezerTrackId", "deezerTrackId")),
            DeezerAlbumId = FirstNonEmpty(item.DeezerAlbumId, ReadPayloadString(payload, "DeezerAlbumId", "deezerAlbumId")),
            DeezerArtistId = FirstNonEmpty(item.DeezerArtistId, ReadPayloadString(payload, "DeezerArtistId", "deezerArtistId")),
            SpotifyTrackId = FirstNonEmpty(item.SpotifyTrackId, ReadPayloadString(payload, "SpotifyId", "spotifyId", "SpotifyTrackId", "spotifyTrackId")),
            SpotifyAlbumId = FirstNonEmpty(item.SpotifyAlbumId, ReadPayloadString(payload, "SpotifyAlbumId", "spotifyAlbumId")),
            SpotifyArtistId = FirstNonEmpty(item.SpotifyArtistId, ReadPayloadString(payload, "SpotifyArtistId", "spotifyArtistId")),
            AppleTrackId = FirstNonEmpty(item.AppleTrackId, ReadPayloadString(payload, "AppleId", "appleId", "AppleTrackId", "appleTrackId")),
            AppleAlbumId = FirstNonEmpty(item.AppleAlbumId, ReadPayloadString(payload, "AppleAlbumId", "appleAlbumId")),
            AppleArtistId = FirstNonEmpty(item.AppleArtistId, ReadPayloadString(payload, "AppleArtistId", "appleArtistId")),
            QobuzTrackId = FirstNonEmpty(item.QobuzTrackId, ReadPayloadString(payload, "QobuzId", "qobuzId", "QobuzTrackId", "qobuzTrackId")),
            TidalTrackId = FirstNonEmpty(item.TidalTrackId, ReadPayloadString(payload, "TidalId", "tidalId", "TidalTrackId", "tidalTrackId")),
            AmazonTrackId = EngineLinkParser.NormalizeAmazonTrackId(item.AmazonTrackId)
                ?? EngineLinkParser.NormalizeAmazonTrackId(ReadPayloadString(payload, "AmazonId", "amazonId", "AmazonTrackId", "amazonTrackId")),
            ArtistName = item.ArtistName,
            TrackTitle = item.TrackTitle,
            DurationMs = item.DurationMs ?? ReadPayloadInt(payload, "DurationMs", "durationMs"),
            DestinationFolderId = item.DestinationFolderId ?? ReadPayloadLong(payload, "DestinationFolderId", "destinationFolderId"),
            ContentType = FirstNonEmpty(item.ContentType, ReadPayloadString(payload, "ContentType", "contentType"))
        };
    }

    private static JsonDocument? TryParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadPayloadString(JsonElement? root, params string[] names)
    {
        if (root?.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (root.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static int? ReadPayloadInt(JsonElement? root, params string[] names)
    {
        var value = ReadPayloadLong(root, names);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static long? ReadPayloadLong(JsonElement? root, params string[] names)
    {
        if (root?.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!root.Value.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
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
    DateTimeOffset UpdatedAt,
    string? FinalDestinationsJson = null)
{
    public string? QobuzTrackId { get; init; }
    public string? QobuzAlbumId { get; init; }
    public string? QobuzArtistId { get; init; }
    public string? TidalTrackId { get; init; }
    public string? TidalAlbumId { get; init; }
    public string? TidalArtistId { get; init; }
    public string? AmazonTrackId { get; init; }
    public string? AmazonAlbumId { get; init; }
    public string? AmazonArtistId { get; init; }

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
