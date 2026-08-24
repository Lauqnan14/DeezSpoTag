using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed record SharedIdentityResolveItem(long LocalTrackId);

public sealed record SharedIdentityResolveResult(
    long LocalTrackId,
    string? TargetItemId,
    string Status);

public sealed class SharedIdentityResolver
{
    public const string StatusResolved = "resolved";
    public const string StatusPendingRefresh = "pending_refresh";
    public const string StatusUnresolved = "unresolved";
    public const string StatusStale = "stale";
    private readonly LibraryRepository _libraryRepository;

    public SharedIdentityResolver(LibraryRepository libraryRepository)
    {
        _libraryRepository = libraryRepository;
    }

    public async Task<IReadOnlyList<SharedIdentityResolveResult>> ResolveAsync(
        string targetService,
        IReadOnlyList<SharedIdentityResolveItem> items,
        CancellationToken cancellationToken = default)
    {
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

        var metadata = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            normalizedTarget,
            distinctItems.Select(static item => item.LocalTrackId).ToList(),
            cancellationToken);
        return distinctItems
            .Select(item => metadata.TryGetValue(item.LocalTrackId, out var targetItemId)
                            && !string.IsNullOrWhiteSpace(targetItemId)
                ? new SharedIdentityResolveResult(
                    item.LocalTrackId,
                    targetItemId,
                    StatusResolved)
                : new SharedIdentityResolveResult(
                    item.LocalTrackId,
                    null,
                    StatusPendingRefresh))
            .ToList();
    }
}
