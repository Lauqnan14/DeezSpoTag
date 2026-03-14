using DeezSpoTag.Services.Download.Deezer;
using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Services.Download.Shared;

public interface IQueueEngineProcessor
{
    string Engine { get; }
    Task ProcessQueueItemAsync(DownloadQueueItem item, IDeezerQueueContext context, CancellationToken cancellationToken);
}
