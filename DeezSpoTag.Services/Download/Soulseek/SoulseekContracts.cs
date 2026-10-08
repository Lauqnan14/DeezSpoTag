namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The connection states DeezSpoTag surfaces for Soulseek, mirroring the provider state model the login
///     page and sidebar already use for every other platform.
/// </summary>
public enum SoulseekConnectionState
{
    /// <summary>No slskd base URL has been configured.</summary>
    NotConfigured,

    /// <summary>slskd is reachable but not logged in to Soulseek.</summary>
    Disconnected,

    /// <summary>slskd is connected and logged in.</summary>
    Connected,

    /// <summary>slskd could not be reached.</summary>
    Unavailable,

    /// <summary>slskd answered, but with an error such as a rejected API key.</summary>
    Error
}

/// <summary>A snapshot of the Soulseek connection state.</summary>
/// <param name="State">The current state.</param>
/// <param name="Message">A human-readable summary safe to show in the UI.</param>
/// <param name="Username">The logged-in Soulseek username, when known.</param>
/// <param name="LastError">The last connection error, when the state is <see cref="SoulseekConnectionState.Error"/>.</param>
/// <param name="CheckedAtUtc">When the state was last probed.</param>
/// <param name="ResponseTimeMs">How long the probe took, when it succeeded.</param>
public sealed record SoulseekConnectionStatus(
    SoulseekConnectionState State,
    string Message,
    string? Username = null,
    string? LastError = null,
    DateTimeOffset? CheckedAtUtc = null,
    double? ResponseTimeMs = null)
{
    /// <summary>Gets a value indicating whether Soulseek downloads can currently be attempted.</summary>
    public bool IsUsable => State == SoulseekConnectionState.Connected;
}

/// <summary>Whether a search is being run for a person or by automation.</summary>
public enum SoulseekSearchMode
{
    /// <summary>A person is browsing results; weaker matches may be shown for them to judge.</summary>
    Manual,

    /// <summary>Automation is queueing the download; weak matches must be rejected.</summary>
    Automated
}

/// <summary>The track facts a Soulseek search is built from.</summary>
/// <param name="Artist">Track artist.</param>
/// <param name="Title">Track title.</param>
/// <param name="Album">Album name, when known.</param>
/// <param name="DurationMs">Track duration, when known.</param>
/// <param name="Isrc">ISRC, when known.</param>
/// <param name="ReleaseYear">Release year, when known.</param>
/// <param name="AlbumArtist">Album artist, when known.</param>
public sealed record SoulseekSearchTarget(
    string Artist,
    string Title,
    string? Album = null,
    int? DurationMs = null,
    string? Isrc = null,
    int? ReleaseYear = null,
    string? AlbumArtist = null);

/// <summary>One file from a peer directory after parsing and current download policy have been applied.</summary>
public sealed record SoulseekBrowseFile(
    string Id,
    string Username,
    string RemoteDirectory,
    string Filename,
    string DisplayFilename,
    string Title,
    string? Artist,
    string? Album,
    int? TrackNumber,
    long Size,
    int? DurationSeconds,
    int? BitrateKbps,
    int? BitDepth,
    int? SampleRateHz,
    string QualityCode,
    string QualityLabel,
    bool Eligible,
    string? RejectedBecause,
    string SidecarRole = SoulseekSidecarPolicy.Other);

/// <summary>Everything slskd reported about one candidate file, before DeezSpoTag judges it.</summary>
/// <param name="Username">The peer offering the file.</param>
/// <param name="Filename">The full remote filename.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Extension">File extension as reported by slskd.</param>
/// <param name="BitrateKbps">Bitrate in kbps, when slskd determined it.</param>
/// <param name="BitDepth">Bit depth, when slskd determined it.</param>
/// <param name="SampleRateHz">Sample rate in Hz, when slskd determined it.</param>
/// <param name="DurationSeconds">Duration in seconds, when slskd determined it.</param>
/// <param name="IsVariableBitrate">Whether the peer advertised a variable bitrate file.</param>
/// <param name="IsLocked">Whether the file is locked by the peer.</param>
/// <param name="PeerQueueLength">The peer's download queue length at search time.</param>
/// <param name="PeerHasFreeUploadSlot">Whether the peer advertised a free upload slot.</param>
/// <param name="PeerUploadSpeed">The peer's advertised upload speed in bytes per second.</param>
public sealed record SoulseekRawCandidate(
    string Username,
    string Filename,
    long Size,
    string? Extension = null,
    int? BitrateKbps = null,
    int? BitDepth = null,
    int? SampleRateHz = null,
    int? DurationSeconds = null,
    bool IsVariableBitrate = false,
    bool IsLocked = false,
    long PeerQueueLength = 0,
    bool PeerHasFreeUploadSlot = false,
    int PeerUploadSpeed = 0)
{
    /// <summary>
    ///     Stable identity for this candidate across a search: the peer plus the exact remote path.
    /// </summary>
    /// <remarks>
    ///     This is what live dedupe keys on. The same file can be reported more than once as peers respond
    ///     over time, and re-reading the whole response set each tick would otherwise re-add it and make
    ///     rows flicker in and out.
    /// </remarks>
    public string Identity => $"{Username}\u0000{Filename}";
}

