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
    /// Primary engine used for the Atmos/profile A download branch.
    /// </summary>
    public string AtmosEngine { get; set; } = "apple";

    /// <summary>
    /// Allows Atmos lookup to try the other Atmos-capable engine when the selected one has no mapping.
    /// </summary>
    public bool AtmosSearchFallback { get; set; } = false;

    /// <summary>
    /// Allows the queued Atmos download to fall back to the other Atmos-capable engine if the selected engine fails.
    /// </summary>
    public bool AtmosDownloadFallback { get; set; } = false;

}
