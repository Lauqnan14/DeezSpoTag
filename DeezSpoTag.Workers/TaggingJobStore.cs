using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace DeezSpoTag.Workers;

public sealed class TaggingJobStore
{
    private const string UpdatedAtUtcParameterName = "updatedAtUtc";
    private readonly ILogger<TaggingJobStore> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _claimGate = new(1, 1);
    private bool _schemaEnsured;
    private readonly object _schemaLock = new();

    public TaggingJobStore(IConfiguration configuration, ILogger<TaggingJobStore> logger)
    {
        _logger = logger;
        var rawConnection =
            Environment.GetEnvironmentVariable("QUEUE_DB")
            ?? configuration.GetConnectionString("Queue")
            ?? Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");

        _connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "queue.db")
            ?? throw new InvalidOperationException("Queue database connection string is not configured.");
    }

    public async Task<long> EnqueueAsync(
        string filePath,
        string? trackId,
        string operation,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var normalizedPath = NormalizePath(filePath);
        var normalizedOperation = NormalizeOperation(operation);
        var clampedMaxAttempts = Math.Clamp(maxAttempts, 1, 20);

        const string existingSql = @"
SELECT id
FROM tagging_job
WHERE file_path = @filePath
  AND operation = @operation
  AND status IN ('pending', 'in_progress')
LIMIT 1;";
        await using (var existingCommand = new SqliteCommand(existingSql, connection))
        {
            existingCommand.Parameters.AddWithValue("filePath", normalizedPath);
            existingCommand.Parameters.AddWithValue("operation", normalizedOperation);
            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is not null and not DBNull)
            {
                var existingId = Convert.ToInt64(existing);
                if (!string.IsNullOrWhiteSpace(trackId))
                {
                    const string patchSql = @"
UPDATE tagging_job
SET track_id = COALESCE(track_id, @trackId),
    max_attempts = CASE WHEN max_attempts < @maxAttempts THEN @maxAttempts ELSE max_attempts END,
    updated_at_utc = @nowUtc
WHERE id = @id;";
                    await using var patchCommand = new SqliteCommand(patchSql, connection);
                    patchCommand.Parameters.AddWithValue("trackId", trackId!.Trim());
                    patchCommand.Parameters.AddWithValue("maxAttempts", clampedMaxAttempts);
                    patchCommand.Parameters.AddWithValue("nowUtc", DateTimeOffset.UtcNow.ToString("O"));
                    patchCommand.Parameters.AddWithValue("id", existingId);
                    await patchCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                return existingId;
            }
        }

        const string insertSql = @"
INSERT INTO tagging_job
    (file_path, track_id, operation, status, attempt_count, max_attempts, next_attempt_utc, last_error, worker_id, enqueued_at_utc, started_at_utc, completed_at_utc, updated_at_utc)
VALUES
    (@filePath, @trackId, @operation, 'pending', 0, @maxAttempts, @nextAttemptUtc, NULL, NULL, @enqueuedAtUtc, NULL, NULL, @updatedAtUtc);
SELECT last_insert_rowid();";
        await using var insertCommand = new SqliteCommand(insertSql, connection);
        var nowUtc = DateTimeOffset.UtcNow.ToString("O");
        insertCommand.Parameters.AddWithValue("filePath", normalizedPath);
        insertCommand.Parameters.AddWithValue("trackId", (object?)trackId?.Trim() ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("operation", normalizedOperation);
        insertCommand.Parameters.AddWithValue("maxAttempts", clampedMaxAttempts);
        insertCommand.Parameters.AddWithValue("nextAttemptUtc", nowUtc);
        insertCommand.Parameters.AddWithValue("enqueuedAtUtc", nowUtc);
        insertCommand.Parameters.AddWithValue(UpdatedAtUtcParameterName, nowUtc);
        var inserted = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return inserted is null or DBNull ? 0 : Convert.ToInt64(inserted);
    }

    public async Task<TaggingJobRecord?> TryClaimNextAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await _claimGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            const string selectSql = @"
SELECT id, file_path, track_id, operation, status, attempt_count, max_attempts, next_attempt_utc, last_error, worker_id, enqueued_at_utc, started_at_utc, completed_at_utc, updated_at_utc
FROM tagging_job
WHERE status = 'pending'
  AND next_attempt_utc <= @nowUtc
ORDER BY next_attempt_utc ASC, enqueued_at_utc ASC
LIMIT 1;";

            TaggingJobRecord? candidate = null;
            await using (var selectCommand = new SqliteCommand(selectSql, connection, transaction))
            {
                selectCommand.Parameters.AddWithValue("nowUtc", DateTimeOffset.UtcNow.ToString("O"));
                await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    candidate = ReadRecord(reader);
                }
            }

            if (candidate is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var startedUtc = DateTimeOffset.UtcNow.ToString("O");
            const string updateSql = @"
UPDATE tagging_job
SET status = 'in_progress',
    attempt_count = attempt_count + 1,
    started_at_utc = @startedAtUtc,
    worker_id = @workerId,
    updated_at_utc = @updatedAtUtc
WHERE id = @id
  AND status = 'pending';";

            await using (var updateCommand = new SqliteCommand(updateSql, connection, transaction))
            {
                updateCommand.Parameters.AddWithValue("startedAtUtc", startedUtc);
                updateCommand.Parameters.AddWithValue(UpdatedAtUtcParameterName, startedUtc);
                updateCommand.Parameters.AddWithValue("workerId", workerId);
                updateCommand.Parameters.AddWithValue("id", candidate.Id);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            const string readSql = @"
SELECT id, file_path, track_id, operation, status, attempt_count, max_attempts, next_attempt_utc, last_error, worker_id, enqueued_at_utc, started_at_utc, completed_at_utc, updated_at_utc
FROM tagging_job
WHERE id = @id
LIMIT 1;";
            TaggingJobRecord? claimed = null;
            await using (var readCommand = new SqliteCommand(readSql, connection, transaction))
            {
                readCommand.Parameters.AddWithValue("id", candidate.Id);
                await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    claimed = ReadRecord(reader);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return claimed;
        }
        finally
        {
            _claimGate.Release();
        }
    }

    public async Task MarkCompletedAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE tagging_job
SET status = 'succeeded',
    last_error = NULL,
    completed_at_utc = @completedAtUtc,
    updated_at_utc = @updatedAtUtc
WHERE id = @id;";
        await using var command = new SqliteCommand(sql, connection);
        var nowUtc = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("completedAtUtc", nowUtc);
        command.Parameters.AddWithValue(UpdatedAtUtcParameterName, nowUtc);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        TaggingJobRecord record,
        string error,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        if (record is null || record.Id <= 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var sanitizedError = string.IsNullOrWhiteSpace(error)
            ? "Unknown tagging error."
            : error.Trim();

        if (record.AttemptCount >= record.MaxAttempts)
        {
            const string deadSql = @"
UPDATE tagging_job
SET status = 'dead_letter',
    last_error = @lastError,
    completed_at_utc = @completedAtUtc,
    updated_at_utc = @updatedAtUtc
WHERE id = @id;";
            await using var command = new SqliteCommand(deadSql, connection);
            var nowUtc = now.ToString("O");
            command.Parameters.AddWithValue("lastError", sanitizedError);
            command.Parameters.AddWithValue("completedAtUtc", nowUtc);
            command.Parameters.AddWithValue(UpdatedAtUtcParameterName, nowUtc);
            command.Parameters.AddWithValue("id", record.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var nextAttemptUtc = now.Add(retryDelay).ToString("O");
        const string retrySql = @"
UPDATE tagging_job
SET status = 'pending',
    last_error = @lastError,
    next_attempt_utc = @nextAttemptUtc,
    updated_at_utc = @updatedAtUtc
WHERE id = @id;";
        await using var retryCommand = new SqliteCommand(retrySql, connection);
        retryCommand.Parameters.AddWithValue("lastError", sanitizedError);
        retryCommand.Parameters.AddWithValue("nextAttemptUtc", nextAttemptUtc);
        retryCommand.Parameters.AddWithValue(UpdatedAtUtcParameterName, now.ToString("O"));
        retryCommand.Parameters.AddWithValue("id", record.Id);
        await retryCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RequeueStaleInProgressAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var staleThresholdUtc = DateTimeOffset.UtcNow.Subtract(staleAfter).ToString("O");
        var nowUtc = DateTimeOffset.UtcNow.ToString("O");

        const string sql = @"
UPDATE tagging_job
SET status = 'pending',
    next_attempt_utc = @nextAttemptUtc,
    worker_id = NULL,
    updated_at_utc = @updatedAtUtc
WHERE status = 'in_progress'
  AND started_at_utc IS NOT NULL
  AND started_at_utc <= @staleThresholdUtc;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("nextAttemptUtc", nowUtc);
        command.Parameters.AddWithValue(UpdatedAtUtcParameterName, nowUtc);
        command.Parameters.AddWithValue("staleThresholdUtc", staleThresholdUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        lock (_schemaLock)
        {
            if (_schemaEnsured)
            {
                return;
            }

            _schemaEnsured = true;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string schemaSql = @"
CREATE TABLE IF NOT EXISTS tagging_job (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL COLLATE NOCASE,
    track_id TEXT NULL,
    operation TEXT NOT NULL,
    status TEXT NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    max_attempts INTEGER NOT NULL DEFAULT 5,
    next_attempt_utc TEXT NOT NULL,
    last_error TEXT NULL,
    worker_id TEXT NULL,
    enqueued_at_utc TEXT NOT NULL,
    started_at_utc TEXT NULL,
    completed_at_utc TEXT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_tagging_job_poll
ON tagging_job (status, next_attempt_utc, enqueued_at_utc);

CREATE INDEX IF NOT EXISTS idx_tagging_job_started
ON tagging_job (status, started_at_utc);

CREATE UNIQUE INDEX IF NOT EXISTS ux_tagging_job_active_path_operation
ON tagging_job (file_path, operation)
WHERE status IN ('pending', 'in_progress');";

        await using var command = new SqliteCommand(schemaSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Tagging job schema ensured.");
    }

    private static TaggingJobRecord ReadRecord(SqliteDataReader reader)
    {
        return new TaggingJobRecord(
            Id: reader.GetInt64(0),
            FilePath: reader.GetString(1),
            TrackId: reader.IsDBNull(2) ? null : reader.GetString(2),
            Operation: reader.GetString(3),
            Status: reader.GetString(4),
            AttemptCount: reader.GetInt32(5),
            MaxAttempts: reader.GetInt32(6),
            NextAttemptUtc: ParseDateTimeOffset(reader, 7),
            LastError: reader.IsDBNull(8) ? null : reader.GetString(8),
            WorkerId: reader.IsDBNull(9) ? null : reader.GetString(9),
            EnqueuedAtUtc: ParseDateTimeOffset(reader, 10) ?? DateTimeOffset.MinValue,
            StartedAtUtc: ParseDateTimeOffset(reader, 11),
            CompletedAtUtc: ParseDateTimeOffset(reader, 12),
            UpdatedAtUtc: ParseDateTimeOffset(reader, 13) ?? DateTimeOffset.MinValue);
    }

    private static DateTimeOffset? ParseDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizePath(string filePath)
    {
        return Path.GetFullPath(filePath.Trim());
    }

    private static string NormalizeOperation(string operation)
    {
        var normalized = string.IsNullOrWhiteSpace(operation) ? "retag" : operation.Trim().ToLowerInvariant();
        return normalized;
    }
}

public sealed record TaggingJobRecord(
    long Id,
    string FilePath,
    string? TrackId,
    string Operation,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset? NextAttemptUtc,
    string? LastError,
    string? WorkerId,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset UpdatedAtUtc);
