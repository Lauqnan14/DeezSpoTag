namespace DeezSpoTag.Services.Download.Shared;

public interface IPostDownloadTaskScheduler
{
    ValueTask EnqueueAsync(
        string queueUuid,
        string engine,
        Func<IServiceProvider, CancellationToken, Task> workItem,
        CancellationToken cancellationToken = default);
}
