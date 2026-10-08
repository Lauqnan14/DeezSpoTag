using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The queued state of one Soulseek download.
/// </summary>
/// <remarks>
///     <para>
///         There is deliberately no per-engine identity here. A Soulseek candidate is a peer filename rather
///         than a catalogue track id, so the fields below describe the search to run and the transfer once it
///         has been chosen, and the chosen candidate is recorded on the instance so a resumed download does
///         not have to search again.
///     </para>
/// </remarks>
public sealed class SoulseekQueueItem : EngineQueueItemBase
{
    /// <summary>The engine id, matching <c>DownloadSourceCatalog</c>.</summary>
    public const string EngineId = "soulseek";

    public SoulseekQueueItem()
    {
        Engine = EngineId;
        SourceService = EngineId;
    }

    /// <summary>
    ///     The resolved-source value that stands for "a peer search at download time".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A Soulseek download is not a catalogue lookup: it has no store URL to resolve, and the engine
    ///         runs the peer search itself when the item downloads. The fallback machinery still needs a
    ///         non-empty resolution so the step is not discarded, and this obviously-fake scheme is what it
    ///         hands back.
    ///     </para>
    ///     <para>
    ///         It therefore has to be recognisable as this engine's own answer. Treated as an ordinary URL it
    ///         looks like a store link that belongs to some other service, the resolution is reported as a
    ///         mapping failure, and the item is filed as unavailable - a Soulseek download that never ran.
    ///     </para>
    /// </remarks>
    public const string PeerSearchResolutionSentinel = "soulseek://peer-search";

    /// <summary>Gets or sets the peer's username once a candidate has been chosen.</summary>
    public string SoulseekUsername { get; set; } = "";

    /// <summary>Gets or sets the chosen candidate's remote filename.</summary>
    public string SoulseekRemotePath { get; set; } = "";

    /// <summary>Gets or sets the slskd transfer id once the transfer has been enqueued.</summary>
    public string SoulseekTransferId { get; set; } = "";

    /// <summary>Gets or sets the Soulseek quality code that was requested for this item.</summary>
    public string SoulseekQualityCode { get; set; } = "";

    /// <summary>Gets or sets the candidate's score, kept for diagnostics and the activity view.</summary>
    public double SoulseekCandidateScore { get; set; }

    /// <summary>Gets or sets how many peers answered the search that produced the chosen candidate.</summary>
    public int SoulseekResponseCount { get; set; }

    /// <summary>Gets or sets how many candidates passed scoring.</summary>
    public int SoulseekAcceptedCandidateCount { get; set; }

    [JsonIgnore]
    public SoulseekDownloadStatus Status { get; set; } = SoulseekDownloadStatus.Queued;

    /// <summary>
    ///     Catalogue artwork for the queue display only. Never a tagging or embedding source.
    /// </summary>
    /// <remarks>
    ///     A Soulseek candidate is a peer filename, so this is the only artwork available for the queue entry.
    ///     The base <c>Cover</c> is left empty on purpose: the post-download pipeline treats that field as
    ///     prefetched artwork and would otherwise embed a URL the user never chose through their profile.
    /// </remarks>
    public string SoulseekDisplayCoverUrl { get; set; } = "";

    /// <summary>
    ///     Gets or sets the size the peer reported for the chosen candidate, in bytes.
    /// </summary>
    /// <remarks>
    ///     Carried so the delivered file can be checked against what was offered: a peer that sends a
    ///     different file is caught here rather than after it has been tagged into the library.
    /// </remarks>
    public long SoulseekRemoteSizeBytes { get; set; }