/// <summary>A candidate after quality normalization, peer policy and scoring have run.</summary>
/// <param name="Raw">The underlying slskd facts.</param>
/// <param name="Quality">The Soulseek quality code, for example <c>FLAC</c> or <c>MP3_320</c>.</param>
/// <param name="QualityLabel">The user-facing quality label.</param>
/// <param name="TierValue">The DeezSpoTag quality tier this maps onto, or <see langword="null"/> when unknown.</param>
/// <param name="CanonicalRank">The canonical rank of the mapped tier, or <see langword="null"/> when unknown.</param>
/// <param name="Score">The candidate's score, higher is better.</param>
/// <param name="IdentityConfidence">
///     How much identity the filename itself carried, from 0 to 1. Used only to break ties between otherwise
///     equal candidates; it is deliberately not folded into <paramref name="Score"/>, because the shared
///     matcher already accounts for the artist.
/// </param>
/// <param name="Accepted">Whether the candidate passed scoring and policy.</param>
/// <param name="RejectedBecause">Why the candidate was rejected, when it was.</param>
public sealed record SoulseekCandidate(
    SoulseekRawCandidate Raw,
    string Quality,
    string QualityLabel,
    string? TierValue = null,
    int? CanonicalRank = null,
    double Score = 0,
    double IdentityConfidence = 0,
    bool Accepted = false,
    string? RejectedBecause = null)
{
    /// <summary>Gets the peer's username.</summary>
    public string Username => Raw.Username;

    /// <summary>Gets the remote filename.</summary>
    public string Filename => Raw.Filename;
}

/// <summary>The outcome of a search that DeezSpoTag ran through slskd.</summary>
/// <param name="SearchId">The slskd search identifier.</param>
/// <param name="SearchText">The search text that was issued.</param>
/// <param name="Candidates">The scored candidates.</param>
/// <param name="Best">The winning candidate, when one was found.</param>
/// <param name="Completed">Whether the search ran to completion before the timeout.</param>
/// <param name="TimedOut">Whether the search hit its timeout.</param>
/// <param name="ResponseCount">How many peers answered the search.</param>
/// <param name="QueueUuid">The download queue item the search was for, when there was one.</param>
/// <param name="ErrorCode">A stable code for why the search produced nothing usable, when it failed.</param>
/// <param name="Error">A user-safe sentence saying the search failed.</param>
public sealed record SoulseekSearchOutcome(
    Guid SearchId,
    string SearchText,
    IReadOnlyList<SoulseekCandidate> Candidates,
    SoulseekCandidate? Best = null,
    bool Completed = false,
    bool TimedOut = false,
    int ResponseCount = 0,
    string? QueueUuid = null,
    string? ErrorCode = null,
    string? Error = null)
{
    /// <summary>
    ///     Gets whether the search failed rather than finding nothing.
    /// </summary>
    /// <remarks>
    ///     Derived from the code rather than tracked separately, so an outcome cannot claim both a failure and a
    ///     clean completion. A failed search is also not <see cref="Completed"/> or <see cref="TimedOut"/>: peers
    ///     answered and the search finished, but its files were never retrieved, so neither of those two words
    ///     describes it.
    /// </remarks>
    public bool Failed => ErrorCode is not null;
}

/// <summary>The state of a transfer as DeezSpoTag tracks it.</summary>
/// <param name="TransferId">The slskd transfer identifier, when one exists.</param>
/// <param name="Username">The peer being downloaded from.</param>
/// <param name="Filename">The remote filename.</param>
/// <param name="State">The decoded slskd state label.</param>
/// <param name="IsTerminal">Whether the transfer has finished.</param>
/// <param name="IsSuccessful">Whether the transfer succeeded.</param>
/// <param name="BytesTransferred">Bytes received so far.</param>
/// <param name="Size">Total size in bytes.</param>
/// <param name="AverageSpeed">Average speed in bytes per second.</param>
/// <param name="PlaceInQueue">Position in the peer's queue.</param>
/// <param name="Progress">Completion percentage.</param>
/// <param name="Error">The failure detail, when the transfer failed.</param>
public sealed record SoulseekTransferStatus(
    Guid? TransferId,
    string Username,
    string Filename,
    string State,
    bool IsTerminal,
    bool IsSuccessful,
    long BytesTransferred,
    long Size,
    double AverageSpeed,
    int? PlaceInQueue,
    double Progress,
    string? Error = null);

