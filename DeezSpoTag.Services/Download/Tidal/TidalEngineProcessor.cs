using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalEngineProcessor : QueueEngineProcessorBase
{
    private const string EngineName = "tidal";
    private readonly TidalDownloadService _tidalDownloader;
    private readonly ILogger<TidalEngineProcessor> _logger;

    public TidalEngineProcessor(
        EngineProcessorCommonDependencies commonDependencies,
        TidalDownloadService tidalDownloader,
        ILogger<TidalEngineProcessor> logger) : base(EngineName, commonDependencies)
    {
        _tidalDownloader = tidalDownloader;
        _logger = logger;
    }

    public override async Task ProcessQueueItemAsync(DownloadQueueItem item, CancellationToken cancellationToken)
    {
        await EngineQueueProcessorHelper.ProcessQueueItemAsync(
            item,
            EngineName,
            CommonDependencies.CreateProcessorDeps(_logger),
            new EngineQueueProcessorHelper.ProcessorCallbacks<TidalQueueItem>(
                ResolveTidalSourceId,
                (payload, settings) =>
                {
                    DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(settings, payload.QualityBucket);
                    return TidalRequestBuilder.BuildRequest(payload, settings);
                },
                static (request, context) =>
                {
                    var tidalRequest = (TidalDownloadRequest)request;
                    tidalRequest.OutputDir = context.OutputDir;
                    tidalRequest.FilenameFormat = context.FilenameFormat;
                },
                async (payload, request, settings, progressReporter, cancellationToken) =>
                {
                    var tidalRequest = (TidalDownloadRequest)request;
                    return await _tidalDownloader.DownloadAsync(
                        tidalRequest,
                        settings.EmbedMaxQualityCover,
                        settings.Tags,
                        progressReporter,
                        cancellationToken);
                },
                null,
                request => $"Download start: {item.QueueUuid} engine=tidal quality={((TidalDownloadRequest)request).Quality}",
                payload => payload.Title,
                static payload => payload.ToQueuePayload()),
            cancellationToken);
    }

    private static string ResolveTidalSourceId(TidalQueueItem payload)
    {
        var persistedId = EngineLinkParser.NormalizeNumericTrackId(payload.TidalId);
        return persistedId ?? string.Empty;
    }
}
