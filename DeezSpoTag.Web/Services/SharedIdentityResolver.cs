using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

public sealed record SharedIdentityResolveItem(
    long LocalTrackId,
    string? FilePath = null,
    string? SearchName = null,
    string? SearchArtists = null);

public sealed record SharedIdentityResolveResult(
    long LocalTrackId,
    string? TargetItemId,
    string Status,
    bool Searched,
    bool Confirmed);

public sealed class SharedIdentityResolver
{
    public const string StatusResolved = "resolved";
    public const string StatusPendingRefresh = "pending_refresh";
    public const string StatusUnresolved = "unresolved";
    public const string StatusStale = "stale";
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RefreshThrottle = TimeSpan.FromMinutes(5);

    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<SharedIdentityResolver> _logger;

    public SharedIdentityResolver(
        LibraryRepository libraryRepository,
        ILogger<SharedIdentityResolver> logger)
    {
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SharedIdentityResolveResult>> ResolveAsync(
        string targetService,
        IReadOnlyList<SharedIdentityResolveItem> items,
        Func<SharedIdentityResolveItem, CancellationToken, Task<string?>> search,
        Func<string, CancellationToken, Task<bool>>? confirmMissing = null,
        bool confirmExisting = false,
        string? currentRevision = null,
        Func<string, CancellationToken, Task>? requestRefresh = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var normalizedTarget = (targetService ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedTarget) || items.Count == 0)
        {
            return [];
        }

        var distinctItems = items
            .Where(static item => item.LocalTrackId > 0)
            .GroupBy(static item => item.LocalTrackId)
            .Select(static group => group.First())
            .ToList();
        if (distinctItems.Count == 0)
        {
            return [];
        }

        var localTrackIds = distinctItems.Select(static item => item.LocalTrackId).ToList();
        var metadata = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            normalizedTarget,
            localTrackIds,
            cancellationToken);
        var ledgerRows = (await _libraryRepository.GetWatchlistSharedIdentitiesAsync(
                localTrackIds,
                normalizedTarget,
                cancellationToken))
            .ToDictionary(static row => row.LocalTrackId);
        var now = DateTimeOffset.UtcNow;
        var refreshRequestedThisBatch = false;
        var results = new List<SharedIdentityResolveResult>(distinctItems.Count);

        foreach (var item in distinctItems)
        {
            ledgerRows.TryGetValue(item.LocalTrackId, out var ledger);
            metadata.TryGetValue(item.LocalTrackId, out var mappedId);
            var hasMetadata = !string.IsNullOrWhiteSpace(mappedId);

            if (hasMetadata)
            {
                var shouldConfirm = confirmExisting
                    || (ledger != null
                        && confirmMissing != null
                        && await _libraryRepository.HasCompletedMediaServerRefreshSinceAsync(
                            normalizedTarget,
                            ledger.UpdatedAt,
                            cancellationToken));
                if (shouldConfirm && confirmMissing != null)
                {
                    var missing = await confirmMissing(mappedId!, cancellationToken);
                    if (missing)
                    {
                        await _libraryRepository.DeleteMediaServerTrackMetadataAsync(
                            normalizedTarget,
                            [item.LocalTrackId],
                            cancellationToken);
                        var pending = await UpsertPendingRefreshAsync(
                            item.LocalTrackId,
                            normalizedTarget,
                            ledger,
                            "Confirmed missing after write-lag or completed library refresh.",
                            now,
                            cancellationToken);
                        results.Add(new SharedIdentityResolveResult(
                            item.LocalTrackId,
                            null,
                            StatusPendingRefresh,
                            Searched: false,
                            Confirmed: true));
                        refreshRequestedThisBatch = await MaybeRequestRefreshAsync(
                            normalizedTarget,
                            pending.AttemptCount,
                            pending.NextRetryUtc,
                            item.LocalTrackId,
                            refreshRequestedThisBatch,
                            requestRefresh,
                            now,
                            cancellationToken);
                        continue;
                    }
                }

                var flippedToResolved = ledger == null
                    || !string.Equals(ledger.Status, StatusResolved, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(ledger.TargetItemId, mappedId, StringComparison.Ordinal);
                await _libraryRepository.UpsertWatchlistSharedIdentityAsync(
                    new WatchlistSharedIdentityUpsertInput(
                        item.LocalTrackId,
                        normalizedTarget,
                        mappedId,
                        StatusResolved,
                        LastError: null,
                        AttemptCount: 0,
                        NextRetryUtc: null,
                        LastRefreshRequestedUtc: ledger?.LastRefreshRequestedUtc),
                    cancellationToken);
                if (flippedToResolved && ledger != null)
                {
                    await EnqueueCatchUpAsync(
                        item.LocalTrackId,
                        normalizedTarget,
                        currentRevision,
                        cancellationToken);
                }

                results.Add(new SharedIdentityResolveResult(
                    item.LocalTrackId,
                    mappedId,
                    StatusResolved,
                    Searched: false,
                    Confirmed: shouldConfirm));
                continue;
            }

            if (ledger?.NextRetryUtc is { } nextRetry && nextRetry > now)
            {
                results.Add(new SharedIdentityResolveResult(
                    item.LocalTrackId,
                    null,
                    ledger.Status,
                    Searched: false,
                    Confirmed: false));
                continue;
            }

            var foundId = await search(item, cancellationToken);
            if (!string.IsNullOrWhiteSpace(foundId))
            {
                await _libraryRepository.UpsertMediaServerTrackMetadataAsync(
                    [
                        new MediaServerTrackMetadataUpsertDto(
                            item.LocalTrackId,
                            normalizedTarget,
                            foundId,
                            item.FilePath,
                            now)
                    ],
                    cancellationToken);
                await _libraryRepository.UpsertWatchlistSharedIdentityAsync(
                    new WatchlistSharedIdentityUpsertInput(
                        item.LocalTrackId,
                        normalizedTarget,
                        foundId,
                        StatusResolved,
                        LastError: null,
                        AttemptCount: 0,
                        NextRetryUtc: null,
                        LastRefreshRequestedUtc: ledger?.LastRefreshRequestedUtc),
                    cancellationToken);
                await EnqueueCatchUpAsync(
                    item.LocalTrackId,
                    normalizedTarget,
                    currentRevision,
                    cancellationToken);
                results.Add(new SharedIdentityResolveResult(
                    item.LocalTrackId,
                    foundId,
                    StatusResolved,
                    Searched: true,
                    Confirmed: false));
                continue;
            }

            var missed = await UpsertPendingRefreshAsync(
                item.LocalTrackId,
                normalizedTarget,
                ledger,
                "No target match found.",
                now,
                cancellationToken);
            _logger.LogInformation(
                "Identity miss localTrackId={LocalTrackId} target={Target} attempt={Attempt} nextRetry={NextRetry}",
                item.LocalTrackId,
                normalizedTarget,
                missed.AttemptCount,
                missed.NextRetryUtc);
            results.Add(new SharedIdentityResolveResult(
                item.LocalTrackId,
                null,
                StatusPendingRefresh,
                Searched: true,
                Confirmed: false));
            refreshRequestedThisBatch = await MaybeRequestRefreshAsync(
                normalizedTarget,
                missed.AttemptCount,
                missed.NextRetryUtc,
                item.LocalTrackId,
                refreshRequestedThisBatch,
                requestRefresh,
                now,
                cancellationToken);
        }

        return results;
    }

