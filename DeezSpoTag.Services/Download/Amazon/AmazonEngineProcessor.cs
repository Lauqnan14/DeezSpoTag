using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Core.Models.Settings;
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
            BuildCallbacks(item),
            cancellationToken);
    }

    private EngineQueueProcessorHelper.ProcessorCallbacks<AmazonQueueItem> BuildCallbacks(DownloadQueueItem item) =>
        new(
            ResolveAmazonSourceId,
            BuildRequest,
            ApplyRequestContext,
            DownloadAsync,
            null,
            _ => $"Download start: {item.QueueUuid} engine=amazon",
            payload => payload.Title,
            static payload => payload.ToQueuePayload());

    private static AmazonDownloadRequest BuildRequest(AmazonQueueItem payload, DeezSpoTagSettings settings)
    {
        DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(settings, payload.QualityBucket);
        return AmazonRequestBuilder.BuildRequest(payload, settings);
    }

    private static void ApplyRequestContext(object request, EngineAudioPostDownloadHelper.EngineTrackContext context)
    {
        var amazonRequest = (AmazonDownloadRequest)request;
        amazonRequest.OutputDir = context.OutputDir;
        amazonRequest.FilenameFormat = context.FilenameFormat;
    }

    private async Task<string> DownloadAsync(
        AmazonQueueItem payload,
        object request,
        DeezSpoTagSettings settings,
        Func<double, double, Task>? progressReporter,
        CancellationToken cancellationToken)
    {
        var amazonRequest = (AmazonDownloadRequest)request;
        if (RequiresStrictPreflight(amazonRequest.Quality))
        {
            payload.RequestedQuality = amazonRequest.Quality;
            payload.DeliveredQuality = "Quality not preflighted";
            throw new InvalidOperationException(
                "Amazon Ultra HD FLAC requires verified 24-bit catalog quality before download; current public provider does not expose bit-depth preflight.");
        }

        return await _amazonDownloader.DownloadAsync(
            amazonRequest,
            settings.EmbedMaxQualityCover,
            settings.Tags,
            progressReporter,
            cancellationToken);
    }

    private static bool RequiresStrictPreflight(string? quality)
        => string.Equals(
            (quality ?? string.Empty).Trim(),
            "ULTRA_HD_FLAC",
            StringComparison.OrdinalIgnoreCase);

    private static string ResolveAmazonSourceId(AmazonQueueItem payload)
    {
        var persistedId = EngineLinkParser.NormalizeAmazonTrackId(payload.AmazonId);
        if (!string.IsNullOrWhiteSpace(persistedId))
        {
            return persistedId;
        }

        var fromSource = EngineLinkParser.TryExtractAmazonTrackId(payload.SourceUrl, TimeSpan.FromMilliseconds(250));
        if (!string.IsNullOrWhiteSpace(fromSource))
        {
            return fromSource;
        }

        return EngineLinkParser.TryExtractAmazonTrackId(payload.Url, TimeSpan.FromMilliseconds(250)) ?? string.Empty;
    }
}
