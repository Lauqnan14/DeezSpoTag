using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace DeezSpoTag.Web.Services;

[ExcludeFromCodeCoverage]
public sealed class DownloadQueuePreResolutionService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadQueuePreResolutionService> _logger;

    public DownloadQueuePreResolutionService(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        DownloadOrchestrationService orchestrationService,
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadQueuePreResolutionService> logger)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _orchestrationService = orchestrationService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ResolveOneLookaheadItemAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Queue pre-resolution pass failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ResolveOneLookaheadItemAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        if (!settings.EnableQueuePreResolution)
        {
            return;
        }

        var downloadGate = await _orchestrationService.EvaluateDownloadGateAsync(cancellationToken);
        if (!downloadGate.Allowed)
        {
            return;
        }

        var windowSize = Math.Clamp(settings.QueuePreResolutionWindow, 1, 25);
        var retryDelay = TimeSpan.FromMinutes(Math.Clamp(settings.QueuePreResolutionRetryMinutes, 1, 60));
        var now = DateTimeOffset.UtcNow;
        var tasks = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var candidate = QueuePreResolutionPlanner.SelectNext(
            tasks,
            settings.QueueOrder,
            windowSize,
            retryDelay,
            now);
        if (candidate == null)
        {
            return;
        }

        await ResolveCandidateAsync(candidate, now, cancellationToken);
    }

    private async Task ResolveCandidateAsync(
        DownloadQueueItem item,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var originalPayloadJson = item.PayloadJson ?? string.Empty;
        var resolvingPayload = QueuePreResolutionPayload.ParseOrEmpty(originalPayloadJson);
        QueuePreResolutionPayload.MarkResolving(resolvingPayload, startedAt);
        var resolvingPayloadJson = resolvingPayload.ToJsonString();
        var claimed = await _queueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
            item.QueueUuid,
            item.PayloadJson,
            resolvingPayloadJson,
            cancellationToken: cancellationToken);
        if (!claimed)
        {
            return;
        }

        var resolvingItem = item with { PayloadJson = resolvingPayloadJson };
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
            var result = await resolver.ResolveQueuedPayloadAsync(resolvingItem, cancellationToken);
            var resolvedPayload = QueuePreResolutionPayload.ParseOrEmpty(resolvingPayloadJson);
            if (IsProviderRateLimit(result.Error))
            {
                QueuePreResolutionPayload.ApplyFailed(resolvedPayload, "Pre-resolution deferred by provider rate limit.", DateTimeOffset.UtcNow);
                await _queueRepository.TryUpdateQueuedIdentityIfCurrentAsync(
                    BuildIdentityUpdateItem(item, resolvedPayload.ToJsonString(), result.Engine),
                    resolvingPayloadJson,
                    status: "queued",
                    error: null,
                    cancellationToken: cancellationToken);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                if (IsDownloadGateDeferral(result.Error))
                {
                    QueuePreResolutionPayload.ApplyPending(resolvedPayload);
                    await _queueRepository.TryUpdateQueuedIdentityIfCurrentAsync(
                        BuildIdentityUpdateItem(item, resolvedPayload.ToJsonString(), result.Engine),
                        resolvingPayloadJson,
                        status: "queued",
                        error: null,
                        cancellationToken: cancellationToken);
                    return;
                }

                QueuePreResolutionPayload.ApplyFailed(resolvedPayload, result.Error!, DateTimeOffset.UtcNow);
            }
            else
            {
                QueuePreResolutionPayload.ApplyResolved(resolvedPayload, result, DateTimeOffset.UtcNow);
            }

            var finalPayloadJson = resolvedPayload.ToJsonString();
            await _queueRepository.TryUpdateQueuedIdentityIfCurrentAsync(
                BuildIdentityUpdateItem(item, finalPayloadJson, result.Engine),
                resolvingPayloadJson,
                string.IsNullOrWhiteSpace(result.Error) ? "queued" : "failed",
                result.Error,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsProviderRateLimit(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Queue pre-resolution deferred by provider rate limit for {QueueUuid} ({Artist} - {Title}).",
                    item.QueueUuid,
                    item.ArtistName,
                    item.TrackTitle);

                var deferredPayload = QueuePreResolutionPayload.ParseOrEmpty(resolvingPayloadJson);
                QueuePreResolutionPayload.ApplyFailed(deferredPayload, "Pre-resolution deferred by provider rate limit.", DateTimeOffset.UtcNow);
                await _queueRepository.TryUpdateQueuedIdentityIfCurrentAsync(
                    BuildIdentityUpdateItem(item, deferredPayload.ToJsonString(), item.Engine),
                    resolvingPayloadJson,
                    status: "queued",
                    error: null,
                    cancellationToken: CancellationToken.None);
                return;
            }

            _logger.LogWarning(
                ex,
                "Queue pre-resolution failed for {QueueUuid} ({Artist} - {Title}).",
                item.QueueUuid,
                item.ArtistName,
                item.TrackTitle);
            var failedPayload = QueuePreResolutionPayload.ParseOrEmpty(resolvingPayloadJson);
            if (IsDownloadGateDeferral(ex.Message))
            {
                QueuePreResolutionPayload.ApplyPending(failedPayload);
                await _queueRepository.TryUpdateQueuedIdentityIfCurrentAsync(
                    BuildIdentityUpdateItem(item, failedPayload.ToJsonString(), item.Engine),
                    resolvingPayloadJson,
                    status: "queued",
                    error: null,
                    cancellationToken: CancellationToken.None);
                return;
            }

            QueuePreResolutionPayload.ApplyFailed(failedPayload, ex.Message, DateTimeOffset.UtcNow);
            await _queueRepository.TryUpdateQueuedIdentityIfCurrentAsync(
                BuildIdentityUpdateItem(item, failedPayload.ToJsonString(), item.Engine),
                resolvingPayloadJson,
                status: "failed",
                error: ex.Message,
                cancellationToken: CancellationToken.None);
        }
    }

    private static DownloadQueueItem BuildIdentityUpdateItem(
        DownloadQueueItem current,
        string payloadJson,
        string? resolvedEngine)
    {
        using var document = ParsePayloadDocument(payloadJson);
        var root = document.RootElement;
        return current with
        {
            Engine = FirstNonEmpty(resolvedEngine, ReadString(root, "Engine", "engine"), current.Engine) ?? current.Engine,
            ArtistName = FirstNonEmpty(ReadString(root, "Artist", "artist"), current.ArtistName) ?? current.ArtistName,
            TrackTitle = FirstNonEmpty(ReadString(root, "Title", "title"), current.TrackTitle) ?? current.TrackTitle,
            Isrc = FirstNonEmpty(ReadString(root, "Isrc", "isrc"), current.Isrc),
            DeezerTrackId = FirstNonEmpty(ReadString(root, "DeezerId", "deezerId"), current.DeezerTrackId),
            DeezerAlbumId = FirstNonEmpty(ReadString(root, "DeezerAlbumId", "deezerAlbumId"), current.DeezerAlbumId),
            DeezerArtistId = FirstNonEmpty(ReadString(root, "DeezerArtistId", "deezerArtistId"), current.DeezerArtistId),
            SpotifyTrackId = FirstNonEmpty(ReadString(root, "SpotifyId", "spotifyId"), current.SpotifyTrackId),
            SpotifyAlbumId = FirstNonEmpty(ReadString(root, "SpotifyAlbumId", "spotifyAlbumId"), current.SpotifyAlbumId),
            SpotifyArtistId = FirstNonEmpty(ReadString(root, "SpotifyArtistId", "spotifyArtistId"), current.SpotifyArtistId),
            AppleTrackId = FirstNonEmpty(ReadString(root, "AppleId", "appleId"), current.AppleTrackId),
            AppleAlbumId = FirstNonEmpty(ReadString(root, "AppleAlbumId", "appleAlbumId"), current.AppleAlbumId),
            AppleArtistId = FirstNonEmpty(ReadString(root, "AppleArtistId", "appleArtistId"), current.AppleArtistId),
            DurationMs = ReadDurationMs(root) ?? current.DurationMs,
            DestinationFolderId = ReadInt64(root, "DestinationFolderId", "destinationFolderId") ?? current.DestinationFolderId,
            ContentType = FirstNonEmpty(ReadString(root, "ContentType", "contentType"), current.ContentType),
            PayloadJson = payloadJson
        };
    }

    private static JsonDocument ParsePayloadDocument(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return JsonDocument.Parse("{}");
        }

        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .FirstOrDefault();
    }

    private static string? ReadString(JsonElement root, string pascalName, string camelName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty(pascalName, out var pascalValue) && pascalValue.ValueKind == JsonValueKind.String)
        {
            return pascalValue.GetString();
        }

        return root.TryGetProperty(camelName, out var camelValue) && camelValue.ValueKind == JsonValueKind.String
            ? camelValue.GetString()
            : null;
    }

    private static int? ReadDurationMs(JsonElement root)
    {
        var durationMs = ReadInt32(root, "DurationMs", "durationMs");
        if (durationMs.HasValue && durationMs.Value > 0)
        {
            return durationMs.Value;
        }

        var durationSeconds = ReadInt32(root, "DurationSeconds", "durationSeconds");
        return durationSeconds.HasValue && durationSeconds.Value > 0
            ? durationSeconds.Value * 1000
            : null;
    }

    private static int? ReadInt32(JsonElement root, string pascalName, string camelName)
    {
        var value = ReadInt64(root, pascalName, camelName);
        if (!value.HasValue || value.Value < int.MinValue || value.Value > int.MaxValue)
        {
            return null;
        }

        return (int)value.Value;
    }

    private static long? ReadInt64(JsonElement root, string pascalName, string camelName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryReadInt64(root, pascalName, out var pascalValue))
        {
            return pascalValue;
        }

        return TryReadInt64(root, camelName, out var camelValue) ? camelValue : null;
    }

    private static bool TryReadInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), out value),
            _ => false
        };
    }

    private static bool IsProviderRateLimit(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
        {
            return true;
        }

        return exception.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderRateLimit(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDownloadGateDeferral(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("Downloads waiting for enrichment to finish", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Downloads waiting for post-enrichment finalization to finish", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Downloads paused while AutoTag is running", StringComparison.OrdinalIgnoreCase));
    }
}