    private async Task EnqueueCatchUpAsync(
        long localTrackId,
        string targetService,
        string? currentRevision,
        CancellationToken cancellationToken)
    {
        await _libraryRepository.EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
            localTrackId,
            targetService,
            currentRevision ?? string.Empty,
            cancellationToken);
    }

    private async Task<WatchlistSharedIdentityDto> UpsertPendingRefreshAsync(
        long localTrackId,
        string targetService,
        WatchlistSharedIdentityDto? previous,
        string lastError,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attempt = (previous?.AttemptCount ?? 0) + 1;
        var nextRetry = now.Add(RetryDelay);
        var row = new WatchlistSharedIdentityUpsertInput(
            localTrackId,
            targetService,
            TargetItemId: null,
            StatusPendingRefresh,
            lastError,
            attempt,
            nextRetry,
            previous?.LastRefreshRequestedUtc);
        await _libraryRepository.UpsertWatchlistSharedIdentityAsync(row, cancellationToken);
        return new WatchlistSharedIdentityDto(
            localTrackId,
            targetService,
            null,
            StatusPendingRefresh,
            lastError,
            attempt,
            nextRetry,
            previous?.LastRefreshRequestedUtc,
            now);
    }

    private async Task<bool> MaybeRequestRefreshAsync(
        string targetService,
        int attemptCount,
        DateTimeOffset? nextRetryUtc,
        long localTrackId,
        bool alreadyRequestedThisBatch,
        Func<string, CancellationToken, Task>? requestRefresh,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (alreadyRequestedThisBatch || requestRefresh == null)
        {
            return alreadyRequestedThisBatch;
        }

        var lastRequested = await _libraryRepository.GetLastSharedIdentityRefreshRequestedUtcAsync(
            targetService,
            cancellationToken);
        if (lastRequested.HasValue && now - lastRequested.Value < RefreshThrottle)
        {
            return false;
        }

        await requestRefresh(targetService, cancellationToken);
        await _libraryRepository.MarkSharedIdentityRefreshRequestedAsync(
            targetService,
            now,
            cancellationToken);
        await _libraryRepository.UpsertWatchlistSharedIdentityAsync(
            new WatchlistSharedIdentityUpsertInput(
                localTrackId,
                targetService,
                TargetItemId: null,
                StatusPendingRefresh,
                "No target match found.",
                attemptCount,
                nextRetryUtc,
                now),
            cancellationToken);
        return true;
    }
}
