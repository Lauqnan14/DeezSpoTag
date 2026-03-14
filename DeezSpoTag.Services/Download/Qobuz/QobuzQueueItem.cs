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

    public string QobuzId { get; set; } = "";
    public string QobuzResolutionSource { get; set; } = "";
    public int? QobuzResolutionScore { get; set; }

    [JsonIgnore]
    public QobuzDownloadStatus Status { get; set; } = QobuzDownloadStatus.Queued;

    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(
            MapStatusForUi(Status),
            new Dictionary<string, object?>
            {
                ["qobuzId"] = QobuzId,
                ["qobuzResolutionSource"] = QobuzResolutionSource,
                ["qobuzResolutionScore"] = QobuzResolutionScore
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
