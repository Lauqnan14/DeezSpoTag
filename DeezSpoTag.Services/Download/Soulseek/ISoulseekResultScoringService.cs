namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Decides whether a Soulseek candidate is good enough, and how good it is.
/// </summary>
/// <remarks>
///     The rules are strict and inspired by Crate: identity first, then quality, then peer health, then
///     filename hygiene. Automated queueing uses tighter thresholds than a manual browse.
/// </remarks>
public interface ISoulseekResultScoringService
{
    /// <summary>
    ///     Scores and filters a set of raw candidates.
    /// </summary>
    /// <param name="target">The track that was searched for.</param>
    /// <param name="raw">The candidates slskd returned.</param>
    /// <param name="mode">Whether a person or automation is driving the search.</param>
    /// <param name="allowedQualities">
    ///     Optional narrowing of the Source-enabled quality codes. Null uses the current Source selection;
    ///     an empty list permits none.
    /// </param>
    /// <param name="allowUnknownQuality">
    ///     Overrides the persisted setting. Unknown quality is never accepted unless this is explicitly on.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="requiredQualityCode">
    ///     The quality code this particular search must satisfy, when the caller knows it - the queue's current
    ///     ladder step. Null adds no minimum; Source-enabled qualities still apply.
    /// </param>
    /// <returns>The scored candidates, best first, each marked accepted or rejected with a reason.</returns>
    Task<IReadOnlyList<SoulseekCandidate>> ScoreAsync(
        SoulseekSearchTarget target,
        IReadOnlyList<SoulseekRawCandidate> raw,
        SoulseekSearchMode mode = SoulseekSearchMode.Manual,
        IReadOnlyList<string>? allowedQualities = null,
        bool? allowUnknownQuality = null,
        CancellationToken cancellationToken = default,
        string? requiredQualityCode = null);

    /// <summary>
    ///     Picks the best accepted candidate.
    /// </summary>
    /// <returns>The winning candidate, or <see langword="null"/> when nothing was accepted.</returns>
    SoulseekCandidate? SelectBest(IReadOnlyList<SoulseekCandidate> candidates);
}
