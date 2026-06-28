using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Qobuz;

public sealed class QobuzQueueItem : EngineQueueItemBase
{
    public QobuzQueueItem()
    {
        Engine = "qobuz";
        SourceService = "qobuz";
    }

    public string QobuzResolutionSource { get; set; } = "";
    public int? QobuzResolutionScore { get; set; }
    public string QobuzRequestedQuality { get; set; } = "";
    public string QobuzResolvedQuality { get; set; } = "";
    public int QobuzMaximumBitDepth { get; set; }
    public double QobuzMaximumSamplingRate { get; set; }
    public string QobuzCatalogQuality { get; set; } = "";
    public string QobuzQualityDecisionReason { get; set; } = "";

    [JsonIgnore]
    public QobuzDownloadStatus Status { get; set; } = QobuzDownloadStatus.Queued;

    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(
            MapStatusForUi(Status),
            new Dictionary<string, object?>
            {
                ["qobuzResolutionSource"] = QobuzResolutionSource,
                ["qobuzResolutionScore"] = QobuzResolutionScore,
                ["qobuzRequestedQuality"] = QobuzRequestedQuality,
                ["qobuzResolvedQuality"] = QobuzResolvedQuality,
                ["qobuzMaximumBitDepth"] = QobuzMaximumBitDepth,
                ["qobuzMaximumSamplingRate"] = QobuzMaximumSamplingRate,
                ["qobuzCatalogQuality"] = QobuzCatalogQuality,
                ["qobuzQualityDecisionReason"] = QobuzQualityDecisionReason
            });

    private static string MapStatusForUi(QobuzDownloadStatus status)
        => QueuePayloadBuilder.MapStatusForUi(status.ToString());
}

public enum QobuzDownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Skipped
}
