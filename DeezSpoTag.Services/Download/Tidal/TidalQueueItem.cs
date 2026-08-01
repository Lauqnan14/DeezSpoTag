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

    [JsonIgnore]
    public TidalDownloadStatus Status { get; set; } = TidalDownloadStatus.Queued;

    public string TidalResolvedRepresentation { get; set; } = "";
    public string TidalResolvedQuality { get; set; } = "";
    public bool TidalAtmosConfirmed { get; set; }
    public string TidalPublicProviderId { get; set; } = "";
    public DateTimeOffset? TidalResolvedAtUtc { get; set; }
    public string TidalAcquisitionStage { get; set; } = "";

    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(
            MapStatusForUi(Status),
            new Dictionary<string, object?>
            {
                ["tidalResolvedRepresentation"] = TidalResolvedRepresentation,
                ["tidalResolvedQuality"] = TidalResolvedQuality,
                ["tidalAtmosConfirmed"] = TidalAtmosConfirmed,
                ["tidalPublicProviderId"] = TidalPublicProviderId,
                ["tidalResolvedAtUtc"] = TidalResolvedAtUtc,
                ["tidalAcquisitionStage"] = TidalAcquisitionStage
            });

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
