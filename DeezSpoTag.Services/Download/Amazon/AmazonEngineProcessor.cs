using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonEngineProcessor : IQueueEngineProcessor
{
    private const string EngineName = "amazon";
    private readonly EngineProcessorCommonDependencies _commonDependencies;
    private readonly IAmazonDownloadService _amazonDownloader;
    private readonly ILogger<AmazonEngineProcessor> _logger;

    public AmazonEngineProcessor(
        EngineProcessorCommonDependencies commonDependencies,
        IAmazonDownloadService amazonDownloader,
        ILogger<AmazonEngineProcessor> logger)
    {
        _commonDependencies = commonDependencies;
        _amazonDownloader = amazonDownloader;
        _logger = logger;
    }

    public string Engine => EngineName;

    Task IQueueEngineProcessor.ProcessQueueItemAsync(
        DownloadQueueItem item,
        DeezSpoTag.Services.Download.Deezer.IDeezerQueueContext context,
        CancellationToken cancellationToken) =>
        ProcessQueueItemAsync(item, cancellationToken);

    public async Task ProcessQueueItemAsync(DownloadQueueItem next, CancellationToken stoppingToken)
    {
        await EngineQueueProcessorHelper.ProcessQueueItemAsync(
            next,
            EngineName,
            _commonDependencies.CreateProcessorDeps(_logger),
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
                request => $"Download start: {next.QueueUuid} engine=amazon",
                payload => payload.Title,
                static payload => payload.ToQueuePayload()),
            stoppingToken);
    }
}
