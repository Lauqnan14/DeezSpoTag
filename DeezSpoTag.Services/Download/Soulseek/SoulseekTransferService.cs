using DeezSpoTag.Core.Models.Soulseek;
using Microsoft.Data.Sqlite;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The default <see cref="ISoulseekTransferService"/>.
/// </summary>
/// <remarks>
///     <para>
///         This owns the rule that a completed slskd transfer is not a completed DeezSpoTag download.
///         <see cref="WaitForCompletionAsync"/> returns a <see cref="SoulseekCompletedFile"/> only when the
///         file exists on disk, is non-empty, and its size is consistent with what slskd reported.
///     </para>
/// </remarks>
public sealed class SoulseekTransferService : ISoulseekTransferService
{
    /// <summary>How often the transfer is polled while it runs.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Slack allowed when comparing the file size to what the peer advertised.</summary>
    private const double SizeToleranceRatio = 0.02;

    /// <summary>
    ///     How many times in a row slskd may fail to answer before the transfer is given up on.
    /// </summary>
    /// <remarks>
    ///     A transport that stops answering says nothing about whether the peer is sending, so the same transfer
    ///     is asked again rather than written off and replaced by a second download of a file already in flight.
    ///     It does need an end: a transfer that never answers again would otherwise be watched for ever, holding
    ///     a worker and reporting progress for something nobody can see. Reset by any successful read, so only
    ///     consecutive failures count.
    /// </remarks>
    private const int MaxConsecutiveTransportFailures = 3;

    private readonly ISlskdClient _client;
    private readonly ISoulseekCredentialProvider _credentials;
    private readonly SoulseekRepository _repository;
    private readonly ISoulseekRealtimePublisher _realtime;
    private readonly ILogger<SoulseekTransferService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekTransferService"/> class.</summary>
    public SoulseekTransferService(
        ISlskdClient client,
        ISoulseekCredentialProvider credentials,
        SoulseekRepository repository,
        ILogger<SoulseekTransferService> logger,
        ISoulseekRealtimePublisher? realtime = null)
    {
        _client = client;
        _credentials = credentials;
        _repository = repository;
        _logger = logger;
        _realtime = realtime ?? NullSoulseekRealtimePublisher.Instance;
    }

    /// <inheritdoc />
    public async Task<Guid?> EnqueueAsync(string username, string filename, long size, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        var credentials = await RequireCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var transfers = await _client
            .EnqueueDownloadsAsync(
                credentials,
                username,
                [new SlskdQueueDownload { Filename = filename, Size = size }],
                cancellationToken)
            .ConfigureAwait(false);

        return transfers.FirstOrDefault()?.Id;
    }

    /// <summary>
    ///     Takes the non-audio files a peer is sharing alongside the audio and reports where each one landed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A cover image or a cue sheet sitting in a peer's folder is frequently the only artwork or lyrics
    ///         that will ever exist for a release no streaming service carries, which is why a reader who opts
    ///         in should actually get it.
    ///     </para>
    ///     <para>
    ///         Each file is fetched and verified on its own terms and every failure is swallowed and logged.
    ///         This is decoration: a peer that will not give up its cover image, or slskd being unable to place
    ///         it, must never turn a completed audio download into a failed one.
    ///     </para>
    /// </remarks>
    /// <param name="username">The peer the audio came from, and the only peer these may come from.</param>
    /// <param name="remotePaths">Full remote paths, all direct children of one browsed directory.</param>
    /// <param name="outputDirectory">Where the audio landed, so the sidecars sit beside it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     The local paths of the files that arrived, keyed by their full remote path. A path that is absent
    ///     simply did not arrive.
    /// </returns>
    public async Task<IReadOnlyDictionary<string, string>> FetchSidecarsAsync(
        string username,
        IReadOnlyList<string> remotePaths,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var fetched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(username)
            || remotePaths is null
            || remotePaths.Count == 0
            || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return fetched;
        }

        var credentials = await RequireCredentialsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var remotePath in remotePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = SoulseekRemotePath.Normalize(remotePath);
            if (normalized.Length == 0)
            {
                continue;
            }

            // Each sidecar is its own operation with its own ownership id, so two sidecars that resolve to the same
            // path - or one that collides with a path an audio claim already holds - are separate claims rather than
            // a shared one that either could release out from under the other.
            var ownershipId = Guid.NewGuid();

