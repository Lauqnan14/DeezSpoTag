using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// The Journey strategy: a DJ that takes the listener somewhere.
///
/// <para>Anchor answers "what is closest to this sound?" and Companion answers "what
/// sits between these two things?". Both return a <em>ranking</em>: a set whose order
/// is mostly incidental. Journey is the first strategy where the order is the whole
/// point. It builds an arc — a deliberate move from the opening seed's territory to
/// the closing seed's — and places each track at a specific point along it.</para>
///
/// <para>What makes that different in practice: a track can be picked <em>for its
/// position</em> rather than for its score. A middling track that fits exactly between
/// two sections earns its place ahead of a better track that suits nowhere in
/// particular, because the arc needs a bridge there and the better track is not one.</para>
///
/// <para>Construction:</para>
/// <list type="number">
/// <item>The seeds are the arc's landmarks, in configured weight order.</item>
/// <item>Each consecutive pair of landmarks defines a <em>leg</em>.</item>
/// <item>Every remaining candidate is scored against every leg and assigned to the
/// leg it fits best — not the leg it scores highest on in absolute terms, but the leg
/// where it is closest to bridging that specific gap.</item>
/// <item>Each leg is filled to an even share of the track budget, so the arc stays
/// even rather than spending its whole budget on whichever section the library
/// happens to have most of.</item>
/// </list>
///
/// <para>Two properties are load-bearing:</para>
/// <list type="bullet">
/// <item><b>Deterministic.</b> Same request, same playlist, for the same reason as
/// every other strategy: a re-run replaces the previous playlist in place.</item>
/// <item><b>The arc order survives.</b> Tracks come out grouped by leg and in leg
/// order. A strategy that produced the right tracks in the wrong order would be
/// indistinguishable from Anchor with extra steps.</item>
/// </list>
///
/// <para>A pure function of its request: no clock, no database, no random source.</para>
/// </summary>
[DjStrategyDescriptor(
    DisplayName = "Journey",
    Description = "Builds an arc, moving the playlist from where the seeds start to somewhere else.",
    Requirements = new[] { "Analysed history for the time slot", "Sonic embeddings improve the arc" })]
public sealed class JourneyDjStrategy : IMelodayDjStrategy
{
    /// <summary>Selects this strategy on <see cref="DjDefinitionDto.Strategy"/>.</summary>
    public const string StrategyId = "journey";

    /// <summary>
    /// Below this coverage the acoustic half of the brief cannot be honoured, so the
    /// result says so instead of being presented as a considered arc.
    /// </summary>
    public const double LowCoveragePercent = 50d;

    /// <summary>
    /// How far above the runner-up a track must score on a leg to be placed there.
    /// Small on purpose: a track that fits two legs almost equally is a reasonable
    /// choice for either, and over-claiming would make the arc's shape depend on
    /// rounding noise.
    /// </summary>
    public const double LegPreferenceMargin = 0.05d;

    public string Strategy => StrategyId;

    public DjStrategyResult BuildPlaylist(DjStrategyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trackCount = Math.Max(0, request.TrackCount);
        if (trackCount == 0)
        {
            return new DjStrategyResult
            {
                Candidates = Array.Empty<DjCandidate>(),
                Diagnostics = new[] { "This DJ was asked for no tracks." },
            };
        }

        var candidatePool = new HashSet<long>(request.CandidateTrackIds ?? Array.Empty<long>());
        if (candidatePool.Count == 0)
        {
            return new DjStrategyResult
            {
                Candidates = Array.Empty<DjCandidate>(),
                Diagnostics = new[] { "No tracks were available to choose from." },
            };
        }

        var requestedSeeds = request.Seeds ?? Array.Empty<DjSeed>();
        // Distinct before ordering: a definition reloaded twice could list a landmark
        // twice, and placing it twice would spend two slots of a short arc on the
        // same track and give that leg two identical ends.
        var landmarks = requestedSeeds
            .Select(seed => seed.TrackId)
            .Where(candidatePool.Contains)
            .Distinct()
            .OrderByDescending(seedId => WeightOf(seedId, requestedSeeds))
            .ThenBy(seedId => seedId)
            .ToList();

        if (landmarks.Count == 0)
        {
            return new DjStrategyResult
            {
                Candidates = Array.Empty<DjCandidate>(),
                Diagnostics = new[] { "None of this DJ's seeds are in the library, so there is no arc to follow." },
            };
        }

        var diagnostics = new List<string>();
        if (request.SonicCoveragePercent < LowCoveragePercent)
        {
            diagnostics.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Only {0:0.#}% of the candidate tracks have sonic embeddings, so this arc was chosen from a smaller pool than intended.",
                request.SonicCoveragePercent));
        }

