using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Services.Library.Sonic;

/// <summary>One scored neighbour returned by a Sonic similarity query.</summary>
/// <param name="TrackId">The candidate track.</param>
/// <param name="Similarity">Cosine similarity in [0,1] for L2-normalized vectors.</param>
/// <param name="Rank">1-based position in the ordered result.</param>
/// <param name="SeedTrackId">Which seed produced the best match, when known.</param>
public sealed record SonicSimilarityMatch(
    long TrackId,
    double Similarity,
    int Rank,
    long? SeedTrackId = null)
{
    /// <summary>Cosine distance. For normalized vectors this is exactly 1 - similarity.</summary>
    public double Distance => 1d - Similarity;
}

/// <summary>Identity of the stored representation a similarity result was computed from.</summary>
public sealed record SonicModelIdentity(string ModelId, string ModelVersion, string EmbeddingVersion)
{
    public static readonly SonicModelIdentity Current =
        new("discogs-effnet-bs64-1", "1", "embedding-v1");
}

/// <summary>Timing and size facts about a built index, used to decide when exact search
/// is still affordable and to justify any later approximate index.</summary>
public sealed record SonicIndexMetrics(
    int VectorCount,
    int Dimensions,
    long BuildMilliseconds,
    long ApproximateVectorBytes)
{
    /// <summary>Approximate resident size of the held vectors. float32 only; excludes
    /// object headers and the surrounding collections.</summary>
    public double ApproximateMegabytes => ApproximateVectorBytes / 1024d / 1024d;
}

/// <summary>A snapshot of one library's current index state, used to decide whether a
/// cached index is still valid.</summary>
public sealed record SonicIndexStamp(int VectorCount, long NewestAnalyzedTicks);

/// <summary>
/// Vector math for Sonic similarity.
///
/// Vectors stored by Sonic Analysis are L2 normalized, which makes cosine
/// similarity a plain dot product and cosine distance 1 - similarity. This is a
/// separate implementation from the 13-dimensional <c>VibeSimilarityScorer</c>
/// cosine helper: the operation is the same but the domain contract is not, and
/// stuffing a 1280-wide embedding through the scalar scorer's feature builder
/// would misrepresent what it measures.
/// </summary>
public static class SonicVectorMath
{
    /// <summary>Cosine similarity of two L2-normalized vectors. Returns 0 for any
    /// vector pair that is not usable, so a corrupt row can never produce a
    /// confident match and a width mismatch can never throw.</summary>
    public static double Similarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        // A width mismatch is not comparable, it is "dissimilar". Returning 0 keeps
        // a caller that momentarily mixes two embedding generations from
        // crashing halfway through a ranking.
        if (left.Count == 0 || left.Count != right.Count)
        {
            return 0d;
        }

        // Array inputs take the vectorised path. This is the shape the index
        // actually uses; the interface walk below is only the general fallback.
        if (left is float[] leftArray && right is float[] rightArray)
        {
            return Similarity(leftArray, rightArray, leftArray.Length);
        }

