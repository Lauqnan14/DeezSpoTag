using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     The SoundCloud download engine.
/// </summary>
/// <remarks>
///     A thin adapter onto the shared engine pipeline, shaped like every other engine's processor. Candidate
///     resolution, the acquired staging file, quality verification, tagging, the final destination, retries, and
///     fallback all come from <c>EngineQueueProcessorHelper</c> and the services it calls, so SoundCloud behaves
///     like the rest rather than reimplementing any of it.
/// </remarks>
public sealed class SoundCloudEngineProcessor : QueueEngineProcessorBase
{
    private const string EngineName = SoundCloudQueueItem.EngineId;

    private readonly SoundCloudDownloadService _soundCloudDownloader;
    private readonly ILogger<SoundCloudEngineProcessor> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoundCloudEngineProcessor" /> class.</summary>
    public SoundCloudEngineProcessor(
        EngineProcessorCommonDependencies commonDependencies,
        SoundCloudDownloadService soundCloudDownloader,
        ILogger<SoundCloudEngineProcessor> logger)
        : base(EngineName, commonDependencies)
    {
        _soundCloudDownloader = soundCloudDownloader;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task ProcessQueueItemAsync(DownloadQueueItem item, CancellationToken cancellationToken)
    {
        await EngineQueueProcessorHelper.ProcessQueueItemAsync(
            item,
            EngineName,
            CommonDependencies.CreateProcessorDeps(_logger),
            new EngineQueueProcessorHelper.ProcessorCallbacks<SoundCloudQueueItem>(
                ResolveSoundCloudSourceId,
                (payload, settings) =>
                {
                    DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(settings, payload.QualityBucket);
                    return SoundCloudRequestBuilder.BuildRequest(payload, settings);
                },
                static (request, context) =>
                {
                    var soundCloudRequest = (SoundCloudDownloadRequest)request;
                    soundCloudRequest.OutputDir = context.OutputDir;
                    soundCloudRequest.FilenameFormat = context.FilenameFormat;
                },
                async (payload, request, settings, progressReporter, cancellationToken) =>
                {
                    var soundCloudRequest = (SoundCloudDownloadRequest)request;
                    payload.SoundCloudResolvedQuality = soundCloudRequest.Quality;
                    payload.SoundCloudResolvedUrl = soundCloudRequest.SoundCloudTrackUrl;
                    payload.SoundCloudResolvedAtUtc ??= DateTimeOffset.UtcNow;
                    payload.SoundCloudAcquisitionStage = "audio_acquisition";
                    payload.Status = SoundCloudDownloadStatus.Downloading;
                    try
                    {
                        var downloadedPath = await _soundCloudDownloader
                            .DownloadAsync(soundCloudRequest, progressReporter, cancellationToken)
                            .ConfigureAwait(false);
                        payload.SoundCloudResolvedUrl = soundCloudRequest.SoundCloudTrackUrl;
                        payload.SoundCloudAcquisitionStage = "audio_acquired";
                        payload.Status = SoundCloudDownloadStatus.Completed;
                        return downloadedPath;
                    }
                    catch (SoundCloudExistingFinalDestinationException existing)
                        when (DownloadLifecycleCheckpoint.TryAdoptExistingAudioAtPath(payload, existing.FilePath))
                    {
                        payload.SoundCloudAcquisitionStage = "audio_recovered";
                        return existing.FilePath;
                    }
                    catch
                    {
                        payload.SoundCloudResolvedUrl = soundCloudRequest.SoundCloudTrackUrl;
                        payload.SoundCloudAcquisitionStage = "audio_acquisition_failed";
                        payload.Status = SoundCloudDownloadStatus.Failed;
                        throw;
                    }
                },
                (payload, _) =>
                {
                    payload.SoundCloudResolvedQuality = payload.Quality;
                    payload.SoundCloudResolvedAtUtc ??= DateTimeOffset.UtcNow;
                    payload.SoundCloudAcquisitionStage = "identity_resolved";
                    return Task.CompletedTask;
                },
                request => $"Download start: {item.QueueUuid} engine=soundcloud quality={((SoundCloudDownloadRequest)request).Quality}",
                payload => payload.Title,
                static payload => payload.ToQueuePayload(),
                async (payload, acquiredPath, token) =>
                {
                    var promoted = await _soundCloudDownloader
                        .PromoteAcceptedAudioAsync(payload, acquiredPath, token)
                        .ConfigureAwait(false);
                    payload.SoundCloudAcquisitionStage = "audio_accepted";
                    return promoted;
                },
                (payload, rejectedPath, _) =>
                {
                    _soundCloudDownloader.DeleteRejectedStagingAudio(payload, rejectedPath);
                    return Task.CompletedTask;
                }),
            cancellationToken);
    }

    /// <summary>
    ///     Returns the SoundCloud identity this download will use.
    /// </summary>
    /// <remarks>
    ///     The permalink is preferred over the bare id because a SoundCloud id cannot be turned back into a URL:
    ///     the path contains the uploader and track slugs, not the numeric id. An item that arrived as a
    ///     fallback therefore reports the url it was resolved to, and an item that was pasted reports its own.
    /// </remarks>
    private static string ResolveSoundCloudSourceId(SoundCloudQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.SoundCloudResolvedUrl)
            && SoundCloudHydrationParser.IsSoundCloudTrackUrl(payload.SoundCloudResolvedUrl))
        {
            return payload.SoundCloudResolvedUrl;
        }

        if (SoundCloudHydrationParser.IsSoundCloudTrackUrl(payload.SourceUrl))
        {
            return payload.SourceUrl;
        }

        return payload.SoundCloudId ?? string.Empty;
    }
}