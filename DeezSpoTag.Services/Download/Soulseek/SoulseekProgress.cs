namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>The stage a Soulseek search has reached, for the realtime progress event.</summary>
public enum SoulseekSearchStage
{
    Queued,
    Starting,
    Searching,
    Completed,
    TimedOut,
    Failed,
    Cancelled
}

/// <summary>Search progress, published so the UI can show a search running rather than appearing stalled.</summary>
/// <param name="SearchId">The slskd search identifier.</param>
/// <param name="QueueUuid">The download queue item, when the search was started for one.</param>
/// <param name="SearchText">The Soulseek search text.</param>
/// <param name="Stage">Where the search has reached.</param>
/// <param name="Completed">Whether the search ran to completion.</param>
/// <param name="TimedOut">Whether the search hit its timeout.</param>
/// <param name="ResponseCount">How many peers have responded so far.</param>
/// <param name="ElapsedSeconds">How long the search has been running.</param>
/// <param name="ErrorCode">A stable code for why the search produced nothing usable, when it failed.</param>
/// <param name="Error">A user-safe sentence saying the search failed.</param>
public sealed record SoulseekSearchProgress(
    Guid SearchId,
    string? QueueUuid,
    string SearchText,
    SoulseekSearchStage Stage,
    bool Completed = false,
    bool TimedOut = false,
    int ResponseCount = 0,
    double ElapsedSeconds = 0,
    IReadOnlyList<SoulseekCandidate>? Results = null,
    bool Final = false,
    string? ErrorCode = null,
    string? Error = null);

/// <summary>The stage a Soulseek download has reached.</summary>
public enum SoulseekDownloadStage
{
    Queued,
    Searching,
    Enqueued,
    Transferring,
    Verifying,
    Completed,
    Failed
}

/// <summary>Transfer progress, published so the download appears in the activity view like any other engine.</summary>
/// <param name="QueueUuid">The download queue item.</param>
/// <param name="Username">The peer being downloaded from.</param>
/// <param name="Filename">The remote filename.</param>
/// <param name="Stage">Where the transfer has reached.</param>
/// <param name="Progress">Completion percentage.</param>
/// <param name="BytesTransferred">Bytes received so far.</param>
/// <param name="Size">Total size in bytes.</param>
/// <param name="AverageSpeed">Average speed in bytes per second.</param>
/// <param name="Verified">Whether the finished file was found and verified on disk.</param>
/// <param name="Error">The failure detail, when the download failed.</param>
public sealed record SoulseekDownloadProgress(
    string QueueUuid,
    string Username,
    string Filename,
    SoulseekDownloadStage Stage,
    double Progress = 0,
    long BytesTransferred = 0,
    long Size = 0,
    double AverageSpeed = 0,
    bool Verified = false,
    string? Error = null);

/// <summary>Post-import progress, so the user can see tagging and enrichment pick up where the transfer left off.</summary>
/// <param name="QueueUuid">The download queue item.</param>
/// <param name="Stage">The import stage.</param>
/// <param name="FinalPath">Where the file ended up, once it is known.</param>
/// <param name="EnrichmentStatus">The queue item's enrichment status.</param>
/// <param name="Error">The failure detail, when the import failed.</param>
public sealed record SoulseekImportUpdate(
    string QueueUuid,
    string Stage,
    string? FinalPath = null,
    string? EnrichmentStatus = null,
    string? Error = null);
