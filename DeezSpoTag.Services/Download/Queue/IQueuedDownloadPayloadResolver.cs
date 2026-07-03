namespace DeezSpoTag.Services.Download.Queue;

public sealed record QueuedDownloadPayloadResolution(
    DownloadQueueItem Item,
    string? Error);

public interface IQueuedDownloadPayloadResolver
{
    Task<QueuedDownloadPayloadResolution> ResolveAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken);
}
