namespace DeezSpoTag.Services.Download.Queue;

public static class QueuePreResolutionPlanner
{
    private static readonly HashSet<string> EligibleStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued",
        "resolving"
    };

    public static DownloadQueueItem? SelectNext(
        IReadOnlyList<DownloadQueueItem> tasks,
        string? queueOrder,
        int windowSize,
        TimeSpan retryDelay,
        DateTimeOffset now)
    {
        var clampedWindow = Math.Clamp(windowSize, 1, 25);
        return OrderQueue(tasks, queueOrder)
            .Where(static item => EligibleStatuses.Contains(item.Status))
            .Take(clampedWindow)
            .FirstOrDefault(item => ShouldAttemptResolution(item, retryDelay, now));
    }

    public static IEnumerable<DownloadQueueItem> OrderQueue(
        IReadOnlyList<DownloadQueueItem> tasks,
        string? queueOrder)
    {
        var newestFirst = string.Equals(queueOrder, "recent", StringComparison.OrdinalIgnoreCase);
        if (newestFirst)
        {
            return tasks
                .OrderBy(static item => item.QueueOrder is null)
                .ThenByDescending(static item => item.QueueOrder)
                .ThenByDescending(static item => item.CreatedAt)
                .ThenByDescending(static item => item.Id);
        }

        return tasks
            .OrderBy(static item => item.QueueOrder is null)
            .ThenBy(static item => item.QueueOrder)
            .ThenBy(static item => item.CreatedAt)
            .ThenBy(static item => item.Id);
    }

    private static bool ShouldAttemptResolution(
        DownloadQueueItem item,
        TimeSpan retryDelay,
        DateTimeOffset now)
    {
        var payload = QueuePreResolutionPayload.ParseOrEmpty(item.PayloadJson);
        return !QueuePreResolutionPayload.IsResolved(payload)
               && !QueuePreResolutionPayload.IsResolving(payload)
               && !QueuePreResolutionPayload.IsFailedOnCooldown(payload, retryDelay, now);
    }
}