/// <summary>A transfer that has finished and whose file has been verified in staging.</summary>
/// <param name="Username">The peer the file came from.</param>
/// <param name="Filename">The remote filename.</param>
/// <param name="LocalPath">The verified local path of the completed file.</param>
/// <param name="Size">The file size in bytes.</param>
public sealed record SoulseekCompletedFile(
    string Username,
    string Filename,
    string LocalPath,
    long Size);

/// <summary>Why a peer was accepted or rejected.</summary>
/// <param name="Accepted">Whether the peer may be downloaded from.</param>
/// <param name="Reason">A short reason code or message, when rejected or noteworthy.</param>
public sealed record SoulseekPeerDecision(bool Accepted, string? Reason = null);

/// <summary>A share DeezSpoTag wants slskd to expose, derived from folder settings.</summary>
/// <param name="FolderId">The DeezSpoTag folder id.</param>
/// <param name="LocalPath">The local path to expose.</param>
/// <param name="Alias">The remote alias to hide the local folder name.</param>
/// <param name="IncludeFilters">DeezSpoTag-side include patterns.</param>
/// <param name="ExcludeFilters">DeezSpoTag-side exclude patterns.</param>
public sealed record SoulseekDesiredShare(
    long FolderId,
    string LocalPath,
    string? Alias = null,
    IReadOnlyList<string>? IncludeFilters = null,
    IReadOnlyList<string>? ExcludeFilters = null);

/// <summary>A share slskd currently reports.</summary>
/// <param name="ShareId">The slskd share identifier.</param>
/// <param name="LocalPath">The local path slskd is sharing.</param>
/// <param name="Alias">The alias, when configured.</param>
/// <param name="IsExcluded">Whether slskd has the share excluded.</param>
/// <param name="RemotePath">The path exposed to remote peers.</param>
/// <param name="Files">File count from the last scan.</param>
public sealed record SoulseekActualShare(
    string ShareId,
    string? LocalPath,
    string? Alias = null,
    bool IsExcluded = false,
    string? RemotePath = null,
    int? Files = null);

/// <summary>The severity of a share diagnostic.</summary>
public enum SoulseekShareDiagnosticSeverity
{
    /// <summary>Informational.</summary>
    Info,

    /// <summary>Worth the user's attention.</summary>
    Warning,

    /// <summary>Something is wrong.</summary>
    Error
}

/// <summary>One share-sync finding.</summary>
/// <param name="Code">A stable short code for the UI and tests.</param>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="FolderId">The folder involved, when the finding is about one folder.</param>
public sealed record SoulseekShareDiagnostic(
    string Code,
    SoulseekShareDiagnosticSeverity Severity,
    string Message,
    long? FolderId = null);

/// <summary>
///     A comparison of what DeezSpoTag wants shared against what slskd reports, plus a paste-ready
///     configuration block.
/// </summary>
/// <remarks>
/// slskd has no API for creating or updating shares, so the generated block is for the user to apply
/// to the slskd instance. This type never claims to have changed anything remotely.
/// </remarks>
/// <param name="GeneratedAtUtc">When the reconciliation ran.</param>
/// <param name="EnabledFolders">Folders the user has enabled for sharing.</param>
/// <param name="SkippedFolders">Folders that were skipped, with the reason.</param>
/// <param name="MissingFromSlskd">Enabled folders slskd is not currently sharing.</param>
/// <param name="UnexpectedlyShared">Paths slskd shares that no enabled folder accounts for.</param>
/// <param name="Diagnostics">Diagnostics for the sync.</param>
/// <param name="GeneratedYaml">A paste-ready <c>shares.directories</c> block.</param>
/// <param name="SlskdUnavailable">Whether slskd could not be reached, so the diff is one-sided.</param>
public sealed record SoulseekShareReconciliation(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<SoulseekDesiredShare> EnabledFolders,
    IReadOnlyList<(long FolderId, string Reason)> SkippedFolders,
    IReadOnlyList<SoulseekDesiredShare> MissingFromSlskd,
    IReadOnlyList<SoulseekActualShare> UnexpectedlyShared,
    IReadOnlyList<SoulseekShareDiagnostic> Diagnostics,
    string GeneratedYaml,
    bool SlskdUnavailable = false);
