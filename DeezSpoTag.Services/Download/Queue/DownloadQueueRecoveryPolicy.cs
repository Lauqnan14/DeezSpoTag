namespace DeezSpoTag.Services.Download.Queue;

public static class DownloadQueueRecoveryPolicy
{
    public static readonly TimeSpan RunningStallThreshold = TimeSpan.FromMinutes(15);

    public static string BuildStallTimeoutMessage(string engine)
    {
        var normalizedEngine = string.IsNullOrWhiteSpace(engine) ? "download" : engine.Trim().ToLowerInvariant();
        return $"{normalizedEngine} download stalled without progress for {RunningStallThreshold.TotalMinutes:0} minutes.";
    }

    public static string BuildRecoveryFailureMessage(string engine)
    {
        var normalizedEngine = string.IsNullOrWhiteSpace(engine) ? "download" : engine.Trim().ToLowerInvariant();
        return $"{normalizedEngine} download was recovered after {RunningStallThreshold.TotalMinutes:0} minutes without progress.";
    }
}
