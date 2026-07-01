using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Amazon;

public static class AmazonRequestBuilder
{
    public static AmazonDownloadRequest BuildRequest(AmazonQueueItem item, DeezSpoTagSettings settings)
    {
        var request = RequestBuilderCommon.CreateCommonRequest<AmazonDownloadRequest>(item, settings);
        request.Quality = string.IsNullOrWhiteSpace(item.Quality) ? "ULTRA_HD_FLAC" : item.Quality;
        return request;
    }
}

public sealed class AmazonDownloadRequest : EngineDownloadRequestBase
{
    public string Quality { get; set; } = "ULTRA_HD_FLAC";
}
