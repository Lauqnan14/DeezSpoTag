using System.Globalization;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>A recorded Soulseek search.</summary>
/// <param name="SearchId">The slskd search identifier.</param>
/// <param name="QueueUuid">The download queue item this search belongs to, when it was started for one.</param>
/// <param name="SearchText">The Soulseek search text that was issued.</param>
/// <param name="Mode">Whether a person or automation ran the search.</param>
/// <param name="FileCount">Files slskd reported.</param>
/// <param name="ResponseCount">Peers that responded.</param>
/// <param name="CandidateCount">Candidates DeezSpoTag scored.</param>
/// <param name="Completed">Whether the search ran to completion.</param>
/// <param name="TimedOut">Whether the search hit its timeout.</param>
/// <param name="BestCandidateId">The winning candidate's id, when one was chosen.</param>
/// <param name="StartedAtUtc">When the search started.</param>
/// <param name="EndedAtUtc">When the search ended.</param>
/// <param name="ErrorCode">Why the search failed, when it failed to produce a usable result set.</param>
/// <param name="Error">The user-safe failure message, when the search failed.</param>
public sealed record SoulseekSearchRecord(
    Guid SearchId,
    string? QueueUuid,
    string SearchText,
    SoulseekSearchMode Mode,
    int FileCount,
    int ResponseCount,
    int CandidateCount,
    bool Completed,
    bool TimedOut,
    long? BestCandidateId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? ErrorCode = null,
    string? Error = null);

/// <summary>How a search finished, once it has finished.</summary>
/// <param name="Completed">Whether the search ran to completion.</param>
/// <param name="TimedOut">Whether the search hit its timeout.</param>
/// <param name="FileCount">How many files the peers offered in total.</param>
/// <param name="ResponseCount">How many peers answered.</param>
/// <param name="CandidateCount">How many candidates were scored.</param>
/// <param name="BestCandidateId">The winning candidate's id, when one was chosen.</param>
public sealed record SoulseekSearchCompletion(
    bool Completed,
    bool TimedOut,
    int FileCount,
    int ResponseCount,
    int CandidateCount,
    long? BestCandidateId);

/// <summary>A recorded candidate within a search.</summary>
/// <param name="Id">The candidate row id.</param>
/// <param name="SearchId">The search the candidate came from.</param>
/// <param name="Username">The peer offering the file.</param>
/// <param name="Filename">The remote filename.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="QualityCode">The normalized Soulseek quality code.</param>
/// <param name="QualityLabel">The user-facing quality label.</param>
/// <param name="TierValue">The DeezSpoTag quality tier, when known.</param>
/// <param name="CanonicalRank">The tier's canonical rank, when known.</param>
/// <param name="Score">The candidate's score.</param>
/// <param name="Accepted">Whether the candidate passed scoring and policy.</param>
/// <param name="RejectedBecause">Why it was rejected, when it was.</param>
/// <param name="CreatedAtUtc">When the candidate was recorded.</param>
public sealed record SoulseekCandidateRecord(
    long Id,
    Guid SearchId,
    string Username,
    string Filename,
    long Size,
    string QualityCode,
    string QualityLabel,
    string? TierValue,
    int? CanonicalRank,
    double Score,
    bool Accepted,
    string? RejectedBecause,

    /// <summary>The peer's upload speed in bytes per second, as slskd reported it when the peer answered.</summary>
    long PeerUploadSpeed,

    /// <summary>How many requests the peer had queued ahead of this search when it answered.</summary>
    long PeerQueueLength,

    /// <summary>Whether the peer had a free upload slot when it answered.</summary>
    bool PeerHasFreeUploadSlot,
    DateTimeOffset CreatedAtUtc);