        double dot = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * (double)right[index];
        }

        return double.IsFinite(dot) ? Math.Clamp(dot, -1d, 1d) : 0d;
    }

    /// <summary>
    /// Hot-path similarity for the concrete array form the index actually holds.
    ///
    /// This exists for throughput, not for a different contract. A 50-seed query
    /// against 25k vectors is 1.6 billion multiply-adds; going through
    /// <see cref="IReadOnlyList{T}"/> costs an interface dispatch per element and
    /// measured roughly two orders of magnitude slower than the loop below. The
    /// four independent accumulators also break the serial dependency chain that
    /// would otherwise limit this to one multiply per cycle.
    /// </summary>
    internal static double Similarity(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length)
        {
            return 0d;
        }

        return Similarity(left, right, left.Length);
    }

    private static double Similarity(float[] left, float[] right, int length)
    {
        float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f;
        var index = 0;
        for (; index + 3 < length; index += 4)
        {
            a0 += left[index] * right[index];
            a1 += left[index + 1] * right[index + 1];
            a2 += left[index + 2] * right[index + 2];
            a3 += left[index + 3] * right[index + 3];
        }

        for (; index < length; index++)
        {
            a0 += left[index] * right[index];
        }

        var dot = a0 + a1 + a2 + a3;
        if (!float.IsFinite(dot))
        {
            return 0d;
        }

        // Guard against accumulated floating point drift pushing a self-comparison
        // marginally past 1, which would produce a negative distance.
        return Math.Clamp(dot, -1d, 1d);
    }

    /// <summary>True when the vector has the declared width and holds only finite values.</summary>
    public static bool IsUsable(IReadOnlyList<float> vector, int dimensions)
        => vector.Count > 0
            && vector.Count == dimensions
            && IsFinite(vector);

    /// <summary>Array form of <see cref="IsUsable"/>, for the index load path.</summary>
    internal static bool IsUsable(float[] vector, int dimensions)
        => vector.Length > 0
            && vector.Length == dimensions
            && IsFinite(vector);

    private static bool IsFinite(float[] vector)
    {
        for (var index = 0; index < vector.Length; index++)
        {
            if (!float.IsFinite(vector[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(IReadOnlyList<float> vector)
        => !vector.Any(float.IsNaN) && !vector.Any(float.IsInfinity);
}

/// <summary>
/// Exact in-memory nearest-neighbour index over one library's Sonic vectors.
///
/// This is deliberately the brute-force implementation. It is the correctness
/// oracle that any later approximate index must be measured against, and it is
/// the fallback when one is unavailable. Approximate search is introduced behind
/// this same interface and only if the recorded metrics justify it.
/// </summary>
public interface ISonicSimilarityIndex
{
    /// <summary>Ensures a current index exists for the library, rebuilding only when stale.</summary>
    Task<SonicIndexMetrics> EnsureBuiltAsync(long libraryId, CancellationToken cancellationToken = default);

    /// <summary>Current metrics, or null when nothing is built.</summary>
    SonicIndexMetrics? CurrentMetrics { get; }

    /// <summary>Drops the cached index so the next query rebuilds it.</summary>
    void Invalidate(long libraryId);
}

internal sealed class ExactSonicSimilarityIndex : ISonicSimilarityIndex
{
    private readonly LibraryRepository _repository;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    private readonly object _gate = new();
    private readonly Dictionary<long, BuiltIndex> _built = new();

    public ExactSonicSimilarityIndex(LibraryRepository repository)
    {
        _repository = repository;
    }

    public SonicIndexMetrics? CurrentMetrics
    {
        get
        {
            lock (_gate)
            {
                return _built.Values
                    .Select(entry => entry.Metrics)
                    .OrderByDescending(metrics => metrics.VectorCount)
                    .FirstOrDefault();
            }
        }
    }

    public async Task<SonicIndexMetrics> EnsureBuiltAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        var stamp = await ReadStampAsync(libraryId, cancellationToken);

        lock (_gate)
        {
            if (_built.TryGetValue(libraryId, out var existing) && existing.Matches(stamp))
            {
                return existing.Metrics;
            }
        }

        var embeddings = await _repository.GetLibrarySonicEmbeddingsAsync(
            libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            cancellationToken);

        // Only usable rows enter the index. A corrupt vector is excluded here so
        // it can never surface as a low-similarity candidate either.
        var usable = embeddings
            .Where(embedding => embedding.IsUsable
                && string.Equals(embedding.DistanceMetric, "cosine", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var vectors = new Dictionary<long, float[]>(usable.Count);
        long bytes = 0;
        foreach (var embedding in usable)
        {
            var copy = embedding.Vector.ToArray();
            vectors[embedding.TrackId] = copy;
            bytes += (long)copy.Length * sizeof(float);
        }

        _stopwatch.Restart();
        // Materialise the newest timestamp so a rebuild is triggered by any new or
        // replaced vector, not only by additions.
        var newest = usable.Count == 0
            ? 0L
            : usable.Max(embedding => embedding.AnalyzedAtUtc.UtcTicks);
        var buildMs = _stopwatch.ElapsedMilliseconds;
        _stopwatch.Stop();

        var metrics = new SonicIndexMetrics(
            vectors.Count,
            vectors.Count == 0 ? 0 : vectors.Values.First().Length,
            buildMs,
            bytes);

        lock (_gate)
        {
            _built[libraryId] = new BuiltIndex(new SonicIndexStamp(vectors.Count, newest), metrics, vectors);
        }

        return metrics;
    }

    public void Invalidate(long libraryId)
    {
        lock (_gate)
        {
            _built.Remove(libraryId);
        }
    }

    /// <summary>Scoring against an already-built index. Callers must have ensured it first.</summary>
    internal IReadOnlyList<long> ScoredTrackIds(long libraryId)
    {
        lock (_gate)
        {
            return _built.TryGetValue(libraryId, out var index)
                ? index.Vectors.Keys.ToList()
                : Array.Empty<long>();
        }
    }

    internal IReadOnlyDictionary<long, float[]> VectorsFor(long libraryId)
    {
        lock (_gate)
        {
            return _built.TryGetValue(libraryId, out var index)
                ? index.Vectors
                : new Dictionary<long, float[]>();
        }
    }

    /// <summary>
    /// The live vector arrays for a library, as a flat list.
    ///
    /// Returned as concrete float[] pairs on purpose. The scoring loop is the
    /// hottest code in the feature, and handing back the concrete array type
    /// lets it bind the array overload of the similarity math instead of paying
    /// an interface dispatch per element.
    /// </summary>
    internal IReadOnlyList<(long TrackId, float[] Vector)> ScoredVectors(long libraryId)
    {
        lock (_gate)
        {
            return _built.TryGetValue(libraryId, out var index)
                ? index.Vectors
                    .Select(pair => (pair.Key, pair.Value))
                    .ToArray()
                : Array.Empty<(long, float[])>();
        }
    }

    private async Task<SonicIndexStamp> ReadStampAsync(long libraryId, CancellationToken cancellationToken)
    {
        var coverage = await _repository.GetSonicCoverageAsync(
            libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            cancellationToken);

        return new SonicIndexStamp(coverage.TracksWithEmbedding, coverage.NewestAnalyzedUtcTicks);
    }

    private sealed record BuiltIndex(
        SonicIndexStamp Stamp,
        SonicIndexMetrics Metrics,
        Dictionary<long, float[]> Vectors)
    {
        /// <summary>
        /// A cached index is current when the database still reports the same
        /// number of vectors and the same newest analysis timestamp.
        ///
        /// Both signals are needed. The count alone misses a replaced vector, and
        /// the timestamp alone misses nothing on its own but can be shared by two
        /// rows written in the same instant. Together they catch both additions
        /// and replacements, which is what "invalidate when embeddings change"
        /// has to mean in practice.
        /// </summary>
        internal bool Matches(SonicIndexStamp stamp)
            => Stamp.VectorCount == stamp.VectorCount
                && Stamp.NewestAnalyzedTicks == stamp.NewestAnalyzedTicks;
    }
}
