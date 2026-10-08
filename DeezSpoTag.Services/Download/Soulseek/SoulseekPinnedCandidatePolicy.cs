namespace DeezSpoTag.Services.Download.Soulseek;

using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;

/// <summary>
///     Distinguishes a reader's exact peer-file selection from an unpinned Soulseek ladder step.
/// </summary>
/// <remarks>
///     <para>
///         A manual selection names a peer and a file. It never advances to another quality, peer or engine.
///         An unpinned request from another source may search again and follow its configured fallback plan.
///     </para>
///     <para>
///         Two failures are not the file's fault. A cancelled item is the reader stopping it, and an
///         unattached or unconfigured Soulseek has no network to search - retrying either would fail the same
///         way twice and, in the cancellation case, ignore the reader.
///     </para>
/// </remarks>
public static class SoulseekPinnedCandidatePolicy
{
    /// <summary>A manual Soulseek selection is the peer and file, not merely a preference for the engine.</summary>
    public static bool IsManualSelection(DownloadIntent intent)
        => IsManualSelection(intent.SourceService, intent.SoulseekUsername, intent.SoulseekRemotePath);

    // The queued quality code is set when the reader selects a file. Automated Soulseek searches can also
    // record a peer and remote path, so those two fields alone cannot identify a manual selection on restart.
    public static bool IsManualSelection(EngineQueueItemBase payload)
        => payload is SoulseekQueueItem item
           && !string.IsNullOrWhiteSpace(item.SoulseekQualityCode)
           && IsManualSelection(item.SourceService, item.SoulseekUsername, item.SoulseekRemotePath);

    public static bool IsManualSelection(JsonObject payload)
        => !string.IsNullOrWhiteSpace(Read(payload, "SoulseekQualityCode", "soulseekQualityCode"))
           && IsManualSelection(
            Read(payload, "SourceService", "sourceService"),
            Read(payload, "SoulseekUsername", "soulseekUsername"),
            Read(payload, "SoulseekRemotePath", "soulseekRemotePath"));

    private static bool IsManualSelection(string? source, string? peer, string? path)
        => string.Equals(source, SoulseekQueueItem.EngineId, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(peer)
           && !string.IsNullOrWhiteSpace(path);

    private static string? Read(JsonObject payload, string pascal, string camel)
        => payload[pascal]?.ToString() ?? payload[camel]?.ToString();

    /// <summary>Whether an unpinned peer-search attempt may try another peer.</summary>
    /// <remarks>
    ///     <para>
    ///         Losing track of a transfer is excluded, and the reason is that it is not a peer's failure at all.
    ///         slskd may be fetching the file perfectly well while this app fails to record or watch it, so asking
    ///         a second peer would start a second download of a file already in flight and charge an innocent
    ///         volunteer for a bookkeeping failure here.
    ///     </para>
    /// </remarks>
    /// <param name="failure">What the unpinned attempt threw.</param>
    public static bool ShouldSearchAgain(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // A login that has been revoked is not the file's fault and not a peer's. Searching again cannot help:
        // the next attempt would reach the same gate and refuse the same way, having cost a peer search and
        // left the reader with three failed attempts instead of one clear instruction.
        return failure is not OperationCanceledException
            and not SoulseekUnavailableException
            and not SoulseekTransferMonitoringException
            and not SoulseekLoginRequiredException;
    }
}
