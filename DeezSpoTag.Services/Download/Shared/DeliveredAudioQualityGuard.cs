using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Shared;

internal static class DeliveredAudioQualityGuard
{
    public static async Task EnsurePlanStepSatisfiedAsync(
        EngineQueueItemBase payload,
        string filePath,
        string queueUuid,
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener listener,
        CancellationToken cancellationToken)
    {
        var result = Validate(payload, filePath);
        payload.RequestedQuality = result.RequestedQuality;
        payload.DeliveredQuality = result.DeliveredQuality;
        if (result.Success)
        {
            return;
        }

        RecordRejectedAttempt(payload, result);
        await QueueHelperUtils.UpdatePayloadAsync(
            queueRepository,
            queueUuid,
            payload,
            cancellationToken);
        listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            status = "running",
            engine = payload.Engine,
            quality = payload.Quality,
            requestedQuality = payload.RequestedQuality,
            deliveredQuality = payload.DeliveredQuality,
            autoIndex = payload.AutoIndex,
            fallbackPlan = payload.FallbackPlan,
            fallbackHistory = payload.FallbackHistory
        });
        throw new DeliveredAudioQualityBelowPlanStepException(result.Message);
    }

    public static DeliveredAudioQualityResult Validate(EngineQueueItemBase payload, string filePath)
    {
        var requestedQuality = ResolveCurrentRequestedQuality(payload);
        var actual = ActualDownloadQualityLabel.Inspect(payload, filePath);
        if (actual == null)
        {
            return DeliveredAudioQualityResult.Inconclusive(requestedQuality);
        }

        if (IsDeliveredQualityAccepted(payload.Engine, requestedQuality, actual))
        {
            return DeliveredAudioQualityResult.Accepted(requestedQuality, actual.Label);
        }

        return DeliveredAudioQualityResult.Rejected(
            requestedQuality,
            actual.Label,
            $"Requested {FormatRequestedQuality(payload.Engine, requestedQuality)} but the provider delivered {actual.Label}.");
    }

    public static void RecordRejectedAttempt(
        EngineQueueItemBase payload,
        DeliveredAudioQualityResult result)
    {
        FallbackAttemptRecorder.RecordCurrent(
            payload,
            "rejected",
            "quality_below_requested",
            result.Message);
    }

    private static string ResolveCurrentRequestedQuality(EngineQueueItemBase payload)
    {
        if (payload.AutoIndex >= 0 && payload.AutoIndex < payload.FallbackPlan.Count)
        {
            var step = payload.FallbackPlan[payload.AutoIndex];
            if (string.Equals(step.Engine, payload.Engine, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(step.Quality))
            {
                return step.Quality!;
            }
        }

        return payload.Quality;
    }

    private static bool IsDeliveredQualityAccepted(string? engine, string? requestedQuality, ActualAudioQuality actual)
    {
        var normalized = (requestedQuality ?? string.Empty).Trim().ToUpperInvariant();
        if (IsTidalEngine(engine))
        {
            return TidalStereoQuality.Accepts(TidalStereoQuality.Normalize(requestedQuality), actual);
        }

        if (IsAmazonEngine(engine))
        {
            return normalized switch
            {
                "ULTRA_HD_FLAC" => actual.IsLossless
                    && actual.BitsPerSample >= 24
                    && actual.SampleRate > 0,
                "HD_FLAC" => actual.IsLossless
                    && actual.BitsPerSample > 0
                    && actual.SampleRate > 0,
                "OPUS" => true,
                _ => true
            };
        }

        return normalized switch
        {
            "27" or "HI_RES_LOSSLESS" => actual.IsLossless
                && actual.BitsPerSample >= 24
                && actual.SampleRate > 96000,
            "7" or "HI_RES" or "ULTRA_HD_FLAC" => actual.IsLossless
                && actual.BitsPerSample >= 24
                && actual.SampleRate > 0
                && actual.SampleRate <= 96000,
            "6" or "LOSSLESS" or "HD_FLAC" or "9" or "ALAC" => actual.IsLossless
                && actual.BitsPerSample > 0
                && actual.BitsPerSample <= 16,
            "5" or "HIGH" or "3" => !actual.IsLossless || actual.BitrateKbps >= 256,
            "LOW" or "1" or "AAC" or "OPUS" => true,
            _ => true
        };
    }

    private static bool IsAmazonEngine(string? engine)
        => string.Equals(engine?.Trim(), "amazon", StringComparison.OrdinalIgnoreCase);

    private static bool IsTidalEngine(string? engine)
        => string.Equals(engine?.Trim(), "tidal", StringComparison.OrdinalIgnoreCase);

    private static string FormatRequestedQuality(string? engine, string? quality)
    {
        if (IsTidalEngine(engine))
        {
            return TidalStereoQuality.FormatRequested(quality);
        }

        var normalized = (quality ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "27" or "HI_RES_LOSSLESS" => "24-bit Max Hi-Res",
            "7" or "HI_RES" => "24-bit Hi-Res",
            "ULTRA_HD_FLAC" => "24-bit Ultra HD FLAC",
            _ => string.IsNullOrWhiteSpace(quality) ? engine ?? "requested quality" : quality
        };
    }
}

internal sealed record DeliveredAudioQualityResult(
    bool Success,
    bool Conclusive,
    string RequestedQuality,
    string DeliveredQuality,
    string Message)
{
    public static DeliveredAudioQualityResult Accepted(string requested, string delivered)
        => new(true, true, requested, delivered, string.Empty);

    public static DeliveredAudioQualityResult Inconclusive(string requested)
        => new(true, false, requested, "Quality unverified", string.Empty);

    public static DeliveredAudioQualityResult Rejected(string requested, string delivered, string message)
        => new(false, true, requested, delivered, message);
}

internal sealed class DeliveredAudioQualityBelowPlanStepException : InvalidOperationException
{
    public DeliveredAudioQualityBelowPlanStepException(string message) : base(message)
    {
    }
}
