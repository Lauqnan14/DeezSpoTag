using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Tidal;

public static class TidalRequestBuilder
{
    public static TidalDownloadRequest BuildRequest(TidalQueueItem item, DeezSpoTagSettings settings)
    {
        var request = RequestBuilderCommon.CreateCommonRequest<TidalDownloadRequest>(item, settings);
        request.Quality = RequestBuilderCommon.ResolvePreferredQuality(
            item.Quality,
            settings.TidalQuality,
            "LOSSLESS");
        request.IsVideo = string.Equals(item.ContentType, DeezSpoTag.Services.Download.Shared.Models.DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase);
        request.VideoOutputRoot = settings.Video?.VideoDownloadLocation ?? string.Empty;
        request.VideoMaxResolution = settings.Video?.TidalVideoMaxResolution ?? 1080;
        if (request.ServiceUrl.StartsWith("tidal:track:", StringComparison.OrdinalIgnoreCase))
        {
            request.ServiceUrl = string.Empty;
        }
        return request;
    }
}

public sealed class TidalDownloadRequest : EngineDownloadRequestBase
{
    public string Quality { get; set; } = "";
    public bool IsVideo { get; set; }
    public string VideoOutputRoot { get; set; } = "";
    public int VideoMaxResolution { get; set; } = 1080;
}
