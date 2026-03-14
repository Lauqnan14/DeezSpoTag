using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonQueueItem : EngineQueueItemBase
{
    public AmazonQueueItem()
    {
        Engine = "amazon";
        SourceService = "amazon";
    }

    public string AmazonId { get; set; } = "";

    [JsonIgnore]
    public AmazonDownloadStatus Status { get; set; } = AmazonDownloadStatus.Queued;

    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(MapStatusForUi(Status));

    private static string MapStatusForUi(AmazonDownloadStatus status)
        => QueuePayloadBuilder.MapStatusForUi(status.ToString());
}

public enum AmazonDownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Skipped
}
