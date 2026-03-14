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

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalEngineProcessor : IQueueEngineProcessor
{
    private const string EngineName = "tidal";
    private readonly EngineProcessorCommonDependencies _commonDependencies;
    private readonly TidalDownloadService _tidalDownloader;
    private readonly ILogger<TidalEngineProcessor> _logger;

    public TidalEngineProcessor(
        EngineProcessorCommonDependencies commonDependencies,
        TidalDownloadService tidalDownloader,
        ILogger<TidalEngineProcessor> logger)
    {
        _commonDependencies = commonDependencies;
        _tidalDownloader = tidalDownloader;
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
            new EngineQueueProcessorHelper.ProcessorCallbacks<TidalQueueItem>(
                payload => string.IsNullOrWhiteSpace(payload.TidalId) ? payload.SpotifyId : payload.TidalId,
                static (payload, settings) => TidalRequestBuilder.BuildRequest(payload, settings),
                static (request, context) =>
                {
                    var tidalRequest = (TidalDownloadRequest)request;
                    tidalRequest.OutputDir = context.OutputDir;
                    tidalRequest.FilenameFormat = context.FilenameFormat;
                },
                async (payload, request, settings, progressReporter, cancellationToken) =>
                {
                    var tidalRequest = (TidalDownloadRequest)request;

                    async Task<string> DownloadWithQualityAsync(string quality)
                    {
                        tidalRequest.Quality = quality;
                        return await _tidalDownloader.DownloadAsync(
                            tidalRequest,
                            settings.EmbedMaxQualityCover,
                            settings.Tags,
                            progressReporter,
                            cancellationToken);
                    }

                    try
                    {
                        return await DownloadWithQualityAsync(tidalRequest.Quality);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                               && settings.FallbackBitrate
                                               && !string.Equals(settings.Service, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        var fallbackQuality = EngineQualityFallback.GetNextLowerQuality(EngineName, tidalRequest.Quality);
                        if (string.IsNullOrWhiteSpace(fallbackQuality))
                        {
                            throw;
                        }

                        _logger.LogWarning(ex, "Tidal download failed at quality {Quality}, retrying at {Fallback}", tidalRequest.Quality, fallbackQuality);
                        tidalRequest.Quality = fallbackQuality;
                        payload.Quality = fallbackQuality;
                        return await DownloadWithQualityAsync(fallbackQuality);
                    }
                },
                async (payload, cancellationToken) =>
                {
                    if (!string.IsNullOrWhiteSpace(payload.SourceUrl) || !string.IsNullOrWhiteSpace(payload.SpotifyId))
                    {
                        return;
                    }

                    var resolvedUrl = await _tidalDownloader.ResolveTrackUrlAsync(
                        payload.Title ?? string.Empty,
                        payload.Artist ?? string.Empty,
                        payload.Isrc ?? string.Empty,
                        payload.DurationSeconds * 1000,
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(resolvedUrl))
                    {
                        payload.SourceUrl = resolvedUrl;
                    }
                },
                request => $"Download start: {next.QueueUuid} engine=tidal quality={((TidalDownloadRequest)request).Quality}",
                payload => payload.Title,
                static payload => payload.ToQueuePayload()),
            stoppingToken);
    }
}
