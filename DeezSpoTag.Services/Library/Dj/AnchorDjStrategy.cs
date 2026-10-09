using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// The Anchor strategy: a DJ that plays a recognisable sound.
///
/// <para>A real anchor DJ has a fixed point of reference — a handful of tracks that
/// define the sound — and fills the rest of the hour with whatever sits closest to
/// that reference. That is this strategy: the seeds go in first and in full, then the
/// pool is filled by acoustic distance from them, best match first.</para>
///
/// <para>Anchor is the conservative strategy, and deliberately so. It is the one a
/// person can predict: give it the same seeds and it will play the same set.
/// Companion and Journey move away from the seeds on purpose; Anchor does not.</para>
///
/// <para>Two properties are load-bearing:</para>
/// <list type="bullet">
/// <item><b>Deterministic.</b> Same request, same playlist. Otherwise a re-run
/// replaces a good playlist with a different one for no reason, which reads as the DJ
/// being unreliable.</item>
/// <item><b>Every seed that fits appears.</b> The seeds are the DJ's identity, so a
/// seed that fits within the track count is never dropped in favour of a marginally
/// closer non-seed.</item>
/// </list>
///
/// <para>A pure function of its request. It reads no clock, no database and no
/// random source.</para>
/// </summary>
[DjStrategyDescriptor(
    DisplayName = "Anchor",
    Description = "Plays a recognisable sound: seeds first, then the tracks closest to them.",
    Requirements = new[] { "Analysed history for the time slot", "Sonic embeddings improve the track choices" })]
public sealed class AnchorDjStrategy : IMelodayDjStrategy
{
    /// <summary>Selects this strategy on <see cref="DjDefinitionDto.Strategy"/>.</summary>
    public const string StrategyId = "anchor";

    /// <summary>
    /// Below this coverage the acoustic half of the brief cannot be honoured, so the
    /// result says so instead of being presented as a considered set.
    /// </summary>
    public const double LowCoveragePercent = 50d;

    /// <summary>
    /// How much a track that resembles several seeds scores above one that resembles
    /// only the best, capped so corroboration can never outweigh a closer match.
    /// </summary>
    public const double CorroborationBonus = 0.1d;

    public const double CorroborationCap = 0.3d;

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
            // Without a seed there is nothing to anchor to. Returning the
            // best-scoring tracks anyway would produce a plausible playlist with no
            // relationship to the DJ that was asked for.
            return new DjStrategyResult
            {
                Candidates = Array.Empty<DjCandidate>(),
                Diagnostics = new[] { "None of this DJ's seeds are in the library, so there is nothing to anchor to." },
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

        var weights = NormalizedSeedWeights(seeds, requestedSeeds);
        var affinities = request.SeedAffinities ?? new Dictionary<long, IReadOnlyDictionary<long, double>>();

        var ordered = new List<DjCandidate>(Math.Min(trackCount, candidatePool.Count));

        // Seeds first, best-anchored first, so the playlist opens on the tracks that
        // define it. A seed is included whenever one still fits.
        foreach (var seedId in seeds
            .OrderByDescending(seedId => weights[seedId])
            .ThenBy(seedId => seedId))
        {
            if (ordered.Count >= trackCount)
            {
                break;
            }

            ordered.Add(new DjCandidate(seedId, 1d, DjItemReasons.Seed));
        }

        // Then the nearest neighbours of the seed set, best first.
        foreach (var neighbour in candidatePool
            .Where(trackId => !seeds.Contains(trackId))
            .Select(trackId => new DjCandidate(
                trackId,
                AffinityToSeeds(trackId, weights, affinities),
                DjItemReasons.Neighbour))
            .Where(candidate => candidate.Similarity > 0d)
            .OrderByDescending(static candidate => candidate.Similarity)
            .ThenBy(static candidate => candidate.TrackId))
        {
            if (ordered.Count >= trackCount)
            {
                break;
            }

            ordered.Add(neighbour);
        }

        if (ordered.Count < trackCount && seeds.Count > 1)
        {
            // Only worth reporting when the seeds could have filled more of the gap.
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
    /// Each seed's share of the pull, normalised so the weights sum to one.
    ///
    /// <para>Normalising rather than summing keeps a DJ with five seeds and a DJ with
    /// one comparable in influence. The seed list is how a person configures a DJ, so
    /// adding a seed should redistribute the pull, not make every seed pull harder.</para>
    /// </summary>
    private static Dictionary<long, double> NormalizedSeedWeights(
        IReadOnlySet<long> seeds,
        IReadOnlyList<DjSeed> requested)
    {
        var weightById = new Dictionary<long, double>(requested.Count);
        foreach (var seed in requested)
        {
            weightById.TryAdd(seed.TrackId, seed.EffectiveWeight);
        }

        var total = seeds.Sum(seedId => weightById.GetValueOrDefault(seedId));
        var weights = new Dictionary<long, double>(seeds.Count);
        foreach (var seedId in seeds)
        {
            weights[seedId] = total > 0d
                ? weightById.GetValueOrDefault(seedId) / total
                : 1d / seeds.Count;
        }

        return weights;
    }

    /// <summary>
    /// A candidate's affinity to the seed set: its strongest single seed affinity,
    /// plus a bonus for also being close to other seeds.
    ///
    /// <para>The bonus is what stops a DJ collapsing onto one seed. Without it, a
    /// library where every track resembles the same anchor yields a playlist of
    /// near-identical tracks, and "close to the sound" quietly becomes "similar to
    /// track one".</para>
    /// </summary>
    private static double AffinityToSeeds(
        long trackId,
        IReadOnlyDictionary<long, double> weights,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> affinities)
    {
        // A track the caller never scored has no affinity to any seed, and reports
        // zero rather than an invented one.
        if (!affinities.TryGetValue(trackId, out var perSeed) || perSeed.Count == 0)
        {
            return 0d;
        }

        var best = 0d;
        var supporting = 0;
        foreach (var (seedId, affinity) in perSeed)
        {
            if (!weights.ContainsKey(seedId) || !double.IsFinite(affinity))
            {
                continue;
            }

            if (affinity > best)
            {
                best = affinity;
            }

            if (affinity >= 0.5d)
            {
                supporting++;
            }
        }

        if (best <= 0d)
        {
            return 0d;
        }

        var corroboration = supporting > 1
            ? Math.Min(CorroborationBonus * (supporting - 1), CorroborationCap)
            : 0d;

        return Math.Clamp(best + corroboration, 0d, 1d);
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