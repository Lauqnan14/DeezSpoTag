namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// Allows downloading multiple quality variants (e.g., Atmos + stereo) for the same items
/// by enqueuing two tasks with different source/quality/destination settings.
/// </summary>
public sealed class MultiQualityDownloadSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When enabled, the app may enqueue a second download task for the same track.
    /// </summary>
    public bool SecondaryEnabled { get; set; } = false;

    /// <summary>
    /// Optional destination folder for Atmos/profile A downloads.
    /// If null, uses the request destination folder (or default download root).
    /// </summary>
    public long? PrimaryDestinationFolderId { get; set; }

    /// <summary>
    /// Optional destination folder for stereo/profile B downloads.
    /// If null, uses the request destination folder (or default download root).
    /// </summary>
    public long? SecondaryDestinationFolderId { get; set; }

    /// <summary>
    /// Secondary source preference. Common values: auto, tidal, qobuz, amazon, deezer, apple.
    /// </summary>
    public string SecondaryService { get; set; } = "auto";

    /// <summary>
    /// Secondary quality selector (engine-specific value), or blank to use engine defaults.
    /// Examples: HI_RES_LOSSLESS (tidal), 27 (qobuz), 9/3/1 (deezer), ALAC/AAC/ATMOS (apple).
    /// </summary>
    public string SecondaryQuality { get; set; } = string.Empty;

    /// <summary>
    /// If true, the secondary auto chain excludes Apple steps.
    /// This supports "Atmos from Apple, stereo from elsewhere" patterns.
    /// </summary>
    public bool SecondaryExcludeApple { get; set; } = true;
}

