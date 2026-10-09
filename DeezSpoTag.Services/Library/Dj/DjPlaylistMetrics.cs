using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// How similar two tracks are to each other, for one pair.
///
/// <para>Supplied by the caller rather than computed here, so this stays pure. Evaluation
/// runs after a playlist has been chosen, which is the one point where asking the
/// similarity index a follow-up question is affordable: the chosen set is a few dozen
/// tracks, not the library.</para>
/// </summary>
public sealed record DjTrackPairDistance(long TrackA, long TrackB, double Distance)
{
    public static DjTrackPairDistance Between(long left, long right, double distance)
        => new(Math.Min(left, right), Math.Max(left, right), distance);
}

/// <summary>
/// What one generated playlist actually looks like.
///
/// <para>Every figure here answers a question a person would otherwise have to answer by
/// listening to the whole thing.</para>
///
/// <para>The important one is <see cref="MeanPairwiseDistance"/>. The risk with picking
/// "the most similar tracks" is not that it fails; it is that it succeeds and returns
/// forty tracks that sound the same, which passes every behavioural test a strategy
/// has. Only measuring the chosen tracks against each other catches that.</para>
/// </summary>
public sealed record DjPlaylistMetrics
{
    /// <summary>
    /// Below this, a playlist is reported as collapsed onto a single sound.
    ///
    /// <para>An absolute threshold, and sound one: vectors are L2-normalised in a
    /// fixed 1280-dimensional space, so two unrelated tracks sit near orthogonality at a
    /// cosine distance around 1, and tracks that genuinely sound alike sit near 0. A
    /// library-relative threshold cannot work here — in a library where everything is
    /// similar, every selection looks as varied as the library, so a collapsed playlist
    /// reads as perfectly healthy.</para>
    /// </summary>
    public const double CollapsedPairwiseDistance = 0.10d;

    public required int TrackCount { get; init; }

    public required int SeedCount { get; init; }

    /// <summary>How many of the DJ's seeds ended up in the playlist.</summary>
    public required int SeedsPlaced { get; init; }

    public required int DistinctTracks { get; init; }

    /// <summary>True when the chosen tracks were actually compared to each other.</summary>
    public required bool PairwiseMeasured { get; init; }

    /// <summary>
    /// Mean dissimilarity between the chosen tracks themselves.
    ///
    /// <para>Zero when the pair distances were not supplied, which is reported as
    /// unmeasured rather than as perfect.</para>
    /// </summary>
    public required double MeanPairwiseDistance { get; init; }

    /// <summary>
    /// The single most similar pair in the playlist.
    ///
    /// <para>Catches one duplicated-sounding pair inside an otherwise varied set, which
    /// the mean would hide.</para>
    /// </summary>
    public required double ClosestPairDistance { get; init; }

    /// <summary>
    /// Mean dissimilarity from each chosen track to its closest seed: how far the DJ
    /// strayed from what it was asked to play.
    /// </summary>
    public required double MeanDistanceToNearestSeed { get; init; }

    /// <summary>
    /// True when the playlist is measurably more varied than a single sound.
    ///
    /// <para>False when unmeasured. An unmeasured playlist has not been shown to be
    /// healthy, and reporting it as healthy would make silence look like success.</para>
    /// </summary>
    public bool IsSpreadHealthy => PairwiseMeasured
        && MeanPairwiseDistance >= CollapsedPairwiseDistance;

    public required IReadOnlyDictionary<string, int> ReasonCounts { get; init; }

    /// <summary>
    /// Whether every seed that fitted within the track count was placed.
    ///
    /// <para>A DJ whose seeds were crowded out is not playing the thing it was
    /// configured to play, whatever the similarities say.</para>
    /// </summary>
    public bool RepresentsItsSeeds => SeedsPlaced == SeedCount;

    public string Summarise() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} tracks, {1}/{2} seeds, pairwise distance {3}{4}, nearest-seed distance {5:F3}, reasons {6}",
        TrackCount,
        SeedsPlaced,
        SeedCount,
        PairwiseMeasured ? MeanPairwiseDistance.ToString("F3", CultureInfo.InvariantCulture) : "not measured",
        PairwiseMeasured ? string.Empty : " (unmeasured)",
        MeanDistanceToNearestSeed,
        string.Join(", ", ReasonCounts.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + "=" + entry.Value)));
}

/// <summary>
/// Whether a playlist actually moved, for the strategy that claims to.
///
/// <para>Anchor and Companion return a ranking and promise nothing about order. Journey
/// claims the order is the point, so it is the only one this says anything about.</para>
/// </summary>
public sealed record DjArcMetrics
{
    /// <summary>Mean similarity to the opening landmark across the first half.</summary>
    public required double OpeningAffinity { get; init; }

    /// <summary>Mean similarity to the opening landmark across the second half.</summary>
    public required double ClosingAffinity { get; init; }

    /// <summary>
    /// Opening minus closing. Positive means the second half sits further from the
    /// opening landmark, which is what travelling looks like.
    ///
    /// <para>Near zero means the arc never moved and the strategy is a ranking wearing
    /// an arc-shaped variable name.</para>
    /// </summary>
    public double Progression => Math.Round(OpeningAffinity - ClosingAffinity, 4);

    /// <summary>True when the second half measurably moved from the opening landmark.</summary>
    public bool Progressed => Progression >= 0.05d;

    public string Summarise() => string.Format(
        CultureInfo.InvariantCulture,
        "opening affinity {0:F3}, closing {1:F3}, progression {2:F3}{3}",
        OpeningAffinity,
        ClosingAffinity,
        Progression,
        Progressed ? string.Empty : " (no progression)");
}

/// <summary>
/// A strategy's output, measured.
///
/// <para>Kept separate from the stored generation because evaluation happens against a
/// resolved affinity space, not against a database row: it is a property of the
/// decision, and it has to be computable without one.</para>
/// </summary>
public sealed record DjEvaluationReport
{
    public required string Strategy { get; init; }
    public required DjPlaylistMetrics Metrics { get; init; }
    public DjArcMetrics? Arc { get; init; }
    public IReadOnlyList<long> TrackIds { get; init; } = Array.Empty<long>();

    /// <summary>
    /// Anything worth a person's attention. Empty means the run looks sound on every
    /// measure available here, which is not the same as saying the playlist is good.
    /// </summary>
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}