namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// LRCLIB lookup tunables. These are owned by the profile's "lrclib" platform card
/// (<c>profile.autoTag.custom.lrclib</c>) and are consumed by both the AutoTag runner and the
/// download lyrics pipeline, so the definition and its defaults live in one place.
///
/// The defaults here are the source of truth for the "no value stored" case and mirror
/// <c>DeezSpoTag.Web.Services.AutoTag.LrclibConfig</c>.
/// </summary>
public sealed class LrclibOptions
{
    /// <summary>Maximum difference, in seconds, between the file duration and LRCLIB's reported
    /// duration for a candidate to be accepted. Clamped to 0-60 when used.</summary>
    public int DurationToleranceSeconds { get; set; } = 10;

    /// <summary>Send the track duration to LRCLIB's metadata lookup when it is known.</summary>
    public bool UseDurationHint { get; set; } = true;

    /// <summary>Fall back to LRCLIB's /api/search when the exact metadata lookup misses.</summary>
    public bool SearchFallback { get; set; } = true;

    /// <summary>Rank synced lyrics above plain lyrics when several candidates match.</summary>
    public bool PreferSynced { get; set; } = true;

    public LrclibOptions Clone() => new()
    {
        DurationToleranceSeconds = DurationToleranceSeconds,
        UseDurationHint = UseDurationHint,
        SearchFallback = SearchFallback,
        PreferSynced = PreferSynced
    };
}