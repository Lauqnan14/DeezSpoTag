namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// Musixmatch lookup tunables. Owned by the profile's "musixmatch" platform card
/// (<c>profile.autoTag.custom.musixmatch</c>) and consumed by the download lyrics pipeline, so the
/// definition and its defaults live in one place.
///
/// The defaults here are the source of truth for the "no value stored" case. They preserve the
/// constants that were previously hardcoded in <c>LyricsService</c>.
/// </summary>
public sealed class MusixmatchOptions
{
    /// <summary>Maximum difference, in seconds, between the file duration and a candidate's length.</summary>
    public int DurationToleranceSeconds { get; set; } = 10;

    /// <summary>How many candidates the Musixmatch track search may return.</summary>
    public int SearchPageSize { get; set; } = 10;

    /// <summary>Drift allowance for word-level (richsync) synchronisation.</summary>
    public int RichsyncMaxDeviationSeconds { get; set; } = 10;

    /// <summary>Drift allowance for line-level (subtitle) synchronisation.</summary>
    public int SubtitleMaxDeviationSeconds { get; set; } = 10;

    public MusixmatchOptions Clone() => new()
    {
        DurationToleranceSeconds = DurationToleranceSeconds,
        SearchPageSize = SearchPageSize,
        RichsyncMaxDeviationSeconds = RichsyncMaxDeviationSeconds,
        SubtitleMaxDeviationSeconds = SubtitleMaxDeviationSeconds
    };
}