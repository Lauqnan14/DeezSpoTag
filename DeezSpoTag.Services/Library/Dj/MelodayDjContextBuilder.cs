using System;
using System.Collections.Generic;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// Builds a DJ's context from a Meloday time slot's own listening history.
/// </summary>
/// <remarks>
/// <para>Seeds come from the raw history entries for this slot, ranked by play count
/// and then by track id. Both the ranking and the tie-break are total, so the same
/// slot and day always produce the same seeds. That is a deliberate difference from
/// Meloday's existing balanced historical selection, which samples randomly and is
/// therefore not reproducible — correct for a playlist meant to feel fresh, wrong for
/// an input that a retry has to reproduce.</para>
///
/// <para>Seeds must also carry an analysis: a strategy reasons about mood and genre
/// through <c>TrackAnalysisResultDto</c>, and a track it cannot describe is not a seed
/// it can use. An unanalysed library therefore produces no seeds and no DJ, which falls
/// back to the standard Meloday playlist rather than emitting something thin.</para>
/// </remarks>
public sealed class MelodayDjContextBuilder
{
    /// <summary>Upper bound on seeds, so a long history cannot dominate the brief.</summary>
    public const int MaxSeeds = 60;

    public MelodayDjContext Build(
        long libraryId,
        string libraryName,
        string slotId,
        string slotName,
        string weekdayId,
        IReadOnlyList<int> daypartHours,
        bool usedAllDayFallback,
        IReadOnlyList<PlayHistoryEntryDto> history,
        IReadOnlySet<long> excludedTrackIds,
        IReadOnlyList<long> eligibleTrackIds,
        IReadOnlyDictionary<long, TrackAnalysisResultDto> trackAnalyses,
        IReadOnlySet<long> embeddedTrackIds,
        double sonicCoveragePercent,
        string occurrenceKey)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(excludedTrackIds);
        ArgumentNullException.ThrowIfNull(trackAnalyses);
        ArgumentNullException.ThrowIfNull(embeddedTrackIds);

        var playCounts = new Dictionary<long, int>();
        foreach (var entry in history)
        {
            if (excludedTrackIds.Contains(entry.TrackId) || !trackAnalyses.ContainsKey(entry.TrackId))
            {
                continue;
            }

            playCounts[entry.TrackId] = playCounts.TryGetValue(entry.TrackId, out var count)
                ? count + entry.PlayCount
                : entry.PlayCount;
        }

        // Play count descending, then track id ascending. The id tie-break is what makes
        // this deterministic: two tracks heard equally often have no inherent order, and
        // leaving it to the dictionary's would let it vary between runs.
        var seeds = playCounts
            .OrderByDescending(static entry => entry.Value)
            .ThenBy(static entry => entry.Key)
            .Take(MaxSeeds)
            .Select(entry => new DjSeed(
                entry.Key,
                entry.Value,
                embeddedTrackIds.Contains(entry.Key)))
            .ToList();

        return new MelodayDjContext
        {
            LibraryId = libraryId,
            LibraryName = libraryName,
            SlotId = slotId,
            SlotName = slotName,
            WeekdayId = weekdayId,
            ContextSource = usedAllDayFallback
                ? MelodayDjContextSources.AllDayFallback
                : MelodayDjContextSources.Daypart,
            DaypartHours = daypartHours,
            Seeds = seeds,
            EligibleTrackIds = eligibleTrackIds ?? Array.Empty<long>(),
            ExcludedTrackIds = excludedTrackIds,
            SonicCoveragePercent = Math.Round(sonicCoveragePercent, 1),
            EmbeddedTrackIds = embeddedTrackIds,
            OccurrenceKey = occurrenceKey,
        };
    }
}
