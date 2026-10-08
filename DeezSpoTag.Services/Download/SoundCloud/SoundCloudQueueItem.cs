using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     The queued state of one SoundCloud download.
/// </summary>
/// <remarks>
///     Deliberately minimal, like every other engine's queue item. The cross-platform identity lives on the
///     base (<see cref="EngineQueueItemBase.SoundCloudId"/> and <see cref="EngineQueueItemBase.SourceUrl"/>);
///     only the state this engine records while acquiring audio is added here.
/// </remarks>
public sealed class SoundCloudQueueItem : EngineQueueItemBase
{
    /// <summary>The engine id, matching <c>DownloadSourceCatalog</c>.</summary>
    public const string EngineId = "soundcloud";

    public SoundCloudQueueItem()
    {
        Engine = EngineId;
        SourceService = EngineId;
    }

    [JsonIgnore]
    public SoundCloudDownloadStatus Status { get; set; } = SoundCloudDownloadStatus.Queued;

    /// <summary>Gets or sets the tier SoundCloud actually advertised for the stream that was used.</summary>
    public string SoundCloudResolvedQuality { get; set; } = "";

    /// <summary>Gets or sets the exact permalink the stream was resolved from.</summary>
    public string SoundCloudResolvedUrl { get; set; } = "";

    /// <summary>Gets or sets when the identity and stream were resolved.</summary>
    public DateTimeOffset? SoundCloudResolvedAtUtc { get; set; }

    /// <summary>Gets or sets how far acquisition progressed, for diagnostics and recovery.</summary>
    public string SoundCloudAcquisitionStage { get; set; } = "";

    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(
            MapStatusForUi(Status),
            new Dictionary<string, object?>
            {
                ["soundCloudResolvedQuality"] = SoundCloudResolvedQuality,
                ["soundCloudResolvedUrl"] = SoundCloudResolvedUrl,
                ["soundCloudResolvedAtUtc"] = SoundCloudResolvedAtUtc,
                ["soundCloudAcquisitionStage"] = SoundCloudAcquisitionStage
            });

    private static string MapStatusForUi(SoundCloudDownloadStatus status)
        => QueuePayloadBuilder.MapStatusForUi(status.ToString());
}

/// <summary>
///     Where a SoundCloud download is in its lifecycle.
/// </summary>
public enum SoundCloudDownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Skipped
}