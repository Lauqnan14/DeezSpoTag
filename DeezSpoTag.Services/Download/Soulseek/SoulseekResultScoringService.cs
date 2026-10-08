using System.Globalization;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Matching;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Scores and filters Soulseek candidates.
/// </summary>
/// <remarks>
///     <para>
///         The rules are strict and in a fixed order, so a rejection reason always names the first thing that
///         disqualified a candidate:
///     </para>
///     <list type="number">
///         <item>blocked user, then blocked filename pattern</item>
///         <item>peer health: queue length, free slot, upload speed, cooldown</item>
///         <item>Source-enabled quality, and unknown quality unless explicitly allowed</item>
///         <item>filename noise, harder in automated mode</item>
///         <item>quality ceiling implied by the requested quality</item>
///         <item>identity match via the shared <see cref="TrackCandidateValidator"/></item>
///     </list>
///     <para>
///         Automated mode applies the same order with tighter thresholds, so a weak match that a person would
///         be happy to see is still rejected before anything is queued.
///     </para>
/// </remarks>
public sealed class SoulseekResultScoringService : ISoulseekResultScoringService
{
    private readonly SoulseekSettingsService _settings;
    private readonly ISoulseekPeerPolicyService _peerPolicy;
    private readonly ILogger<SoulseekResultScoringService> _logger;

    /// <summary>Weights for the final score. They sum to 1 so the result reads as a percentage.</summary>
    private const double IdentityWeight = 0.45;
    private const double QualityWeight = 0.3;
    private const double PeerWeight = 0.15;
    private const double HygieneWeight = 0.1;

    /// <summary>Noise that is merely untidy rather than disqualifying, scored but not rejected.</summary>
    private const double SoftNoiseScorePenalty = 0.04;

    /// <summary>Noise that disqualifies an automated download outright.</summary>
    private const double AutomatedNoiseScoreCeiling = 0.5;

    /// <summary>Minimum score an automated candidate must reach.</summary>
    private const double AutomatedMinimumScore = 0.55;

