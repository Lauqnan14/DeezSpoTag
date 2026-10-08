namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Publishes Soulseek progress to whatever is listening.
/// </summary>
/// <remarks>
///     <para>
///         The implementation lives in the web tier because it targets SignalR, and the download services live
///         in <c>DeezSpoTag.Services</c>, which cannot depend on the web tier. This interface is the seam: the
///         services report progress, and the web tier decides how to deliver it.
///     </para>
///     <para>
///         Every member is optional. A background worker with no UI attached simply does without one, so
///         progress reporting can never be a reason for a download to fail.
///     </para>
/// </remarks>
public interface ISoulseekRealtimePublisher
{
    void PublishConnectionState(SoulseekConnectionStatus status);

    void PublishEngineHealth(string state, string? message);

    void PublishSearchUpdate(SoulseekSearchProgress progress);

    void PublishSearchResult(SoulseekSearchOutcome outcome);

    void PublishDownloadUpdate(SoulseekDownloadProgress progress);

    void PublishShareSyncUpdate(SoulseekShareReconciliation reconciliation);

    void PublishShareScanUpdate(SoulseekShareScanStatus status);

    void PublishImportUpdate(SoulseekImportUpdate update);
}

/// <summary>
///     A publisher that discards everything.
/// </summary>
/// <remarks>
///     Used when no web tier is attached, so the download path does not need a null check at every call site.
/// </remarks>
public sealed class NullSoulseekRealtimePublisher : ISoulseekRealtimePublisher
{
    public static NullSoulseekRealtimePublisher Instance { get; } = new();

    public void PublishConnectionState(SoulseekConnectionStatus status)
    {
    }

    public void PublishEngineHealth(string state, string? message)
    {
    }

    public void PublishSearchUpdate(SoulseekSearchProgress progress)
    {
    }

    public void PublishSearchResult(SoulseekSearchOutcome outcome)
    {
    }

    public void PublishDownloadUpdate(SoulseekDownloadProgress progress)
    {
    }

    public void PublishShareSyncUpdate(SoulseekShareReconciliation reconciliation)
    {
    }

    public void PublishShareScanUpdate(SoulseekShareScanStatus status)
    {
    }

    public void PublishImportUpdate(SoulseekImportUpdate update)
    {
    }
}
