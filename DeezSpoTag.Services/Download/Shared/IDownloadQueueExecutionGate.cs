namespace DeezSpoTag.Services.Download.Shared;

public sealed record DownloadQueueExecutionDecision(
    bool Allowed,
    string ReasonCode,
    string Message,
    bool EnhancementPaused = false);

public interface IDownloadQueueExecutionGate
{
    Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(
        CancellationToken cancellationToken = default);
}
