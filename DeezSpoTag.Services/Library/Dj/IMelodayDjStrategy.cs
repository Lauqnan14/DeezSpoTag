using System;
using System.Collections.Generic;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>Why a track entered a DJ's playlist, as the strategy itself classified it.</summary>
public sealed record DjCandidate(
    long TrackId,
    double Similarity,
    string Reason);

/// <summary>One resolved seed, with the embedding that made it usable if it had one.</summary>
public sealed record DjSeed(
    long TrackId,
    double Weight,
    bool HasEmbedding)
{
    /// <summary>Seed weight, guarded. A NaN here poisons every comparison it touches.</summary>
    public double EffectiveWeight => double.IsFinite(Weight) ? Math.Clamp(Weight, 0d, 10d) : 0d;
}

/// <summary>
/// Everything a strategy is given, and nothing it does not need.
///
/// <para>Built by the caller and passed in whole, so a strategy is a pure function
/// over its input. That is what makes a strategy testable without a database, a
/// subprocess or a clock, and it is also what keeps strategies from quietly
/// becoming a second copy of the orchestration.</para>
/// </summary>
public sealed record DjStrategyRequest
{
    public required DjDefinitionDto Definition { get; init; }

    public required IReadOnlyList<DjSeed> Seeds { get; init; }

    /// <summary>Tracks the strategy may choose from, already scoped to the library.</summary>
    public required IReadOnlyList<long> CandidateTrackIds { get; init; }

    /// <summary>
    /// How many tracks to return. Already clamped to the definition's bounds by the
    /// caller, so a strategy never has to defend against a nonsensical count.
    /// </summary>
    public required int TrackCount { get; init; }

    /// <summary>
    /// What fraction of the candidate pool had embeddings, 0 to 100.
    ///
    /// <para>Handed to the strategy rather than computed inside it, because a
    /// strategy that discovers low coverage partway through is already too late to
    /// do anything honest about it. A low value means the acoustic part of the brief
    /// cannot be honoured and the result is worth less than it looks.</para>
    /// </summary>
    public required double SonicCoveragePercent { get; init; }

    /// <summary>
    /// A stable tie-breaker for the run. Strategies that pick between equally good
    /// tracks must take this rather than a random number, so the same occurrence
    /// regenerates identically.
    /// </summary>
    public required string OccurrenceKey { get; init; }

    /// <summary>
    /// Resolved acoustic similarity, keyed by candidate track and then by seed.
    ///
    /// <para>Passed in rather than computed inside a strategy: the caller already ran
    /// the exact search, and a strategy that scored its own tracks would duplicate
    /// that implementation and could not be tested without a database.</para>
    ///
    /// <para>A candidate absent from this map has no measured affinity to any seed
    /// and is treated as having none. That is the honest reading — the alternative,
    /// scoring it highly, would put an unscored track in a playlist on a guess.</para>
    ///
    /// <para>Empty when Sonic is off or nothing was embedded, in which case Anchor
    /// falls back to playing its seeds alone and says so.</para>
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>>? SeedAffinities { get; init; }
}

/// <summary>What a strategy produced, and anything it wants the caller to know.</summary>
public sealed record DjStrategyResult
{
    public required IReadOnlyList<DjCandidate> Candidates { get; init; }

    /// <summary>
    /// Why the strategy could not do its best, in words a person can read. Empty
    /// when it did its best, which is not the same as it produced no tracks.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    /// <summary>Human-readable note of what the seeds were, for the generation record.</summary>
    public string? SeedSummary { get; init; }
}

/// <summary>Shared results for strategies, kept here so each one need not repeat them.</summary>
public static class DjStrategyResultFactory
{
    public static DjStrategyResult Empty(string diagnostic)
        => new()
        {
            Candidates = Array.Empty<DjCandidate>(),
            Diagnostics = new[] { diagnostic },
        };
}

/// <summary>
/// Builds one DJ's playlist from its seeds.
///
/// <para>Implementations are the Anchor, Companion and Journey strategies, each a
/// different answer to how a DJ fills the space between its seeds. Adding one is a
/// new registration in the DI container and nothing else, which is the point:
/// <see cref="Strategy"/> is stored on the definition, so a new kind of DJ is data.</para>
///
/// <para>A strategy must not touch the database, a subprocess or a clock, and must
/// not run inference. Sonic similarity is resolved by the caller before the strategy
/// is called, because a strategy that computed its own similarity would be
/// untestable and would duplicate the one implementation of exact similarity search
/// that already exists.</para>
/// </summary>
public interface IMelodayDjStrategy
{
    /// <summary>
    /// The value stored on <see cref="DjDefinitionDto.Strategy"/> that selects this
    /// implementation.
    /// </summary>
    string Strategy { get; }

    /// <summary>
    /// Orders tracks for one occurrence. Must be deterministic for a given request:
    /// the same seeds and candidates have to produce the same playlist, or a
    /// re-run would replace a good playlist with a different one for no reason.
    /// </summary>
    DjStrategyResult BuildPlaylist(DjStrategyRequest request);
}
