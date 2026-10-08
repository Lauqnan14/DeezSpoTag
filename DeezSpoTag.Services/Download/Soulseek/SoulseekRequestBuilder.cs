using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The resolved work for one Soulseek download.
/// </summary>
/// <remarks>
///     This carries the search target and the chosen candidate. The candidate may be null when the engine runs
///     with nothing pre-selected, in which case the engine searches at download time, which is how Soulseek
///     works: a Soulseek "resolution" is a live peer search, not a catalogue lookup.
/// </remarks>
public sealed class SoulseekDownloadRequest : EngineDownloadRequestBase
{
    /// <summary>Gets or sets the track to search for.</summary>
    public SoulseekSearchTarget Target { get; set; } = new(string.Empty, string.Empty);

    /// <summary>Gets or sets the peer's username, when a candidate was already chosen.</summary>
    public string Username { get; set; } = "";

    /// <summary>Gets or sets the chosen candidate's remote filename, when one was already chosen.</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>Gets or sets the chosen candidate's size in bytes.</summary>
    public long RemoteSizeBytes { get; set; }

    /// <summary>Gets or sets the Soulseek quality code to satisfy.</summary>
    public string SoulseekQuality { get; set; } = "";

    /// <summary>Gets or sets the download queue item this request serves, used to attribute progress.</summary>
    public string QueueUuid { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether the search should use the stricter automated rules.
    /// </summary>
    /// <remarks>
    ///     Taken from the user's automation setting rather than guessed per call, so a manual queue that the
    ///     user has turned automation off for still shows the looser results they can judge.
    /// </remarks>
    public bool Automation { get; set; }

    /// <summary>
    ///     Gets or sets DeezSpoTag's effective global download directory, where slskd must place completed downloads.
    /// </summary>
    /// <remarks>
    ///     This is derived only from <see cref="DeezSpoTagSettings.DownloadLocation"/>. Null means the global
    ///     download location is not configured, in which case no alternative directory may be searched.
    /// </remarks>
    public string? CompletedDownloadsRoot { get; set; }

    /// <summary>
    ///     Gets or sets the full remote paths of non-audio files to take from the same peer folder.
    /// </summary>
    /// <remarks>
    ///     Empty unless the reader opted in and ticked them. They are decoration, so every failure to take one
    ///     is logged and skipped: the audio file is the deliverable and must not be held up by its cover.
    /// </remarks>
    public IReadOnlyList<string> SidecarRemotePaths { get; set; } = [];

    /// <summary>
    ///     Gets or sets the local directory the sidecars are written into.
    /// </summary>
    /// <remarks>
    ///     Beside the audio, so a cover taken with a track is still on disk when the artwork step runs and
    ///     nothing has to be cleaned up if the download is abandoned before the move.
    /// </remarks>
    public string? SidecarOutputDir { get; set; }

    /// <summary>
    ///     Gets or sets the callback invoked the moment slskd reports a transfer for this request.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Called after the association has been written and before anything is waited on, so the queue row
    ///         carries the transfer id while the transfer is still running. A transfer is otherwise only recorded
    ///         at a terminal outcome, which means a download interrupted mid-flight leaves nothing to resume.
    ///     </para>
    ///     <para>
    ///         Transient and engine-owned. It is how the processor records the id on its payload; the association
    ///         in the transfer table remains the authority if this has not completed.
    ///     </para>
    /// </remarks>
    public Func<SoulseekTransferStatus, CancellationToken, Task>? TransferAttachedAsync { get; set; }

    /// <summary>
    ///     Gets or sets the token that is cancelled when the host is shutting down.
    /// </summary>
    /// <remarks>
    ///     Kept apart from the item's own token on purpose. The reader cancelling an item means the transfer
    ///     should stop; the host shutting down means slskd keeps fetching and a later run picks the transfer back
    ///     up. Treating the second as the first throws away a download that was nearly finished.
    /// </remarks>
    public CancellationToken HostStoppingToken { get; set; }
}

/// <summary>
///     Builds the resolved work for a Soulseek download from the queued payload.
/// </summary>
public static class SoulseekRequestBuilder
{
    public static SoulseekDownloadRequest BuildRequest(SoulseekQueueItem item, DeezSpoTagSettings settings)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);

