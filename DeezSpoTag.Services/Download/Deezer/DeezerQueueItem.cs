using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Deezer;

public sealed class DeezerQueueItem : EngineQueueItemBase
{
    public DeezerQueueItem()
    {
        Engine = "deezer";
        SourceService = "deezer";
        CollectionType = "track";
    }

    public string DeezerAlbumId { get; set; } = "";
    public string DeezerArtistId { get; set; } = "";
    public int Bitrate { get; set; }

    [JsonIgnore]
    public DeezerDownloadStatus Status { get; set; } = DeezerDownloadStatus.Queued;

    public Dictionary<string, object> ToQueuePayload()
    {
        return BuildQueuePayload(MapStatusForUi(Status));
    }

    private static string MapStatusForUi(DeezerDownloadStatus status)
    {
        return QueuePayloadBuilder.MapStatusForUi(status.ToString());
    }
}

public enum DeezerDownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Skipped
}
