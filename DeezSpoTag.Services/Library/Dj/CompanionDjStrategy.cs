using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// The Companion strategy: a DJ that fills the gaps between its seeds.
///
/// <para>Where Anchor answers "what is closest to this sound?", Companion answers
/// "what sits between these two things?". It is the strategy for a DJ whose seeds
/// are deliberately far apart — a warm-up that starts in ambient and ends in
/// something with a pulse, say — where filling outward from the whole seed set would
/// produce a playlist that is neither.</para>
///
/// <para>The difference from Anchor is concrete. Anchor scores every candidate against
/// the seed set as a single bag, so a track close to any seed ranks highly. Companion
/// scores each candidate against the <em>neighbouring pair</em>, so a track that sits
/// acoustically between two seeds outranks one that merely sits near a single seed,
/// even if that single seed is a better match.</para>
///
/// <para>Two properties are load-bearing:</para>
/// <list type="bullet">
/// <item><b>Deterministic.</b> Same request, same playlist, for the same reason as
/// every other strategy: a re-run replaces the previous playlist in place.</item>
/// <item><b>Seeds stay in the sequence.</b> They are the landmarks the bridging is
/// measured against, so they are never dropped in favour of a better-placed
/// neighbour.</item>
/// </list>
///
/// <para>A pure function of its request: no clock, no database, no random source.</para>
/// </summary>
[DjStrategyDescriptor(
    DisplayName = "Companion",
    Description = "Bridges between the seeds, filling the spaces between tracks rather than the ends.",
    Requirements = new[] { "Analysed history for the time slot" })]
public sealed class CompanionDjStrategy : IMelodayDjStrategy
{
    /// <summary>Selects this strategy on <see cref="DjDefinitionDto.Strategy"/>.</summary>
    public const string StrategyId = "companion";

    /// <summary>
    /// Below this coverage the acoustic half of the brief cannot be honoured, so the
    /// result says so instead of being presented as a considered set.
    /// </summary>
    public const double LowCoveragePercent = 50d;

    /// <summary>
    /// How much a track close to several distinct seeds beats one close to a single
    /// seed. This is the whole point of the strategy, so it is weighted well above
    /// Anchor's corroboration bonus.
    /// </summary>
    public const double BridgeBonus = 0.25d;

    public const double BridgeCap = 0.5d;

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
        var seeds = requestedSeeds
            .Select(seed => seed.TrackId)
            .Where(candidatePool.Contains)
            .ToHashSet();

        if (seeds.Count == 0)
        {
            return new DjStrategyResult
            {
                Candidates = Array.Empty<DjCandidate>(),
                Diagnostics = new[] { "None of this DJ's seeds are in the library, so there is nothing to bridge." },
            };
        }

        var diagnostics = new List<string>();
        if (request.SonicCoveragePercent < LowCoveragePercent)
        {
            diagnostics.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Only {0:0.#}% of the candidate tracks have sonic embeddings, so this set was chosen from a smaller pool than intended.",
                request.SonicCoveragePercent));
        }

        var affinities = request.SeedAffinities
            ?? new Dictionary<long, IReadOnlyDictionary<long, double>>();

        if (seeds.Count == 1)
        {
            // Bridging needs two landmarks. With one seed there is no gap, so say so
            // rather than quietly behaving like Anchor and implying otherwise.
            diagnostics.Add(
                "This DJ has one usable seed, so there is nothing to bridge between; its neighbours were filled in from that seed.");
        }

        var ordered = new List<DjCandidate>(Math.Min(trackCount, candidatePool.Count));

        foreach (var seedId in OrderedSeeds(seeds, requestedSeeds))
        {
            if (ordered.Count >= trackCount)
            {
                break;
            }

            ordered.Add(new DjCandidate(seedId, 1d, DjItemReasons.Seed));
        }

        // Bridging candidates first, strongest bridge to weakest.
        foreach (var companion in candidatePool
            .Where(trackId => !seeds.Contains(trackId))
            .Select(trackId => new DjCandidate(
                trackId,
                BridgeScore(trackId, seeds, affinities),
                DjItemReasons.Companion))
            .Where(candidate => candidate.Similarity > 0d)
            .OrderByDescending(static candidate => candidate.Similarity)
            .ThenBy(static candidate => candidate.TrackId))
        {
            if (ordered.Count >= trackCount)
            {
                break;
            }

            ordered.Add(companion);
        }

        if (ordered.Count < trackCount && seeds.Count > 1)
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
            SeedSummary = DescribeSeeds(seeds, ordered.Count(s => DjItemReasons.Seed.Equals(s.Reason, StringComparison.Ordinal))),
        };
    }

    /// <summary>
    /// Seed order, by configured weight then by track id.
    ///
    /// <para>Not by acoustic closeness to anything: Companion has no reference point
    /// outside its own seeds, so the only ordering signal available is the weight a
    /// person set.</para>
    /// </summary>
    private static IEnumerable<long> OrderedSeeds(
        IReadOnlySet<long> seeds,
        IReadOnlyList<DjSeed> requested)
    {
        var weightById = new Dictionary<long, double>(requested.Count);
        foreach (var seed in requested)
        {
            weightById.TryAdd(seed.TrackId, seed.EffectiveWeight);
        }

        return seeds
            .OrderByDescending(seedId => weightById.GetValueOrDefault(seedId))
            .ThenBy(seedId => seedId);
    }

    /// <summary>
    /// How well a track bridges the seed set.
    ///
    /// <para>The mean of the two strongest seed affinities, rather than the single
    /// best. That is what makes a track between two seeds beat a track that merely
    /// sits near one: a single strong affinity averages down against a weak second
    /// reading, while a track genuinely between two lands high on both.</para>
    ///
    /// <para>A bonus is then added in proportion to how many distinct seeds the track
    /// is genuinely close to, because a track close to four seeds is a better
    /// companion than one close to two by the same margin.</para>
    /// </summary>
    private static double BridgeScore(
        long trackId,
        IReadOnlySet<long> seeds,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> affinities)
    {
        // Never measured, so nothing to bridge. Choosing it would put a track in on
        // a guess, wearing the same confidence as a measured one.
        if (!affinities.TryGetValue(trackId, out var perSeed) || perSeed.Count == 0)
        {
            return 0d;
        }

        var scored = new List<double>();
        foreach (var (seedId, affinity) in perSeed)
        {
            if (seeds.Contains(seedId) && double.IsFinite(affinity) && affinity > 0d)
            {
                scored.Add(Math.Clamp(affinity, 0d, 1d));
            }
        }

        if (scored.Count == 0)
        {
            return 0d;
        }

        scored.Sort();
        scored.Reverse();

        // Close to a single seed and nothing else is not bridging; it is a neighbour,
        // which is Anchor's job.
        if (scored.Count == 1)
        {
            return scored[0] * 0.5d;
        }

        var strongest = scored[0];
        var secondStrongest = scored[1];
        var mean = (strongest + secondStrongest) / 2d;

        var bonus = Math.Min(BridgeBonus * (scored.Count - 1), BridgeCap);
        return Math.Clamp(mean + bonus, 0d, 1d);
    }

    private static string DescribeSeeds(IReadOnlySet<long> seeds, int placedSeeds)
        => placedSeeds == seeds.Count
            ? string.Format(CultureInfo.InvariantCulture, "{0} seed track(s)", seeds.Count)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} seed track(s), {1} placed",
                seeds.Count,
                placedSeeds);
}