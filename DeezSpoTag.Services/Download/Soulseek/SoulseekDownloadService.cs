using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Drives one Soulseek transfer from enqueue to a verified local file.
/// </summary>
public interface ISoulseekDownloadService
{
    /// <summary>
    ///     Runs a Soulseek download: search if needed, enqueue the transfer, follow it to completion, and
    ///     verify the finished file.
    /// </summary>
    /// <param name="request">The resolved work.</param>
    /// <param name="queueUuid">The download queue item, for progress reporting and the transfer record.</param>
    /// <param name="expectedPath">Where the finished file is expected to land.</param>
    /// <param name="progress">Optional progress reporter, called with percentage and bytes per second.</param>
    /// <param name="cancellationToken">Cancellation token. Cancelling asks slskd to cancel the transfer.</param>
    /// <returns>The verified local file.</returns>
    /// <remarks>
    ///     A completed slskd transfer is not a completed DeezSpoTag download. This method only returns once the
    ///     file exists on disk and is non-empty; a transfer that finished with no usable file is an error, so
    ///     the queue can never record a phantom success.
    /// </remarks>
    Task<SoulseekCompletedFile> DownloadAsync(
        SoulseekDownloadRequest request,
        string queueUuid,
        string expectedPath,
        Func<double, double, Task>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Promotes a verified file to the path the rest of the pipeline expects.
    /// </summary>
    /// <returns>The path the caller should use from here on.</returns>
    Task<string> PromoteAcceptedAudioAsync(SoulseekQueueItem payload, string acquiredPath, CancellationToken cancellationToken);

    /// <summary>Deletes a file that failed verification so it cannot be mistaken for a good download.</summary>
    Task DeleteRejectedStagingAudio(SoulseekQueueItem payload, string rejectedPath, CancellationToken cancellationToken);
}

/// <summary>
///     The default <see cref="ISoulseekDownloadService"/>.
/// </summary>
/// <remarks>
///     <para>
///         slskd and DeezSpoTag share the global download root. This service verifies the completed source there
///         and then promotes it to the path persisted by the shared template pipeline.
///     </para>
/// </remarks>
public sealed class SoulseekDownloadService : ISoulseekDownloadService
{

    private readonly ISoulseekSearchService _search;
    private readonly ISoulseekTransferService _transfer;
    private readonly ISoulseekCredentialProvider _credentials;
    private readonly ISoulseekPeerPolicyService _peerPolicy;
    private readonly ISoulseekResultScoringService _scoring;
    private readonly SoulseekRepository _repository;
    private readonly ISoulseekConnectionService? _connection;
    private readonly ILogger<SoulseekDownloadService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekDownloadService"/> class.</summary>
    public SoulseekDownloadService(
        ISoulseekSearchService search,
        ISoulseekTransferService transfer,
        ISoulseekCredentialProvider credentials,
        ISoulseekPeerPolicyService peerPolicy,
        ISoulseekResultScoringService scoring,
        SoulseekRepository repository,
        ILogger<SoulseekDownloadService> logger,
        ISoulseekConnectionService? connection = null)
    {
        _search = search;
        _transfer = transfer;
        _credentials = credentials;
        _peerPolicy = peerPolicy;
        _scoring = scoring;
        _repository = repository;
        _logger = logger;

        // Optional so a host that registers the engine without the Soulseek network still resolves it. Absent
        // means no eligibility answer, and then the gate below is skipped rather than guessed at.
        _connection = connection;
    }

    /// <inheritdoc />
    public async Task<SoulseekCompletedFile> DownloadAsync(
        SoulseekDownloadRequest request,
        string queueUuid,
        string expectedPath,
        Func<double, double, Task>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ArgumentException.ThrowIfNullOrWhiteSpace(queueUuid);

        var failedPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? lastFailure = null;
        var requestIsPinned = HasPinnedCandidate(request);
        var maximumAttempts = requestIsPinned ? 1 : MaxPeerAttempts;

        // One ownership id for the whole operation, not per attempt. The destination path belongs to this queue item
        // rather than to one peer's try, and several attempts may in turn reserve the same path: a fresh id per
        // attempt would make the second attempt collide with the first one's own reservation and refuse the download
        // for a reason that has nothing to do with the peer.
        var ownershipId = Guid.NewGuid();

        // Only unpinned track requests may search again after a peer failure. A file the reader selected
        // is attempted once; its original failure is returned without substituting another peer or file.
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An exact reader selection is a promise, not a hint. It is tried once and never replaced by a
            // different peer. Automatic items can retry other peers up to the normal attempt limit.
            var pinned = requestIsPinned;
            try
            {
                return await AttemptAsync(
                        request,
                        queueUuid,
                        expectedPath,
                        progress,
                        pinned,
                        failedPeers,
                        ownershipId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!requestIsPinned && SoulseekPinnedCandidatePolicy.ShouldSearchAgain(ex))
            {
                // Neither a cancelled item, an unattached Soulseek nor "nobody on the network has this track"
                // is a peer's fault, and none of them has another peer to ask.
                if (ex is SoulseekNoMatchException or SoulseekUnavailableException)
                {
                    throw;
                }

                lastFailure = ex;

                var peer = PeerOf(ex);
                if (peer is not null)
                {
                    failedPeers.Add(peer);
                }

                _logger.LogWarning(
                    ex,
                    "Soulseek attempt {Attempt} of {Max} failed for queue {QueueUuid} (peer {Peer}).",
                    attempt,
                    maximumAttempts,
                    queueUuid,
                    peer ?? "unknown");

                if (attempt == maximumAttempts)
                {
                    break;
                }
            }
        }

        // A monitoring failure says the app lost the transfer, not that the network could not supply it. Its
        // message is the diagnosis, so it travels as itself rather than being flattened into the generic line
        // below, which claims every peer was tried and none of them delivered.
        if (lastFailure is SoulseekTransferMonitoringException monitoringFailure)
        {
            _logger.LogError(
                monitoringFailure,
                "Soulseek lost track of the transfer for queue {QueueUuid}.",
                queueUuid);
            throw monitoringFailure;
        }

        // The item's error is one line under its title, so it gets one short sentence and no internals. The
        // peers and the transport's words are the diagnosis, so they go to the log here and stay on the item's
        // history, where a reader with a log open can use them.
        _logger.LogError(
            lastFailure,
            "Soulseek could not deliver the file for queue {QueueUuid} after {Attempts} peer attempt(s); peers={Peers}.",
            queueUuid,
            maximumAttempts,
            failedPeers.Count == 0 ? "none identified" : string.Join(", ", failedPeers));

        throw new InvalidOperationException("Soulseek could not deliver the file.", lastFailure);
    }

