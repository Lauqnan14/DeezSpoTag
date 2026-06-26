using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadQueueRecoveryRuntime
{
    public DownloadQueueRecoveryRuntime(
        DownloadRetryScheduler retryScheduler,
        IActivityLogWriter activityLog,
        IDeezSpoTagListener listener)
    {
        RetryScheduler = retryScheduler;
        ActivityLog = activityLog;
        Listener = listener;
    }

    public DownloadRetryScheduler RetryScheduler { get; }

    public IActivityLogWriter ActivityLog { get; }

    public IDeezSpoTagListener Listener { get; }
}