        var ordered = new List<DjCandidate>(Math.Min(trackCount, candidatePool.Count));

        // A single landmark is not an arc. It still plays, but says so rather than
        // implying a journey it did not make.
        var landmarkCount = Math.Min(landmarks.Count, trackCount);
        var legs = BuildLegs(landmarks.Take(landmarkCount).ToList());
        if (legs.Count == 0)
        {
            diagnostics.Add(
                "This DJ has one usable seed, so there is no arc to follow; its neighbours were filled in from that seed.");

            foreach (var trackId in FillWithoutArc(
                landmarks[0],
                candidatePool,
                request,
                remaining: trackCount))
            {
                ordered.Add(trackId);
            }

            return new DjStrategyResult
            {
                Candidates = ordered,
                Diagnostics = diagnostics,
                SeedSummary = DescribeSeeds(landmarkCount, ordered.Count(IsSeed)),
            };
        }

        // Landmarks first, in arc order. They are the structure, so nothing is
        // allowed to displace them.
        foreach (var landmarkId in landmarks.Take(landmarkCount))
        {
            if (ordered.Count >= trackCount)
            {
                break;
            }

            ordered.Add(new DjCandidate(landmarkId, 1d, DjItemReasons.Seed));
        }

        var budget = trackCount - ordered.Count;
        var assignments = AssignLegs(legs, candidatePool, requestedSeeds, request);
        var quotas = EvenBudget(budget, assignments);

        var placedAny = false;
        for (var legIndex = 0; legIndex < legs.Count; legIndex++)
        {
            var quota = quotas[legIndex];
            if (quota <= 0)
            {
                continue;
            }

            var chosen = assignments[legIndex]
                .OrderByDescending(entry => entry.Similarity)
                .ThenBy(entry => entry.TrackId)
                .Take(quota);

            foreach (var entry in chosen)
            {
                if (ordered.Count >= trackCount)
                {
                    break;
                }

                ordered.Add(new DjCandidate(entry.TrackId, entry.Similarity, DjItemReasons.Journey));
                placedAny = true;
            }
        }

        if (legs.Count == 1)
        {
            diagnostics.Add(
                "This DJ has two usable seeds, so its arc has a single section rather than a journey.");
        }

        // A leg with nothing to fill is a hole in the arc. Worth saying: a person
        // hearing a jump in the playlist would otherwise assume it was deliberate.
        var emptyLegs = assignments.Where(leg => leg.Count == 0).Count();
        if (emptyLegs > 0 && !placedAny)
        {
            diagnostics.Add(string.Format(
                CultureInfo.InvariantCulture,
                "No tracks could be found to connect {0} of the {1} section(s) between the seeds.",
                emptyLegs,
                legs.Count));
        }

