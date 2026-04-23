using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
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
                                               && ShouldUseInEngineQualityFallback(payload))
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
                        payload.DurationSeconds,
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(resolvedUrl))
                    {
                        payload.SourceUrl = resolvedUrl;
                    }
                },
                request => $"Download start: {item.QueueUuid} engine=tidal quality={((TidalDownloadRequest)request).Quality}",
                payload => payload.Title,
                static payload => payload.ToQueuePayload()),
            cancellationToken);
    }

    private static bool ShouldUseInEngineQualityFallback(TidalQueueItem payload)
    {
        return EngineFallbackPlanPolicy.ShouldUseInEngineFallback(payload, EngineName);
    }

    private static string ResolveTidalSourceId(TidalQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.TidalId))
        {
            return payload.TidalId.Trim();
        }

        var fromSource = TryExtractTrackId(payload.SourceUrl);
        if (!string.IsNullOrWhiteSpace(fromSource))
        {
            return fromSource;
        }

        return TryExtractTrackId(payload.Url) ?? string.Empty;
    }

    private static string? TryExtractTrackId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        var segments = parsed.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = segments[i + 1];
            if (candidate.All(char.IsDigit))
            {
                return candidate;
            }
        }

        return null;
    }
}
