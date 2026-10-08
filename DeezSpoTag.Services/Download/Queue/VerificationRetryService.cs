using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

/// <summary>
/// Releases queue items that failed while a public download API had no verified session.
/// </summary>
/// <remarks>
/// <para>
/// A file queued before its public API session is verified cannot download. It exhausts its
/// <c>MaxRetries</c> budget against an API that was never going to work and ends up permanently
/// failed, requiring a manual Retry click. This service reverses that: items are held as failed
/// with a verification flag, and a completed session verification releases them.
/// </para>
/// <para>
/// Releasing an item reuses the ordinary manual-retry path, so the item gets the same treatment as
/// a user pressing Retry: the engine ladder restarts at its first step, the retry budget is reset,
/// and the item keeps its original queue position.
/// </para>
/// <para>
/// This service only acts on retry. It never gates enqueues and never blocks a running download.
/// </para>
/// </remarks>
public sealed class VerificationRetryService
{
    private readonly DownloadQueueRepository _queueRepository;
    private readonly PublicApiSessionVerificationStore _verificationStore;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IActivityLogWriter _activityLog;
    private readonly ILogger<VerificationRetryService> _logger;

    private bool HasCompletedInitialSweep()
        => _settingsService.LoadSettings().VerificationRetryInitialSweepCompleted;

    private void MarkInitialSweepCompleted()
    {
        lock (_settingsService)
        {
            var settings = _settingsService.LoadSettings();
            if (settings.VerificationRetryInitialSweepCompleted)
            {
                return;
            }

            settings.VerificationRetryInitialSweepCompleted = true;
            _settingsService.SaveSettings(settings);
        }
    }

    public VerificationRetryService(
        DownloadQueueRepository queueRepository,
        PublicApiSessionVerificationStore verificationStore,
        DeezSpoTagSettingsService settingsService,
        IActivityLogWriter activityLog,
        ILogger<VerificationRetryService> logger)
    {
        _queueRepository = queueRepository;
        _verificationStore = verificationStore;
        _settingsService = settingsService;
        _activityLog = activityLog;
        _logger = logger;
    }

    /// <summary>
    /// Stamps a newly queued item with whether it was queued while a public API was unverified, and
    /// when it was queued. Cheap and synchronous: it reads the persisted record only.
    /// </summary>
    public async Task<bool> StampQueuedItemAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return false;
        }

        var unverified = _verificationStore.HasAnyUnverifiedPublicApi();
        await _queueRepository.MarkPublicApiUnverifiedWhenQueuedAsync(
            queueUuid,
            unverified,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return unverified;
    }

    /// <summary>
    /// Called when a public API session verification completes. Records the verification and releases
    /// the failed items that were waiting on it.
    /// </summary>
    public async Task<int> OnSessionVerifiedAsync(
        string slug,
        Func<string, CancellationToken, Task<bool>> releaseItemAsync,
        CancellationToken cancellationToken = default)
    {
        if (!PublicApiSessionVerificationStore.TryNormalizeSlug(slug, out var normalized))
        {
            return 0;
        }

        _verificationStore.RecordVerified(normalized);

        // The initial sweep runs once and releases every failed item. Later verifications release
        // only items that were explicitly stamped while unverified.
        var requireFlag = HasCompletedInitialSweep();
        var candidates = await _queueRepository.GetFailedForVerificationRetryAsync(
            requireUnverifiedFlag: requireFlag,
            cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var released = 0;
        foreach (var queueUuid in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await releaseItemAsync(queueUuid, cancellationToken))
            {
                released++;
            }
        }

        if (!requireFlag)
        {
            MarkInitialSweepCompleted();
        }

        _activityLog.Info(
            $"Verification retry released {released} failed item(s) after {normalized} verification (initialSweep={!requireFlag}).");
        return released;
    }

}
