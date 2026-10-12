using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

/// <summary>
/// Recovers failed public-API downloads after verification. Eligibility survives failed attempts;
/// each verification can release a file once, and late failures are handled by the existing sweep.
/// </summary>
public sealed class VerificationRetryService
{
    private readonly SemaphoreSlim _releaseGate = new(1, 1);
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

    // The flag means eligible for verification recovery, including outages after a past
    // verification. It is written centrally on enqueue and cleared only on success/cancellation.
    public static IReadOnlyList<string> GetPublicApiSources(DownloadQueueItem item)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (PublicApiSessionVerificationStore.TryNormalizeSlug(item.Engine, out var engine)) sources.Add(engine);
        try
        {
            if (JsonNode.Parse(item.PayloadJson ?? "{}") is JsonObject payload)
                foreach (var step in DownloadExecutionPlan.Read(payload))
                    if (PublicApiSessionVerificationStore.TryNormalizeSlug(step.Engine, out var source)) sources.Add(source);
        }
        catch (System.Text.Json.JsonException) { /* A public engine still identifies an invalid legacy payload. */ }
        return sources.ToArray();
    }

    public Task<int> OnSessionVerifiedAsync(string slug, Func<string, CancellationToken, Task<bool>> releaseItemAsync,
        CancellationToken cancellationToken = default)
        => OnSessionsVerifiedAsync(new[] { slug }, releaseItemAsync, cancellationToken);

    public async Task<int> OnSessionsVerifiedAsync(IEnumerable<string> slugs,
        Func<string, CancellationToken, Task<bool>> releaseItemAsync, CancellationToken cancellationToken = default)
    {
        var providers = slugs.Where(slug => PublicApiSessionVerificationStore.TryNormalizeSlug(slug, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (providers.Length == 0) return 0;
        await _releaseGate.WaitAsync(cancellationToken);
        try
        {
            var verifiedAt = DateTimeOffset.UtcNow;
            foreach (var provider in providers) _verificationStore.RecordVerified(provider, verifiedAt);
            return await ReleaseFailedAsync(verifiedAt, releaseItemAsync, initialSweep: !HasCompletedInitialSweep(), cancellationToken);
        }
        finally { _releaseGate.Release(); }
    }

    public async Task<int> RecoverPendingAsync(Func<string, CancellationToken, Task<bool>> releaseItemAsync,
        CancellationToken cancellationToken = default)
    {
        await _releaseGate.WaitAsync(cancellationToken);
        try
        {
            var latest = PublicApiSessionVerificationStore.PublicApiSlugs.Select(_verificationStore.GetLastVerifiedAtUtc).Max();
            return latest.HasValue ? await ReleaseFailedAsync(latest.Value, releaseItemAsync, initialSweep: false, cancellationToken) : 0;
        }
        finally { _releaseGate.Release(); }
    }

    private async Task<int> ReleaseFailedAsync(DateTimeOffset verifiedAt,
        Func<string, CancellationToken, Task<bool>> releaseItemAsync, bool initialSweep, CancellationToken cancellationToken)
    {
        var candidates = await _queueRepository.GetFailedForVerificationRetryAsync(!initialSweep, cancellationToken, verifiedAt);
        var released = 0;
        foreach (var uuid in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await releaseItemAsync(uuid, cancellationToken)) continue;
                // Consume this verification, retaining eligibility for a later verification if
                // the new attempt fails. A download that fails later is picked up by the sweep.
                await _queueRepository.MarkPublicApiVerificationRetryAttemptAsync(uuid, verifiedAt, cancellationToken);
                released++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Verification recovery failed for {QueueUuid}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(uuid));
            }
        }
        if (initialSweep && candidates.Count > 0 && released == candidates.Count) MarkInitialSweepCompleted();
        if (released > 0) _activityLog.Info($"Verification retry released {released} failed item(s).");
        return released;
    }
}
