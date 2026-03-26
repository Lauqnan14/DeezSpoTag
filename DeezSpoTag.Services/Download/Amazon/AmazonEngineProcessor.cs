using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Queue;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonEngineProcessor : QueueEngineProcessorBase
{
    private const string EngineName = "amazon";
    private readonly IAmazonDownloadService _amazonDownloader;
    private readonly ILogger<AmazonEngineProcessor> _logger;

    public AmazonEngineProcessor(
        EngineProcessorCommonDependencies commonDependencies,
        IAmazonDownloadService amazonDownloader,
        ILogger<AmazonEngineProcessor> logger) : base(EngineName, commonDependencies)
    {
        _amazonDownloader = amazonDownloader;
        _logger = logger;
    }

    public override async Task ProcessQueueItemAsync(DownloadQueueItem item, CancellationToken cancellationToken)
    {
        await EngineQueueProcessorHelper.ProcessQueueItemAsync(
            item,
            EngineName,
            CommonDependencies.CreateProcessorDeps(_logger),
            new EngineQueueProcessorHelper.ProcessorCallbacks<AmazonQueueItem>(
                payload => string.IsNullOrWhiteSpace(payload.AmazonId) ? payload.SpotifyId : payload.AmazonId,
                static (payload, settings) => AmazonRequestBuilder.BuildRequest(payload, settings),
                static (request, context) =>
                {
                    var amazonRequest = (AmazonDownloadRequest)request;
                    amazonRequest.OutputDir = context.OutputDir;
                    amazonRequest.FilenameFormat = context.FilenameFormat;
                },
                async (payload, request, settings, progressReporter, cancellationToken) =>
                {
                    var amazonRequest = (AmazonDownloadRequest)request;
                    return await _amazonDownloader.DownloadAsync(
                        amazonRequest,
                        settings.EmbedMaxQualityCover,
                        settings.Tags,
                        progressReporter,
                        cancellationToken);
                },
                null,
                request => $"Download start: {item.QueueUuid} engine=amazon",
                payload => payload.Title,
                static payload => payload.ToQueuePayload()),
            cancellationToken);
    }
}