            try
            {
                var leaf = SoulseekRemotePath.GetLeaf(normalized);
                var expected = Path.Join(outputDirectory, leaf);
                Directory.CreateDirectory(outputDirectory);

                // Reserved before the enqueue, for the same reason the audio path is: a file already sitting at the
                // sidecar's destination, or one another operation is filling, is not this sidecar's to collect.
                var claimed = await TryClaimSidecarAsync(ownershipId, username, normalized, expected, cancellationToken)
                    .ConfigureAwait(false);
                if (!claimed)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "Not taking the Soulseek sidecar {RemotePath} from {Username}: {Path} is already taken or already present.",
                            normalized,
                            username,
                            expected);
                    }

                    continue;
                }

                var enqueued = await _client
                    .EnqueueDownloadsAsync(
                        credentials,
                        username,
                        [new SlskdQueueDownload { Filename = normalized, Size = 0 }],
                        cancellationToken)
                    .ConfigureAwait(false);

                var transferId = enqueued.FirstOrDefault()?.Id;
                if (transferId is null)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "slskd enqueued no transfer for the sidecar {RemotePath} from {Username}; skipping it.",
                            normalized,
                            username);
                    }

                    continue;
                }

                // Attached before monitoring, so the completion check has the same ownership evidence the audio
                // route requires rather than relying on the path being private to this call.
                await _repository.AttachSourceClaimsAsync(ownershipId, transferId.Value, cancellationToken)
                    .ConfigureAwait(false);

                var completed = await WaitForCompletionAsync(
                        username,
                        transferId.Value,
                        expected,
                        queueUuid: string.Empty,
                        completedDownloadsRoot: null,
                        progress: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (completed is not null)
                {
                    fetched[normalized] = completed.LocalPath;

                    // The decoration has been delivered, so its path is free again. Releasing only here keeps a
                    // failed or cancelled sidecar reserved, and a delivered file is deliberately left in place.
                    await _repository.ReleaseSourceClaimsAsync(ownershipId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is SlskdApiException
                                       or HttpRequestException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or SoulseekUnavailableException
                                       or SoulseekTransferMonitoringException
                                       or ArgumentException
                                       or NotSupportedException)
            {
                // Deliberately enumerated rather than catching everything. These are the failures a peer or a
                // disk can actually produce while fetching a cover image, and every one of them means the same
                // thing here: this sidecar did not arrive, so carry on without it. Catching everything would
                // also swallow a null dereference in this code, which is a bug worth seeing rather than a
                // stranger's missing jpeg worth hiding.
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        ex,
                        "Could not take the Soulseek sidecar {RemotePath} from {Username}; continuing without it.",
                        normalized,
                        username);
                }
            }
        }

        return fetched;
    }

    /// <inheritdoc />
    public async Task<SoulseekTransferStatus?> FindTransferAsync(
        string username,
        string filename,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        var credentials = await RequireCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var transfers = await _client
            .ListDownloadsAsync(credentials, includeRemoved: false, cancellationToken)
            .ConfigureAwait(false);

        // A peer's path is the identity here: the same file from the same peer is the same transfer, and two
        // peers offering the same file are two different transfers that must not be confused for one another.
        foreach (var transfer in transfers)
        {
            if (transfer.Id is null)
            {
                continue;
            }

            var samePeer = string.Equals(transfer.Username, username, StringComparison.OrdinalIgnoreCase);
            var sameFile = string.Equals(transfer.Filename, filename, StringComparison.OrdinalIgnoreCase);
            if (samePeer && sameFile)
            {
                return ToStatus(transfer);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<SoulseekTransferStatus?> GetStatusAsync(string username, Guid transferId, CancellationToken cancellationToken = default)
    {
        var credentials = await RequireCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var transfer = await _client.GetDownloadAsync(credentials, username, transferId, cancellationToken).ConfigureAwait(false);
        return transfer is null ? null : ToStatus(transfer);
    }

    /// <inheritdoc />
    public async Task<SoulseekCompletedFile?> WaitForCompletionAsync(
        string username,
        Guid transferId,
        string expectedPath,
        string queueUuid,
        string? completedDownloadsRoot = null,
        Func<double, double, Task>? progress = null,
        CancellationToken cancellationToken = default,
        CancellationToken hostStoppingToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(expectedPath))
        {
            return null;
        }

        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hostStoppingToken);
        var monitoringToken = monitoring.Token;

        // Credentials exist independently of the terminal state. The user can log out after the transfer has
        // already written its bytes, and refusing to open monitor then throws away a completed download. Reading
        // them up-front and refusing before any disk check is exactly that.
        var credentialsAvailable = true;
        SlskdCredentials credentials;
        try
        {
            credentials = await RequireCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SoulseekUnavailableException or OperationCanceledException)
        {
            _logger.LogInformation(ex, "Soulseek credentials are unavailable for monitoring; answering from disk.");
            credentialsAvailable = false;
            credentials = new SlskdCredentials(string.Empty, string.Empty);
        }

        var persisted = await ReadPersistedIdentityAsync(transferId, queueUuid, username, CancellationToken.None)
            .ConfigureAwait(false);
        var ownedPaths = await ReadClaimedPathsAsync(transferId, queueUuid, username, persisted?.Filename, CancellationToken.None)
            .ConfigureAwait(false);
        if (persisted is { Verified: true, IsSuccessful: true, Size: > 0 }
            && !string.IsNullOrWhiteSpace(persisted.Filename)
            && !string.IsNullOrWhiteSpace(persisted.ExpectedPath)
            && PathsMatch(persisted.ExpectedPath, expectedPath)
            && File.Exists(expectedPath) && new FileInfo(expectedPath).Length == persisted.Size)
        {
            // Already verified staging audio is owned evidence even on installs predating claims.
            return new SoulseekCompletedFile(username, persisted.Filename, expectedPath, persisted.Size);
        }
        if (persisted is { Verified: false } && ownedPaths.Count == 0)
            throw new SoulseekTransferMonitoringException("The recorded Soulseek transfer has no source-path ownership evidence.");

        SoulseekTransferStatus? lastSeen = persisted is null ? null : new SoulseekTransferStatus(
            transferId, username, persisted.Filename, persisted.State,
            persisted.IsTerminal, persisted.IsSuccessful, persisted.BytesTransferred, persisted.Size,
            0, null, persisted.Size > 0 ? Math.Clamp(persisted.BytesTransferred * 100d / persisted.Size, 0, 100) : 0,
            persisted.Error);
        var reportedSize = persisted?.Size ?? 0;
        var remoteFilename = persisted?.Filename ?? string.Empty;
        var consecutiveTransportFailures = 0;

        SoulseekCompletedFile? VerifyActive() => VerifyWhileActive(
            expectedPath, username, remoteFilename, reportedSize, completedDownloadsRoot, ownedPaths);

        async Task<SoulseekCompletedFile> CompleteVerifiedAsync(SoulseekCompletedFile file)
        {
            var status = lastSeen ?? new SoulseekTransferStatus(
                transferId, username, remoteFilename, "completed", true, true,
                file.Size, file.Size, 0, null, 100);
            await RecordTerminalAsync(status, transferId, expectedPath, queueUuid, file, CancellationToken.None)
                .ConfigureAwait(false);
            _realtime.PublishDownloadUpdate(new SoulseekDownloadProgress(
                queueUuid, username, file.Filename, SoulseekDownloadStage.Completed,
                100, file.Size, file.Size, 0, Verified: true));
            return file;
        }

        try
        {
            while (true)
            {
                monitoringToken.ThrowIfCancellationRequested();
                if (VerifyActive() is { } onDisk)
                {
                    return await CompleteVerifiedAsync(onDisk).ConfigureAwait(false);
                }

                SlskdTransfer? transfer;
                if (!credentialsAvailable)
                {
                    // Nobody to ask. The transfer will only be found on disk if it has finished; the monitor
                    // must report that outcome rather than failing silently into the continuity branch.
                    var byDisk = VerifyActive();
                    if (byDisk is not null)
                    {
                        return await CompleteVerifiedAsync(byDisk).ConfigureAwait(false);
                    }

                    throw new SoulseekUnavailableException("Soulseek is not configured.");
                }

                try
                {
                    transfer = await _client.GetDownloadAsync(credentials, username, transferId, monitoringToken)
                        .ConfigureAwait(false);
                    consecutiveTransportFailures = 0;
                }
                catch (Exception ex) when (IsTransient(ex)
                    || (ex is OperationCanceledException && !monitoringToken.IsCancellationRequested))
                {
                    monitoringToken.ThrowIfCancellationRequested();
                    // A file may have arrived while the lookup was timing out.
                    if (VerifyActive() is { } arrived)
                    {
                        return await CompleteVerifiedAsync(arrived).ConfigureAwait(false);
                    }

                    consecutiveTransportFailures++;
                    if (consecutiveTransportFailures > MaxConsecutiveTransportFailures)
                    {
                        throw new SoulseekTransferMonitoringException(
                            $"slskd stopped answering for transfer {transferId}: {ex.Message}", ex);
                    }

                    await PersistObservedAsync(lastSeen is null ? null : lastSeen with { Error = ex.Message },
                        transferId, expectedPath, queueUuid, CancellationToken.None).ConfigureAwait(false);
                    await Task.Delay(PollInterval, monitoringToken).ConfigureAwait(false);
                    continue;
                }

                monitoringToken.ThrowIfCancellationRequested();
                if (transfer is null)
                {
                    var verified = VerifyActive();
                    if (verified is not null)
                    {
                        return await CompleteVerifiedAsync(verified).ConfigureAwait(false);
                    }

                    var missing = Forgotten(transferId, username, remoteFilename, reportedSize);
                    await RecordTerminalAsync(missing, transferId, expectedPath, queueUuid, null, CancellationToken.None)
                        .ConfigureAwait(false);
                    _realtime.PublishDownloadUpdate(new SoulseekDownloadProgress(
                        queueUuid, username, remoteFilename, SoulseekDownloadStage.Failed, Error: missing.Error));
                    await ReleaseUnusedClaimsAsync(transferId, queueUuid, username, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }

                if (transfer.Id != transferId
                    || (!string.IsNullOrWhiteSpace(transfer.Username)
                        && !string.Equals(transfer.Username, username, StringComparison.OrdinalIgnoreCase)))
                    throw new SoulseekTransferMonitoringException("slskd returned a different transfer identity.");
                ownedPaths = await ReadClaimedPathsAsync(transferId, queueUuid, username, transfer.Filename, monitoringToken)
                    .ConfigureAwait(false);
                lastSeen = ToStatus(transfer);
                reportedSize = transfer.Size;
                remoteFilename = transfer.Filename ?? string.Empty;
                await PersistObservedAsync(lastSeen, transferId, expectedPath, queueUuid, CancellationToken.None)
                    .ConfigureAwait(false);
                if (progress is not null)
                {
                    await progress(lastSeen.Progress, lastSeen.AverageSpeed).ConfigureAwait(false);
                }

                _realtime.PublishDownloadUpdate(new SoulseekDownloadProgress(
                    queueUuid, lastSeen.Username, lastSeen.Filename, ToStage(lastSeen),
                    lastSeen.Progress, lastSeen.BytesTransferred, lastSeen.Size, lastSeen.AverageSpeed));

                monitoringToken.ThrowIfCancellationRequested();
                var completed = lastSeen.IsTerminal
                    ? Verify(expectedPath, username, remoteFilename, reportedSize, completedDownloadsRoot, ownedPaths)
                    : VerifyActive();
                if (completed is not null)
                {
                    return await CompleteVerifiedAsync(completed).ConfigureAwait(false);
                }

                if (lastSeen.IsTerminal)
                {
                    await RecordTerminalAsync(lastSeen, transferId, expectedPath, queueUuid, null, CancellationToken.None)
                        .ConfigureAwait(false);
                    _realtime.PublishDownloadUpdate(new SoulseekDownloadProgress(
                        queueUuid, username, remoteFilename, SoulseekDownloadStage.Failed,
                        lastSeen.Progress, lastSeen.BytesTransferred, lastSeen.Size, lastSeen.AverageSpeed,
                        Error: lastSeen.Error ?? "slskd finished without a verified file on disk."));
                    await ReleaseUnusedClaimsAsync(transferId, queueUuid, username, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }

                await Task.Delay(PollInterval, monitoringToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (hostStoppingToken.IsCancellationRequested)
        {
            // The item's linked token is also cancelled during shutdown. slskd keeps the transfer running.
            if (VerifyActive() is { } completed)
            {
                await CompleteVerifiedAsync(completed).ConfigureAwait(false);
            }
            else
            {
                await PersistObservedAsync(lastSeen, transferId, expectedPath, queueUuid, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            var verified = Verify(expectedPath, username, remoteFilename, reportedSize, completedDownloadsRoot, ownedPaths);
            var abandoned = lastSeen is null
                ? Abandoned(transferId, username, remoteFilename, reportedSize)
                : lastSeen with { IsTerminal = true, IsSuccessful = verified is not null };
            await RecordTerminalAsync(abandoned, transferId, expectedPath, queueUuid, verified, CancellationToken.None)
                .ConfigureAwait(false);
            if (await TryCancelAsync(credentials, username, transferId, CancellationToken.None).ConfigureAwait(false)
                && verified is null)
                await ReleaseUnusedClaimsAsync(transferId, queueUuid, username, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            monitoringToken.ThrowIfCancellationRequested();
            if (VerifyActive() is { } completed)
            {
                return await CompleteVerifiedAsync(completed).ConfigureAwait(false);
            }

            // A monitoring error leaves ownership recoverable and cannot authorise another peer download.
            var recoverable = (lastSeen ?? Forgotten(transferId, username, remoteFilename, reportedSize)) with
            {
                IsTerminal = false,
                IsSuccessful = false,
                Error = ex.Message
            };
            await PersistObservedAsync(recoverable, transferId, expectedPath, queueUuid, CancellationToken.None)
                .ConfigureAwait(false);
            if (ex is SoulseekTransferMonitoringException or SoulseekUnavailableException)
            {
                throw;
            }
            throw new SoulseekTransferMonitoringException(
                $"DeezSpoTag could not monitor slskd transfer {transferId}: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Whether a transport failure is worth asking the same transfer again rather than writing it off.
    /// </summary>
    /// <remarks>
    ///     Timeouts, dropped connections and server errors say nothing about whether the peer is sending. A
    ///     rejected key or a missing configuration is a different matter: the transfer cannot be watched by
    ///     anyone until that is fixed, so it keeps its explicit classification and is not retried here.
    /// </remarks>
    private static bool IsTransient(Exception failure)
        => failure is TimeoutException
            or HttpRequestException
            or IOException
            || (failure is SlskdApiException api
                && !api.IsUnauthorized
                && !api.IsNotFound
                && (api.StatusCode is 408 or 429 || api.StatusCode >= 500));

    /// <summary>
    ///     This queue's own record for this transfer, or null when there is none or it belongs elsewhere.
    /// </summary>
    private async Task<SoulseekTransferRecord?> ReadPersistedIdentityAsync(
        Guid transferId,
        string queueUuid,
        string username,
        CancellationToken cancellationToken)
    {
        SoulseekTransferRecord? record;
        try
        {
            record = await _repository.GetTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SoulseekTransferMonitoringException(
                $"DeezSpoTag could not read ownership for slskd transfer {transferId}: {ex.Message}", ex);
        }

        if (record is null)
        {
            return null;
        }

        if (!string.Equals(record.QueueUuid, queueUuid, StringComparison.Ordinal)
            || !string.Equals(record.Username, username, StringComparison.OrdinalIgnoreCase))
        {
            // Another item's or another peer's transfer. Its filename and size would point verification at a file
            // this item never asked for, which is how a wrong file gets accepted as this download.
            _logger.LogInformation(
                "Ignoring Soulseek transfer {TransferId} while monitoring queue {QueueUuid}: it is recorded for queue {RecordedQueue} and peer {RecordedPeer}.",
                transferId,
                queueUuid,
                record.QueueUuid ?? "none",
                record.Username);
            throw new SoulseekTransferMonitoringException("The recorded Soulseek transfer belongs to another queue item or peer.");
        }

        return record;
    }

    /// <summary>
    ///     Writes an observed status without claiming an outcome, so an abandoned transfer keeps its last state.
    /// </summary>
    private async Task PersistObservedAsync(
        SoulseekTransferStatus? status,
        Guid transferId,
        string expectedPath,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        if (status is null)
        {
            return;
        }

        try
        {
            await _repository.UpsertTransferAsync(
                    status with { IsTerminal = false, IsSuccessful = false },
                    queueUuid,
                    expectedPath,
                    verified: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SoulseekTransferMonitoringException(
                $"DeezSpoTag could not preserve ownership for slskd transfer {transferId}: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Reserves a sidecar's destination path before its transfer is enqueued.
    /// </summary>
    /// <remarks>
    ///     A sidecar has no queue item of its own, so it is filed under the peer and the empty queue uuid. It is
    ///     best effort by design: a collision means this decoration is skipped, and skipping decoration must never
    ///     fail audio that has already been acquired.
    /// </remarks>
    private async Task<bool> TryClaimSidecarAsync(
        Guid ownershipId,
        string username,
        string remotePath,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _repository.TryClaimSourcePathsAsync(
                    ownershipId,
                    string.Empty,
                    username,
                    remotePath,
                    [expectedPath],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not reserve the Soulseek sidecar path {Path}.", expectedPath);
            }

            return false;
        }
    }

    /// <summary>
    ///     The canonical local paths this transfer's own claims cover.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Empty means no claim names this transfer, and then no file may be accepted as its completion. That is
    ///         the refusal that matters: a transfer row can exist for a path this attempt never reserved - a legacy
    ///         row written before claims existed, or a peer file that mapped onto a path another operation holds -
    ///         and reading such a path's file would hand this item a download it never made.
    ///     </para>
    ///     <para>
    ///         Compared with the platform's own rule for file names, matching the claim table's collation.
    ///     </para>
    /// </remarks>
    private async Task<IReadOnlySet<string>> ReadClaimedPathsAsync(
        Guid transferId,
        string queueUuid,
        string username,
        string? filename,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SoulseekSourcePathClaim> claims;
        try
        {
            claims = await _repository.GetSourceClaimsForTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The claims are what makes verification safe, so a failure to read them is a monitoring failure
            // rather than a quiet "nothing was claimed". Answering from disk anyway is the unsafe answer.
            throw new SoulseekTransferMonitoringException(
                $"DeezSpoTag could not read the path claims for slskd transfer {transferId}: {ex.Message}", ex);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        // A claim naming another peer is not evidence about this peer's file, even when it is attached to
        // this transfer id.
        foreach (var path in claims
            .Where(claim =>
                string.Equals(claim.QueueUuid, queueUuid, StringComparison.Ordinal)
                && string.Equals(claim.Username, username, StringComparison.OrdinalIgnoreCase)
                && (filename is null || string.Equals(SoulseekRemotePath.Normalize(claim.Filename),
                    SoulseekRemotePath.Normalize(filename), StringComparison.OrdinalIgnoreCase)))
            .Select(claim => claim.Path))
        {
            paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    ///     Verification for a transfer that has not reported itself finished.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The bar is the whole advertised size. The terminal tolerance exists to absorb a rounding
    ///         difference in a transfer that says it is done; applying it to one that is still running would
    ///         accept 29,999 bytes of an advertised 30,000 as the finished file.
    ///     </para>
    ///     <para>
    ///         An unknown advertised size cannot be judged, so it is refused rather than accepted on the file's
    ///         own say-so. Nothing else in this file accepts a file it cannot measure against something.
    ///     </para>
    /// </remarks>
    private SoulseekCompletedFile? VerifyWhileActive(
        string expectedPath,
        string username,
        string filename,
        long reportedSize,
        string? completedDownloadsRoot,
        IReadOnlySet<string> claimedPaths)
    {
        if (reportedSize <= 0)
        {
            return null;
        }

        var actual = ResolveCompletedFilePath(expectedPath, filename, completedDownloadsRoot);
        if (actual is null || !IsClaimedPath(actual, claimedPaths))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(actual);
            return info.Exists && info.Length >= reportedSize
                ? new SoulseekCompletedFile(username, filename, actual, info.Length)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync(string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
    {
        var credentials = await RequireCredentialsAsync(cancellationToken).ConfigureAwait(false);
        await _client.CancelDownloadAsync(credentials, username, transferId, remove, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CleanupStaleTransfersAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var cleared = 0;

        if (credentials is not null)
        {
            try
            {
                var downloads = await _client.ListDownloadsAsync(credentials, includeRemoved: true, cancellationToken)
                    .ConfigureAwait(false);
                var stale = downloads.Count(download => download.Removed || download.StateInfo.IsTerminal);
                if (stale > 0)
                {
                    await _client.ClearCompletedDownloadsAsync(credentials, cancellationToken).ConfigureAwait(false);
                    cleared = stale;
                }
            }
            catch (SlskdApiException ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Could not clear completed Soulseek downloads.");
                }
            }
        }

        // Records DeezSpoTag owns are pruned separately, and only when they are terminal.
        cleared += await _repository
            .DeleteTerminalTransfersOlderThanAsync(DateTimeOffset.UtcNow.AddHours(-24), cancellationToken)
            .ConfigureAwait(false);

        return cleared;
    }

    /// <summary>
    ///     Verifies that a real, non-empty file landed at the expected path.
    /// </summary>
    /// <remarks>
    ///     Size is compared with a small tolerance because a peer may have mis-advertised the length. A file
    ///     that is far smaller than advertised is a truncated download and is rejected.
    /// </remarks>
    private SoulseekCompletedFile? Verify(
        string expectedPath,
        string username,
        string filename,
        long reportedSize,
        string? completedDownloadsRoot,
        IReadOnlySet<string> claimedPaths)
    {
        var actual = ResolveCompletedFilePath(expectedPath, filename, completedDownloadsRoot);
        if (actual is null)
        {
            return null;
        }

        // Terminal does not exempt this. A file that arrived while nobody was watching is exactly the case where
        // an unrelated existing file would be adopted, so ownership is required on this route too.
        if (!IsClaimedPath(actual, claimedPaths))
        {
            // Worth a warning either way, and the wording differs deliberately: no claims at all means this transfer
            // has no reservation on record, which is a different problem from a file at a path reserved for something
            // else.
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    claimedPaths.Count == 0
                        ? "Soulseek reported {Filename} from {Username} as transferred, but no path claim records this transfer, so {Path} is not accepted as its download."
                        : "Soulseek reported {Filename} from {Username} as transferred, but {Path} is not claimed by this transfer, so it is not accepted as this download.",
                    filename,
                    username,
                    actual);
            }

            return null;
        }

        try
        {
            var info = new FileInfo(actual);
            if (!info.Exists)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Soulseek reported {Filename} from {Username} as transferred, but {Path} does not exist.",
                        filename,
                        username,
                        actual);
                }

                return null;
            }
            if (info.Length <= 0)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("Soulseek transfer left an empty file at {Path}.", actual);
                }

                return null;
            }

            if (reportedSize > 0 && info.Length < reportedSize * (1 - SizeToleranceRatio))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Soulseek transfer at {Path} is {Actual} bytes, short of the {Expected} the peer advertised; treating it as incomplete.",
                        actual,
                        info.Length,
                        reportedSize);
                }

                return null;
            }

            return new SoulseekCompletedFile(username, filename, actual, info.Length);
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not inspect the completed Soulseek file at {Path}.", actual);
            }

            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not inspect the completed Soulseek file at {Path}.", actual);
            }

            return null;
        }
    }

    /// <summary>
    ///     The one path the completed file may be at, or null when there is nothing to look at.
    /// </summary>
    /// <remarks>
    ///     The engine's persisted expected path is tried first. The global DeezSpoTag download root is then used
    ///     to locate slskd's completed source; when it is not configured, the transfer is not verified and no
    ///     alternative directory is searched.
    /// </remarks>
    private string? ResolveCompletedFilePath(string expectedPath, string remoteFilename, string? completedDownloadsRoot)
    {
        if (!string.IsNullOrWhiteSpace(expectedPath)
            && File.Exists(expectedPath)
            && (string.IsNullOrWhiteSpace(completedDownloadsRoot)
                || !DownloadPathResolver.IsUnderTopLevelIncompleteDirectory(completedDownloadsRoot, expectedPath)))
        {
            return expectedPath;
        }

        var root = (completedDownloadsRoot ?? string.Empty).Trim();
        if (root.Length == 0)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "The shared DeezSpoTag download location is not configured, so a file completed outside {ExpectedPath} cannot be located.",
                    expectedPath);
            }

            return null;
        }

        var resolved = ResolveSourcePath(remoteFilename, root);
        if (resolved is null && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "The completed Soulseek file for {RemotePath} could not be resolved under the configured root {Root}.",
                remoteFilename,
                root);
        }

        return resolved;
    }

    /// <summary>
    ///     The canonical local path a peer's file will occupy under the shared download root, or null when it
    ///     cannot be resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     Public and static so the download service reserves exactly the path this class will later verify,
    ///     instead of a second copy of the mapping drifting from it. A reservation computed anywhere but here is a
    ///     reservation that may not match the file being read, and the whole point of a claim is that the two are
    ///     the same path.
    /// </para>
    /// <para>
    ///     It resolves without requiring the file to exist, which is what makes it usable before the enqueue: the
    ///     path is reserved first and the file arrives later. An unresolvable path - a rooted name, a ".."
    ///     component, no usable root - returns null so the caller can refuse rather than reserve something else.
    /// </para>
    /// </remarks>
    /// <param name="remoteFilename">The peer's full remote path.</param>
    /// <param name="completedDownloadsRoot">The shared download root, or null when it is not configured.</param>
    public static string? ResolveSourcePath(string remoteFilename, string? completedDownloadsRoot)
    {
        var root = (completedDownloadsRoot ?? string.Empty).Trim();
        if (root.Length == 0)
        {
            return null;
        }

        var resolved = ResolveUnderRoot(root, remoteFilename);

        // The incomplete directory is where slskd puts a transfer still in flight, so it is never a completed
        // source. Excluded here exactly as it is in the private resolver, so the preflight path and the verified
        // path agree.
        return resolved is not null
            && !DownloadPathResolver.IsUnderTopLevelIncompleteDirectory(root, resolved)
                ? resolved
                : null;
    }

    /// <summary>
    ///     Whether a resolved path is one this transfer's own claims cover.
    /// </summary>
    /// <remarks>
    ///     The claim set holds canonical paths, so the candidate is canonicalised the same way before it is looked
    ///     up. Both sides are absolute, normalised and trailing-separator-free, so a file reached through a
    ///     relative path or a <c>.</c> component still matches the claim that reserved it.
    /// </remarks>
    private static bool PathsMatch(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsClaimedPath(string? path, IReadOnlySet<string> claimedPaths)
    {
        if (string.IsNullOrWhiteSpace(path) || claimedPaths.Count == 0)
        {
            return false;
        }

        string canonical;
        try
        {
            var full = Path.GetFullPath(path);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            canonical = trimmed.Length == 0 ? full : trimmed;
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

        return claimedPaths.Contains(canonical);
    }

    /// <summary>
    ///     Turns the peer's remote path into slskd's default destination under the configured root.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         slskd's default destination is the peer's source directory and the peer's own file name, and the
    ///         source directory it uses is the one directory the file sat in - which is why a peer path of
    ///         <c>@@peer\Music\Live and Die in Afrika\04 Isabella.mp3</c> is found at
    ///         <c>&lt;root&gt;/Live and Die in Afrika/04 Isabella.mp3</c> and not at the root itself.
    ///     </para>
    ///     <para>
    ///         It is deliberately not a directory built from album metadata: that is the album DeezSpoTag wants,
    ///         not the folder the peer filed it under, and using it would make the file unfindable. A remote path
    ///         with no directory of its own lands at the root.
    ///     </para>
    ///     <para>
    ///         The name comes off the network, so a rooted path, a "." or ".." component, or anything that
    ///         canonicalises outside the configured root resolves to nothing at all. Nothing is searched, and
    ///         nothing outside the root is ever read.
    ///     </para>
    /// </remarks>
    private static string? ResolveUnderRoot(string root, string remoteFilename)
    {
        var remote = (remoteFilename ?? string.Empty).Trim();
        if (remote.Length == 0 || Path.IsPathRooted(remote))
        {
            return null;
        }

        var segments = remote
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            return null;
        }

        var fileName = segments[^1];
        var sourceDirectory = segments.Length >= 2 ? segments[^2] : null;

        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(root);
            candidate = Path.GetFullPath(
                sourceDirectory is null
                    ? Path.Join(rootFull, fileName)
                    : Path.Join(rootFull, sourceDirectory, fileName));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }

        var relative = Path.GetRelativePath(rootFull, candidate);
        if (relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return candidate;
    }

    /// <summary>
    ///     The last state of a transfer slskd has stopped reporting.
    /// </summary>
    /// <remarks>
    ///     Its own words are gone, so the record says plainly that the transfer disappeared. A status that
    ///     claimed a failure slskd never reported would be a worse lie than an unknown outcome, and the item's
    ///     own error already says what the engine made of it.
    /// </remarks>
    private static SoulseekTransferStatus Forgotten(Guid transferId, string username, string? remoteFilename, long reportedSize)
        => new(
            transferId,
            username,
            remoteFilename ?? string.Empty,
            "forgotten",
            IsTerminal: true,
            IsSuccessful: false,
            BytesTransferred: 0,
            reportedSize,
            AverageSpeed: 0,
            PlaceInQueue: null,
            Progress: 0,
            Error: "slskd no longer reports this transfer.");

    /// <summary>
    ///     A transfer that was still being watched when monitoring stopped.
    /// </summary>
    /// <remarks>
    ///     Recorded so a cancelled or reclaimed attempt still leaves a row. The state is deliberately not
    ///     called failed: nobody reported a failure, the watcher simply stopped, and the verified outcome
    ///     replaces this whole record when a file turns out to be there.
    /// </remarks>
    private static SoulseekTransferStatus Abandoned(Guid transferId, string username, string? remoteFilename, long reportedSize)
        => new(
            transferId,
            username,
            remoteFilename ?? string.Empty,
            "abandoned",
            IsTerminal: true,
            IsSuccessful: false,
            BytesTransferred: 0,
            reportedSize,
            AverageSpeed: 0,
            PlaceInQueue: null,
            Progress: 0,
            Error: "Monitoring stopped before this transfer reported an outcome.");

    /// <summary>
    ///     Records the terminal outcome of a transfer against the file that was actually verified.
    /// </summary>
    /// <remarks>
    ///     A success has to be recorded here. Recording only the failure case would leave the transfer table
    ///     empty for every download that worked, so the history and cleanup endpoints would have nothing to
    ///     report and nothing to prune.
    /// </remarks>
    private async Task RecordTerminalAsync(
        SoulseekTransferStatus status,
        Guid? transferId,
        string expectedPath,
        string queueUuid,
        SoulseekCompletedFile? verified,
        CancellationToken cancellationToken)
    {
        if (transferId is null)
        {
            return;
        }

        // When slskd reported a failure but the file is good, the verified outcome wins, so the stored record
        // reflects what the user actually got.
        var recorded = verified is null
            ? status with
            {
                IsSuccessful = false,
                Error = status.Error ?? (status.IsSuccessful
                    ? "slskd finished without a verified file on disk."
                    : null)
            }
            : status with
            {
                State = "completed",
                IsTerminal = true,
                IsSuccessful = true,
                Error = null,
                BytesTransferred = verified.Size,
                Size = verified.Size,
                Progress = 100
            };

        try
        {
            await _repository
                .UpsertTransferAsync(recorded, queueUuid, expectedPath, verified is not null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            // Persisting history must never turn a good download into a failure.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not record the Soulseek transfer {TransferId}.", transferId);
            }
        }
    }

    private async Task ReleaseUnusedClaimsAsync(Guid transferId, string queueUuid, string username, CancellationToken token)
    {
        var claims = await _repository.GetSourceClaimsForTransferAsync(transferId, token).ConfigureAwait(false);
        if (claims.Count == 0 || claims.Any(claim => claim.QueueUuid != queueUuid
            || !string.Equals(claim.Username, username, StringComparison.OrdinalIgnoreCase)
            || File.Exists(claim.Path))) return;
        foreach (var owner in claims.Select(claim => claim.OwnershipId).Distinct())
            await _repository.ReleaseSourceClaimsAsync(owner, token).ConfigureAwait(false);
    }

    private async Task<bool> TryCancelAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.CancelDownloadAsync(credentials, username, transferId, remove: true, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (SlskdApiException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not cancel Soulseek transfer {TransferId}.", transferId);
            }
            return false;
        }
    }

    private async Task<SlskdCredentials> RequireCredentialsAsync(CancellationToken cancellationToken)
        => await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SoulseekUnavailableException("Soulseek is not configured.");

    private static SoulseekDownloadStage ToStage(SoulseekTransferStatus status) => status switch
    {
        { IsTerminal: true } => SoulseekDownloadStage.Verifying,
        { Progress: > 0 } => SoulseekDownloadStage.Transferring,
        _ => SoulseekDownloadStage.Enqueued
    };

    private static SoulseekTransferStatus ToStatus(SlskdTransfer transfer) => new(
        transfer.Id,
        transfer.Username ?? string.Empty,
        transfer.Filename ?? string.Empty,
        transfer.StateInfo.Label,
        transfer.StateInfo.IsTerminal,
        transfer.StateInfo.IsSuccessful,
        transfer.BytesTransferred,
        transfer.Size,
        transfer.AverageSpeed,
        transfer.PlaceInQueue,
        transfer.Size <= 0 ? 0 : Math.Clamp(transfer.BytesTransferred * 100.0 / transfer.Size, 0, 100),
        transfer.Exception);
}
