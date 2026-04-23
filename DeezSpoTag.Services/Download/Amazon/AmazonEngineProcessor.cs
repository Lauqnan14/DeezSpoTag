using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Queue;
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
        return await _amazonDownloader.DownloadAsync(
            amazonRequest,
            settings.EmbedMaxQualityCover,
            settings.Tags,
            progressReporter,
            cancellationToken);
    }

    private static string ResolveAmazonSourceId(AmazonQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.AmazonId))
        {
            return payload.AmazonId.Trim();
        }

        var fromSource = TryExtractAmazonTrackId(payload.SourceUrl);
        if (!string.IsNullOrWhiteSpace(fromSource))
        {
            return fromSource;
        }

        return TryExtractAmazonTrackId(payload.Url) ?? string.Empty;
    }

    private static string? TryExtractAmazonTrackId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        return TryExtractTrackAsinFromQuery(parsed.Query)
            ?? TryExtractTrackIdFromPath(parsed.AbsolutePath);
    }

    private static string? TryExtractTrackAsinFromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var token = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(pair =>
                pair.Length == 2 &&
                pair[0].Equals("trackAsin", StringComparison.OrdinalIgnoreCase));

        if (token == null || token.Length != 2)
        {
            return null;
        }

        var value = Uri.UnescapeDataString(token[1]);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? TryExtractTrackIdFromPath(string absolutePath)
    {
        var segments = absolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var trackIndex = Array.FindIndex(segments, segment =>
            segment.Equals("tracks", StringComparison.OrdinalIgnoreCase));
        if (trackIndex < 0 || trackIndex >= segments.Length - 1)
        {
            return null;
        }

        var candidate = segments[trackIndex + 1];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }
}
