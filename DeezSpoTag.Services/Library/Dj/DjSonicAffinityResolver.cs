using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library.Sonic;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// Resolves how acoustically close each candidate is to each seed, once, for the
/// caller to hand to a strategy.
/// </summary>
/// <remarks>
/// <para>Kept as the single implementation because doing it per-strategy would mean
/// several copies of the same search, none of them testable without a database, and a
/// strategy free to quietly disagree with the others about what "similar" means.</para>
///
/// <para>Extracted from the retired definition-driven orchestrator unchanged in
/// behaviour, including the reason it issues one query per seed rather than one
/// multi-seed query: a multi-seed result carries only each candidate's best match,
/// which cannot express "close to both ends" — which is precisely what a bridge or an
/// arc needs to know.</para>
/// </remarks>
public sealed class DjSonicAffinityResolver
{
    private readonly ISonicSimilarityService _similarity;

    public DjSonicAffinityResolver(ISonicSimilarityService similarity)
    {
        _similarity = similarity;
    }

    /// <summary>How many neighbours to measure per seed.</summary>
    private const int CandidateHeadroom = 2;

    /// <summary>Ceiling on measured neighbours, so a large library cannot fan out.</summary>
    private const int AffinityCeiling = 400;

    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>>> ResolveAsync(
        long libraryId,
        IReadOnlyList<DjSeed> seeds,
        IReadOnlyCollection<long> pool,
        int trackCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(pool);

        var limit = Math.Clamp(trackCount * CandidateHeadroom, 1, AffinityCeiling);
        var withEmbeddings = seeds
            .Where(static seed => seed.HasEmbedding)
            .Select(static seed => seed.TrackId)
            .Distinct()
            .ToList();

        if (withEmbeddings.Count == 0)
        {
            // No seed has a vector, so nothing can be measured. An empty table is the
            // honest answer; the strategy then plays its seeds alone and says so.
            return new Dictionary<long, IReadOnlyDictionary<long, double>>();
        }

        var affinities = new Dictionary<long, Dictionary<long, double>>();

        foreach (var seedId in withEmbeddings)
        {
            var found = await _similarity.FindNearestAsync(
                new SonicSimilarityQuery
                {
                    LibraryId = libraryId,
                    SeedTrackIds = new[] { seedId },
                    AllowedTrackIds = pool,
                    Limit = limit,
                },
                cancellationToken);

            foreach (var match in found.Matches)
            {
                if (!affinities.TryGetValue(match.TrackId, out var perSeed))
                {
                    perSeed = new Dictionary<long, double>();
                    affinities[match.TrackId] = perSeed;
                }

                perSeed[seedId] = match.Similarity;
            }
        }

        return affinities.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyDictionary<long, double>)entry.Value);
    }
}
