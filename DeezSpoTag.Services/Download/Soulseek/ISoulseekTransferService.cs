namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Manages the Soulseek transfer lifecycle: enqueue in slskd, track status, verify the completed file,
///     and clear stale transfers.
/// </summary>
/// <remarks>
///     A completed slskd transfer is not a completed DeezSpoTag download. Only
///     <see cref="WaitForCompletionAsync"/> returning a <see cref="SoulseekCompletedFile"/> means the file
///     exists in staging and is ready to be imported by the normal post-download pipeline.
/// </remarks>
public interface ISoulseekTransferService
{
    /// <summary>
    ///     Enqueues a single file for download from a peer.
    /// </summary>
    /// <param name="username">The peer to download from.</param>
    /// <param name="filename">The remote filename.</param>
    /// <param name="size">The expected size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identifier of the created transfer, when slskd reported one.</returns>
    Task<Guid?> EnqueueAsync(string username, string filename, long size, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Finds a transfer slskd already holds for this exact peer file.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         slskd answers the enqueue request after a short internal wait, and when a slow peer makes it
    ///         miss that wait it reports a server error instead of a transfer id - while the transfer it
    ///         created keeps running. Reading the transfer list afterwards is how the transfer that is really
    ///         in flight gets found, which is what lets the download watch it, report its progress and verify
    ///         it, rather than declaring a failure for a transfer that is still going.
    ///     </para>
    /// </remarks>
    /// <param name="username">The peer holding the file.</param>
    /// <param name="filename">The peer's full path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SoulseekTransferStatus?> FindTransferAsync(
        string username,
        string filename,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads the current status of a transfer.
    /// </summary>
    Task<SoulseekTransferStatus?> GetStatusAsync(string username, Guid transferId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Waits for a transfer to finish and verifies the resulting file.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="hostStoppingToken"/> is the host's own shutdown signal and is deliberately not the
    ///         same thing as the item's cancellation. An item the reader cancelled should stop its transfer; a host
    ///         shutting down must leave it running, because slskd keeps fetching and a later run reattaches to the
    ///         recorded transfer. Cancelling the remote transfer on shutdown throws away a download that was
    ///         nearly finished.
    ///     </para>
    ///     <para>
    ///         Verification comes from the file, not from the remote state, and it is checked throughout: before
    ///         the first lookup when a persisted identity is available, after each status, and before a monitoring
    ///         failure is classified. A completed file therefore survives a transport that stopped answering.
    ///     </para>
    /// </remarks>
    /// <param name="username">The peer being downloaded from.</param>
    /// <param name="transferId">The slskd transfer identifier.</param>
    /// <param name="expectedPath">
    ///     Where the completed file is expected to land, normally the engine's staging output path.
    /// </param>
    /// <param name="queueUuid">
    ///     The download queue item this transfer serves. Progress events carry it so a client can attribute a
    ///     transfer to a download instead of seeing an orphaned progress bar.
    /// </param>
    /// <param name="completedDownloadsRoot">
    ///     The host directory slskd writes completed downloads into, for when it does not write straight to
    ///     <paramref name="expectedPath" />. Null means the two share the staging path.
    /// </param>
    /// <param name="progress">Optional progress reporter, called with percentage and bytes per second.</param>
    /// <param name="cancellationToken">Cancellation token. Cancelling asks slskd to cancel the transfer.</param>
    /// <returns>
    ///     The verified file, or <see langword="null"/> when the transfer finished without a usable file on
    ///     disk. A finished transfer whose file is missing must be treated as a failure, not a success. The
    ///     path returned is where the file really is, which is not necessarily <paramref name="expectedPath" />.
    /// </returns>
    Task<SoulseekCompletedFile?> WaitForCompletionAsync(
        string username,
        Guid transferId,
        string expectedPath,
        string queueUuid,
        string? completedDownloadsRoot = null,
        Func<double, double, Task>? progress = null,
        CancellationToken cancellationToken = default,
        CancellationToken hostStoppingToken = default);

    /// <summary>
    ///     Takes the non-audio files a peer is sharing alongside the audio, such as a cover image or a cue
    ///     sheet, and reports where each one landed.
    /// </summary>
    /// <remarks>
    ///     Every failure is logged and skipped. A sidecar is decoration, so nothing it can do may turn a
    ///     completed audio download into a failed one.
    /// </remarks>
    /// <param name="username">The peer the audio came from, and the only peer these may come from.</param>
    /// <param name="remotePaths">Full remote paths, all direct children of one browsed directory.</param>
    /// <param name="outputDirectory">Where the audio landed, so the sidecars sit beside it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Local paths of the files that arrived, keyed by their full remote path.</returns>
    Task<IReadOnlyDictionary<string, string>> FetchSidecarsAsync(
        string username,
        IReadOnlyList<string> remotePaths,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels a transfer, optionally removing it from slskd's queue.
    /// </summary>
    Task CancelAsync(string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears completed and removed transfers from slskd so its history does not grow without bound.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of entries cleared.</returns>
    Task<int> CleanupStaleTransfersAsync(CancellationToken cancellationToken = default);
}
