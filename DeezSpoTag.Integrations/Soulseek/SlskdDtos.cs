namespace DeezSpoTag.Integrations.Soulseek;

/// <summary>
///     Transport-level data transfer objects for the <c>slskd</c> <c>/api/v0</c> surface.
/// </summary>
/// <remarks>
///     These mirror the wire shapes returned by slskd and are intentionally mechanical. They carry no
///     DeezSpoTag business meaning: deciding which result is correct, which quality tier it maps to, and
///     where a finished file belongs all happen in <c>DeezSpoTag.Services</c>.
/// </remarks>
public sealed record SlskdServerState
{
    /// <summary>Gets or sets a value indicating whether slskd holds a connection to the Soulseek network.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Gets or sets a value indicating whether slskd is logged in to Soulseek.</summary>
    public bool IsLoggedIn { get; init; }

    /// <summary>Gets or sets a value indicating whether slskd is mid connect/disconnect/login transition.</summary>
    public bool IsTransitioning { get; init; }

    /// <summary>Gets or sets the logged-in Soulseek username, when known.</summary>
    public string? Username { get; init; }

    /// <summary>Gets or sets the raw <c>state</c> value as reported by slskd, for diagnostics.</summary>
    public string? RawState { get; init; }
}

/// <summary>A single file entry from a Soulseek search response or directory listing.</summary>
public sealed record SlskdFile
{
    /// <summary>Gets or sets the full remote filename, including the peer's share path.</summary>
    public string Filename { get; init; } = string.Empty;

    /// <summary>Gets or sets the file extension reported by slskd, including the leading dot when present.</summary>
    public string? Extension { get; init; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Gets or sets the bitrate in kbps, when slskd could determine it.</summary>
    public int? BitRate { get; init; }

    /// <summary>Gets or sets the bit depth, when slskd could determine it.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Gets or sets the sample rate in Hz, when slskd could determine it.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Gets or sets the duration in seconds, when slskd could determine it.</summary>
    public int? Length { get; init; }

    /// <summary>Gets or sets a value indicating whether the peer advertised a variable bitrate file.</summary>
    public bool IsVariableBitRate { get; init; }

    /// <summary>Gets or sets a value indicating whether the file is locked by the peer.</summary>
    public bool IsLocked { get; init; }
}

/// <summary>One peer's response to a Soulseek search.</summary>
public sealed record SlskdSearchResponse
{
    /// <summary>Gets or sets the peer's Soulseek username.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Gets or sets the peer's current download queue length.</summary>
    public long QueueLength { get; init; }

    /// <summary>Gets or sets a value indicating whether the peer advertises a free upload slot.</summary>
    public bool HasFreeUploadSlot { get; init; }

    /// <summary>Gets or sets the peer's advertised upload speed in bytes per second.</summary>
    public int UploadSpeed { get; init; }

    /// <summary>Gets or sets the unlocked files the peer offered.</summary>
    public IReadOnlyList<SlskdFile> Files { get; init; } = [];

    /// <summary>Gets or sets the locked files the peer offered.</summary>
    public IReadOnlyList<SlskdFile> LockedFiles { get; init; } = [];

    /// <summary>Gets or sets the total number of files in the response, including locked files.</summary>
    public int FileCount { get; init; }

    /// <summary>Gets or sets the token slskd uses to correlate this response with its search.</summary>
    public int Token { get; init; }
}

/// <summary>The lifecycle state of a search as tracked by slskd.</summary>
public sealed record SlskdSearch
{
    /// <summary>Gets or sets the slskd search identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets or sets the search text that was issued.</summary>
    public string? SearchText { get; init; }

    /// <summary>
    ///     Gets or sets the raw numeric state reported by slskd. Retained for diagnostics only; completion
    ///     is derived from <see cref="EndedAt"/> so that the adapter does not depend on the numeric values
    ///     of slskd's internal <c>SearchStates</c> flags.
    /// </summary>
    public long State { get; init; }

    /// <summary>
    ///     Gets or sets a value indicating whether the search has finished, one way or another. slskd always
    ///     stamps <c>endedAt</c> on termination, including timeout, cancellation and error.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>Gets or sets when the search started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Gets or sets when the search ended, when it has.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets or sets the number of files seen so far.</summary>
    public int FileCount { get; init; }

    /// <summary>Gets or sets the number of peers that responded so far.</summary>
    public int ResponseCount { get; init; }

    /// <summary>Gets or sets the number of files locked by peers.</summary>
    public int LockedFileCount { get; init; }

    /// <summary>Gets or sets the token slskd assigned to the search.</summary>
    public int Token { get; init; }

    /// <summary>Gets or sets the responses, when they were requested inline.</summary>
    public IReadOnlyList<SlskdSearchResponse> Responses { get; init; } = [];
}

/// <summary>The body posted to <c>POST /api/v0/searches</c>.</summary>
public sealed record SlskdSearchRequest
{
    /// <summary>Gets or sets the Soulseek search text.</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>Gets or sets an identifier to reuse, allowing a retried search to be joined.</summary>
    public Guid? Id { get; init; }

