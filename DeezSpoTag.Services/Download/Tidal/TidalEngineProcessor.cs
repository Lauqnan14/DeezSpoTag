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
                    payload.TidalResolvedRepresentation = DownloadEngineSettingsHelper.IsAtmosOnlyPayload(
                        payload.ContentType,
                        payload.Quality)
                            ? "atmos"
                            : "stereo";
                    payload.TidalResolvedQuality = tidalRequest.Quality;
                    payload.TidalAtmosConfirmed = false;
                    payload.TidalResolvedAtUtc ??= DateTimeOffset.UtcNow;
                    payload.TidalAcquisitionStage = "audio_acquisition";
                    try
                    {
                        var downloadedPath = await _tidalDownloader.DownloadAsync(
                            tidalRequest,
                            settings.EmbedMaxQualityCover,
                            settings.Tags,
                            progressReporter,
                            cancellationToken);
                        payload.TidalPublicProviderId = tidalRequest.ResolvedPublicProviderId;
                        payload.TidalId = tidalRequest.TidalId;
                        return downloadedPath;
                    }
                    catch (TidalExistingFinalDestinationException existing)
                        when (DownloadLifecycleCheckpoint.TryAdoptExistingAudioAtPath(payload, existing.FilePath))
                    {
                        payload.TidalId = tidalRequest.TidalId;
                        payload.TidalAcquisitionStage = "audio_recovered";
                        return existing.FilePath;
                    }
                    catch
                    {
                        payload.TidalId = tidalRequest.TidalId;
                        payload.TidalPublicProviderId = tidalRequest.ResolvedPublicProviderId;
                        payload.TidalAcquisitionStage = "audio_acquisition_failed";
                        throw;
                    }
                },
                (payload, _) =>
                {
                    payload.TidalResolvedRepresentation = DownloadEngineSettingsHelper.IsAtmosOnlyPayload(
                        payload.ContentType,
                        payload.Quality)
                            ? "atmos"
                            : "stereo";
                    payload.TidalResolvedQuality = payload.Quality;
                    payload.TidalResolvedAtUtc ??= DateTimeOffset.UtcNow;
                    payload.TidalAcquisitionStage = "identity_resolved";
                    return Task.CompletedTask;
                },
                request => $"Download start: {item.QueueUuid} engine=tidal quality={((TidalDownloadRequest)request).Quality}",
                payload => payload.Title,
                static payload => payload.ToQueuePayload(),
                async (payload, acquiredPath, token) =>
                {
                    var promoted = await _tidalDownloader.PromoteAcceptedAudioAsync(payload, acquiredPath, token);
                    payload.TidalAcquisitionStage = "audio_accepted";
                    payload.TidalAtmosConfirmed = string.Equals(
                        payload.TidalResolvedRepresentation,
                        "atmos",
                        StringComparison.OrdinalIgnoreCase);
                    return promoted;
                },
                (payload, rejectedPath, token) =>
                {
                    _tidalDownloader.DeleteRejectedStagingAudio(payload, rejectedPath);
                    return Task.CompletedTask;
                }),
            cancellationToken);
    }

    private static string ResolveTidalSourceId(TidalQueueItem payload)
    {
        var persistedId = EngineLinkParser.NormalizeNumericTrackId(payload.TidalId);
        return persistedId ?? string.Empty;
    }
}
