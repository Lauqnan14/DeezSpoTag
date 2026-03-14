namespace DeezSpoTag.Services.Tagging;

public sealed record TaggingJobEnqueueRequest(
    string FilePath,
    string? TrackId = null,
    string Operation = "retag",
    int? MaxAttempts = null);

public interface ITaggingJobQueue
{
    Task<long> EnqueueAsync(TaggingJobEnqueueRequest request, CancellationToken cancellationToken = default);
}
