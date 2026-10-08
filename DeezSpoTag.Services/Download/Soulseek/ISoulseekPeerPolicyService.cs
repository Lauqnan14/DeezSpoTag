namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Applies the user's peer rules: blocklists, queue length, free slots, upload speed and cooldowns.
/// </summary>
/// <remarks>
///     This is the Crate-style guard that keeps automation away from peers that will stall a download.
///     It runs before scoring so that a rejected peer never influences candidate selection.
/// </remarks>
public interface ISoulseekPeerPolicyService
{
    /// <summary>
    ///     Decides whether a peer may be downloaded from right now.
    /// </summary>
    /// <param name="candidate">The candidate, carrying the peer's advertised health.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SoulseekPeerDecision> EvaluateAsync(SoulseekRawCandidate candidate, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Decides whether a peer may be downloaded from, against cooldowns already read in bulk.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Scoring a search re-evaluates every candidate it has seen, and a peer with ten files in a
    ///         response is ten candidates. Resolving each one's cooldown individually costs a database read
    ///         per candidate. Passing the set in collapses that to one read per scoring pass.
    ///     </para>
    ///     <para>
    ///         The caller must build the set with <see cref="GetCooldownsAsync" /> at the moment it is used.
    ///         It is a snapshot, not a cache: a caller that needs a decision to reflect the very latest peer
    ///         state, such as the transfer path just before a download starts, should use the overload that
    ///         reads for itself.
    ///     </para>
    /// </remarks>
    /// <param name="candidate">The candidate, carrying the peer's advertised health.</param>
    /// <param name="cooldownsInEffect">
    ///     The peers in cooldown as of the call, with the instant each becomes usable again.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SoulseekPeerDecision> EvaluateAsync(
        SoulseekRawCandidate candidate,
        IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns a value indicating whether a filename matches one of the user's blocked patterns.
    /// </summary>
    /// <remarks>
    ///     This is part of the peer policy rather than a scoring concern, because a blocklisted pattern must
    ///     disqualify a candidate outright rather than merely lower its score.
    /// </remarks>
    bool IsBlockedFilename(string? filename);

    /// <summary>
    ///     Records that a peer let us down, starting or extending its cooldown.
    /// </summary>
    /// <param name="username">The peer to penalise.</param>
    /// <param name="reason">A short reason code, used to make the cooldown grow on repeat offences.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordFailureAsync(string username, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears a peer's penalty after a transfer succeeds.
    /// </summary>
    Task RecordSuccessAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the peers currently in cooldown, with the instant they become usable again.
    /// </summary>
    Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> GetCooldownsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes cooldowns that have expired.
    /// </summary>
    /// <returns>The number of rows removed.</returns>
    Task<int> CleanupExpiredCooldownsAsync(CancellationToken cancellationToken = default);
}