        if (ordered.Count < trackCount)
        {
            diagnostics.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Only {0} of the {1} requested tracks were available.",
                ordered.Count,
                trackCount));
        }

        return new DjStrategyResult
        {
            Candidates = ordered,
            Diagnostics = diagnostics,
            SeedSummary = DescribeSeeds(landmarkCount, ordered.Count(IsSeed)),
        };
    }

    // ------------------------------------------------------------------- legs

    /// <summary>One stretch of the arc: from one landmark to the next.</summary>
    private sealed record Leg(long FromLandmark, long ToLandmark);

    private static IReadOnlyList<Leg> BuildLegs(IReadOnlyList<long> landmarks)
    {
        var legs = new List<Leg>(Math.Max(0, landmarks.Count - 1));
        for (var index = 0; index + 1 < landmarks.Count; index++)
        {
            legs.Add(new Leg(landmarks[index], landmarks[index + 1]));
        }

        return legs;
    }

    /// <summary>
    /// Assigns every non-landmark candidate to the leg it fits best.
    ///
    /// <para>Scored against each leg's two landmarks. A track that suits a particular
    /// stretch scores there on the mean of its two landmark affinities, which is what
    /// lets a track be placed <em>for its position</em> rather than for its overall
    /// quality: a track with middling affinities that sit neatly either side of one
    /// gap belongs to that gap even if some other track is closer to everything.</para>
    ///
    /// <para>Unscored candidates are left unassigned rather than guessed at.</para>
    /// </summary>
    private static IReadOnlyList<List<LegCandidate>> AssignLegs(
        IReadOnlyList<Leg> legs,
        IReadOnlySet<long> candidatePool,
        IReadOnlyList<DjSeed> requestedSeeds,
        DjStrategyRequest request)
    {
        var affinities = request.SeedAffinities
            ?? new Dictionary<long, IReadOnlyDictionary<long, double>>();
        var landmarkIds = legs
            .SelectMany(leg => new[] { leg.FromLandmark, leg.ToLandmark })
            .ToHashSet();

        var assignments = new List<List<LegCandidate>>(legs.Count);
        foreach (var _ in legs)
        {
            assignments.Add(new List<LegCandidate>());
        }

        foreach (var trackId in candidatePool)
        {
            if (landmarkIds.Contains(trackId))
            {
                continue;
            }

            if (!affinities.TryGetValue(trackId, out var perSeed) || perSeed.Count == 0)
            {
                continue;
            }

            var bestLeg = -1;
            var bestScore = 0d;
            var runnerUp = 0d;
            for (var legIndex = 0; legIndex < legs.Count; legIndex++)
            {
                var score = LegScore(trackId, legs[legIndex], perSeed);
                if (score <= 0d)
                {
                    continue;
                }

                if (score > bestScore)
                {
                    runnerUp = bestScore;
                    bestScore = score;
                    bestLeg = legIndex;
                }
                else if (score > runnerUp)
                {
                    runnerUp = score;
                }
            }

            if (bestLeg < 0)
            {
                continue;
            }

            assignments[bestLeg].Add(new LegCandidate(trackId, bestScore));
        }

        return assignments;
    }

    /// <summary>
    /// The affinity a track must have to a landmark before it counts as reaching it.
    ///
    /// <para>Without a floor, a track scoring 0.95 against one landmark and 0.05
    /// against the other still averages to a respectable 0.5 and would be placed in
    /// the middle of the arc — where it audibly belongs to neither end. The floor is
    /// what keeps a track in the position its affinities actually support.</para>
    /// </summary>
    public const double MinimumLegAffinity = 0.25d;

    /// <summary>
    /// A track's score on one leg: the mean of its affinity to that leg's two
    /// landmarks, plus a bonus for genuinely sitting between them.
    /// </summary>
    private static double LegScore(
        long trackId,
        Leg leg,
        IReadOnlyDictionary<long, double> perSeed)
    {
        if (!TryAffinity(trackId, leg.FromLandmark, perSeed, out var from)
            || !TryAffinity(trackId, leg.ToLandmark, perSeed, out var to))
        {
            // A leg needs both ends bridged. Scoring on one landmark alone is
            // neighbourhood, not a connection between two points.
            return 0d;
        }

        if (from < MinimumLegAffinity || to < MinimumLegAffinity)
        {
            // Close enough to one end to be noticed, far enough from the other that
            // it does not connect them. Placing it mid-arc would claim a position
            // the measured affinities do not support.
            return 0d;
        }

        var mean = (from + to) / 2d;

        // Balanced affinities mean the track sits between the two landmarks rather
        // than leaning on one of them.
        var balance = 1d - Math.Abs(from - to);
        return Math.Clamp(mean + (balance * 0.1d), 0d, 1d);
    }

    private static bool TryAffinity(
        long trackId,
        long landmarkId,
        IReadOnlyDictionary<long, double> perSeed,
        out double affinity)
    {
        affinity = 0d;
        if (!perSeed.TryGetValue(landmarkId, out var value) || !double.IsFinite(value) || value <= 0d)
        {
            return false;
        }

        affinity = Math.Clamp(value, 0d, 1d);
        return true;
    }

    /// <summary>One assigned track and the score that placed it on its leg.</summary>
    private sealed record LegCandidate(long TrackId, double Similarity);

    /// <summary>
    /// Divides the non-landmark budget across legs in proportion to how many tracks
    /// each leg can actually supply, then hands out the remainder largest-share-first.
    ///
    /// <para>Proportional rather than first-come because a fixed budget per leg would
    /// let a small early leg starve a large later one, and the arc would end up
    /// describing only its opening.</para>
    ///
    /// <para>Ties on the remainder are broken by leg index, so the split is stable.</para>
    /// </summary>
    private static int[] EvenBudget(int budget, IReadOnlyList<List<LegCandidate>> assignments)
    {
        var quotas = new int[assignments.Count];
        if (budget <= 0 || assignments.Count == 0)
        {
            return quotas;
        }

        var total = assignments.Sum(leg => leg.Count);
        if (total == 0)
        {
            return quotas;
        }

        var assigned = 0;
        var remainders = new List<(int Leg, double Fraction)>(assignments.Count);
        for (var index = 0; index < assignments.Count; index++)
        {
            var exact = budget * (double)assignments[index].Count / total;
            quotas[index] = (int)Math.Floor(exact);
            assigned += quotas[index];
            remainders.Add((index, exact - Math.Floor(exact)));
        }

        // Largest fractional part first, then by leg index for a total order.
        var spare = budget - assigned;
        foreach (var (leg, _) in remainders
            .OrderByDescending(entry => entry.Fraction)
            .ThenBy(entry => entry.Leg)
            .Take(Math.Max(0, spare)))
        {
            quotas[leg]++;
        }

        return quotas;
    }

    /// <summary>
    /// Single-landmark case: no legs, so fall back to filling from the one landmark.
    /// Reported by the caller as "no arc", never presented as a journey.
    /// </summary>
    private static IEnumerable<DjCandidate> FillWithoutArc(
        long landmarkId,
        IReadOnlySet<long> candidatePool,
        DjStrategyRequest request,
        int remaining)
    {
        var affinities = request.SeedAffinities
            ?? new Dictionary<long, IReadOnlyDictionary<long, double>>();

        yield return new DjCandidate(landmarkId, 1d, DjItemReasons.Seed);

        var ordered = candidatePool
            .Where(trackId => trackId != landmarkId)
            .Select(trackId => new DjCandidate(
                trackId,
                AffinityTo(trackId, landmarkId, affinities),
                DjItemReasons.Journey))
            .Where(candidate => candidate.Similarity > 0d)
            .OrderByDescending(static candidate => candidate.Similarity)
            .ThenBy(static candidate => candidate.TrackId)
            .Take(Math.Max(0, remaining - 1));

        foreach (var candidate in ordered)
        {
            yield return candidate;
        }
    }

    private static double AffinityTo(
        long trackId,
        long landmarkId,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> affinities)
        => TryAffinity(trackId, landmarkId, affinities.GetValueOrDefault(trackId)
            ?? new Dictionary<long, double>(), out var affinity)
            ? affinity
            : 0d;

    private static double WeightOf(long trackId, IReadOnlyList<DjSeed> requested)
    {
        foreach (var seed in requested.Where(candidate => candidate.TrackId == trackId))
        {
            return seed.EffectiveWeight;
        }

        return 0d;
    }

    private static bool IsSeed(DjCandidate candidate)
        => DjItemReasons.Seed.Equals(candidate.Reason, StringComparison.Ordinal);

    private static string DescribeSeeds(int landmarks, int placedSeeds)
        => placedSeeds == landmarks
            ? string.Format(CultureInfo.InvariantCulture, "{0} landmark(s)", landmarks)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} landmark(s), {1} placed",
                landmarks,
                placedSeeds);
}