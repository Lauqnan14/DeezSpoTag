using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>One strategy's measured output, alongside every other strategy's.</summary>
public sealed record DjStrategyEvaluation(
    DjEvaluationReport Report,
    IReadOnlyDictionary<string, double> OverlapWithOthers);

/// <summary>Every strategy measured over the same input.</summary>
public sealed record DjEvaluationHarnessResult(
    IReadOnlyList<DjStrategyEvaluation> Evaluations)
{
    /// <summary>The strategy whose measured output this run was centred on.</summary>
    public DjEvaluationReport? Primary => Evaluations.Count > 0 ? Evaluations[0].Report : null;

    /// <summary>Anything a person should look at, across every strategy measured.</summary>
    public IReadOnlyList<string> AllFindings => Evaluations
        .SelectMany(evaluation => evaluation.Report.Findings
            .Select(finding => string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1}",
                evaluation.Report.Strategy,
                finding)))
        .ToList();

    /// <summary>
    /// True when two strategies produced the same or near-same set.
    ///
    /// <para>Distinct names over identical behaviour is worse than one strategy: it
    /// promises a choice that does not exist.</para>
    /// </summary>
    public bool HasIndistinguishableStrategies() => Evaluations
        .SelectMany(evaluation => evaluation.OverlapWithOthers.Values)
        .Any(overlap => overlap >= 0.95d);

    public string Summarise()
    {
        var lines = new List<string>();
        foreach (var evaluation in Evaluations)
        {
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1}{2}",
                evaluation.Report.Strategy,
                evaluation.Report.Metrics.Summarise(),
                evaluation.Report.Arc is null ? string.Empty : " | " + evaluation.Report.Arc.Summarise()));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Runs a set of strategies over one request and measures the results against each
/// other.
///
/// <para>The cross-strategy comparison is the part worth having. Each strategy's own
/// tests can only prove it behaves as designed; running them side by side over the same
/// seeds and the same candidate pool is what shows whether the three are actually
/// three, and whether any of them collapsed onto a single seed while still producing a
/// full-looking playlist.</para>
/// </summary>
public static class DjEvaluationHarness
{
    public static DjEvaluationHarnessResult Run(
        DjStrategyRequest request,
        IEnumerable<IMelodayDjStrategy> strategies,
        Func<IReadOnlyList<long>, IReadOnlyList<DjTrackPairDistance>?>? measurePairs = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(strategies);

        // Each strategy is run first, then the chosen set measured, because the
        // measurement is only affordable once there is a set to measure. Measuring
        // first would mean scoring every pair in the library to describe a handful of
        // playlists.
        var evaluated = new List<(IMelodayDjStrategy Strategy, DjEvaluationReport Report)>();
        foreach (var strategy in strategies)
        {
            if (strategy is null)
            {
                continue;
            }

            var result = strategy.BuildPlaylist(request);
            var trackIds = result.Candidates.Select(candidate => candidate.TrackId).ToList();
            var pairs = measurePairs?.Invoke(trackIds);
            evaluated.Add((strategy, DjPlaylistEvaluator.Evaluate(strategy, request, result, pairs)));
        }

        var results = new List<DjStrategyEvaluation>(evaluated.Count);
        for (var index = 0; index < evaluated.Count; index++)
        {
            var overlaps = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var other = 0; other < evaluated.Count; other++)
            {
                if (other == index)
                {
                    continue;
                }

                overlaps[evaluated[other].Report.Strategy] = DjPlaylistEvaluator.Overlap(
                    evaluated[index].Report.TrackIds,
                    evaluated[other].Report.TrackIds);
            }

            results.Add(new DjStrategyEvaluation(evaluated[index].Report, overlaps));
        }

        return new DjEvaluationHarnessResult(results);
    }

    /// <summary>
    /// Every registered strategy, so a caller does not have to track the list.
    /// </summary>
    /// <remarks>
    /// Takes the catalogue rather than the retired orchestration service: the catalogue
    /// is now the single place that knows which strategies exist, so this cannot fall out
    /// of step with what Random DJ would actually pick from.
    /// </remarks>
    public static DjEvaluationHarnessResult Run(
        IDjStrategyCatalog catalog,
        DjStrategyRequest request)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return Run(request, catalog.GetAll().Select(static descriptor => descriptor.Strategy));
    }
}