    /// <summary>
    ///     Gets or sets the full remote paths of the non-audio files the reader chose to take with this track.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Empty unless the reader turned peer artwork or peer lyrics on and ticked the files in the
    ///         album drawer. A Soulseek release that is on no streaming service often has a cover image sitting
    ///         in the peer's folder and nowhere else, so this is occasionally the only artwork or lyrics that
    ///         will ever exist for a track.
    ///     </para>
    ///     <para>
    ///         These are full remote paths on the same peer as the audio, and they are re-verified against the
    ///         peer's own listing at download time. A sidecar that has since gone is skipped, never fatal:
    ///         the track is the deliverable and the sidecar is decoration.
    ///     </para>
    /// </remarks>
    public List<string> SoulseekSidecarRemotePaths { get; set; } = new();

    /// <summary>
    ///     Gets or sets the local path the fetched peer cover was written to, once it has arrived.
    /// </summary>
    /// <remarks>
    ///     Filled in by the download, and read by the artwork step as a preferred local source. It is a file
    ///     path rather than a URL because that is what a peer can actually give you, and the artwork pipeline
    ///     is fed this one path ahead of its normal resolved-URL chain.
    /// </remarks>
    public string SoulseekPeerArtworkPath { get; set; } = "";

    /// <summary>
    ///     Gets or sets the local path the fetched peer lyrics or cue sheet was written to, once it arrived.
    /// </summary>
    public string SoulseekPeerLyricsPath { get; set; } = "";

    /// <summary>
    ///     Gets or sets whether the verified audio is waiting for the enrichment stage.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Set when the download ends with verified audio and nothing done to it beyond that. It is the
    ///         handoff: the file is complete and must not be fetched again, and tagging, sidecars and the move
    ///         are all still outstanding.
    ///     </para>
    ///     <para>
    ///         Kept distinct from a failure because the difference decides what happens next. A failed
    ///         download is retried, which for a peer source means spending the peer's bandwidth a second time
    ///         to obtain a file that is already sitting on disk.
    ///     </para>
    /// </remarks>
    public bool EnrichmentPending { get; set; }

    /// <summary>
    ///     Gets or sets what the peer's folder is known to be: an album, a single, or nothing known.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Carried because the reader's preference for albums against singles changes which destination
    ///         profile applies, and deciding it at enrichment time would mean re-reading a folder the peer
    ///         may no longer be sharing.
    ///     </para>
    ///     <para>
    ///         Null means genuinely unknown, and is not the same as "single". Only one piece of evidence
    ///         counts here: more than one audio file in the same browsed folder. Album <em>text</em> does not -
    ///         a folder named after a song is routinely a whole album, and a release with one track is
    ///         routinely named after its album. When the evidence is absent the destination profile's own
    ///         preference decides, which is the reader's stated answer rather than a guess.
    ///     </para>
    /// </remarks>
    public string? SoulseekReleaseCategory { get; set; }

    /// <inheritdoc />
    public Dictionary<string, object> ToQueuePayload()
        => BuildQueuePayload(
            QueuePayloadBuilder.MapStatusForUi(Status.ToString()),
            new Dictionary<string, object?>
            {
                ["soulseekUsername"] = SoulseekUsername,
                ["soulseekQualityCode"] = SoulseekQualityCode,
                ["soulseekCandidateScore"] = SoulseekCandidateScore,
                ["soulseekResponseCount"] = SoulseekResponseCount,
                ["soulseekAcceptedCandidateCount"] = SoulseekAcceptedCandidateCount,
                ["soulseekDisplayCoverUrl"] = SoulseekDisplayCoverUrl,

                // Extras are applied after the base payload, so this overrides the base "cover" for the UI
                // without touching the base Cover property the tagging pipeline reads.
                ["cover"] = string.IsNullOrWhiteSpace(SoulseekDisplayCoverUrl)
                    ? "/images/unavailable/unavailable.jpg"
                    : SoulseekDisplayCoverUrl
            });
}

/// <summary>The stages of a Soulseek download as shown in the activity view.</summary>
/// <remarks>
///     These are the same stages every other engine reports, so the shared status mapper understands them
///     unchanged.
/// </remarks>
public enum SoulseekDownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Skipped
}