    /// <summary>
    ///     Gets or sets the search timeout in <b>milliseconds</b>, which is the unit slskd expects.
    /// </summary>
    /// <remarks>
    ///     slskd passes this straight to Soulseek.NET's <c>SearchOptions.SearchTimeout</c>, documented in
    ///     milliseconds with a default of 15000. A value in seconds is silently a thousand times too small.
    /// </remarks>
    public int? SearchTimeout { get; init; }

    /// <summary>Gets or sets the maximum number of peer responses to retain.</summary>
    public int? ResponseLimit { get; init; }

    /// <summary>Gets or sets the maximum number of files to retain per peer.</summary>
    public int? FileLimit { get; init; }

    /// <summary>Gets or sets a value indicating whether slskd should pre-filter peer responses.</summary>
    public bool? FilterResponses { get; init; }

    /// <summary>Gets or sets the maximum peer queue length to accept.</summary>
    public int? MaximumPeerQueueLength { get; init; }

    /// <summary>Gets or sets the minimum peer upload speed to accept.</summary>
    public int? MinimumPeerUploadSpeed { get; init; }

    /// <summary>Gets or sets the minimum number of files a peer must offer to be retained.</summary>
    public int? MinimumResponseFileCount { get; init; }
}

/// <summary>The body posted to <c>POST /api/v0/transfers/downloads/{username}</c>.</summary>
public sealed record SlskdQueueDownload
{
    /// <summary>Gets or sets the remote filename to fetch.</summary>
    public string Filename { get; init; } = string.Empty;

    /// <summary>Gets or sets the expected size in bytes.</summary>
    public long Size { get; init; }
}

/// <summary>
///     The decoded state of a transfer.
/// </summary>
/// <remarks>
///     slskd exposes <c>TransferStates</c> as a numeric bit field or as comma-separated flag names.
///     Both representations are normalised to the same bits before queued/in-progress/terminal states
///     are classified.
/// </remarks>
public sealed record SlskdTransferStateInfo
{
    private const long Requested = 1;
    private const long Queued = 2;
    private const long Initializing = 4;
    private const long InProgress = 8;
    private const long Completed = 16;
    private const long Succeeded = 32;
    private const long Cancelled = 64;
    private const long TimedOut = 128;
    private const long Errored = 256;
    private const long Rejected = 512;
    private const long Aborted = 1024;
    private const long Locally = 2048;
    private const long Remotely = 4096;

    /// <summary>Gets the raw numeric state reported by slskd.</summary>
    public long Raw { get; init; }

    /// <summary>Gets a value indicating whether slskd has requested or queued the transfer.</summary>
    public bool IsQueued { get; init; }

    /// <summary>Gets a value indicating whether the transfer is actively running.</summary>
    public bool IsInProgress { get; init; }

    /// <summary>Gets a value indicating whether the transfer has reached a terminal state.</summary>
    public bool IsTerminal { get; init; }

    /// <summary>Gets a value indicating whether the transfer completed successfully.</summary>
    public bool IsSuccessful { get; init; }

    /// <summary>Gets a value indicating whether the transfer failed.</summary>
    public bool IsFailed { get; init; }

    /// <summary>Gets a short label describing the state, for diagnostics and API responses.</summary>
    public string Label { get; init; } = "none";

    /// <summary>
    ///     Decodes slskd's numeric transfer state bit field.
    /// </summary>
    public static SlskdTransferStateInfo Decode(long raw)
    {
        var terminal = (raw & Completed) == Completed;
        var succeeded = (raw & Completed) == Completed && (raw & Succeeded) == Succeeded;
        var failed = terminal && !succeeded;
        var inProgress = (raw & Initializing) == Initializing || (raw & InProgress) == InProgress;
        var queued = !terminal && !inProgress
            && ((raw & Queued) == Queued || (raw & Requested) == Requested || raw == 0);

        return new SlskdTransferStateInfo
        {
            Raw = raw,
            IsQueued = queued,
            IsInProgress = inProgress,
            IsTerminal = terminal,
            IsSuccessful = succeeded,
            IsFailed = failed,
            Label = Describe(raw, terminal, succeeded, inProgress, queued)
        };
    }

    internal static long ParseRaw(string? value)
    {
        if (long.TryParse(value, out var numeric))
        {
            return numeric;
        }

        var raw = 0L;
        foreach (var flag in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            raw |= flag.ToLowerInvariant() switch
            {
                "requested" => Requested,
                "queued" => Queued,
                "initializing" => Initializing,
                "inprogress" => InProgress,
                "completed" => Completed,
                "succeeded" => Succeeded,
                "cancelled" => Cancelled,
                "timedout" => TimedOut,
                "errored" => Errored,
                "rejected" => Rejected,
                "aborted" => Aborted,
                "locally" => Locally,
                "remotely" => Remotely,
                _ => 0
            };
        }

        return raw;
    }

