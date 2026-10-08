namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Runs Soulseek searches through slskd and turns the responses into DeezSpoTag candidates.
/// </summary>
public interface ISoulseekSearchService
{
    /// <summary>
    ///     Runs a search and returns the scored candidates.
    /// </summary>
    /// <param name="target">The track to search for, built from the same metadata other engines use.</param>
    /// <param name="mode">
    ///     Whether a person or automation is driving the search. Automated searches apply stricter
    ///     matching than manual ones.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="queueUuid">
    ///     The download queue item the search is for, when there is one. It is recorded and published so
    ///     progress can be attributed to a download instead of floating unassociated.
    /// </param>
    /// <param name="requiredQualityCode">
    ///     The quality this search must satisfy, when the caller knows it - the queue's current ladder step.
    ///     Null adds no minimum; the Source-enabled Soulseek qualities still apply.
    /// </param>
    Task<SoulseekSearchOutcome> SearchAsync(
        SoulseekSearchTarget target,
        SoulseekSearchMode mode = SoulseekSearchMode.Manual,
        string? queueUuid = null,
        CancellationToken cancellationToken = default,
        string? requiredQualityCode = null);

    /// <summary>
    ///     Reads a peer's directory listing.
    /// </summary>
    /// <param name="username">The peer to browse.</param>
    /// <param name="directory">The directory within the peer's share, or <see langword="null"/> for the root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <summary>
    ///     Watches an already-started search, publishing ranked results as they arrive.
    /// </summary>
    /// <remarks>
    ///     Split from the blocking search so a caller can return as soon as the remote search exists and let
    ///     results stream over the hub. Both paths share this one loop.
    /// </remarks>
    /// <param name="requiredQualityCode">
    ///     The quality this search must satisfy, when the caller knows it. Carried through to ranking for the
    ///     same reason as on <see cref="SearchAsync" />.
    /// </param>
    Task<SoulseekSearchOutcome> ObserveAsync(
        SoulseekSearchTarget target,
        Guid searchId,
        string searchText,
        SoulseekSearchMode mode,
        string? queueUuid,
        CancellationToken cancellationToken = default,
        string? requiredQualityCode = null);

    Task<IReadOnlyList<DeezSpoTag.Integrations.Soulseek.SlskdDirectory>> BrowseAsync(
        string username,
        string? directory = null,
        CancellationToken cancellationToken = default);


    /// <summary>
    ///     Deletes searches slskd is still holding that DeezSpoTag no longer needs.
    /// </summary>
    /// <param name="olderThan">Only remove searches that ended before this instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of searches deleted.</returns>
    Task<int> CleanupStaleSearchesAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
}
