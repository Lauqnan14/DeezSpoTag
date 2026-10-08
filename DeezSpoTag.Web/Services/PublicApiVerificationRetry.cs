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
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<VerificationRetryService>();
            if (service is null)
            {
                return 0;
            }

            return await service.OnSessionVerifiedAsync(
                slug,
                (queueUuid, token) => ReleaseAsync(scope.ServiceProvider, queueUuid, token),
                cancellationToken);
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

    /// <summary>
    /// Reuses the ordinary manual-retry path so a released item gets identical treatment to a user
    /// pressing Retry: ladder restarted at the first step, retry budget reset, original queue
    /// position kept. The verification flag is cleared so a later verification does not release the
    /// same item twice.
    /// </summary>
    private static async Task<bool> ReleaseAsync(
        IServiceProvider scopedProvider,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        var app = scopedProvider.GetService<DeezSpoTagApp>();
        if (app is null)
        {
            return false;
        }

        var released = await app.RetryDownloadAsync(queueUuid, cancellationToken);
        if (released)
        {
            var repository = scopedProvider.GetService<DownloadQueueRepository>();
            if (repository is not null)
            {
                await repository.ClearPublicApiRetryFlagAsync(queueUuid, cancellationToken);
            }
        }

        return released;
    }
}
