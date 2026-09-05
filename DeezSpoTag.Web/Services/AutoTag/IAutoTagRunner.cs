namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// Typed outcome of a runner pass. The service switches on this instead of parsing
/// error-string prefixes ("stopped" / "paused: ..."), so control flow can no longer
/// break when a message changes.
/// </summary>
public enum AutoTagRunOutcome
{
    Completed,
    Stopped,
    Paused,
    Failed
}

public sealed record AutoTagRunResult(AutoTagRunOutcome Outcome, string? Error)
{
    public bool Success => Outcome == AutoTagRunOutcome.Completed;

    public static AutoTagRunResult Completed() => new(AutoTagRunOutcome.Completed, null);
    public static AutoTagRunResult Stopped(string? error = null) => new(AutoTagRunOutcome.Stopped, error ?? AutoTagProtocol.StoppedOutcome);
    public static AutoTagRunResult Paused(string message) => new(AutoTagRunOutcome.Paused, message);
    public static AutoTagRunResult Failed(string? error) => new(AutoTagRunOutcome.Failed, error);
}

public sealed record AutoTagResumeCursor(
    int PlatformIndex,
    int FileIndex,
    int? PlatformCount = null,
    int? FileCount = null,
    string? LastPath = null);

public interface IAutoTagRunner
{
    Task<AutoTagRunResult> RunAsync(
        string jobId,
        string rootPath,
        string configPath,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        Func<IReadOnlyList<string>, CancellationToken, Task>? batchCompletedCallback,
        AutoTagResumeCursor? resumeCursor,
        CancellationToken cancellationToken);

    Task<bool> StopAsync(string jobId, CancellationToken cancellationToken);
}
