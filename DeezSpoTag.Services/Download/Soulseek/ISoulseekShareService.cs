namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Builds the slskd share configuration from DeezSpoTag folder settings, and reconciles it against
///     what slskd actually reports.
/// </summary>
/// <remarks>
///     <para>
///         The folder tab is the single source of truth for whether a folder is shared. This service only
///         reads it; it never writes a folder's share state.
///     </para>
///     <para>
///         slskd exposes no API for creating or updating shares, so a sync compares the desired set against
///         <c>GET /api/v0/shares</c>, reports drift, and produces a paste-ready configuration block. It never
///         claims to have changed the remote instance.
///     </para>
/// </remarks>
public interface ISoulseekShareService
{
    /// <summary>
    ///     Builds the desired share set from the folder settings the user has enabled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SoulseekDesiredShare>> GetDesiredSharesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads the shares slskd currently reports.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reported shares, or an empty list when slskd is unavailable.</returns>
    Task<IReadOnlyList<SoulseekActualShare>> GetActualSharesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Compares the desired set against the reported set and produces diagnostics plus a paste-ready
    ///     <c>shares.directories</c> block.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SoulseekShareReconciliation> ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Renders a <c>shares.directories</c> block for the given desired shares, using slskd's
    ///     <c>[Alias]\path</c> syntax and <c>!</c> excludes.
    /// </summary>
    string GenerateShareConfiguration(IReadOnlyList<SoulseekDesiredShare> desiredShares);

    /// <summary>
    ///     Asks slskd to rescan its shares so new files become searchable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> RequestRescanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the last known share scan status and when it ran.
    /// </summary>
    Task<SoulseekShareScanStatus> GetScanStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>The last known state of a share scan.</summary>
/// <param name="IsRunning">Whether a scan is currently in progress.</param>
/// <param name="LastScanUtc">When the last scan finished, when one has.</param>
/// <param name="ShareCount">The number of shares slskd reported.</param>
/// <param name="FileCount">The number of files slskd reported across shares.</param>
/// <param name="Error">The failure detail, when the last scan or read failed.</param>
/// <param name="SlskdUnavailable">Whether slskd could not be reached.</param>
public sealed record SoulseekShareScanStatus(
    bool IsRunning,
    DateTimeOffset? LastScanUtc,
    int ShareCount,
    long FileCount,
    string? Error = null,
    bool SlskdUnavailable = false);
