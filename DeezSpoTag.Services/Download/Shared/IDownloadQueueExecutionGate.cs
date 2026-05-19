namespace DeezSpoTag.Services.Download.Shared;

public interface IDownloadQueueExecutionGate
{
    Task<bool> CanStartDownloadAsync(CancellationToken cancellationToken = default);
}
