namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// Soulseek download behaviour settings.
/// </summary>
/// <remarks>
/// <para>
///     These are download *behaviour* settings only. Where the slskd instance lives and which API key to
///     use is connection configuration and stays on the login page, in the encrypted platform auth state.
///     Keeping the two apart means the API key never passes through <c>config.json</c>.
/// </para>
/// </remarks>
public class SoulseekDownloadSettings
{
    /// <summary>
    ///     Gets or sets whether candidates whose quality could not be determined may be accepted.
    /// </summary>
    /// <remarks>
    ///     Defaults to <see langword="false"/>. An undeterminable quality carries no rank, so accepting one
    ///     by accident would let an unknown file be treated as a match. The design requires this to be an
    ///     explicit opt-in.
    /// </remarks>
    public bool AllowUnknownQuality { get; set; } = false;

    /// <summary>Gets or sets how long a single search may run, in seconds.</summary>
    public int SearchTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the minimum peer upload speed in bytes per second.</summary>
    public long MinimumPeerUploadSpeedBytesPerSecond { get; set; } = 0;

    /// <summary>Gets or sets the largest peer download queue length that will be considered.</summary>
    public int MaximumPeerQueueLength { get; set; } = 5;

    /// <summary>
    ///     Gets or sets whether the peer must advertise a free upload slot. Peers without a free slot cannot
    ///     take a download, so this defaults to on.
    /// </summary>
    public bool RequireFreeUploadSlot { get; set; } = true;

    /// <summary>Gets or sets Soulseek usernames that must never be downloaded from.</summary>
    public List<string> BlockedUsers { get; set; } = new();

    /// <summary>
    ///     Gets or sets filename patterns that disqualify a candidate. Matching is case-insensitive.
    /// </summary>
    public List<string> BlockedFilenamePatterns { get; set; } = new();

    /// <summary>
    ///     Gets or sets how long a peer is avoided after letting a download down, in minutes.
    /// </summary>
    public int PeerCooldownMinutes { get; set; } = 60;

    /// <summary>
    ///     Gets or sets how long a search may remain in slskd before DeezSpoTag deletes it, in minutes.
    /// </summary>
    public int SearchRetentionMinutes { get; set; } = 15;

    /// <summary>
    ///     Gets or sets whether transfers that finished but produced no usable file should be retried
    ///     automatically. Defaults to off so a broken peer is not hammered.
    /// </summary>
    public bool AutoRetryIncompleteTransfers { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether Soulseek automation is switched on.</summary>
    /// <remarks>
    ///     This is a matching-strictness preference, not a kill switch. On, an unattended search applies the
    ///     stricter automated rules and can reject a weak match outright; off, a person driving the item sees
    ///     more candidates and can retry a weak match deliberately. It does not decide whether the engine runs
    ///     at all - the engine is switched off the same way as every other one, by not selecting Soulseek as a
    ///     source or clearing it in the custom engine order.
    /// </remarks>
    public bool AutomationEnabled { get; set; } = false;

    /// <summary>
    ///     Gets or sets whether a cover image the peer shared with the audio may be used as this track's
    ///     artwork.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Off by default, and it stays off until the reader turns it on. A peer is a stranger, so this is
    ///         opting to accept an image from one; nothing is fetched on the strength of it being present.
    ///     </para>
    ///     <para>
    ///         When on, the peer's own image is preferred over catalogue artwork for that download only. It
    ///         is the art that belongs to the exact release the peer shared, which for an obscure release is
    ///         frequently the only art there is. Every other engine, and this one's own fallback order when
    ///         the setting is off, are unaffected.
    ///     </para>
    /// </remarks>
    public bool UsePeerArtwork { get; set; } = false;

    /// <summary>
    ///     Gets or sets whether lyrics a peer shared with the audio may be used as this track's lyrics.
    /// </summary>
    /// <remarks>
    ///     Off by default, for the same reason as <see cref="UsePeerArtwork" />. Peer folders carry a cue
    ///     sheet far more often than an <c>.lrc</c>, and a cue sheet's lyric block is read the same way.
    /// </remarks>
    public bool UsePeerLyrics { get; set; } = false;

    /// <summary>Creates a copy so callers cannot mutate persisted settings by reference.</summary>
    public SoulseekDownloadSettings Clone() => new()
    {
        AllowUnknownQuality = AllowUnknownQuality,
        SearchTimeoutSeconds = SearchTimeoutSeconds,
        MinimumPeerUploadSpeedBytesPerSecond = MinimumPeerUploadSpeedBytesPerSecond,
        MaximumPeerQueueLength = MaximumPeerQueueLength,
        RequireFreeUploadSlot = RequireFreeUploadSlot,
        BlockedUsers = new List<string>(BlockedUsers),
        BlockedFilenamePatterns = new List<string>(BlockedFilenamePatterns),
        PeerCooldownMinutes = PeerCooldownMinutes,
        SearchRetentionMinutes = SearchRetentionMinutes,
        AutoRetryIncompleteTransfers = AutoRetryIncompleteTransfers,
        AutomationEnabled = AutomationEnabled,
        UsePeerArtwork = UsePeerArtwork,
        UsePeerLyrics = UsePeerLyrics
    };
}
