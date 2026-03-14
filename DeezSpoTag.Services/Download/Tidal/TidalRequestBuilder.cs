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
        return request;
    }
}

public sealed class TidalDownloadRequest : EngineDownloadRequestBase
{
    public string Quality { get; set; } = "";
}
