namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Owns the Soulseek connection and session state that the download engine, the API and the UI share.
/// </summary>
/// <remarks>
///     The login page and the sidebar are already driven by the shipped connection probe. This service is
///     the single place the rest of the Soulseek integration asks about connectivity, so a download is
///     never attempted against an instance that is known to be down.
/// </remarks>
public interface ISoulseekConnectionService
{
    /// <summary>
    ///     Probes slskd and returns the current connection state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="force">
    ///     When <see langword="true"/>, probes even if a recent probe result is cached. Used by the
    ///     connection test endpoint.
    /// </param>
    Task<SoulseekConnectionStatus> GetStatusAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-checks slskd now and reports whether Soulseek may be used for new work.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the gate new remote work passes through, so it always probes rather than reading the
    ///         cache. A cached answer is right for display and wrong for admission: up to twenty seconds may
    ///         have passed since it was taken, which is long enough for a peer to have dropped or for the reader
    ///         to have logged out.
    ///     </para>
    ///     <para>
    ///         It never asks slskd to log in. That is the explicit Connect action's job, and a search or a
    ///         download silently reconnecting is how a source comes back after a logout without the reader
    ///         doing anything.
    ///     </para>
    /// </remarks>
    Task<SoulseekConnectionStatus> GetEligibilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Forces a fresh probe and, when slskd is reachable but logged out, asks it to reconnect before
    ///     returning the resulting state.
    /// </summary>
    Task<SoulseekConnectionStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns a value indicating whether Soulseek downloads may currently be attempted.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Invalidates the cached state so the next read probes again. Called after the user saves or
    ///     disconnects slskd credentials.
    /// </summary>
    void Invalidate();
}