    /// <summary>Initializes a new instance of the <see cref="SoulseekResultScoringService"/> class.</summary>
    public SoulseekResultScoringService(
        SoulseekSettingsService settings,
        ISoulseekPeerPolicyService peerPolicy,
        ILogger<SoulseekResultScoringService> logger)
    {
        _settings = settings;
        _peerPolicy = peerPolicy;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SoulseekCandidate>> ScoreAsync(
        SoulseekSearchTarget target,
        IReadOnlyList<SoulseekRawCandidate> raw,
        SoulseekSearchMode mode = SoulseekSearchMode.Manual,
        IReadOnlyList<string>? allowedQualities = null,
        bool? allowUnknownQuality = null,
        CancellationToken cancellationToken = default,
        string? requiredQualityCode = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(raw);

        var settings = _settings.GetSettings();
        var permittedQualities = _settings.GetEnabledQualityCodes().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedQualities is not null)
        {
            permittedQualities.IntersectWith(allowedQualities);
        }
        var unknownAllowed = allowUnknownQuality ?? settings.AllowUnknownQuality;
        // The queue's current ladder step may add a minimum quality requirement. A manual search has no
        // step requirement; Source still determines which quality codes are eligible in both cases.
        var requestedPreference = requiredQualityCode?.Trim() ?? string.Empty;
        var requestedCode = requestedPreference.Length == 0
            ? string.Empty
            : SoulseekQuality.NormalizeCode(requestedPreference);
        // Read once for the whole pass rather than once per candidate. Without this, a peer offering ten
        // files costs ten database reads, and re-scoring the accumulated set on every poll tick multiplies
        // that by the number of ticks.
        var cooldowns = await _peerPolicy.GetCooldownsAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<SoulseekCandidate>(raw.Count);

        foreach (var candidate in raw)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ScoreOneAsync(
                target,
                candidate,
                mode,
                permittedQualities,
                unknownAllowed,
                requestedCode,
                settings,
                cooldowns,
                cancellationToken).ConfigureAwait(false));
        }

        return results
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.CanonicalRank ?? 0)
            .ThenByDescending(candidate => candidate.IdentityConfidence)
            .ThenBy(candidate => candidate.Raw.PeerQueueLength)
            .ToList();
    }

    /// <inheritdoc />
    public SoulseekCandidate? SelectBest(IReadOnlyList<SoulseekCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => candidate.Accepted)
            .OrderByDescending(candidate => candidate.Raw.PeerUploadSpeed)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Raw.PeerQueueLength)
            .FirstOrDefault();
    }

    private async Task<SoulseekCandidate> ScoreOneAsync(
        SoulseekSearchTarget target,
        SoulseekRawCandidate raw,
        SoulseekSearchMode mode,
        IReadOnlySet<string> permittedQualities,
        bool unknownAllowed,
        string requestedCode,
        SoulseekDownloadSettings settings,
        IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldowns,
        CancellationToken cancellationToken)
    {
        var quality = SoulseekQuality.Normalize(
            raw.Filename,
            raw.Extension,
            raw.BitrateKbps,
            raw.BitDepth,
            raw.SampleRateHz);

        SoulseekCandidate Reject(string reason, double score = 0)
            => new(raw, quality.Code, quality.Label, quality.TierValue, quality.CanonicalRank, score, 0, false, reason);

        if (SoulseekPeerPolicyService.IsBlockedUser(raw.Username, settings))
        {
            return Reject("blocked_user");
        }

        var facts = SoulseekFilenameParser.Parse(raw.Filename, raw.DurationSeconds);

        if (_peerPolicy.IsBlockedFilename(raw.Filename))
        {
            return Reject("blocked_filename_pattern");
        }

        if (facts.HasNoise && ContainsHardNoise(facts))
        {
            return Reject("filename_noise");
        }

        // A video, preview or trailer is not the audio being asked for, so automation refuses it outright. A
        // person can still see it in a manual search, where it only costs score.
        if (mode == SoulseekSearchMode.Automated && facts.IsJunk)
        {
            return Reject("filename_noise");
        }

        var peerDecision = await _peerPolicy.EvaluateAsync(raw, cooldowns, cancellationToken).ConfigureAwait(false);
        if (!peerDecision.Accepted)
        {
            return Reject(peerDecision.Reason ?? "peer_rejected");
        }

        if (quality.IsUnknown && !unknownAllowed)
        {
            return Reject("unknown_quality");
        }

        if (!quality.IsUnknown
            && !permittedQualities.Contains(quality.Code, StringComparer.OrdinalIgnoreCase))
        {
            return Reject("quality_not_allowed");
        }

        if (quality.IsUnknown && unknownAllowed
            && !permittedQualities.Contains(SoulseekQualityInfo.UnknownCode, StringComparer.OrdinalIgnoreCase))
        {
            return Reject("quality_not_allowed");
        }

        if (!SatisfiesRequestedQuality(quality, requestedCode))
        {
            return Reject("below_requested_quality");
        }

        var identity = MatchIdentity(target, facts, raw, mode);
        if (!identity.Accepted)
        {
            return Reject(identity.Reason);
        }

        var score = ComputeScore(identity.Score, quality, raw, facts, mode);

        if (mode == SoulseekSearchMode.Automated && score < AutomatedMinimumScore)
        {
            return Reject("score_below_automated_minimum", score);
        }

        return new SoulseekCandidate(
            raw,
            quality.Code,
            quality.Label,
            quality.TierValue,
            quality.CanonicalRank,
            score,
            SoulseekFilenameParser.IdentityConfidence(facts),
            Accepted: true);
    }

    /// <summary>
    ///     Runs the shared catalogue matcher over the facts recovered from the filename.
    /// </summary>
    /// <remarks>
    ///     Automated mode turns on the strict artist match and requires a candidate duration when the target
    ///     has one, which is the main lever that makes automated selection stricter than a manual browse.
    /// </remarks>
    private static TrackCandidateValidationResult MatchIdentity(
        SoulseekSearchTarget target,
        SoulseekFilenameFacts facts,
        SoulseekRawCandidate raw,
        SoulseekSearchMode mode)
    {
        // A manual search of free text is a peer query, not a track request: the page knows no artist to
        // search by, so the whole term used to be compared as if it were a title. Searching "mejja" then
        // rejected every Mejja file with a title mismatch, because the term was read against the title
        // parsed out of "Mejja - Thank Me Later.mp3" instead of against the artist it names. Grading the
        // term against the whole recovered identity is what such a query means, and it is deliberately
        // confined to a manual search with no artist: an automated download still has a real artist and
        // title and goes through the shared validator below, unchanged.
        if (mode == SoulseekSearchMode.Manual
            && string.IsNullOrWhiteSpace(target.Artist)
            && !string.IsNullOrWhiteSpace(target.Title))
        {
            var queryScore = SoulseekQueryMatcher.Score(target.Title, facts, raw.Filename);
            return queryScore > SoulseekQueryMatcher.NoMatch
                ? new TrackCandidateValidationResult(true, SoulseekQueryMatcher.MatchReason, queryScore)
                : new TrackCandidateValidationResult(
                    false,
                    SoulseekQueryMatcher.NoMatchReason,
                    SoulseekQueryMatcher.NoMatch);
        }

        var source = new TrackMatchSource(
            target.Isrc,
            target.Title,
            target.Artist,
            target.Album,
            target.DurationMs,
            target.ReleaseYear);

        // A Soulseek candidate has no catalogue id, so the peer and filename stand in as its identity. This
        // is what satisfies the validator's provider-id requirement without inventing one.
        var candidate = new TrackMatchCandidate(
            $"{raw.Username}:{raw.Filename}",
            Isrc: null,
            facts.Title,
            facts.Artist,
            facts.Album,
            facts.DurationMs,
            ReleaseYear: null);

        var options = mode == SoulseekSearchMode.Automated
            ? new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                AllowIsrcMismatch: true,
                MaxMetadataDurationDifferenceMs: 5_000)
            : new TrackCandidateValidationOptions(
                StrictWithoutIsrc: false,
                AllowMissingCandidateArtist: true,
                RequireCandidateDurationWhenSourceHasDuration: false,
                AllowIsrcMismatch: true,
                MaxMetadataDurationDifferenceMs: 12_000);

        return TrackCandidateValidator.Validate(source, candidate, options);
    }

    private static double ComputeScore(
        double identityScore,
        SoulseekQualityInfo quality,
        SoulseekRawCandidate raw,
        SoulseekFilenameFacts facts,
        SoulseekSearchMode mode)
    {
        // The shared validator's score already accounts for artist, title, duration and album. Multiplying it
        // by the parser's confidence as well would count the artist twice and make a clean, well-named
        // release score lower than it should, so confidence is used only to break ties in the ordering.
        var identity = identityScore;
        var qualityScore = ScoreQuality(quality);
        var peerScore = ScorePeer(raw);
        var hygiene = ScoreHygiene(facts, mode);

        var score = (identity * IdentityWeight)
            + (qualityScore * QualityWeight)
            + (peerScore * PeerWeight)
            + (hygiene * HygieneWeight);

        if (facts.HasNoise)
        {
            score -= SoftNoiseScorePenalty;
        }

        // Unknown quality is never allowed to outrank a known one just because its peer looked healthy.
        if (quality.IsUnknown)
        {
            score = Math.Min(score, 0.5);
        }

        return Math.Round(Math.Clamp(score, 0, 1), 4);
    }

    private static double ScoreQuality(SoulseekQualityInfo quality)
    {
        if (quality.CanonicalRank is not { } rank)
        {
            return 0;
        }

        // 120 is the highest canonical rank in the catalog (Max Hi-Res) and 25 the lowest (MP3 96).
        return Math.Clamp((rank - 25) / 95.0, 0, 1);
    }

    private static double ScorePeer(SoulseekRawCandidate raw)
    {
        // A short queue is best, a full one is worst.
        var queue = raw.PeerQueueLength <= 0
            ? 1.0
            : Math.Clamp(1.0 - (raw.PeerQueueLength / 10.0), 0, 1);

        // Doubling the free-slot flag matters more than queue length: a peer without a slot cannot serve us
        // at all, whatever its queue looks like.
        var slot = raw.PeerHasFreeUploadSlot ? 1.0 : 0.0;

        // 1 MiB/s and up is a healthy upload; scale linearly below that.
        var speed = raw.PeerUploadSpeed <= 0
            ? 0.0
            : Math.Clamp(raw.PeerUploadSpeed / 1_048_576.0, 0, 1);

        return Math.Clamp((queue * 0.3) + (slot * 0.45) + (speed * 0.25), 0, 1);
    }

    private static double ScoreHygiene(SoulseekFilenameFacts facts, SoulseekSearchMode mode)
    {
        if (!facts.HasNoise)
        {
            return 1.0;
        }

        // In automated mode any noise is treated as a hard signal, so hygiene contributes nothing.
        return mode == SoulseekSearchMode.Automated ? 0.0 : 0.5;
    }

    private static bool ContainsHardNoise(SoulseekFilenameFacts facts)
    {
        if (facts.NoiseReasons is null || facts.NoiseReasons.Count == 0)
        {
            return false;
        }

        // A file that is not audio, or whose name we could not read, is never worth downloading. Promotional
        // tokens and untidy bracketing are only a score penalty, so a person still sees the result.
        return facts.NoiseReasons
            .Any(candidate => candidate is "non_audio_file" or "unparseable_name" or "hash_filename" or "empty_filename" or "no_filename");
    }

    /// <summary>
    ///     Returns a value indicating whether a candidate meets the quality the user asked for.
    /// </summary>
    /// <remarks>
    ///     An empty step request adds no minimum, so an explicitly allowed unknown-quality candidate is
    ///     allowed through here. An explicit step, on the other hand, is a hard requirement: a candidate
    ///     whose quality could not be determined never satisfies it, because "I could not tell" is not
    ///     evidence of meeting the bar.
    /// </remarks>
    private static bool SatisfiesRequestedQuality(SoulseekQualityInfo quality, string requestedCode)
    {
        if (string.IsNullOrEmpty(requestedCode))
        {
            return true;
        }

        if (quality.IsUnknown)
        {
            return false;
        }

        if (SoulseekQuality.IsLosslessCode(requestedCode))
        {
            return quality.IsLossless;
        }

        // A lossy request is satisfied by a lossy candidate at the requested bitrate or better, and by any
        // lossless candidate, which is an upgrade.
        if (quality.IsLossless)
        {
            return true;
        }

        var requestedRank = SoulseekQuality.NormalizeCodeWithFacts(requestedCode).CanonicalRank ?? 0;
        return (quality.CanonicalRank ?? 0) >= requestedRank;
    }

}
