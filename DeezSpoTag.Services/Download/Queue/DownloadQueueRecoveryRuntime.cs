using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadQueueRecoveryRuntime
{
    public DownloadQueueRecoveryRuntime(
        DownloadRetryScheduler retryScheduler,
        EngineFallbackCoordinator fallbackCoordinator,
        DeezSpoTagSettingsService settingsService,
        IActivityLogWriter activityLog,
        IDeezSpoTagListener listener)
    {
        RetryScheduler = retryScheduler;
        FallbackCoordinator = fallbackCoordinator;
        SettingsService = settingsService;
        ActivityLog = activityLog;
        Listener = listener;
    }

    public DownloadRetryScheduler RetryScheduler { get; }

    public EngineFallbackCoordinator FallbackCoordinator { get; }

    public DeezSpoTagSettingsService SettingsService { get; }

    public IActivityLogWriter ActivityLog { get; }

    public IDeezSpoTagListener Listener { get; }
}
