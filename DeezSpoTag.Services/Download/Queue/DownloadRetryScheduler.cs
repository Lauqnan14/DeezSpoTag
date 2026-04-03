using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadRetryScheduler
{
    private sealed record FallbackResetState(
        List<string> AutoSources,
        DownloadSourceOrder.AutoSourceStep FirstStep,
        List<FallbackPlanStep> FallbackPlan);

    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IActivityLogWriter _activityLog;
    private readonly IDeezSpoTagListener _listener;
    private readonly ILogger<DownloadRetryScheduler> _logger;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly Func<bool>? _isAutoTagRunning;

    public DownloadRetryScheduler(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        IActivityLogWriter activityLog,
        IDeezSpoTagListener listener,
        ILogger<DownloadRetryScheduler> logger,
        DownloadCancellationRegistry cancellationRegistry,
        Func<bool>? isAutoTagRunning = null)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _activityLog = activityLog;
        _listener = listener;
        _logger = logger;
        _cancellationRegistry = cancellationRegistry;
        _isAutoTagRunning = isAutoTagRunning;
    }

    public void ScheduleRetry(string queueUuid, string engine, string reason)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        if (!TryCreateRetrySchedule(queueUuid, engine, reason, out var attempt, out var delaySeconds))
        {
            return;
        }

        _ = Task.Run(() => ExecuteScheduledRetryAsync(queueUuid, engine, attempt, delaySeconds));
    }

    private bool TryCreateRetrySchedule(string queueUuid, string engine, string reason, out int attempt, out int delaySeconds)
    {
        var settings = _settingsService.LoadSettings();
        attempt = _attempts.AddOrUpdate(queueUuid, 1, (_, current) => current + 1);
        var maxRetries = settings.MaxRetries;
        if (maxRetries <= 0 || attempt > maxRetries)
        {
            _activityLog.Warn($"Auto-retry stopped (engine={engine} attempt={attempt} max={maxRetries}): {queueUuid} {reason}");
            _attempts.TryRemove(queueUuid, out _);
            delaySeconds = 0;
            return false;
        }

        delaySeconds = Math.Max(0, settings.RetryDelaySeconds + (settings.RetryDelayIncrease * (attempt - 1)));
        _activityLog.Warn($"Auto-retry scheduled (engine={engine} attempt={attempt} delay={delaySeconds}s): {queueUuid} {reason}");
        return true;
    }

    private async Task ExecuteScheduledRetryAsync(string queueUuid, string engine, int attempt, int delaySeconds)
    {
        try
        {
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }

            await WaitForAutoTagIdleAsync();
            if (WasRetryUserCanceled(queueUuid))
            {
                return;
            }

            var item = await _queueRepository.GetByUuidAsync(queueUuid);
            if (item == null || !IsRetryableStatus(item.Status))
            {
                return;
            }

            if (WasRetryUserCanceled(queueUuid))
            {
                return;
            }

            await ResetFallbackStateAsync(item);
            await _queueRepository.RequeueAsync(queueUuid);
            _activityLog.Info($"Auto-retry queued (engine={engine} attempt={attempt}): {queueUuid}");
            NotifyRetryQueued(queueUuid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to auto-retry {QueueUuid}", queueUuid);
            _activityLog.Error($"Auto-retry failed (engine={engine}): {queueUuid} {ex.Message}");
        }
    }

    private async Task WaitForAutoTagIdleAsync()
    {
        while (_isAutoTagRunning?.Invoke() == true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private bool WasRetryUserCanceled(string queueUuid)
    {
        if (!_cancellationRegistry.WasUserCanceled(queueUuid))
        {
            return false;
        }

        _activityLog.Info($"Auto-retry skipped (user canceled): {queueUuid}");
        return true;
    }

    private static bool IsRetryableStatus(string? status)
        => (status ?? string.Empty) is "failed" or "cancelled" or "canceled";

    private void NotifyRetryQueued(string queueUuid)
    {
        _listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            status = "inQueue",
            progress = 0,
            downloaded = 0,
            failed = 0,
            error = default(string)
        });
    }

    public void Clear(string queueUuid)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        _attempts.TryRemove(queueUuid, out _);
    }

    private async Task ResetFallbackStateAsync(DownloadQueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return;
        }

        try
        {
            var settings = _settingsService.LoadSettings();
            var payloadNode = JsonNode.Parse(item.PayloadJson);
            if (payloadNode is not JsonObject payloadObj)
            {
                return;
            }

            var payloadContentType = ReadString(payloadObj, "ContentType");
            var payloadQuality = ReadString(payloadObj, "Quality");
            var payloadAutoSources = ReadStringArray(payloadObj, "AutoSources");
            var payloadFallbackPlan = ReadFallbackPlan(payloadObj);
            var contentType = string.IsNullOrWhiteSpace(item.ContentType) ? payloadContentType : item.ContentType;
            var fallbackState = ResolveFallbackState(item, settings, payloadObj, contentType, payloadQuality, payloadAutoSources, payloadFallbackPlan);

            payloadObj["AutoIndex"] = 0;
            payloadObj["Engine"] = fallbackState.FirstStep.Source;
            payloadObj["SourceService"] = fallbackState.FirstStep.Source;
            payloadObj["AutoSources"] = new JsonArray(
                fallbackState.AutoSources.Select(source => (JsonNode)JsonValue.Create(source)!).ToArray());
            payloadObj["FallbackPlan"] = new JsonArray(
                fallbackState.FallbackPlan
                    .Select(step => (JsonNode)new JsonObject
                    {
                        ["StepId"] = step.StepId,
                        ["Engine"] = step.Engine,
                        ["Quality"] = step.Quality,
                        ["RequiredInputs"] = new JsonArray(step.RequiredInputs.Select(input => (JsonNode)JsonValue.Create(input)!).ToArray()),
                        ["ResolutionStrategy"] = step.ResolutionStrategy
                    })
                    .ToArray());
            payloadObj["FallbackHistory"] = new JsonArray();
            payloadObj["FallbackQueuedExternally"] = false;
            if (!string.IsNullOrWhiteSpace(fallbackState.FirstStep.Quality))
            {
                payloadObj["Quality"] = fallbackState.FirstStep.Quality;
            }

            await _queueRepository.UpdatePayloadAsync(item.QueueUuid, payloadObj.ToJsonString());
            await _queueRepository.UpdateEngineAsync(item.QueueUuid, fallbackState.FirstStep.Source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to reset fallback state for {QueueUuid}", item.QueueUuid);
        }
    }

    private static FallbackResetState ResolveFallbackState(
        DownloadQueueItem item,
        Core.Models.Settings.DeezSpoTagSettings settings,
        JsonObject payloadObj,
        string? contentType,
        string? payloadQuality,
        List<string> payloadAutoSources,
        List<FallbackPlanStep> payloadFallbackPlan)
    {
        if (IsVideoPayload(contentType, payloadQuality, payloadObj))
        {
            var firstStep = new DownloadSourceOrder.AutoSourceStep("apple", DownloadContentTypes.Video);
            payloadObj["ContentType"] = DownloadContentTypes.Video;
            payloadObj["Quality"] = DownloadContentTypes.Video;
            return BuildSingleStepFallback(firstStep, "direct_url");
        }

        if (Shared.DownloadEngineSettingsHelper.IsAtmosOnlyPayload(contentType, payloadQuality))
        {
            var firstStep = new DownloadSourceOrder.AutoSourceStep("apple", "ATMOS");
            payloadObj["ContentType"] = "atmos";
            return BuildSingleStepFallback(firstStep, "direct_url");
        }

        if (payloadFallbackPlan.Count > 0)
        {
            var normalizedAutoSources = payloadFallbackPlan
                .Where(step => !string.IsNullOrWhiteSpace(step.Engine))
                .Select(step => DownloadSourceOrder.EncodeAutoSource(step.Engine, step.Quality))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedAutoSources.Count > 0)
            {
                var firstStep = DownloadSourceOrder.DecodeAutoSource(normalizedAutoSources[0]);
                if (string.IsNullOrWhiteSpace(firstStep.Source))
                {
                    firstStep = new DownloadSourceOrder.AutoSourceStep(item.Engine ?? "deezer", payloadQuality);
                }

                return new FallbackResetState(normalizedAutoSources, firstStep, payloadFallbackPlan);
            }
        }

        if (payloadAutoSources.Count > 0)
        {
            var firstStep = DownloadSourceOrder.DecodeAutoSource(payloadAutoSources[0]);
            if (string.IsNullOrWhiteSpace(firstStep.Source))
            {
                firstStep = new DownloadSourceOrder.AutoSourceStep(item.Engine ?? "deezer", payloadQuality);
            }

            var fallbackPlan = payloadAutoSources
                .Select((source, index) =>
                {
                    var step = DownloadSourceOrder.DecodeAutoSource(source);
                    var engine = string.IsNullOrWhiteSpace(step.Source) ? item.Engine ?? "deezer" : step.Source;
                    return new FallbackPlanStep(
                        StepId: $"retry-step-{index}",
                        Engine: engine,
                        Quality: step.Quality,
                        RequiredInputs: Array.Empty<string>(),
                        ResolutionStrategy: "direct_url");
                })
                .ToList();
            return new FallbackResetState(payloadAutoSources, firstStep, fallbackPlan);
        }

        var resolvedAutoSources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);
        var resolvedFirstStep = resolvedAutoSources.Count > 0
            ? DownloadSourceOrder.DecodeAutoSource(resolvedAutoSources[0])
            : new DownloadSourceOrder.AutoSourceStep(item.Engine ?? "deezer", null);
        var resolvedFallbackPlan = resolvedAutoSources
            .Select((source, index) =>
            {
                var step = DownloadSourceOrder.DecodeAutoSource(source);
                var engine = string.IsNullOrWhiteSpace(step.Source) ? item.Engine ?? "deezer" : step.Source;
                return new FallbackPlanStep(
                    StepId: $"retry-step-{index}",
                    Engine: engine,
                    Quality: step.Quality,
                    RequiredInputs: Array.Empty<string>(),
                    ResolutionStrategy: "direct_url");
            })
            .ToList();
        return new FallbackResetState(resolvedAutoSources, resolvedFirstStep, resolvedFallbackPlan);
    }

    private static FallbackResetState BuildSingleStepFallback(DownloadSourceOrder.AutoSourceStep firstStep, string resolutionStrategy)
    {
        var autoSources = new List<string> { DownloadSourceOrder.EncodeAutoSource(firstStep.Source, firstStep.Quality) };
        var fallbackPlan = new List<FallbackPlanStep>
        {
            new(
                StepId: "retry-step-0",
                Engine: firstStep.Source,
                Quality: firstStep.Quality,
                RequiredInputs: Array.Empty<string>(),
                ResolutionStrategy: resolutionStrategy)
        };
        return new FallbackResetState(autoSources, firstStep, fallbackPlan);
    }

    private static bool IsVideoPayload(string? contentType, string? quality, JsonObject payloadObj)
    {
        if (AppleVideoClassifier.IsVideoContentType(contentType))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(quality)
            && quality.Contains("video", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sourceUrl = ReadString(payloadObj, "SourceUrl") ?? ReadString(payloadObj, "sourceUrl");
        var collectionType = ReadString(payloadObj, "CollectionType") ?? ReadString(payloadObj, "collectionType");
        return AppleVideoClassifier.IsVideo(sourceUrl, collectionType, contentType);
    }

    private static string? ReadString(JsonObject payloadObj, string key)
    {
        if (payloadObj[key] is not JsonNode node)
        {
            return null;
        }

        var value = node.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static List<string> ReadStringArray(JsonObject payloadObj, string key)
    {
        if (payloadObj[key] is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(static entry => entry?.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToList();
    }

    private static List<FallbackPlanStep> ReadFallbackPlan(JsonObject payloadObj)
    {
        if (payloadObj["FallbackPlan"] is not JsonArray planArray)
        {
            return new List<FallbackPlanStep>();
        }

        var steps = new List<FallbackPlanStep>();
        foreach (var node in planArray)
        {
            if (node is not JsonObject stepObj)
            {
                continue;
            }

            var engine = ReadString(stepObj, "Engine");
            if (string.IsNullOrWhiteSpace(engine))
            {
                continue;
            }

            var stepId = ReadString(stepObj, "StepId") ?? $"retry-step-{steps.Count}";
            var quality = ReadString(stepObj, "Quality");
            var resolutionStrategy = ReadString(stepObj, "ResolutionStrategy") ?? "direct_url";
            var requiredInputs = ReadStringArray(stepObj, "RequiredInputs");
            steps.Add(new FallbackPlanStep(stepId, engine, quality, requiredInputs, resolutionStrategy));
        }

        return steps;
    }
}
