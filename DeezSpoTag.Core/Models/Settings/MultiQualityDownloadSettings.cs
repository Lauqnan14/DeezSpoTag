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

}
