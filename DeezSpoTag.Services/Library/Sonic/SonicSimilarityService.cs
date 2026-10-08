using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Services.Library.Sonic;

/// <summary>
/// A query for tracks similar to one or more seeds.
///
/// Candidates are supplied by the caller rather than discovered here. That keeps
/// library scope, folder scope, availability and recent-play exclusions in the
/// hands of whoever already owns those rules, and guarantees this service can
/// never widen a playlist's candidate set on its own.
/// </summary>
public sealed record SonicSimilarityQuery
{
    /// <summary>Library whose vectors may be considered. Never crossed.</summary>
    public required long LibraryId { get; init; }

    /// <summary>Tracks to find neighbours for. A seed is never returned as its own neighbour.</summary>
    public required IReadOnlyList<long> SeedTrackIds { get; init; }

    /// <summary>Restricts which tracks may be returned. Null means every scored track.</summary>
    public IReadOnlyCollection<long>? AllowedTrackIds { get; init; }

    /// <summary>Tracks that must never be returned, such as recently played or already chosen.</summary>
    public IReadOnlyCollection<long>? ExcludedTrackIds { get; init; }

    /// <summary>Maximum neighbours to return.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>
    /// Optional ceiling on cosine distance. Null means no ceiling, which is the
    /// right default when the caller wants the closest N rather than a quality
    /// floor: a numeric default of 0 would silently mean "exact matches only".
    /// </summary>
    public double? MaximumDistance { get; init; }

    /// <summary>Suppresses seed tracks from the results. Defaults to true.</summary>
    public bool ExcludeSeeds { get; init; } = true;
}

/// <summary>Result of a Sonic similarity query, including why it may be short.</summary>
public sealed record SonicSimilarityResult(
    IReadOnlyList<SonicSimilarityMatch> Matches,
    SonicIndexMetrics? Metrics,
    int SeedCount,
    int CandidateCount)
{
    public bool HasMatches => Matches.Count > 0;
}

/// <summary>
/// Provider-agnostic Sonic similarity.
///
/// Callers ask "which tracks are similar to these" and receive track ids,
/// similarities and ranks. They never receive vectors, which keeps the embedding
/// representation an implementation detail that can change without a caller
/// changing, and keeps Melody from having to understand audio embeddings at all.
/// </summary>
public interface ISonicSimilarityService
{
    /// <summary>Nearest neighbours for a set of seeds, best match per candidate across all seeds.</summary>
    Task<SonicSimilarityResult> FindNearestAsync(SonicSimilarityQuery query, CancellationToken cancellationToken = default);

