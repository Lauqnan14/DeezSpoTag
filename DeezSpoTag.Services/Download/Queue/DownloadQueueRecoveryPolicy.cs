namespace DeezSpoTag.Services.Download.Queue;

public static class DownloadQueueRecoveryPolicy
{
    public static readonly TimeSpan RunningStallThreshold = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan OrphanedRunningThreshold = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PostDownloadPendingLease = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AcquisitionStageLease = TimeSpan.FromMinutes(5);

    public static string BuildAcquisitionStallMessage(string engine, string? provider, string? stage)
    {
        var normalizedEngine = Normalize(engine) is { Length: > 0 } value ? value : "download";
        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? normalizedEngine : provider.Trim();
        var normalizedStage = string.IsNullOrWhiteSpace(stage) ? "provider session" : stage.Trim().Replace('_', ' ');
        return $"{normalizedProvider} timed out during {normalizedStage} before any audio transfer started.";
    }

    public static bool IsWatchlistClaimOwnedByQueue(DownloadQueueItem? item, DateTimeOffset nowUtc)
    {
        if (item == null)
        {
            return false;
        }

        var queueStatus = Normalize(item.Status);
        if (queueStatus is "queued" or "resolving" or "preparing" or "prepared" or "inqueue" or "running" or "downloading" or "paused" or "retrying")
        {
            return true;
        }

        if (queueStatus is not "completed" and not "complete")
        {
            return false;
        }

        var enrichmentStatus = Normalize(item.EnrichmentStatus);
        var finalizationStatus = Normalize(item.FinalizationStatus);
        if (enrichmentStatus == "running" || finalizationStatus == "running")
        {
            return nowUtc - item.UpdatedAt <= PostDownloadPendingLease;
        }

        if (enrichmentStatus != "pending" && finalizationStatus != "pending")
        {
            return false;
        }

        return nowUtc - item.UpdatedAt <= PostDownloadPendingLease;
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

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
