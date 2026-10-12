using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Bridge from a completed public API session verification to verification-driven retry.
/// </summary>
/// <remarks>
/// Lives in the web layer because releasing an item needs <see cref="DeezSpoTagApp"/>, which is
/// registered in the web host. The controllers call this after a verification completes; the policy
/// itself stays in <see cref="VerificationRetryService"/>.
/// </remarks>
public static class PublicApiVerificationRetry
{
    /// <summary>
    /// Records the verification and releases any queue items that were waiting on it.
    /// Never throws: a failure to release must not turn a successful verification into an error
    /// response, because the session itself is already valid at this point.
    /// </summary>
    public static async Task<int> OnVerifiedAsync(
        string slug,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken, bool deferRetry = false, string? verifiedProviders = null)
    {
        if (deferRetry) return 0;
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<VerificationRetryService>();
            if (service is null)
            {
                return 0;
            }

            var sessions = scope.ServiceProvider.GetRequiredService<ZarzSignedSessionCoordinator>();
            var providers = string.IsNullOrWhiteSpace(verifiedProviders) ? new[] { slug } : verifiedProviders.Split(',');
            var verified = new List<string>();
            foreach (var provider in providers)
                if (DeezSpoTag.Services.Download.PublicApiSessionVerificationStore.TryNormalizeSlug(provider, out var normalized)
                    && await sessions.PeekUsableSessionAsync(normalized, cancellationToken)) verified.Add(normalized);
            IReadOnlyList<string>? usable = null;
            return await service.OnSessionsVerifiedAsync(verified, async (uuid, token) =>
            {
                usable ??= await scope.ServiceProvider.GetRequiredService<WatchlistPublicApiReadinessService>().GetUsablePublicSourcesAsync(token);
                return await ReleaseAsync(scope.ServiceProvider, uuid, usable, token);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PublicApiVerificationRetry));
            logger?.LogWarning(
                ex,
                "Verification-driven retry could not release items after {Slug} verification.",
                slug);
            return 0;
        }
    }

    public static async Task<int> RecoverPendingAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<VerificationRetryService>();
        IReadOnlyList<string>? usable = null;
        return await service.RecoverPendingAsync(async (uuid, token) =>
        {
            usable ??= await scope.ServiceProvider.GetRequiredService<WatchlistPublicApiReadinessService>().GetUsablePublicSourcesAsync(token);
            return await ReleaseAsync(scope.ServiceProvider, uuid, usable, token);
        }, cancellationToken);
    }

    private static async Task<bool> ReleaseAsync(IServiceProvider scopedProvider, string queueUuid,
        IReadOnlyList<string> usable, CancellationToken cancellationToken)
    {
        var repository = scopedProvider.GetRequiredService<DownloadQueueRepository>();
        var item = await repository.GetByUuidAsync(queueUuid, cancellationToken);
        if (item is null || !string.Equals(item.Status, "failed", StringComparison.OrdinalIgnoreCase)) return false;
        var sources = VerificationRetryService.GetPublicApiSources(item);
        if (sources.Count > 0 && !sources.Any(source => usable.Contains(source, StringComparer.OrdinalIgnoreCase))) return false;
        if (sources.Count == 0 && usable.Count == 0) return false;
        var app = scopedProvider.GetRequiredService<DeezSpoTagApp>();
        return await app.RetryDownloadAsync(queueUuid, cancellationToken, verificationRecovery: true);
    }
}
