using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Fallback;

internal static class FallbackAttemptRecorder
{
    public static void RecordCurrent(
        EngineQueueItemBase payload,
        string status,
        string errorClass,
        string detail)
    {
        var stepId = $"step-{Math.Max(0, payload.AutoIndex)}";
        if (payload.FallbackHistory.Any(attempt =>
                string.Equals(attempt.StepId, stepId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.Status, status, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ErrorClass, errorClass, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.Detail, detail, StringComparison.Ordinal)))
        {
            return;
        }

        payload.FallbackHistory.Add(new FallbackAttempt(stepId, status, errorClass, detail));
    }
}