/// <summary>A recorded transfer.</summary>
/// <param name="Id">The row id.</param>
/// <param name="QueueUuid">The download queue item this transfer serves.</param>
/// <param name="TransferId">The slskd transfer identifier.</param>
/// <param name="Username">The peer being downloaded from.</param>
/// <param name="Filename">The remote filename.</param>
/// <param name="State">The decoded slskd state label.</param>
/// <param name="IsTerminal">Whether the transfer reached a terminal state.</param>
/// <param name="IsSuccessful">Whether the transfer succeeded.</param>
/// <param name="BytesTransferred">Bytes received so far.</param>
/// <param name="Size">Total size in bytes.</param>
/// <param name="ExpectedPath">Where the completed file was expected to land.</param>
/// <param name="Verified">Whether the completed file was found and verified on disk.</param>
/// <param name="Error">The failure detail, when the transfer failed.</param>
/// <param name="UpdatedAtUtc">When the record was last written.</param>
public sealed record SoulseekTransferRecord(
    long Id,
    string? QueueUuid,
    Guid TransferId,
    string Username,
    string Filename,
    string State,
    bool IsTerminal,
    bool IsSuccessful,
    long BytesTransferred,
    long Size,
    string? ExpectedPath,
    bool Verified,
    string? Error,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
///     A reservation on one local path, held by one attempt to fetch a specific peer file.
/// </summary>
/// <remarks>
/// <para>
///     slskd and DeezSpoTag share one download root, so two operations can resolve to the same path. Size
///     alone cannot tell an arriving file from a file that was already there, which is how an unrelated download
///     gets adopted as this one. A claim is the evidence: a path may only be accepted by the attempt that
///     reserved it, and only for the peer file it was reserved for.
/// </para>
/// <para>
///     <see cref="TransferId"/> stays null between reserving the path and learning the slskd transfer that will
///     fill it. That gap is the crash window, and the claim is deliberately kept across it so a restart adopts
///     the existing reservation instead of starting a second transfer.
/// </para>
/// </remarks>
/// <param name="OwnershipId">The attempt that holds this claim.</param>
/// <param name="QueueUuid">The download queue item the attempt serves.</param>
/// <param name="Username">The peer the attempt is fetching from.</param>
/// <param name="Filename">The remote filename the path was reserved for.</param>
/// <param name="Path">The canonical local path reserved.</param>
/// <param name="TransferId">The slskd transfer attached to this claim, once it is known.</param>
public sealed record SoulseekSourcePathClaim(
    Guid OwnershipId,
    string QueueUuid,
    string Username,
    string Filename,
    string Path,
    Guid? TransferId);

/// <summary>A peer's reliability record.</summary>
/// <param name="Username">The peer's Soulseek username.</param>
/// <param name="SuccessCount">Completed transfers from this peer.</param>
/// <param name="FailureCount">Transfers from this peer that failed.</param>
/// <param name="CooldownExpiresUtc">When the peer becomes usable again, when it is in cooldown.</param>
/// <param name="LastFailureReason">The most recent failure reason.</param>
/// <param name="UpdatedAtUtc">When the record was last written.</param>
public sealed record SoulseekPeerStat(
    string Username,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset? CooldownExpiresUtc,
    string? LastFailureReason,
    DateTimeOffset UpdatedAtUtc);

/// <summary>The state of the most recent share scan.</summary>
/// <param name="IsRunning">Whether a scan is in progress.</param>
/// <param name="ShareCount">The number of shares slskd reported.</param>
/// <param name="FileCount">The number of files across shares.</param>
/// <param name="Error">The failure detail, when the scan or read failed.</param>
/// <param name="StartedAtUtc">When the scan started.</param>
/// <param name="CompletedAtUtc">When the scan finished.</param>
public sealed record SoulseekShareScanRecord(
    bool IsRunning,
    int ShareCount,
    long FileCount,
    string? Error,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>
/// Persistence for Soulseek searches, candidates, transfers, peer reliability and share scan state.
/// </summary>
/// <remarks>
/// <para>
///     Follows the repository pattern already used by <c>DownloadQueueRepository</c> and
///     <c>LibraryRepository</c>: hand-written SQL over <c>Microsoft.Data.Sqlite</c> with an
///     <c>EnsureSchemaAsync</c> step, rather than EF Core.
/// </para>
/// <para>
///     Soulseek state is keyed by <c>queue_uuid</c> and lives in its own tables. It is deliberately NOT
///     stored as columns on <c>download_task</c>: a Soulseek candidate is a peer filename, not a catalogue
///     track identity, so there is no per-engine identity to persist and adding columns there would pull
///     Soulseek into the guarded cross-engine identity contract for no benefit.
/// </para>
/// </remarks>
public sealed class SoulseekRepository
{
    private const string SearchTable = "soulseek_search";
    private const string CandidateTable = "soulseek_candidate";
    private const string TransferTable = "soulseek_transfer";
    private const string PeerStatTable = "soulseek_peer_stat";
    private const string ShareScanTable = "soulseek_share_scan_state";
    private const string SourceClaimTable = "soulseek_source_path_claim";

    // Every write path opens its statement the same way. The leading newline is
    // part of the fragment so each statement below still reads as one block.
    private const string InsertStatementPreamble = "\nINSERT INTO ";

    /// <summary>
    ///     The claim table's path column, collated to match how the platform compares file names.
    /// </summary>
    /// <remarks>
    ///     A claim is "this exact local path is mine", so the uniqueness that enforces it has to be the same
    ///     comparison the filesystem uses. Windows treats <c>Track.mp3</c> and <c>track.mp3</c> as one name, so
    ///     a case-sensitive key would let two attempts reserve the same file; Linux treats them as two, and
    ///     collapsing them there would reserve a path nobody is downloading into.
    /// </remarks>
    private static string SourceClaimPathColumn =>
        OperatingSystem.IsWindows() ? "path TEXT PRIMARY KEY COLLATE NOCASE" : "path TEXT PRIMARY KEY";

    /// <summary>
    ///     Applies once, when the schema is created.
    /// </summary>
    /// <remarks>
    ///     <c>journal_mode</c> is stored in the database file header, so it survives closing every connection.
    ///     Re-issuing it per connection is not merely redundant: it is a write that takes a lock, and the
    ///     search scorer opens a connection per newly seen peer. It is therefore set exactly once, here.
    /// </remarks>
    private const string JournalModePragma = "PRAGMA journal_mode=WAL;";

    /// <summary>
    ///     Applies on every connection.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="JournalModePragma" />, <c>synchronous</c> and <c>busy_timeout</c> are
    ///     per-connection settings in SQLite and must be re-issued to take effect, so they stay here.
    /// </remarks>
    private const string ConnectionPragmas = @"
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;";

    private readonly string _connectionString;
    private readonly ILogger<SoulseekRepository> _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;

    /// <summary>Initializes a new instance of the <see cref="SoulseekRepository"/> class.</summary>
    public SoulseekRepository(IConfiguration configuration, ILogger<SoulseekRepository> logger)
    {
        _logger = logger;
        var rawConnection =
            Environment.GetEnvironmentVariable("QUEUE_DB")
            ?? configuration.GetConnectionString("Queue")
            ?? Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");

        _connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "queue.db")
            ?? throw new InvalidOperationException("Soulseek state database connection string is not configured.");
    }

    /// <summary>Records the start of a search and returns its row id.</summary>
    public async Task<long> RecordSearchStartedAsync(
        Guid searchId,
        string searchText,
        SoulseekSearchMode mode,
        string? queueUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = InsertStatementPreamble + SearchTable + @" (search_id, queue_uuid, search_text, mode, started_at_utc, is_completed, is_timed_out)
VALUES (@searchId, @queueUuid, @searchText, @mode, @startedAt, 0, 0)
ON CONFLICT(search_id) DO UPDATE SET
    queue_uuid = excluded.queue_uuid,
    search_text = excluded.search_text,
    mode = excluded.mode,
    started_at_utc = excluded.started_at_utc;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("searchId", searchId.ToString("D"));
        command.Parameters.AddWithValue("queueUuid", (object?)queueUuid ?? DBNull.Value);
        command.Parameters.AddWithValue("searchText", searchText);
        command.Parameters.AddWithValue("mode", mode.ToString());
        command.Parameters.AddWithValue("startedAt", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await ResolveSearchRowIdAsync(connection, searchId, cancellationToken);
    }

    /// <summary>Updates a search's terminal state and its candidate counts.</summary>
    public async Task RecordSearchCompletedAsync(
        Guid searchId,
        SoulseekSearchCompletion completion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE " + SearchTable + @"
SET is_completed = @completed,
    is_timed_out = @timedOut,
    file_count = @fileCount,
    response_count = @responseCount,
    candidate_count = @candidateCount,
    best_candidate_id = @bestCandidateId,
    ended_at_utc = @endedAt
WHERE search_id = @searchId;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("completed", completion.Completed);
        command.Parameters.AddWithValue("timedOut", completion.TimedOut);
        command.Parameters.AddWithValue("fileCount", completion.FileCount);
        command.Parameters.AddWithValue("responseCount", completion.ResponseCount);
        command.Parameters.AddWithValue("candidateCount", completion.CandidateCount);
        command.Parameters.AddWithValue("bestCandidateId", (object?)completion.BestCandidateId ?? DBNull.Value);
        command.Parameters.AddWithValue("endedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("searchId", searchId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Records that a search ended without producing a usable result set because reading its responses failed.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     This deliberately does not clear the candidates, and deliberately does not mark the search completed or
    ///     timed out. Peers did answer - the counts seen while polling are kept, because they are the only record
    ///     that this was a real search - but the files they offered were never scored, so reporting the search as
    ///     finished would claim a result set nobody ever saw.
    /// </para>
    /// <para>
    ///     Callers pass user-safe text. The exception that caused this belongs in the log, not in a payload the UI
    ///     renders.
    /// </para>
    /// </remarks>
    public async Task RecordSearchFailedAsync(
        Guid searchId,
        int fileCount,
        int responseCount,
        string errorCode,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
UPDATE " + SearchTable + @"
SET is_completed = 0,
    is_timed_out = 0,
    file_count = @fileCount,
    response_count = @responseCount,
    error_code = @errorCode,
    error = @error,
    ended_at_utc = @endedAt
WHERE search_id = @searchId;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("fileCount", fileCount);
        command.Parameters.AddWithValue("responseCount", responseCount);
        command.Parameters.AddWithValue("errorCode", errorCode);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("endedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("searchId", searchId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads a recorded search.</summary>
    public async Task<SoulseekSearchRecord?> GetSearchAsync(Guid searchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT search_id, queue_uuid, search_text, mode, file_count, response_count, candidate_count,
       is_completed, is_timed_out, best_candidate_id, started_at_utc, ended_at_utc, error_code, error
FROM " + SearchTable + @"
WHERE search_id = @searchId;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("searchId", searchId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSearch(reader) : null;
    }

    /// <summary>Lists the searches recorded for a download queue item, newest first.</summary>
    public async Task<IReadOnlyList<SoulseekSearchRecord>> GetSearchesForQueueAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT search_id, queue_uuid, search_text, mode, file_count, response_count, candidate_count,
       is_completed, is_timed_out, best_candidate_id, started_at_utc, ended_at_utc, error_code, error
FROM " + SearchTable + @"
WHERE queue_uuid = @queueUuid
ORDER BY started_at_utc DESC;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SoulseekSearchRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSearch(reader));
        }

        return results;
    }

    /// <summary>Deletes searches that finished before <paramref name="olderThanUtc"/>.</summary>
    /// <returns>The number of searches deleted.</returns>
    public async Task<int> DeleteSearchesOlderThanAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM " + SearchTable + " WHERE started_at_utc < @cutoff;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", Format(olderThanUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Replaces the candidates recorded for a search and returns their row ids.</summary>
    public async Task<IReadOnlyList<long>> ReplaceCandidatesAsync(
        Guid searchId,
        IReadOnlyList<SoulseekCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var delete = new SqliteCommand("DELETE FROM " + CandidateTable + " WHERE search_id = @searchId;", connection))
        {
            delete.Parameters.AddWithValue("searchId", searchId.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var ids = new List<long>(candidates.Count);
        const string insert = InsertStatementPreamble + CandidateTable + @"
    (search_id, username, filename, size_bytes, quality_code, quality_label, tier_value,
     canonical_rank, score, is_accepted, rejected_because, peer_upload_speed, peer_queue_length,
     peer_has_free_upload_slot, created_at_utc)
VALUES
    (@searchId, @username, @filename, @size, @qualityCode, @qualityLabel, @tierValue,
     @canonicalRank, @score, @accepted, @rejectedBecause, @peerUploadSpeed, @peerQueueLength,
     @peerHasFreeUploadSlot, @createdAt)
RETURNING id;";

        foreach (var candidate in candidates)
        {
            await using var command = new SqliteCommand(insert, connection);
            command.Parameters.AddWithValue("searchId", searchId.ToString("D"));
            command.Parameters.AddWithValue("username", candidate.Username);
            command.Parameters.AddWithValue("filename", candidate.Filename);
            command.Parameters.AddWithValue("size", candidate.Raw.Size);
            command.Parameters.AddWithValue("qualityCode", candidate.Quality);
            command.Parameters.AddWithValue("qualityLabel", candidate.QualityLabel);
            command.Parameters.AddWithValue("tierValue", (object?)candidate.TierValue ?? DBNull.Value);
            command.Parameters.AddWithValue("canonicalRank", (object?)candidate.CanonicalRank ?? DBNull.Value);
            command.Parameters.AddWithValue("score", candidate.Score);
            command.Parameters.AddWithValue("accepted", candidate.Accepted);
            command.Parameters.AddWithValue("rejectedBecause", (object?)candidate.RejectedBecause ?? DBNull.Value);
            command.Parameters.AddWithValue("peerUploadSpeed", candidate.Raw.PeerUploadSpeed);
            command.Parameters.AddWithValue("peerQueueLength", candidate.Raw.PeerQueueLength);
            command.Parameters.AddWithValue("peerHasFreeUploadSlot", candidate.Raw.PeerHasFreeUploadSlot);
            command.Parameters.AddWithValue("createdAt", Format(DateTimeOffset.UtcNow));

            var id = await command.ExecuteScalarAsync(cancellationToken);
            if (id is not null)
            {
                ids.Add(Convert.ToInt64(id, CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }

    /// <summary>Lists the candidates recorded for a search, best score first.</summary>
    public async Task<IReadOnlyList<SoulseekCandidateRecord>> GetCandidatesAsync(
        Guid searchId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, search_id, username, filename, size_bytes, quality_code, quality_label, tier_value,
       canonical_rank, score, is_accepted, rejected_because, peer_upload_speed, peer_queue_length,
       peer_has_free_upload_slot, created_at_utc
FROM " + CandidateTable + @"
WHERE search_id = @searchId
ORDER BY score DESC, id ASC;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("searchId", searchId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SoulseekCandidateRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SoulseekCandidateRecord(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                await ReadNullableStringAsync(reader, 7, cancellationToken),
                await ReadNullableInt32Async(reader, 8, cancellationToken),
                reader.GetDouble(9),
                reader.GetBoolean(10),
                await ReadNullableStringAsync(reader, 11, cancellationToken),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetBoolean(14),
                ParseDate(reader.GetString(15))));
        }

        return results;
    }

    /// <summary>Inserts or updates a transfer record and returns its row id.</summary>
    public async Task<long> UpsertTransferAsync(
        SoulseekTransferStatus status,
        string? queueUuid,
        string? expectedPath,
        bool verified,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = InsertStatementPreamble + TransferTable + @"
    (transfer_id, queue_uuid, username, filename, state, is_terminal, is_successful,
     bytes_transferred, size_bytes, expected_path, is_verified, error, updated_at_utc)
VALUES
    (@transferId, @queueUuid, @username, @filename, @state, @isTerminal, @isSuccessful,
     @bytesTransferred, @size, @expectedPath, @verified, @error, @updatedAt)
ON CONFLICT(transfer_id) DO UPDATE SET
    queue_uuid = excluded.queue_uuid,
    username = excluded.username,
    filename = excluded.filename,
    state = excluded.state,
    is_terminal = excluded.is_terminal,
    is_successful = excluded.is_successful,
    bytes_transferred = excluded.bytes_transferred,
    size_bytes = excluded.size_bytes,
    expected_path = COALESCE(excluded.expected_path, " + TransferTable + @".expected_path),
    is_verified = excluded.is_verified,
    error = excluded.error,
    updated_at_utc = excluded.updated_at_utc
RETURNING id;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("transferId", status.TransferId?.ToString("D") ?? Guid.Empty.ToString("D"));
        command.Parameters.AddWithValue("queueUuid", (object?)queueUuid ?? DBNull.Value);
        command.Parameters.AddWithValue("username", status.Username);
        command.Parameters.AddWithValue("filename", status.Filename);
        command.Parameters.AddWithValue("state", status.State);
        command.Parameters.AddWithValue("isTerminal", status.IsTerminal);
        command.Parameters.AddWithValue("isSuccessful", status.IsSuccessful);
        command.Parameters.AddWithValue("bytesTransferred", status.BytesTransferred);
        command.Parameters.AddWithValue("size", status.Size);
        command.Parameters.AddWithValue("expectedPath", (object?)expectedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("verified", verified);
        command.Parameters.AddWithValue("error", (object?)status.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("updatedAt", Format(DateTimeOffset.UtcNow));

        var id = await command.ExecuteScalarAsync(cancellationToken);
        return id is null ? 0 : Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a recorded transfer.</summary>
    public async Task<SoulseekTransferRecord?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, transfer_id, username, filename, state, is_terminal, is_successful,
       bytes_transferred, size_bytes, expected_path, is_verified, error, updated_at_utc
FROM " + TransferTable + @"
WHERE transfer_id = @transferId;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("transferId", transferId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTransfer(reader) : null;
    }

    /// <summary>Lists the transfers recorded for a download queue item.</summary>
    public async Task<IReadOnlyList<SoulseekTransferRecord>> GetTransfersForQueueAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT id, queue_uuid, transfer_id, username, filename, state, is_terminal, is_successful,
       bytes_transferred, size_bytes, expected_path, is_verified, error, updated_at_utc
FROM " + TransferTable + @"
WHERE queue_uuid = @queueUuid
ORDER BY updated_at_utc DESC;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SoulseekTransferRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadTransfer(reader));
        }

        return results;
    }

    /// <summary>
    ///     Deletes transfer records that finished before <paramref name="olderThanUtc"/>.
    /// </summary>
    /// <remarks>
    ///     Only terminal records are eligible. An in-flight transfer must never be forgotten, or a running
    ///     download would lose the ability to be reconciled.
    /// </remarks>
    public async Task<int> DeleteTerminalTransfersOlderThanAsync(
        DateTimeOffset olderThanUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
DELETE FROM " + TransferTable + @"
WHERE is_terminal = 1
  AND updated_at_utc < @cutoff;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", Format(olderThanUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Forgets one terminal transfer, so a removed download stops appearing in local history.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     The terminal test is in the mutation, not only in the caller. A transfer that is still running holds a
    ///     file slskd is fetching and a claim another attempt may be relying on, so forgetting it here would strand
    ///     both. The caller re-reads state for a readable answer, and this makes the delete itself refuse a row that
    ///     stopped being terminal in between.
    /// </para>
    /// <para>
    ///     This deletes history only. The audio on disk, the queue item, its enrichment state and any source path
    ///     claim belong to other tables and other decisions, and are deliberately untouched.
    /// </para>
    /// </remarks>
    /// <returns>Whether a row was removed. False means there was no terminal row with that transfer id.</returns>
    public async Task<bool> DeleteTerminalTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
DELETE FROM " + TransferTable + @"
WHERE transfer_id = @transferId
  AND is_terminal = 1;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("transferId", transferId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    ///     Reserves every path one attempt will fetch into, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     All or nothing, in a single transaction. Half a reservation would leave an attempt holding one path it
    ///     can verify and refusing another it needs, and the missing one would be exactly the path a peer was
    ///     asked to fill.
    /// </para>
    /// <para>
    ///     A path already holding a file is refused even if nothing has claimed it. There is no evidence the file
    ///     belongs to this attempt, and adopting it on size alone is the failure this whole mechanism exists to
    ///     prevent.
    /// </para>
    /// </remarks>
    /// <returns>True when every path is now held by this attempt.</returns>
    public async Task<bool> TryClaimSourcePathsAsync(
        Guid ownershipId,
        string queueUuid,
        string username,
        string filename,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Deduplicated on the canonical form first, so one operation cannot collide with itself over a path that
        // reaches it written two ways.
        var canonical = new List<string>();
        foreach (var normalized in paths
            .Select(path => TryCanonicalizeSourcePath(path, out var canonicalPath) ? canonicalPath : null)
            .Where(normalized => normalized is not null && !canonical.Contains(normalized, SourcePathComparer)))
        {
            canonical.Add(normalized!);
        }

        if (canonical.Count == 0)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var path in canonical)
        {
            // A path this owner already holds is re-asserted rather than treated as a collision. One download
            // operation may try several peers in turn, and the next attempt reserves the same paths for the same
            // operation; the reservation belongs to the operation, so re-taking it is a no-op rather than a
            // conflict. A claim held by any other owner is a real collision and stops everything.
            await using (var reassert = new SqliteCommand(
                @"
UPDATE " + SourceClaimTable + @"
SET queue_uuid = @queueUuid,
    username = @username,
    filename = @filename
WHERE path = @path
  AND ownership_id = @ownershipId
  AND transfer_id IS NULL;",
                connection,
                transaction))
            {
                reassert.Parameters.AddWithValue("path", path);
                reassert.Parameters.AddWithValue("ownershipId", ownershipId.ToString("D"));
                reassert.Parameters.AddWithValue("queueUuid", queueUuid);
                reassert.Parameters.AddWithValue("username", username);
                reassert.Parameters.AddWithValue("filename", filename);

                if (await reassert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
                {
                    continue;
                }
            }

            if (File.Exists(path))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using var command = new SqliteCommand(
                InsertStatementPreamble + SourceClaimTable + @" (path, ownership_id, queue_uuid, username, filename, transfer_id)
VALUES (@path, @ownershipId, @queueUuid, @username, @filename, NULL);",
                connection,
                transaction);
            command.Parameters.AddWithValue("path", path);
            command.Parameters.AddWithValue("ownershipId", ownershipId.ToString("D"));
            command.Parameters.AddWithValue("queueUuid", queueUuid);
            command.Parameters.AddWithValue("username", username);
            command.Parameters.AddWithValue("filename", filename);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Another operation already holds this path. It is not stolen, and this attempt claims nothing.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Ties every claim one attempt holds to the slskd transfer that will fill them.
    /// </summary>
    /// <remarks>
    ///     Only claims this attempt already owns are updated. A claim that has been released and re-taken by
    ///     somebody else in the meantime belongs to them, and overwriting its transfer id would point their
    ///     monitoring at this transfer.
    /// </remarks>
    public async Task AttachSourceClaimsAsync(Guid ownershipId, Guid transferId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var check = new SqliteCommand("SELECT COUNT(*) FROM " + SourceClaimTable
            + " WHERE ownership_id = @owner AND transfer_id IS NOT NULL AND transfer_id <> @transfer;", connection, transaction);
        check.Parameters.AddWithValue("owner", ownershipId.ToString("D"));
        check.Parameters.AddWithValue("transfer", transferId.ToString("D"));
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) != 0)
            throw new InvalidOperationException("The source paths are already attached to a different transfer.");
        await using var command = new SqliteCommand("UPDATE " + SourceClaimTable
            + " SET transfer_id = @transfer WHERE ownership_id = @owner;", connection, transaction);
        command.Parameters.AddWithValue("owner", ownershipId.ToString("D"));
        command.Parameters.AddWithValue("transfer", transferId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("No reserved source paths were available to attach.");
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Lists the path claims held for a download queue item.</summary>
    public Task<IReadOnlyList<SoulseekSourcePathClaim>> GetSourceClaimsForQueueAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
        => ReadSourceClaimsAsync(BuildSourceClaimsForQueueSql(), "queueUuid", queueUuid, cancellationToken);

    /// <summary>Lists the path claims attached to one slskd transfer.</summary>
    public async Task<IReadOnlyList<SoulseekSourcePathClaim>> GetSourceClaimsForTransferAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
        => await ReadSourceClaimsAsync(BuildSourceClaimsForTransferSql(), "transferId", transferId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    ///     Gives up every path claim held by one attempt.
    /// </summary>
    /// <remarks>
    ///     Called only when the attempt is certain it is finished with the paths: the remote transfer is gone with
    ///     nothing usable left behind, or the file was promoted or rejected. Everything else keeps the claim, so a
    ///     file that may still be arriving cannot be claimed by a second operation and read as its own.
    /// </remarks>
    /// <returns>How many claims were released.</returns>
    public async Task ReleaseSourceClaimsAsync(Guid ownershipId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM " + SourceClaimTable + " WHERE ownership_id = @ownershipId;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("ownershipId", ownershipId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SoulseekSourcePathClaim>> ReadSourceClaimsAsync(
        string query,
        string parameterName,
        string parameterValue,
        CancellationToken cancellationToken)
    {
        var values = new List<SoulseekSourcePathClaim>();
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue(parameterName, parameterValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadSourceClaim(reader));
        }

        return values;
    }

    private static string BuildSourceClaimsForQueueSql()
        => @"
SELECT ownership_id, queue_uuid, username, filename, path, transfer_id
FROM " + SourceClaimTable + @"
WHERE queue_uuid = @queueUuid;";

    private static string BuildSourceClaimsForTransferSql()
        => @"
SELECT ownership_id, queue_uuid, username, filename, path, transfer_id
FROM " + SourceClaimTable + @"
WHERE transfer_id = @transferId;";

    // Not const: the claim table's path column is collated per platform, so the statement is built at
    // runtime. Every other part of the schema is still a single literal.
    private static string BuildSchemaDdlSql()
        => @"
CREATE TABLE IF NOT EXISTS " + SearchTable + @" (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    search_id TEXT NOT NULL UNIQUE,
    queue_uuid TEXT,
    search_text TEXT NOT NULL,
    mode TEXT NOT NULL,
    file_count INTEGER NOT NULL DEFAULT 0,
    response_count INTEGER NOT NULL DEFAULT 0,
    candidate_count INTEGER NOT NULL DEFAULT 0,
    is_completed INTEGER NOT NULL DEFAULT 0,
    is_timed_out INTEGER NOT NULL DEFAULT 0,
    best_candidate_id INTEGER,
    error_code TEXT,
    error TEXT,
    started_at_utc TEXT NOT NULL,
    ended_at_utc TEXT
);
CREATE TABLE IF NOT EXISTS " + CandidateTable + @" (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    search_id TEXT NOT NULL,
    username TEXT NOT NULL,
    filename TEXT NOT NULL,
    size_bytes INTEGER NOT NULL DEFAULT 0,
    quality_code TEXT NOT NULL,
    quality_label TEXT NOT NULL,
    tier_value TEXT,
    canonical_rank INTEGER,
    score REAL NOT NULL DEFAULT 0,
    is_accepted INTEGER NOT NULL DEFAULT 0,
    rejected_because TEXT,
    peer_upload_speed INTEGER NOT NULL DEFAULT 0,
    peer_queue_length INTEGER NOT NULL DEFAULT 0,
    peer_has_free_upload_slot INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS " + TransferTable + @" (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    transfer_id TEXT NOT NULL UNIQUE,
    queue_uuid TEXT,
    username TEXT NOT NULL,
    filename TEXT NOT NULL,
    state TEXT NOT NULL,
    is_terminal INTEGER NOT NULL DEFAULT 0,
    is_successful INTEGER NOT NULL DEFAULT 0,
    bytes_transferred INTEGER NOT NULL DEFAULT 0,
    size_bytes INTEGER NOT NULL DEFAULT 0,
    expected_path TEXT,
    is_verified INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    updated_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS " + PeerStatTable + @" (
    username TEXT PRIMARY KEY,
    success_count INTEGER NOT NULL DEFAULT 0,
    failure_count INTEGER NOT NULL DEFAULT 0,
    cooldown_expires_utc TEXT,
    last_failure_reason TEXT,
    updated_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS " + ShareScanTable + @" (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    is_running INTEGER NOT NULL DEFAULT 0,
    share_count INTEGER NOT NULL DEFAULT 0,
    file_count INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    started_at_utc TEXT,
    completed_at_utc TEXT
);
CREATE TABLE IF NOT EXISTS " + SourceClaimTable + @" (
    " + SourceClaimPathColumn + @",
    ownership_id TEXT NOT NULL,
    queue_uuid TEXT NOT NULL,
    username TEXT NOT NULL,
    filename TEXT NOT NULL,
    transfer_id TEXT
);
CREATE INDEX IF NOT EXISTS idx_soulseek_search_queue ON " + SearchTable + @" (queue_uuid);
CREATE INDEX IF NOT EXISTS idx_soulseek_candidate_search ON " + CandidateTable + @" (search_id);
CREATE INDEX IF NOT EXISTS idx_soulseek_transfer_queue ON " + TransferTable + @" (queue_uuid);
CREATE INDEX IF NOT EXISTS idx_soulseek_transfer_terminal ON " + TransferTable + @" (is_terminal, updated_at_utc);
CREATE INDEX IF NOT EXISTS idx_soulseek_claim_queue ON " + SourceClaimTable + @" (queue_uuid);
CREATE INDEX IF NOT EXISTS idx_soulseek_claim_transfer ON " + SourceClaimTable + @" (transfer_id);
CREATE INDEX IF NOT EXISTS idx_soulseek_claim_owner ON " + SourceClaimTable + @" (ownership_id);";

    private static SoulseekSourcePathClaim ReadSourceClaim(SqliteDataReader reader) => new(
        Guid.TryParse(reader.GetString(0), out var ownershipId) ? ownershipId : Guid.Empty,
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) || !Guid.TryParse(reader.GetString(5), out var transferId)
            ? null
            : transferId);

    /// <summary>
    ///     The one form of a local path that can be stored and compared, or false when it is not a path at all.
    /// </summary>
    /// <remarks>
    ///     Absolute, normalised and without a trailing separator, so the same file reached through a relative path,
    ///     a <c>.</c> component or a doubled separator collapses to one claim rather than becoming three claims
    ///     on one file.
    /// </remarks>
    private static bool TryCanonicalizeSourcePath(string? path, out string canonical)
    {
        canonical = string.Empty;
        var trimmed = (path ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(trimmed);
            var withoutTrailingSeparator = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            canonical = withoutTrailingSeparator.Length == 0 ? full : withoutTrailingSeparator;
            return canonical.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>How two canonical paths are compared, matching the claim table's collation.</summary>
    private static StringComparer SourcePathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Reads a peer's reliability record.</summary>
    public async Task<SoulseekPeerStat?> GetPeerStatAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT username, success_count, failure_count, cooldown_expires_utc, last_failure_reason, updated_at_utc
FROM " + PeerStatTable + @"
WHERE username = @username;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SoulseekPeerStat(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            await ReadNullableDateAsync(reader, 3, cancellationToken),
            await ReadNullableStringAsync(reader, 4, cancellationToken),
            ParseDate(reader.GetString(5)));
    }

    /// <summary>Records a successful transfer from a peer, clearing any cooldown.</summary>
    public async Task RecordPeerSuccessAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = InsertStatementPreamble + PeerStatTable + @" (username, success_count, failure_count, cooldown_expires_utc, last_failure_reason, updated_at_utc)
VALUES (@username, 1, 0, NULL, NULL, @updatedAt)
ON CONFLICT(username) DO UPDATE SET
    success_count = " + PeerStatTable + @".success_count + 1,
    cooldown_expires_utc = NULL,
    last_failure_reason = NULL,
    updated_at_utc = excluded.updated_at_utc;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("updatedAt", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Records a transfer failure and puts the peer into cooldown.
    /// </summary>
    /// <remarks>
    ///     Repeat offences extend the cooldown, so a peer that fails repeatedly is avoided for longer without
    ///     needing a separate policy table.
    /// </remarks>
    public async Task RecordPeerFailureAsync(
        string username,
        string reason,
        TimeSpan cooldown,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = InsertStatementPreamble + PeerStatTable + @" (username, success_count, failure_count, cooldown_expires_utc, last_failure_reason, updated_at_utc)
VALUES (@username, 0, 1, @expires, @reason, @updatedAt)
ON CONFLICT(username) DO UPDATE SET
    failure_count = " + PeerStatTable + @".failure_count + 1,
    cooldown_expires_utc = MAX(
        COALESCE(" + PeerStatTable + @".cooldown_expires_utc, excluded.cooldown_expires_utc),
        excluded.cooldown_expires_utc),
    last_failure_reason = excluded.last_failure_reason,
    updated_at_utc = excluded.updated_at_utc;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("expires", Format(DateTimeOffset.UtcNow.Add(cooldown)));
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("updatedAt", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Lists the peers currently in cooldown.</summary>
    public async Task<IReadOnlyList<SoulseekPeerStat>> GetPeersInCooldownAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT username, success_count, failure_count, cooldown_expires_utc, last_failure_reason, updated_at_utc
FROM " + PeerStatTable + @"
WHERE cooldown_expires_utc IS NOT NULL
  AND cooldown_expires_utc > @now
ORDER BY cooldown_expires_utc ASC;";

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("now", Format(DateTimeOffset.UtcNow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SoulseekPeerStat>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SoulseekPeerStat(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                await ReadNullableDateAsync(reader, 3, cancellationToken),
                await ReadNullableStringAsync(reader, 4, cancellationToken),
                ParseDate(reader.GetString(5))));
        }

        return results;
    }

    /// <summary>Removes cooldowns that have already expired.</summary>
    public async Task<int> DeleteExpiredCooldownsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
DELETE FROM " + PeerStatTable + @"
WHERE cooldown_expires_utc IS NOT NULL
  AND cooldown_expires_utc <= @now;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("now", Format(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records the start of a share scan.</summary>
    public async Task RecordShareScanStartedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = InsertStatementPreamble + ShareScanTable + @" (id, is_running, share_count, file_count, started_at_utc)
VALUES (1, 1, 0, 0, @startedAt)
ON CONFLICT(id) DO UPDATE SET
    is_running = 1,
    error = NULL,
    started_at_utc = excluded.started_at_utc,
    completed_at_utc = NULL;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("startedAt", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records the outcome of a share scan.</summary>
    public async Task RecordShareScanCompletedAsync(
        int shareCount,
        long fileCount,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = InsertStatementPreamble + ShareScanTable + @" (id, is_running, share_count, file_count, error, started_at_utc, completed_at_utc)
VALUES (1, 0, @shareCount, @fileCount, @error, @startedAt, @completedAt)
ON CONFLICT(id) DO UPDATE SET
    is_running = 0,
    share_count = excluded.share_count,
    file_count = excluded.file_count,
    error = excluded.error,
    completed_at_utc = excluded.completed_at_utc;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("shareCount", shareCount);
        command.Parameters.AddWithValue("fileCount", fileCount);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("startedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("completedAt", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads the current share scan state.</summary>
    public async Task<SoulseekShareScanRecord?> GetShareScanStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = @"
SELECT is_running, share_count, file_count, error, started_at_utc, completed_at_utc
FROM " + ShareScanTable + @"
WHERE id = 1;";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SoulseekShareScanRecord(
            reader.GetBoolean(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            await ReadNullableStringAsync(reader, 3, cancellationToken),
            await ReadNullableDateAsync(reader, 4, cancellationToken),
            await ReadNullableDateAsync(reader, 5, cancellationToken));
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var connection = await OpenRawConnectionAsync(cancellationToken);
            await using (var journal = new SqliteCommand(JournalModePragma, connection))
            {
                await journal.ExecuteNonQueryAsync(cancellationToken);
            }

            var sql = BuildSchemaDdlSql();

            await using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // CREATE TABLE IF NOT EXISTS leaves an existing table exactly as it was, so a database written
            // before these columns existed would silently keep dropping them. The peer facts below are what a
            // search response carries and a directory listing does not, and the drawer reads them back off a
            // persisted search - without the columns they arrive as zero and the header claims a peer is idle
            // and slow when it was neither.
            await SqliteSchemaUtils.EnsureColumnAsync(connection, CandidateTable, "peer_upload_speed", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await SqliteSchemaUtils.EnsureColumnAsync(connection, CandidateTable, "peer_queue_length", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await SqliteSchemaUtils.EnsureColumnAsync(connection, CandidateTable, "peer_has_free_upload_slot", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

            // A search whose results could not be read is a different thing from a search that found nothing,
            // and only the search table can keep them apart across a reload. Without these columns the drawer
            // reclassifies it as "no peers responded" from a search that had peers answering moments earlier.
            await SqliteSchemaUtils.EnsureColumnAsync(connection, SearchTable, "error_code", "TEXT", cancellationToken);
            await SqliteSchemaUtils.EnsureColumnAsync(connection, SearchTable, "error", "TEXT", cancellationToken);

            _schemaEnsured = true;
        }
        catch (SqliteException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to create the Soulseek state schema.");
            }

            throw;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task<long> ResolveSearchRowIdAsync(
        SqliteConnection connection,
        Guid searchId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(
            "SELECT id FROM " + SearchTable + " WHERE search_id = @searchId;",
            connection);
        command.Parameters.AddWithValue("searchId", searchId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static SoulseekSearchRecord ReadSearch(SqliteDataReader reader) => new(
        Guid.TryParse(reader.GetString(0), out var searchId) ? searchId : Guid.Empty,
        ReadNullableString(reader, 1),
        reader.GetString(2),
        Enum.TryParse<SoulseekSearchMode>(reader.GetString(3), out var mode) ? mode : SoulseekSearchMode.Manual,
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetBoolean(7),
        reader.GetBoolean(8),
        ReadNullableInt64(reader, 9),
        ParseDate(reader.GetString(10)),
        ReadNullableDate(reader, 11),
        ReadNullableString(reader, 12),
        ReadNullableString(reader, 13));

    private static SoulseekTransferRecord ReadTransfer(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        ReadNullableString(reader, 1),
        Guid.TryParse(reader.GetString(2), out var transferId) ? transferId : Guid.Empty,
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetBoolean(6),
        reader.GetBoolean(7),
        reader.GetInt64(8),
        reader.GetInt64(9),
        ReadNullableString(reader, 10),
        reader.GetBoolean(11),
        ReadNullableString(reader, 12),
        ParseDate(reader.GetString(13)));

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<string?> ReadNullableStringAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);

    private static async Task<int?> ReadNullableInt32Async(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt32(ordinal);

    private static async Task<DateTimeOffset?> ReadNullableDateAsync(SqliteDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken)
            ? null
            : ParseDate(reader.GetString(ordinal));

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        return await OpenRawConnectionAsync(cancellationToken);
    }

    /// <summary>
    ///     Opens a configured connection without touching the schema.
    /// </summary>
    /// <remarks>
    ///     <see cref="EnsureSchemaAsync"/> must use this rather than <see cref="OpenConnectionAsync"/>, or
    ///     the two would call each other forever.
    /// </remarks>
    private async Task<SqliteConnection> OpenRawConnectionAsync(CancellationToken cancellationToken)
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
        await using (var pragma = new SqliteCommand(ConnectionPragmas, connection))
        {
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }
}
