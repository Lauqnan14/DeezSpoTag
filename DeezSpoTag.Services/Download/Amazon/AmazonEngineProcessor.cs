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
                ResolveAmazonSourceId,
                (payload, settings) =>
                {
                    DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(settings, payload.QualityBucket);
                    return AmazonRequestBuilder.BuildRequest(payload, settings);
                },
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

        var query = parsed.Query ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var trimmed = query.TrimStart('?');
            var tokens = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                var pair = token.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2)
                {
                    continue;
                }

                if (!pair[0].Equals("trackAsin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = Uri.UnescapeDataString(pair[1]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        var segments = parsed.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("tracks", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = segments[i + 1];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
