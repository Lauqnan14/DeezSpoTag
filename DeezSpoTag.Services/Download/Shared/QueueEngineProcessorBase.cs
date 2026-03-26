using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Services.Download.Shared;

public abstract class QueueEngineProcessorBase : IQueueEngineProcessor
{
    protected QueueEngineProcessorBase(string engineName, EngineProcessorCommonDependencies commonDependencies)
    {
        Engine = engineName;
        CommonDependencies = commonDependencies;
    }

    protected EngineProcessorCommonDependencies CommonDependencies { get; }

    public string Engine { get; }

    Task IQueueEngineProcessor.ProcessQueueItemAsync(
        DownloadQueueItem item,
        DeezSpoTag.Services.Download.Deezer.IDeezerQueueContext context,
        CancellationToken cancellationToken) =>
        ProcessQueueItemAsync(item, cancellationToken);

    public abstract Task ProcessQueueItemAsync(DownloadQueueItem item, CancellationToken cancellationToken);
}