        var request = RequestBuilderCommon.CreateCommonRequest<SoulseekDownloadRequest>(item, settings);
        var soulseek = settings.Soulseek ?? new SoulseekDownloadSettings();

        request.Target = new SoulseekSearchTarget(
            item.Artist,
            item.Title,
            string.IsNullOrWhiteSpace(item.Album) ? null : item.Album,
            DurationMs: item.AppleDurationMs > 0 ? item.AppleDurationMs : null,
            Isrc: string.IsNullOrWhiteSpace(item.Isrc) ? null : item.Isrc);

        // A candidate chosen during resolution is reused, so a resumed or retried download does not run a
        // second search and does not risk a different result. A candidate the reader picked in the search tab
        // arrives the same way, which is what makes "queue this file" mean this file.
        request.Username = item.SoulseekUsername;
        request.RemotePath = item.SoulseekRemotePath;
        request.RemoteSizeBytes = item.SoulseekRemoteSizeBytes;
        // A pinned candidate is what the reader chose, so the ladder must not be asked for a better one. The
        // requested code is left empty and the quality travels as the pinned file's own.
        request.SoulseekQuality = HasPinnedCandidate(item)
            ? item.SoulseekQualityCode
            : (string.IsNullOrWhiteSpace(item.SoulseekQualityCode)
                ? ResolveRequestedCode(item)
                : item.SoulseekQualityCode);
        request.QueueUuid = item.Id;
        request.Automation = soulseek.AutomationEnabled;
        request.CompletedDownloadsRoot = NormalizeRoot(DownloadPathResolver.ResolveIoPath(settings.DownloadLocation));

        // The non-audio files the reader chose. Both halves of the opt-in are honoured here rather than at
        // the drawer, so a request that somehow arrives with sidecars while the settings are off still
        // downloads nothing extra.
        var sidecars = new List<string>();
        if (soulseek.UsePeerArtwork || soulseek.UsePeerLyrics)
        {
            sidecars.AddRange(item.SoulseekSidecarRemotePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim()));
        }

        request.SidecarRemotePaths = sidecars;
        request.SidecarOutputDir = sidecars.Count > 0 ? request.OutputDir : null;

        return request;
    }

    /// <summary>
    ///     The effective global DeezSpoTag download root, or null when it has not been configured.
    /// </summary>
    /// <remarks>
    ///     An unset or blank value is left unset rather than defaulted. The default would be a guess at where a
    ///     file might be, and a guess that is wrong reads a directory the user never pointed at.
    /// </remarks>
    private static string? NormalizeRoot(string? configured)
    {
        var root = (configured ?? string.Empty).Trim();
        return root.Length == 0 ? null : root;
    }

    /// <summary>Whether the reader pinned the exact peer file this item is for.</summary>
    private static bool HasPinnedCandidate(SoulseekQueueItem item)
        => !string.IsNullOrWhiteSpace(item.SoulseekUsername) && !string.IsNullOrWhiteSpace(item.SoulseekRemotePath);

    /// <summary>
    ///     Resolves the Soulseek quality code this item should satisfy.
    /// </summary>
    /// <remarks>
    ///     The queued quality wins when it is a Soulseek code, because the resolution step already applied the
    ///     user's preferences. Otherwise the destination folder's desired tier is translated so a folder
    ///     pinned to "CD Lossless" asks for lossless.
    /// </remarks>
    private static string ResolveRequestedCode(SoulseekQueueItem item)
    {
        var queued = (item.Quality ?? string.Empty).Trim();
        if (queued.Length > 0
            && SoulseekQuality.KnownCodes.Contains(queued, StringComparer.OrdinalIgnoreCase))
        {
            return SoulseekQuality.NormalizeCode(queued);
        }

        if (item.RequestedQuality is { Length: > 0 } requested
            && SoulseekQuality.KnownCodes.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            return SoulseekQuality.NormalizeCode(requested);
        }

        return SoulseekQuality.ResolveCodeForTier(item.Quality) ?? string.Empty;
    }
}