    /// <summary>
    ///     How many peers one item may try before it is a failure.
    /// </summary>
    /// <remarks>
    ///     Soulseek peers are volunteers on consumer lines, so a transfer that dies part way is ordinary
    ///     rather than exceptional. Three peers clears a genuinely bad one without turning a track nobody has
    ///     into a long search.
    /// </remarks>
    private const int MaxPeerAttempts = 3;

    /// <summary>
    ///     The peer named in a transfer failure, so the next attempt knows to avoid it.
    /// </summary>
    private static string? PeerOf(Exception failure)
        => failure is SoulseekTransferException transfer ? transfer.Username : null;

    /// <summary>Whether the request names the exact peer file the reader chose.</summary>
    private static bool HasPinnedCandidate(SoulseekDownloadRequest request)
        => !string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(request.RemotePath);

    /// <summary>
    ///     One download attempt: resolve a candidate - the reader's file, or a fresh search - and transfer it.
    /// </summary>
    private async Task<SoulseekCompletedFile> AttemptAsync(
        SoulseekDownloadRequest request,
        string queueUuid,
        string expectedPath,
        Func<double, double, Task>? progress,
        bool pinned,
        IReadOnlySet<string> excludedPeers,
        Guid ownershipId,
        CancellationToken cancellationToken)
    {
        // Before anything touches the network. A transfer this queue already owns is still running in slskd,
        // or finished and left a file nobody collected, and in both cases the right move is to watch that
        // transfer rather than to search again and start a second one for the same track.
        var recovered = await FindRecoverableTransferAsync(request, queueUuid, cancellationToken).ConfigureAwait(false);
        if (recovered is not null)
        {
            _logger.LogInformation(
                "Resuming recorded Soulseek transfer {TransferId} for queue {QueueUuid} ({Username}/{Filename}) instead of enqueueing again.",
                recovered.TransferId,
                queueUuid,
                recovered.Username,
                recovered.Filename);

            return await WatchRecoveredTransferAsync(
                    request,
                    queueUuid,
                    recovered,
                    expectedPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var (username, remotePath, remoteSize) = await ResolveCandidateAsync(
                request,
                queueUuid,
                ignorePinnedCandidate: !pinned,
                excludedPeers,
                cancellationToken)
            .ConfigureAwait(false);

        _ = await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SoulseekUnavailableException("Soulseek is not configured.");

        // Re-checked here, immediately before the one call that makes slskd fetch something new.
        //
        // Reaching this point means no recorded transfer was adopted, so there is nothing to keep monitoring and
        // no file on disk that this attempt is responsible for. That is what makes it safe to refuse: refusing
        // here costs nothing that recovery would have saved. Refusing any earlier - before the recovery read, or
        // in the monitoring loop - would throw away a transfer that is already running.
        var eligibility = _connection is null
            ? null
            : await _connection.GetEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (eligibility is { IsUsable: false })
        {
            _logger.LogInformation(
                "Not starting a new Soulseek transfer for queue {QueueUuid}: {Reason}",
                queueUuid,
                eligibility.Message);

            throw new SoulseekLoginRequiredException(eligibility.Message);
        }

        // The ownership id was minted once for the whole operation, so a later peer attempt reserves
        // the same paths as the same owner rather than colliding with its own earlier reservation.

        // Reserved before anything is asked of the network. The peer file maps to one canonical path under the
        // shared root, and this attempt may only accept a file at a path it holds: a path that already holds an
        // unrelated file, or that another operation is filling, is refused here rather than being verified later.
        var claims = await ReserveSourcePathsAsync(
                ownershipId,
                queueUuid,
                username,
                remotePath,
                request.CompletedDownloadsRoot,
                expectedPath,
                cancellationToken)
            .ConfigureAwait(false);

        // Repeated immediately before the one call that makes slskd fetch something new. The first reservation
        // protects concurrent app attempts, while another process can still put a file here. Checking again
        // narrows that window, because the reservation
        // is only worth anything if the path was genuinely empty when the peer was asked to fill it.
        if (!await PathsAreStillReservableAsync(claims, cancellationToken).ConfigureAwait(false))
        {
            await ReleaseClaimsAsync(ownershipId, queueUuid, username, remotePath, cancellationToken).ConfigureAwait(false);
            throw new SoulseekTransferMonitoringException(
                $"Soulseek did not start a transfer for \"{remotePath}\" from {username} on queue {queueUuid}: its destination path was taken before the download could be enqueued.");
        }

        // slskd answers the enqueue request after a short internal wait, and a slow peer makes it miss that
        // wait. It then says nothing useful in either of two ways: a server error, or a plain success whose
        // answer carries no transfer in it. Neither is a refusal - the transfer it created keeps running - so
        // both converge on the same recovery: read the transfer list and adopt the transfer that is really in
        // flight. That is what lets the download watch it, report its progress and verify it.
        Guid? transferId;
        try
        {
            transferId = await _transfer
                .EnqueueAsync(username, remotePath, remoteSize, cancellationToken)
                .ConfigureAwait(false);

            if (transferId is null)
            {
                _logger.LogInformation(
                    "slskd enqueued \"{RemotePath}\" from {Username} for queue {QueueUuid} without reporting a transfer; looking for one it already started.",
                    remotePath,
                    username,
                    queueUuid);

                transferId = await AdoptStartedTransferAsync(username, remotePath, queueUuid, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SoulseekUnavailableException)
        {
            _logger.LogWarning(
                ex,
                "slskd could not confirm a transfer for queue {QueueUuid}; looking for one it already started.",
                queueUuid);

            transferId = await AdoptStartedTransferAsync(username, remotePath, queueUuid, cancellationToken)
                .ConfigureAwait(false);
            if (transferId is null)
            {
                if (ex is SoulseekTransferException) throw;
                throw new SoulseekTransferMonitoringException(
                    "slskd could not confirm the enqueue outcome; source paths remain reserved and no new transfer was started.", ex);
            }
        }

        if (transferId is null)
        {
            // There is deliberately no disk check here any more. A file at the reserved path cannot be attributed
            // to this attempt from its existence alone - the peer id slskd refused to confirm is the only link - so
            // accepting one would let an unrelated download be consumed as this one. The claim is kept: the
            // transfer slskd may have started is still unaccounted for, and releasing the path here would let a
            // second attempt fetch the same file while that one runs.
            _logger.LogWarning(
                "slskd started no identifiable transfer for \"{RemotePath}\" from {Username} on queue {QueueUuid}; keeping the reserved paths so the transfer it may have started is not duplicated.",
                remotePath,
                username,
                queueUuid);

            throw new SoulseekTransferMonitoringException(
                $"slskd did not report which transfer was started for \"{remotePath}\" from {username}, so the reserved paths cannot be safely attached or released.");
        }

        _logger.LogInformation(
            "Soulseek transfer {TransferId} started for queue {QueueUuid}: {Username}/{RemotePath}.",
            transferId.Value,
            queueUuid,
            username,
            remotePath);

        // The claims are attached to this transfer before anything is waited on, so the completion check has
        // ownership evidence from its first poll rather than from the moment it happens to notice a file.
        await AttachClaimsAsync(ownershipId, transferId.Value, queueUuid, username, remotePath, cancellationToken)
            .ConfigureAwait(false);

        // Ownership is recorded here, before anything is waited on. A transfer is otherwise only written at a
        // terminal outcome, so an app that stops mid-transfer leaves slskd downloading a file that no row in this
        // app mentions: nothing to resume, nothing to clean up, and the next attempt starts a second transfer for
        // a file that is already arriving. This row is what a later run reads to pick the transfer back up.
        await RecordOwnershipAsync(request, queueUuid, transferId.Value, username, remotePath, remoteSize, expectedPath, cancellationToken)
            .ConfigureAwait(false);

        // A success clears any earlier penalty; a failure sets one. This is what makes cooldowns reflect
        // reality rather than a fixed guess about a peer.
        try
        {
            var completed = await _transfer
                .WaitForCompletionAsync(
                    username,
                    transferId.Value,
                    expectedPath,
                    queueUuid,
                    request.CompletedDownloadsRoot,
                    progress,
                    cancellationToken,
                    request.HostStoppingToken)
                .ConfigureAwait(false);

            if (completed is null)
            {
                // The transfer service already recorded the unverified outcome, so all that is left here is to
                // penalise the peer and say what actually happened. "Reported as complete but no file" is only
                // one of the ways this ends, and it is the least likely: a transfer that stalls at a third of
                // the file is the common one, so the last known state is read back and reported as it was.
                await _peerPolicy.RecordFailureAsync(username, "unverified_transfer", CancellationToken.None)
                    .ConfigureAwait(false);

                var last = await _transfer
                    .GetStatusAsync(username, transferId.Value, CancellationToken.None)
                    .ConfigureAwait(false);

                throw new SoulseekTransferException(
                    DescribeUnverifiedTransfer(last),
                    username);
            }

            // Tie the verified row to the queue item so the download history can be read back by uuid.
            await _repository.UpsertTransferAsync(
                    new SoulseekTransferStatus(
                        transferId,
                        username,
                        completed.Filename,
                        "completed",
                        IsTerminal: true,
                        IsSuccessful: true,
                        completed.Size,
                        completed.Size,
                        0,
                        PlaceInQueue: null,
                        Progress: 100),
                    queueUuid,
                    expectedPath,
                    verified: true,
                    cancellationToken)
                .ConfigureAwait(false);

            await _peerPolicy.RecordSuccessAsync(username, cancellationToken).ConfigureAwait(false);
            return completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SoulseekUnavailableException)
        {
            // Losing hold of a transfer is this app's problem, not the peer's. Charging the peer for it would
            // cool down a volunteer that is sending the file perfectly well. A login refusal never reaches here
            // at all: it is thrown before this try block, so there is nothing here to exclude.
            if (ex is not SoulseekTransferMonitoringException)
            {
                await _peerPolicy
                    .RecordFailureAsync(username, ex.GetType().Name, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> PromoteAcceptedAudioAsync(SoulseekQueueItem payload, string acquiredPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ownership = await RequireOwnedAudioAsync(payload, acquiredPath, cancellationToken).ConfigureAwait(false);
        // The destination is read before anything is written, because the payload is updated to the promoted
        // path at the end and reading it afterwards would compare the file with itself.
        var destination = ResolveDestination(payload.FilePath);
        var source = DownloadPathResolver.ResolveIoPath(acquiredPath);

        if (string.IsNullOrWhiteSpace(destination))
        {
            // No expected path was persisted, so there is nowhere to promote to. The verified file stands.
            payload.AcquiredAudioPath = acquiredPath;
            payload.AudioAcquired = true;
            payload.FilePath = acquiredPath;
            await FinalizeOwnedAudioAsync(ownership, acquiredPath, cancellationToken).ConfigureAwait(false);
            return acquiredPath;
        }

        if (PathsEqual(source, destination))
        {
            payload.AcquiredAudioPath = destination;
            payload.AudioAcquired = true;
            payload.FilePath = destination;
            await FinalizeOwnedAudioAsync(ownership, destination, cancellationToken).ConfigureAwait(false);
            return destination;
        }

        if (!File.Exists(source))
        {
            throw new InvalidOperationException($"Soulseek could not promote a file that is not there: {source}");
        }

        if (File.Exists(destination))
        {
            throw new InvalidOperationException(
                $"Soulseek will not replace the file already at {destination}, so the download was not promoted.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // A move is atomic and costs nothing, so it is tried first. It fails when the two paths are on different
        // filesystems, which is the normal case when slskd has its own download volume.
        try
        {
            File.Move(source, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(source) && !File.Exists(destination))
        {
            CopyThenPublish(source, destination);
        }

        // Only now, with the file in place, is the payload pointed at it.
        payload.AcquiredAudioPath = destination;
        payload.AudioAcquired = true;
        payload.FilePath = DownloadPathResolver.NormalizeDisplayPath(destination);

        await FinalizeOwnedAudioAsync(ownership, destination, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    /// <summary>
    ///     Publishes a copy of the file and removes the source, for when a move cannot cross filesystems.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The order is the whole point: copy to a temporary name in the destination directory, check the copy
    ///         is the same length as the source, rename it into place, and only then delete the source. A failure
    ///         at any step leaves the source alone, because the source is the only verified copy of the download.
    ///     </para>
    ///     <para>
    ///         <paramref name="truncateTo" /> exists so the verification step can be exercised; nothing in
    ///         production passes it.
    ///     </para>
    /// </remarks>
    private string CopyThenPublish(string source, string destination, int? truncateTo = null)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        if (File.Exists(destination))
        {
            throw new IOException($"Destination already exists: {destination}");
        }

        var temporary = Path.Join(
            directory,
            $"{Path.GetFileName(destination)}.{Guid.NewGuid():N}{PromotionTempSuffix}");

        try
        {
            var expectedLength = new FileInfo(source).Length;
            if (truncateTo is { } limit)
            {
                File.WriteAllBytes(temporary, new byte[limit]);
            }
            else
            {
                File.Copy(source, temporary, overwrite: false);
            }

            // Flushed and closed before it is measured, so the length that is checked is the length on disk.
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

            var copiedLength = new FileInfo(temporary).Length;
            if (copiedLength != expectedLength)
            {
                throw new IOException(
                    $"The copy of {source} is {copiedLength} bytes against {expectedLength} in the source.");
            }

            File.Move(temporary, destination, overwrite: false);
        }
        catch
        {
            TryDeleteTemporary(temporary);
            throw;
        }

        if (truncateTo is null)
        {
            File.Delete(source);
        }

        return destination;
    }

    /// <summary>
    ///     Removes a half-written promotion copy, and says so when it could not.
    /// </summary>
    /// <remarks>
    ///     The promotion has already failed by the time this runs, so the copy is not what decides the outcome.
    ///     Logging it is what turns "a stray .deezspotag-promote-tmp file is in your downloads folder" from a
    ///     mystery into something a reader can match to the item that caused it.
    /// </remarks>
    private void TryDeleteTemporary(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Could not remove the incomplete Soulseek promotion copy at {Path}.", temporary);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Could not remove the incomplete Soulseek promotion copy at {Path}.", temporary);
            }
        }
    }

    /// <summary>The suffix that marks a promotion copy in flight.</summary>
    private const string PromotionTempSuffix = ".deezspotag-promote-tmp";

    private static string ResolveDestination(string? filePath)
    {
        var configured = (filePath ?? string.Empty).Trim();
        return configured.Length == 0 ? string.Empty : DownloadPathResolver.ResolveIoPath(configured);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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

    /// <inheritdoc />
    public async Task DeleteRejectedStagingAudio(SoulseekQueueItem payload, string rejectedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownership = await RequireOwnedAudioAsync(payload, rejectedPath, cancellationToken).ConfigureAwait(false);
        File.Delete(rejectedPath);
        await _repository.UpsertTransferAsync(new SoulseekTransferStatus(ownership.TransferId, ownership.Username,
            ownership.Filename, "rejected", true, false, ownership.BytesTransferred, ownership.Size, 0, null, 0,
            "Audio failed verification."), payload.Id, rejectedPath, false, cancellationToken).ConfigureAwait(false);
        await ReleaseTransferClaimsAsync(ownership.TransferId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SoulseekTransferRecord> RequireOwnedAudioAsync(SoulseekQueueItem payload, string path, CancellationToken token)
    {
        if (!Guid.TryParse(payload.SoulseekTransferId, out var transferId))
            throw new SoulseekTransferMonitoringException("The audio has no recorded Soulseek transfer owner.");
        var record = await _repository.GetTransferAsync(transferId, token).ConfigureAwait(false);
        var claims = await _repository.GetSourceClaimsForTransferAsync(transferId, token).ConfigureAwait(false);
        if (record is null || !string.Equals(record.QueueUuid, payload.Id, StringComparison.Ordinal)
            || !string.Equals(record.Username, payload.SoulseekUsername, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.Filename, payload.SoulseekRemotePath, StringComparison.OrdinalIgnoreCase)
            || !(record.Verified && PathsEqual(record.ExpectedPath ?? string.Empty, path)
                || claims.Any(claim => claim.QueueUuid == payload.Id
                    && string.Equals(claim.Username, record.Username, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(claim.Filename, record.Filename, StringComparison.OrdinalIgnoreCase)
                    && PathsEqual(claim.Path, path))))
            throw new SoulseekTransferMonitoringException("The audio path does not belong to this Soulseek queue item.");
        return record;
    }

    private async Task FinalizeOwnedAudioAsync(SoulseekTransferRecord ownership, string path, CancellationToken token)
    {
        var size = new FileInfo(path).Length;
        await _repository.UpsertTransferAsync(new SoulseekTransferStatus(ownership.TransferId, ownership.Username,
            ownership.Filename, "completed", true, true, size, size, 0, null, 100), ownership.QueueUuid, path, true, token)
            .ConfigureAwait(false);
        await ReleaseTransferClaimsAsync(ownership.TransferId, token).ConfigureAwait(false);
    }

    private async Task ReleaseTransferClaimsAsync(Guid transferId, CancellationToken token)
    {
        foreach (var owner in (await _repository.GetSourceClaimsForTransferAsync(transferId, token).ConfigureAwait(false))
            .Select(claim => claim.OwnershipId).Distinct())
            await _repository.ReleaseSourceClaimsAsync(owner, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Returns the peer and filename to download, searching when nothing was pre-selected.
    /// </summary>
    private async Task<(string Username, string RemotePath, long Size)> ResolveCandidateAsync(
        SoulseekDownloadRequest request,
        string queueUuid,
        bool ignorePinnedCandidate,
        IReadOnlySet<string> excludedPeers,
        CancellationToken cancellationToken)
    {
        if (!ignorePinnedCandidate && HasPinnedCandidate(request))
        {
            return (request.Username, request.RemotePath, request.RemoteSizeBytes);
        }

        // Automated queueing always searches in strict mode; a person driving the item gets the looser one so
        // they can see more and retry a weak match deliberately.
        var mode = request.Automation
            ? SoulseekSearchMode.Automated
            : SoulseekSearchMode.Manual;

        // The step's own quality travels with the search, so a FLAC rung cannot be satisfied by an MP3 that
        // happens to be the best-scoring file on the network.
        var outcome = await _search
            .SearchAsync(request.Target, mode, request.QueueUuid, cancellationToken, request.SoulseekQuality)
            .ConfigureAwait(false);

        // A peer that already failed this item is not asked again: the next attempt exists precisely to find
        // a different one, and re-asking the peer that just died at a third of the file is how an item ends.
        var eligible = outcome.Candidates
            .Where(candidate => !excludedPeers.Contains(candidate.Username))
            .ToList();
        var usable = _scoring.SelectBest(eligible);

        if (usable is null)
        {
            _logger.LogWarning(
                "{Diagnosis} Queue {QueueUuid}.",
                BuildNoMatchDiagnosis(request, outcome, excludedPeers),
                request.QueueUuid);

            throw new SoulseekNoMatchException("Soulseek had no matching copy.");
        }

        return (usable.Username, usable.Filename, usable.Raw.Size);
    }

    /// <summary>
    ///     Why nothing matched, for the log.
    /// </summary>
    /// <remarks>
    ///     Which rejections came up, how many candidates were seen and which peers were already tried are what
    ///     make a no-match diagnosable, and they are also what made this the longest string on the item. So this
    ///     text is written to the log and the item gets the one fact the reader needs.
    /// </remarks>
    private static string BuildNoMatchDiagnosis(
        SoulseekDownloadRequest request,
        SoulseekSearchOutcome outcome,
        IReadOnlySet<string> excludedPeers)
    {
        var reason = outcome.Candidates
            .Where(candidate => !candidate.Accepted && !string.IsNullOrWhiteSpace(candidate.RejectedBecause))
            .GroupBy(candidate => candidate.RejectedBecause!)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} ({group.Count()})")
            .Take(3)
            .ToArray();

        var detail = reason.Length == 0 ? "no reason recorded" : string.Join(", ", reason);
        var skipped = excludedPeers.Count == 0
            ? "none"
            : string.Join(", ", excludedPeers);
        return $"No Soulseek candidate passed matching for \"{request.Target.Artist} - {request.Target.Title}\" "
            + $"({outcome.Candidates.Count} seen, {outcome.ResponseCount} peers responded). "
            + $"Closest rejections: {detail}. Peers already tried: {skipped}.";
    }

    /// <summary>
    ///     The transfer's own outcome in a few words, for the item's single error line.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A stalled transfer and a transfer that claims to have finished and left nothing behind are
    ///         different problems, and both used to be reported as "no file was found" - which is what sent this
    ///         looking for a mapping failure. How far it got is the part the reader can act on, so that part
    ///         stays, and everything else - the peer's name, the remote path, the transport's error - goes to the
    ///         log, because this line is rendered under an item's title in a list.
    ///     </para>
    ///     <para>
    ///         The signature is the guard: the line is built from the transfer alone, so it cannot carry the
    ///         internals that used to be in it.
    ///     </para>
    /// </remarks>
    private static string DescribeUnverifiedTransfer(SoulseekTransferStatus? last)
    {
        if (last is null)
        {
            return "Soulseek lost track of the transfer.";
        }

        if (last.IsSuccessful)
        {
            return "Soulseek finished but left no file.";
        }

        return last.Progress <= 0
            ? "Soulseek peer sent no data."
            : $"Soulseek peer stopped at {last.Progress:0.#}%.";
    }

    /// <summary>
    ///     Waits briefly for slskd to show a transfer it has already started for this peer file.
    /// </summary>
    /// <remarks>
    ///     The enqueue request and the transfer list race each other: slskd can create the transfer and then
    ///     fail to answer the request that asked for it. A short wait is enough to catch that, and it is what
    ///     turns a slow peer from a failed item into a watched one.
    /// </remarks>
    private async Task<Guid?> AdoptStartedTransferAsync(string username, string remotePath, string queueUuid, CancellationToken cancellationToken)
        => (await FindStartedTransferAsync(username, remotePath, queueUuid, cancellationToken).ConfigureAwait(false))?.TransferId;

    private async Task<SoulseekTransferStatus?> FindStartedTransferAsync(
        string username,
        string remotePath,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < TransferAdoptionPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var started = await _transfer.FindTransferAsync(username, remotePath, cancellationToken).ConfigureAwait(false);
            if (started?.TransferId is { } id)
            {
                _logger.LogInformation(
                    "Adopted Soulseek transfer {TransferId} for queue {QueueUuid} that slskd had already started.",
                    id,
                    queueUuid);
                return started;
            }

            await Task.Delay(TransferAdoptionPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>How many times the transfer list is read while waiting for slskd to show its transfer.</summary>
    private const int TransferAdoptionPolls = 6;

    /// <summary>The gap between those reads.</summary>
    private static readonly TimeSpan TransferAdoptionPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Watches a transfer this queue already owns, instead of starting another one.
    /// </summary>
    /// <remarks>
    ///     The recorded expected path and size are used rather than this attempt's, because they are what the
    ///     interrupted run was actually fetching. A legacy row with neither is not guessed at: the file it might
    ///     refer to is unknown, so inventing an identity for it would verify the wrong file.
    /// </remarks>
    private async Task<SoulseekCompletedFile> WatchRecoveredTransferAsync(
        SoulseekDownloadRequest request,
        string queueUuid,
        SoulseekTransferRecord recovered,
        string expectedPath,
        Func<double, double, Task>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recovered.ExpectedPath) || string.IsNullOrWhiteSpace(recovered.Filename))
        {
            // Nothing to verify against, so there is no safe way to claim this transfer produced a file.
            throw new SoulseekTransferMonitoringException(
                "A recorded Soulseek transfer cannot be verified because it has no recorded path or file name.");
        }

        var completed = await _transfer
            .WaitForCompletionAsync(
                recovered.Username,
                recovered.TransferId,
                recovered.ExpectedPath,
                queueUuid,
                request.CompletedDownloadsRoot,
                progress,
                cancellationToken,
                request.HostStoppingToken)
            .ConfigureAwait(false);

        if (completed is not null)
        {
            await _repository.UpsertTransferAsync(
                    new SoulseekTransferStatus(
                        recovered.TransferId,
                        recovered.Username,
                        completed.Filename,
                        "completed",
                        IsTerminal: true,
                        IsSuccessful: true,
                        completed.Size,
                        completed.Size,
                        0,
                        PlaceInQueue: null,
                        Progress: 100),
                    queueUuid,
                    expectedPath,
                    verified: true,
                    cancellationToken)
                .ConfigureAwait(false);

            await _peerPolicy.RecordSuccessAsync(recovered.Username, cancellationToken).ConfigureAwait(false);
            return completed;
        }

        // The transfer really did end without a file. That is the transfer's own outcome, so it goes back
        // through the normal request policy rather than being reported as a monitoring problem.
        throw new SoulseekTransferException(
            $"Soulseek transfer {recovered.TransferId} from {recovered.Username} finished without producing the file.",
            recovered.Username);
    }

    /// <summary>
    ///     The paths one attempt reserves before it asks a peer for anything.
    /// </summary>
    /// <remarks>
    ///     Both places the file can legitimately land are claimed: the staging path this queue item expects, and the
    ///     canonical path under the shared root where slskd actually writes a completed file. Only one is ever the
    ///     destination, but which one depends on slskd's own layout, so both are reserved and verification resolves
    ///     to whichever exists.
    /// </remarks>
    private sealed record SoulseekReservedPaths(IReadOnlyList<string> Paths);

    /// <summary>
    ///     Reserves the paths this attempt will download into, refusing when any of them is already taken.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The canonical source path is resolved through the transfer service's own resolver rather than a
    ///         second copy of the last-directory/filename mapping, so the path reserved here is the exact path that
    ///         will later be verified. Two copies of that mapping would eventually disagree, and a claim on one path
    ///         with verification on another is precisely the hole this closes.
    ///     </para>
    ///     <para>
    ///         A path that cannot be resolved from the peer's remote path is not a reason to refuse the download: the
    ///         staging path is still claimed, and the transfer can still succeed. It only means the completed file
    ///         cannot be attributed if it arrives somewhere other than where it was expected.
    ///     </para>
    /// </remarks>
    private async Task<SoulseekReservedPaths> ReserveSourcePathsAsync(
        Guid ownershipId,
        string queueUuid,
        string username,
        string remotePath,
        string? completedDownloadsRoot,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();

        if (!string.IsNullOrWhiteSpace(expectedPath))
        {
            paths.Add(expectedPath);
        }

        var source = SoulseekTransferService.ResolveSourcePath(remotePath, completedDownloadsRoot);
        if (!string.IsNullOrWhiteSpace(source) && !paths.Any(path => PathsEqual(path, source)))
        {
            paths.Add(source);
        }

        if (paths.Count == 0)
        {
            throw new SoulseekTransferMonitoringException(
                "Soulseek could not work out where the completed file will be written, so no ownership could be recorded for it.");
        }

        bool reserved;
        try
        {
            reserved = await _repository.TryClaimSourcePathsAsync(
                    ownershipId,
                    queueUuid,
                    username,
                    remotePath,
                    paths,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure to write the reservation must never be treated as "nothing was there". Claiming the path
            // after the fact would race whoever does have it, so this stops the attempt instead.
            throw OwnershipLost(ex);
        }

        if (!reserved)
        {
            _logger.LogInformation(
                "Soulseek did not reserve {Paths} for \"{RemotePath}\" from {Username} on queue {QueueUuid}: the path is already taken or already holds a file.",
                string.Join(", ", paths),
                remotePath,
                username,
                queueUuid);

            throw new SoulseekTransferMonitoringException(
                $"The Soulseek download destination for \"{remotePath}\" from {username} is already in use, so this download was not started.");
        }

        _logger.LogInformation(
            "Reserved Soulseek download path(s) {Paths} for queue {QueueUuid}: {Username}/{RemotePath}.",
            string.Join(", ", paths),
            queueUuid,
            username,
            remotePath);

        return new SoulseekReservedPaths(paths);
    }

    /// <summary>
    ///     Whether every reserved path is still free, re-checked immediately before the enqueue.
    /// </summary>
    /// <remarks>
    ///     The existence check is repeated rather than trusted from the reservation. A claim proves the path was free
    ///     when it was taken; it says nothing about a file another program - slskd itself, another engine, or the
    ///     user - has since put there.
    /// </remarks>
    private static Task<bool> PathsAreStillReservableAsync(SoulseekReservedPaths reservation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(reservation.Paths.All(path => !File.Exists(path)));
    }

    /// <summary>
    ///     Ties this attempt's claims to the slskd transfer that will fill them.
    /// </summary>
    /// <remarks>
    ///     The claims are validated against the peer file before they are attached. An attachment that does not match
    ///     means the reservation belongs to a different attempt, and overwriting it would point that attempt's
    ///     monitoring at this transfer - two operations then reading each other's file.
    /// </remarks>
    private async Task AttachClaimsAsync(
        Guid ownershipId,
        Guid transferId,
        string queueUuid,
        string username,
        string remotePath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SoulseekSourcePathClaim> claims;
        try
        {
            claims = await _repository.GetSourceClaimsForQueueAsync(queueUuid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw OwnershipLost(ex);
        }

        var mine = claims.Where(claim => claim.OwnershipId == ownershipId).ToList();
        if (mine.Count == 0
            || mine.Any(claim => !string.Equals(claim.Username, username, StringComparison.OrdinalIgnoreCase)
                                  || !PathsEqual(claim.Filename, remotePath)))
        {
            throw new SoulseekTransferMonitoringException(
                $"The reserved Soulseek download paths for queue {queueUuid} no longer match {username}/{remotePath}, so they were not attached to transfer {transferId}.");
        }

        try
        {
            await _repository.AttachSourceClaimsAsync(ownershipId, transferId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw OwnershipLost(ex);
        }
    }

    /// <summary>
    ///     Gives up this attempt's claims, for the one case where the attempt is certain it never started anything.
    /// </summary>
    /// <remarks>
    ///     Only called when the enqueue is known not to have happened. Everything else keeps the claims, because a
    ///     file that may still be arriving must not be claimable by a second operation.
    /// </remarks>
    private async Task ReleaseClaimsAsync(
        Guid ownershipId,
        string queueUuid,
        string username,
        string remotePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await _repository.ReleaseSourceClaimsAsync(ownershipId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failing to release is a leak, not a wrong answer. The download itself is already being refused, and
            // reporting this as the reason would point at the wrong thing.
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "Could not release the Soulseek path claims for queue {QueueUuid} ({Username}/{RemotePath}).",
                    queueUuid,
                    username,
                    remotePath);
            }
        }
    }

    /// <summary>
    ///     Writes the association between a queue item and the transfer now running for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately not terminal and not verified. This records that the transfer exists and what it is
    ///         fetching; claiming an outcome here would be a claim nothing has checked yet.
    ///     </para>
    ///     <para>
    ///         A failure to write it is a monitoring failure, not a peer failure. slskd is already fetching the
    ///         file, so the only honest answers are to carry on watching this transfer or to report that ownership
    ///         was lost - and asking a second peer for the same track would duplicate a download in progress.
    ///     </para>
    /// </remarks>
    private async Task RecordOwnershipAsync(
        SoulseekDownloadRequest request,
        string queueUuid,
        Guid transferId,
        string username,
        string remotePath,
        long remoteSize,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        var association = new SoulseekTransferStatus(
            transferId,
            username,
            remotePath,
            "enqueued",
            IsTerminal: false,
            IsSuccessful: false,
            0,
            remoteSize,
            0,
            PlaceInQueue: null,
            Progress: 0);

        try
        {
            await _repository.UpsertTransferAsync(association, queueUuid, expectedPath, verified: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw OwnershipLost(ex);
        }

        if (request.TransferAttachedAsync is null)
        {
            return;
        }

        try
        {
            await request.TransferAttachedAsync(association, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw OwnershipLost(ex);
        }
    }

    /// <summary>
    ///     The monitoring failure raised when ownership cannot be kept, carrying what actually went wrong.
    /// </summary>
    /// <remarks>
    ///     The underlying message is kept rather than discarded. It is the only thing that says whether the
    ///     database refused the write or the payload update failed, and those need different things fixed.
    /// </remarks>
    private static SoulseekTransferMonitoringException OwnershipLost(Exception cause)
        => new(
            $"The Soulseek transfer started but DeezSpoTag could not record it, so it cannot be watched safely: {cause.Message}",
            cause);

    /// <summary>
    ///     A transfer this queue already owns, which a restarted or retried attempt should watch rather than
    ///     start again.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A transfer that is still running is the one to reattach to: slskd is fetching that file right now,
    ///         and enqueueing it again would ask the peer for the same bytes twice.
    ///     </para>
    ///     <para>
    ///         A transfer that finished successfully and was verified is also worth returning, because its file
    ///         may still be sitting on disk waiting to be consumed - the download was interrupted between the
    ///         transfer finishing and the file being picked up.
    ///     </para>
    ///     <para>
    ///         A reader's exact selection is only resumed onto its own peer and path. Recovering a different file
    ///         onto a promise that named this one is the substitution the pin exists to prevent.
    ///     </para>
    /// </remarks>
    private async Task<SoulseekTransferRecord?> FindRecoverableTransferAsync(
        SoulseekDownloadRequest request,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SoulseekTransferRecord> recorded;
        try
        {
            recorded = await _repository.GetTransfersForQueueAsync(queueUuid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SoulseekTransferMonitoringException(
                $"DeezSpoTag could not read existing slskd transfers for queue {queueUuid}: {ex.Message}", ex);
        }

        var pinned = HasPinnedCandidate(request);

        var recoverable = recorded
            .Where(candidate => candidate.TransferId != Guid.Empty)
            .Where(candidate => !pinned || MatchesPinnedSelection(request, candidate))
            .Where(candidate => !candidate.IsTerminal || (candidate.IsSuccessful && candidate.Verified))
            .OrderByDescending(candidate => candidate.UpdatedAtUtc)
            .FirstOrDefault();

        if (recoverable is not null)
        {
            return recoverable;
        }

        var claims = await _repository.GetSourceClaimsForQueueAsync(queueUuid, cancellationToken).ConfigureAwait(false);
        var reservation = claims.FirstOrDefault(claim =>
            (!pinned || (string.Equals(claim.Username, request.Username, StringComparison.OrdinalIgnoreCase)
                && PathsEqual(claim.Filename, request.RemotePath)))
            && (claim.TransferId is null || !recorded.Any(row => row.TransferId == claim.TransferId)));
        if (reservation is null)
        {
            return null;
        }

        var started = await FindStartedTransferAsync(reservation.Username, reservation.Filename, queueUuid, cancellationToken)
            .ConfigureAwait(false);
        if (started?.TransferId is not { } adoptedId
            || (reservation.TransferId is { } attachedId && attachedId != adoptedId)
            || !string.Equals(started.Username, reservation.Username, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(started.Filename, reservation.Filename, StringComparison.OrdinalIgnoreCase))
        {
            throw new SoulseekTransferMonitoringException(
                "A previous Soulseek attempt still owns its download paths, but its transfer identity could not be established. No new transfer was started.");
        }

        var mine = claims.Where(claim => claim.OwnershipId == reservation.OwnershipId).ToList();
        var sourcePath = SoulseekTransferService.ResolveSourcePath(reservation.Filename, request.CompletedDownloadsRoot);
        // The persisted claim that is not slskd's completed source is the staging path.
        var recordedExpectedPath = mine.FirstOrDefault(claim => !PathsEqual(claim.Path, sourcePath ?? string.Empty))?.Path;
        if (recordedExpectedPath is null && mine.Count == 1) recordedExpectedPath = mine[0].Path;
        if (string.IsNullOrWhiteSpace(recordedExpectedPath))
            throw new SoulseekTransferMonitoringException("The reserved Soulseek staging path could not be identified.");

        await AttachClaimsAsync(reservation.OwnershipId, adoptedId, queueUuid, reservation.Username, reservation.Filename, cancellationToken)
            .ConfigureAwait(false);
        await RecordOwnershipAsync(request, queueUuid, adoptedId, reservation.Username, reservation.Filename, started.Size,
            recordedExpectedPath, cancellationToken).ConfigureAwait(false);
        return await _repository.GetTransferAsync(adoptedId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a recorded transfer is the exact peer file a reader chose.</summary>
    private static bool MatchesPinnedSelection(SoulseekDownloadRequest request, SoulseekTransferRecord candidate)
        => string.Equals(candidate.Username, request.Username, StringComparison.OrdinalIgnoreCase)
           && PathsEqual(candidate.Filename, request.RemotePath);

    }

/// <summary>
///     Raised when the app could not establish or keep hold of a transfer it had already started.
/// </summary>
/// <remarks>
///     <para>
///         This is not a peer failing. The peer may be transferring perfectly well; what failed is this app's
///         ability to record that a transfer exists, or to keep watching one. Answering it by asking a different
///         peer would start a second download of a file slskd is already fetching, and would charge an innocent
///         peer for a bookkeeping failure here.
///     </para>
///     <para>
///         It carries its own message rather than being flattened into the generic "could not deliver the file",
///         because the difference between "the peer would not send it" and "we lost track of it" is the whole
///         thing a reader can act on.
///     </para>
/// </remarks>
public sealed class SoulseekTransferMonitoringException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SoulseekTransferMonitoringException"/> class.</summary>
    public SoulseekTransferMonitoringException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SoulseekTransferMonitoringException"/> class.</summary>
    public SoulseekTransferMonitoringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Raised when Soulseek has no verified login, so no new remote work may start.
/// </summary>
/// <remarks>
///     <para>
///         Not a peer failure and not a missing file. Nothing was asked of any peer, so there is nothing to retry
///         on the network and no peer to hold responsible.
///     </para>
///     <para>
///         It is also not terminal for the item. The reader can log in and the queue's own retry controls pick the
///         work back up, which is why this carries the connection's own reason rather than a generic failure:
///         "log in to Soulseek" and "this track is not on any peer" call for completely different actions.
///     </para>
/// </remarks>
public sealed class SoulseekLoginRequiredException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SoulseekLoginRequiredException"/> class.</summary>
    public SoulseekLoginRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SoulseekLoginRequiredException"/> class.</summary>
    public SoulseekLoginRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Raised when a transfer from a peer did not produce the file.
/// </summary>
/// <remarks>
///     The peer is carried on the exception so the next attempt knows which one to avoid, which is what turns
///     a dead peer into "try another peer" rather than "the ladder is exhausted".
/// </remarks>
public sealed class SoulseekTransferException : Exception
{
    /// <summary>Gets the peer whose transfer failed.</summary>
    public string Username { get; } = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="SoulseekTransferException" /> class.</summary>
    public SoulseekTransferException(string message, string username)
        : base(message)
    {
        Username = username ?? string.Empty;
    }
}

/// <summary>
///     Raised when a Soulseek search produced no acceptable candidate.
/// </summary>
/// <remarks>
///     A distinct type lets the engine report "nothing matched" separately from "the transfer failed", so the
///     activity log explains which happened.
/// </remarks>
public sealed class SoulseekNoMatchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SoulseekNoMatchException"/> class.</summary>
    public SoulseekNoMatchException(string message)
        : base(message)
    {
    }
}
