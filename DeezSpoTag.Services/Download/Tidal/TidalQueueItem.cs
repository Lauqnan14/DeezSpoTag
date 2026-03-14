using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalQueueItem : EngineQueueItemBase
{
    public TidalQueueItem()
    {
        Engine = "tidal";
        SourceService = "tidal";
    }

    public string TidalId { get; set; } = "";

    [JsonIgnore]
    public TidalDownloadStatus Status { get; set; } = TidalDownloadStatus.Queued;

    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(MapStatusForUi(Status));

    private static string MapStatusForUi(TidalDownloadStatus status)
        => QueuePayloadBuilder.MapStatusForUi(status.ToString());
}

public enum TidalDownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Skipped
}
