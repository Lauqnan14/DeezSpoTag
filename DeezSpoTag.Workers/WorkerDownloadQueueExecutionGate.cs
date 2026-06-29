using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Workers;

public sealed class WorkerDownloadQueueExecutionGate : IDownloadQueueExecutionGate
{
    private static readonly DownloadQueueExecutionDecision OpenDecision = new(
        true,
        "worker_queue_open",
        string.Empty);

    public Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(OpenDecision);
}
