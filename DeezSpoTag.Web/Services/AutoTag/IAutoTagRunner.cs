namespace DeezSpoTag.Web.Services.AutoTag;

public sealed record AutoTagRunResult(bool Success, string? Error);

public interface IAutoTagRunner
{
    Task<AutoTagRunResult> RunAsync(
        string jobId,
        string rootPath,
        string configPath,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        CancellationToken cancellationToken);

    Task<bool> StopAsync(string jobId, CancellationToken cancellationToken);
}