    /// <summary>Cosine similarity between two stored vectors, or null when either is unusable.</summary>
    Task<double?> SimilarityAsync(long libraryId, long trackA, long trackB, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cosine distances across many pairs at once, validating the index once.
    ///
    /// <para>Exists because <see cref="SimilarityAsync"/> revalidates the index from
    /// the database on every call. Measuring a 40-track playlist means 780 pairs, so
    /// calling it in a loop costs 780 database round trips for 780 single dot products —
    /// which measured twenty times slower than the arithmetic it wraps. This does the
    /// same work with one validation.</para>
    ///
    /// <para>Pairs whose tracks are absent or unusable are omitted from the result
    /// rather than returned as zero, so a caller can tell "not comparable" from
    /// "identical".</para>
    Task<IReadOnlyList<(long TrackA, long TrackB, double Distance)>> PairDistancesAsync(
        long libraryId,
        IReadOnlyList<(long TrackA, long TrackB)> pairs,
        CancellationToken cancellationToken = default);

    /// <summary>The stored vector for one track, or null when absent or unusable.</summary>
    Task<SonicEmbeddingDto?> GetEmbeddingAsync(long trackId, CancellationToken cancellationToken = default);

    /// <summary>Current coverage for a library, so a caller can refuse to start early.</summary>
    Task<SonicCoverageDto> GetCoverageAsync(long libraryId, CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the index for a library.</summary>
    Task<SonicIndexMetrics> RebuildIndexAsync(long libraryId, CancellationToken cancellationToken = default);
}

internal sealed class SonicSimilarityService : ISonicSimilarityService
{
    private readonly LibraryRepository _repository;
    private readonly ExactSonicSimilarityIndex _index;

    public SonicSimilarityService(LibraryRepository repository, ISonicSimilarityIndex index)
    {
        _repository = repository;
        _index = (ExactSonicSimilarityIndex)index;
    }

    public async Task<SonicSimilarityResult> FindNearestAsync(
        SonicSimilarityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.SeedTrackIds.Count == 0 || query.Limit <= 0)
        {
            return new SonicSimilarityResult(
                Array.Empty<SonicSimilarityMatch>(), null, query.SeedTrackIds.Count, 0);
        }

        var metrics = await _index.EnsureBuiltAsync(query.LibraryId, cancellationToken);
        var scored = _index.ScoredVectors(query.LibraryId);
        if (scored.Count == 0)
        {
            return new SonicSimilarityResult(
                Array.Empty<SonicSimilarityMatch>(), metrics, query.SeedTrackIds.Count, 0);
        }

        var byTrackId = new Dictionary<long, float[]>(scored.Count);
        foreach (var (trackId, vector) in scored)
        {
            byTrackId[trackId] = vector;
        }

        var seeds = query.SeedTrackIds
            .Where(byTrackId.ContainsKey)
            .Distinct()
            .ToList();
        if (seeds.Count == 0)
        {
            return new SonicSimilarityResult(
                Array.Empty<SonicSimilarityMatch>(), metrics, query.SeedTrackIds.Count, 0);
        }

        // Hoist the seed vectors once. Resolving them per candidate was the other
        // half of the multi-seed cost: a 50-seed query against 25k vectors was
        // doing 1.25M dictionary lookups before it computed a single dot product.
        var seedVectors = new float[seeds.Count][];
        for (var index = 0; index < seeds.Count; index++)
        {
            seedVectors[index] = byTrackId[seeds[index]];
        }

        var seedSet = new HashSet<long>(seeds);
        var excludedSet = query.ExcludedTrackIds is { Count: > 0 }
            ? new HashSet<long>(query.ExcludedTrackIds)
            : null;
        var allowed = query.AllowedTrackIds is { Count: > 0 }
            ? new HashSet<long>(query.AllowedTrackIds)
            : null;
        var ceiling = query.MaximumDistance;

        var results = new List<SonicSimilarityMatch>(scored.Count);
        foreach (var (trackId, candidate) in scored)
        {
            if (query.ExcludeSeeds && seedSet.Contains(trackId))
            {
                continue;
            }

            if (excludedSet is not null && excludedSet.Contains(trackId))
            {
                continue;
            }

            if (allowed is not null && !allowed.Contains(trackId))
            {
                continue;
            }

            // Best match across every seed: a candidate is close to the playlist
            // if it is close to any of its anchors, not to all of them.
            var best = double.NegativeInfinity;
            var bestSeed = seeds[0];
            for (var index = 0; index < seedVectors.Length; index++)
            {
                var similarity = SonicVectorMath.Similarity(seedVectors[index], candidate);
                if (similarity > best)
                {
                    best = similarity;
                    bestSeed = seeds[index];
                }
            }

            if (ceiling is { } limit && 1d - best > limit)
            {
                continue;
            }

            results.Add(new SonicSimilarityMatch(trackId, best, 0, bestSeed));
        }

        // Deterministic ordering: similarity descending, then track id ascending.
        // Track id is the tiebreak so two equal-scoring candidates never swap
        // between runs, which would make a generated playlist unreproducible.
        var ordered = results
            .OrderByDescending(match => match.Similarity)
            .ThenBy(match => match.TrackId)
            .Take(query.Limit)
            .ToList();

        return new SonicSimilarityResult(
            ordered.Select((match, index) => match with { Rank = index + 1 }).ToList(),
            metrics,
            seeds.Count,
            results.Count);
    }

    public async Task<double?> SimilarityAsync(
        long libraryId,
        long trackA,
        long trackB,
        CancellationToken cancellationToken = default)
    {
        await _index.EnsureBuiltAsync(libraryId, cancellationToken);
        var vectors = _index.VectorsFor(libraryId);

        if (!vectors.TryGetValue(trackA, out var left) || !vectors.TryGetValue(trackB, out var right))
        {
            return null;
        }

        return SonicVectorMath.Similarity(left, right);
    }

    public async Task<SonicEmbeddingDto?> GetEmbeddingAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _repository.GetSonicEmbeddingAsync(
            trackId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            cancellationToken);

        return embedding is { IsUsable: true } ? embedding : null;
    }

    public async Task<IReadOnlyList<(long TrackA, long TrackB, double Distance)>> PairDistancesAsync(
        long libraryId,
        IReadOnlyList<(long TrackA, long TrackB)> pairs,
        CancellationToken cancellationToken = default)
    {
        if (pairs is null || pairs.Count == 0)
        {
            return Array.Empty<(long TrackA, long TrackB, double Distance)>();
        }

        // One validation for the whole batch. This is the whole point of the method:
        // the per-pair call path pays a database round trip to confirm the index is
        // still current, which for 780 pairs costs far more than 780 dot products.
        await _index.EnsureBuiltAsync(libraryId, cancellationToken);
        var vectors = _index.VectorsFor(libraryId);

        var results = new List<(long TrackA, long TrackB, double Distance)>(pairs.Count);
        foreach (var (left, right) in pairs)
        {
            if (!vectors.TryGetValue(left, out var leftVector)
                || !vectors.TryGetValue(right, out var rightVector))
            {
                continue;
            }

            results.Add((left, right, 1d - SonicVectorMath.Similarity(leftVector, rightVector)));
        }

        return results;
    }

    public Task<SonicCoverageDto> GetCoverageAsync(long libraryId, CancellationToken cancellationToken = default)
        => _repository.GetSonicCoverageAsync(
            libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            cancellationToken);

    public async Task<SonicIndexMetrics> RebuildIndexAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        _index.Invalidate(libraryId);
        return await _index.EnsureBuiltAsync(libraryId, cancellationToken);
    }
}
