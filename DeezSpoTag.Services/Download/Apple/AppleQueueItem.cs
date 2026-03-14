using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleQueueItem : EngineQueueItemBase
{
    public AppleQueueItem()
    {
        Engine = "apple";
        SourceService = "apple";
    }

    public int SegmentTotal { get; set; }
    public int SegmentProgress { get; set; }
    public string VideoResolution { get; set; } = "";
    public bool VideoHdr { get; set; }
    public string VideoAudioProfile { get; set; } = "";
    public bool HasAppleDigitalMaster { get; set; }

    [JsonIgnore]
    public AppleDownloadStatus Status { get; set; } = AppleDownloadStatus.Queued;

    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(
            MapStatusForUi(Status),
            new Dictionary<string, object?>
            {
                ["videoResolution"] = VideoResolution,
                ["videoHdr"] = VideoHdr,
                ["videoAudioProfile"] = VideoAudioProfile,
                ["segmentTotal"] = SegmentTotal,
                ["segmentProgress"] = SegmentProgress,
                ["hasAppleDigitalMaster"] = HasAppleDigitalMaster
            });

    private static string MapStatusForUi(AppleDownloadStatus status)
        => QueuePayloadBuilder.MapStatusForUi(status.ToString());
}

public enum AppleDownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Skipped
}
