namespace DeezSpoTag.Web.Services.AutoTag;

public sealed record AutoTagRunResult(bool Success, string? Error);
public sealed record AutoTagResumeCursor(
    int PlatformIndex,
    int FileIndex,
    int? PlatformCount = null,
    int? FileCount = null);

public interface IAutoTagRunner
{
    Task<AutoTagRunResult> RunAsync(
        string jobId,
        string rootPath,
        string configPath,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        AutoTagResumeCursor? resumeCursor,
        CancellationToken cancellationToken);

    Task<bool> StopAsync(string jobId, CancellationToken cancellationToken);
}
