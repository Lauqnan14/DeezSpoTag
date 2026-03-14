using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Amazon;

public static class AmazonRequestBuilder
{
    public static AmazonDownloadRequest BuildRequest(AmazonQueueItem item, DeezSpoTagSettings settings)
    {
        return RequestBuilderCommon.CreateCommonRequest<AmazonDownloadRequest>(item, settings);
    }
}

public sealed class AmazonDownloadRequest : EngineDownloadRequestBase
{
}