    private static string Describe(long raw, bool terminal, bool succeeded, bool inProgress, bool queued)
    {
        if (succeeded)
        {
            return "completed";
        }

        if (terminal)
        {
            if ((raw & Cancelled) == Cancelled)
            {
                return "cancelled";
            }

            if ((raw & Aborted) == Aborted)
            {
                return "aborted";
            }

            if ((raw & Rejected) == Rejected)
            {
                return "rejected";
            }

            if ((raw & TimedOut) == TimedOut)
            {
                return "timed_out";
            }

            if ((raw & Errored) == Errored)
            {
                return "errored";
            }

            return "completed_unknown";
        }

        if (inProgress)
        {
            return "in_progress";
        }

        if (queued)
        {
            if ((raw & Locally) == Locally)
            {
                return "queued_local";
            }

            if ((raw & Remotely) == Remotely)
            {
                return "queued_remote";
            }

            return "queued";
        }

        return "none";
    }
}

/// <summary>A download (or upload) transfer tracked by slskd.</summary>
public sealed record SlskdTransfer
{
    /// <summary>Gets or sets the slskd transfer identifier.</summary>
    public Guid? Id { get; init; }

    /// <summary>Gets or sets the counterparty's Soulseek username.</summary>
    public string? Username { get; init; }

    /// <summary>Gets or sets the remote filename.</summary>
    public string? Filename { get; init; }

    /// <summary>Gets or sets the total size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Gets or sets the raw numeric state reported by slskd.</summary>
    public long State { get; init; }

    /// <summary>Gets or sets the decoded state.</summary>
    public SlskdTransferStateInfo StateInfo { get; init; } = SlskdTransferStateInfo.Decode(0);

    /// <summary>Gets or sets the number of bytes received so far.</summary>
    public long BytesTransferred { get; init; }

    /// <summary>Gets or sets the average transfer speed in bytes per second.</summary>
    public double AverageSpeed { get; init; }

    /// <summary>Gets or sets the transfer's position in the peer's queue.</summary>
    public int? PlaceInQueue { get; init; }

    /// <summary>Gets or sets when the transfer was requested.</summary>
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>Gets or sets when the transfer was enqueued locally.</summary>
    public DateTimeOffset? EnqueuedAt { get; init; }

    /// <summary>Gets or sets when the transfer started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Gets or sets when the transfer ended, when it has.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets or sets the failure detail slskd recorded, when the transfer failed.</summary>
    public string? Exception { get; init; }

    /// <summary>Gets or sets the number of connection attempts made.</summary>
    public int Attempts { get; init; }

    /// <summary>Gets or sets when slskd will next retry the transfer.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>Gets or sets a value indicating whether the transfer was removed from the queue.</summary>
    public bool Removed { get; init; }

    /// <summary>Gets or sets the batch this transfer belongs to, when enqueued as a batch.</summary>
    public Guid? BatchId { get; init; }
}

/// <summary>A directory entry returned when browsing a peer.</summary>
public sealed record SlskdDirectory
{
    /// <summary>Gets or sets the directory path, relative to the peer's share root.</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>Gets or sets the number of files in the directory.</summary>
    public int FileCount { get; init; }

    /// <summary>Gets or sets the files in the directory.</summary>
    public IReadOnlyList<SlskdFile> Files { get; init; } = [];
}

/// <summary>The body posted to <c>POST /api/v0/users/{username}/directory</c>.</summary>
public sealed record SlskdDirectoryRequest
{
    /// <summary>Gets or sets the directory to list. Omit or leave null for the peer's share root.</summary>
    public string? Directory { get; init; }
}

/// <summary>A share configured inside slskd.</summary>
public sealed record SlskdShare
{
    /// <summary>Gets or sets the slskd share identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the share alias, when one is configured.</summary>
    public string? Alias { get; init; }

    /// <summary>Gets or sets a value indicating whether the share is excluded from being served.</summary>
    public bool IsExcluded { get; init; }

    /// <summary>Gets or sets the local path slskd is sharing.</summary>
    public string? LocalPath { get; init; }

    /// <summary>Gets or sets the raw configuration line the share was parsed from.</summary>
    public string? Raw { get; init; }

    /// <summary>Gets or sets the path the share is exposed as to remote peers.</summary>
    public string? RemotePath { get; init; }

    /// <summary>Gets or sets the number of directories in the last scan.</summary>
    public int? Directories { get; init; }

    /// <summary>Gets or sets the number of files in the last scan.</summary>
    public int? Files { get; init; }
}

/// <summary>A peer's advertised status, used by the peer policy.</summary>
public sealed record SlskdUserStatus
{
    /// <summary>Gets or sets the peer's username.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the peer is online.</summary>
    public bool IsOnline { get; init; }

    /// <summary>Gets or sets the peer's download queue length.</summary>
    public long QueueLength { get; init; }

    /// <summary>Gets or sets the peer's upload speed in bytes per second.</summary>
    public long UploadSpeed { get; init; }

    /// <summary>Gets or sets the number of free upload slots the peer advertises.</summary>
    public long? FreeUploadSlots { get; init; }
}
