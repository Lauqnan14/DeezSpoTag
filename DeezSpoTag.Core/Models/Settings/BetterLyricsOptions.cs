namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// BetterLyrics lookup tunables. Owned by the profile's "betterlyrics" platform card
/// (<c>profile.autoTag.custom.betterlyrics</c>) and consumed by the download lyrics pipeline, so
/// the definition and its defaults live in one place.
///
/// BetterLyrics resolves lyrics by search (song + artist), not by track ID, so a response can
/// describe a different recording — a radio edit, a live take, or a same-titled song. The API does
/// not return a usable match-confidence score, so the length of the timed document is the only
/// trustworthy signal available.
///
/// The two directions are not symmetric, which is why only one is configurable:
///   - A document that ends far <em>short</em> of the track is missing verses. That is a wrong or
///     truncated recording, and it is rejected against a small fixed allowance (see
///     <see cref="ShorterAllowanceSeconds"/>).
///   - A document that runs <em>long</em> is usually correct lyrics plus trailing silence, an
///     outro, or a fade. Rejecting those would throw away good lyrics, so the allowance there is
///     deliberately generous and configurable via <see cref="DurationToleranceSeconds"/>.
/// </summary>
public sealed class BetterLyricsOptions
{
    /// <summary>
    /// How many seconds longer than the track the timed document may run before the response is
    /// treated as a different recording. Kept generous by default because trailing silence,
    /// outros and fade-outs routinely push the document end past the tagged duration. Clamped to
    /// 0-120 when used.
    /// </summary>
    public int DurationToleranceSeconds { get; set; } = 15;

    /// <summary>
    /// How many seconds shorter than the track the timed document may run before it is rejected.
    /// Not surfaced as a setting: a document missing more than this much material is missing
    /// verses, which is never what the user wants, so there is nothing to tune.
    /// </summary>
    public const int ShorterAllowanceSeconds = 5;
}
