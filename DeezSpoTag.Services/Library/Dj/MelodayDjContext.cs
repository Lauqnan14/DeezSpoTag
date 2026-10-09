using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// Whether a DJ's seeds really came from the time slot the playlist claims to be.
/// </summary>
public static class MelodayDjContextSources
{
    /// <summary>History was drawn from the slot's own daypart window.</summary>
    public const string Daypart = "daypart";

    /// <summary>
    /// The slot had no eligible daypart history, so Meloday fell back to all-day
    /// history. The playlist is still real, but it is not evidence of morning or
    /// evening listening behaviour, and recording that is the only honest thing to do.
    /// </summary>
    public const string AllDayFallback = "all-day-fallback";
}

/// <summary>
/// Everything a Meloday time slot hands to a DJ.
///
/// <para>This is deliberately built from raw daypart history rather than from the
/// track ids Meloday already selected. By the time the existing selection has run,
/// its seed set has been randomly sampled, its play frequencies collapsed, and its
/// members added to the exclusion set — a DJ given those inputs would be selecting
/// from a pool that had already discarded the reason it was interesting.</para>
///
/// <para>The seeds are also deterministic, which the existing historical selection
/// deliberately is not. A DJ that changed its mind on retry would look like a
/// different DJ.</para>
/// </remarks>
public sealed record MelodayDjContext
{
    public required long LibraryId { get; init; }

    public required string LibraryName { get; init; }

    public required string SlotId { get; init; }

    public required string SlotName { get; init; }

    public required string WeekdayId { get; init; }

    /// <summary>Daypart or all-day fallback. Never silently omitted.</summary>
    public required string ContextSource { get; init; }

    public required IReadOnlyList<int> DaypartHours { get; init; }

    /// <summary>Deterministic, analysis-backed seed material for this slot.</summary>
    public required IReadOnlyList<DjSeed> Seeds { get; init; }

    /// <summary>Everything the mode stage qualified as a reasonable candidate.</summary>
    public required IReadOnlyList<long> EligibleTrackIds { get; init; }

    /// <summary>Recently played, and anything the mode stage excluded.</summary>
    public required IReadOnlySet<long> ExcludedTrackIds { get; init; }

    /// <summary>Share of the eligible pool that had embeddings, 0 to 100.</summary>
    public required double SonicCoveragePercent { get; init; }

    /// <summary>Tracks that actually had a vector, so strategies can tell.</summary>
    public required IReadOnlySet<long> EmbeddedTrackIds { get; init; }

    /// <summary>
    /// One key for the whole time occasion, shared by every mode of it. See
    /// <see cref="DjOccurrenceKey"/> for why the mode is not part of it.
    /// </summary>
    public required string OccurrenceKey { get; init; }

    public bool UsedAllDayFallback
        => string.Equals(ContextSource, MelodayDjContextSources.AllDayFallback, StringComparison.Ordinal);
}

/// <summary>
/// A resolved DJ choice for one time occurrence: what was configured, what it became,
/// and whether that was a decision or a roll.
/// </summary>
public sealed record MelodayDjResolution
{
    /// <summary>The stored setting: <c>random</c> or a concrete strategy id.</summary>
    public required string ConfiguredDj { get; init; }

    /// <summary>The strategy that will actually build the playlist.</summary>
    public required string ResolvedDj { get; init; }

    public required bool WasRandom { get; init; }

    public required string OccurrenceKey { get; init; }

    /// <summary>
    /// Set when a specific DJ was configured but is not available, and Random was used
    /// instead. Silent substitution would be worse than a visible fallback.
    /// </summary>
    public string? FallbackDiagnostic { get; init; }
}

/// <summary>
/// Turns a stored DJ setting into one concrete strategy for one time occurrence.
/// </summary>
/// <remarks>
/// <para>Random DJ is a selection policy, not a strategy. There is deliberately no
/// <c>RandomDjStrategy</c>: a strategy that picked another strategy would be a
/// strategy with no behaviour of its own, and would make "which DJ ran" unanswerable
/// from the catalogue alone. Resolution happens here instead, so every playlist that
/// exists was built by exactly one registered strategy and its id is recorded.</para>
///
/// <para>Random over the eligible set is a deterministic hash of the occurrence key,
/// not an unseeded draw. See <see cref="DjOccurrenceKey"/>.</para>
/// </remarks>
public static class MelodayDjResolver
{
    /// <summary>Where a specific DJ was configured but could not be used.</summary>
    public const string UnknownDjFallback =
        "The configured DJ is not registered, so Random DJ was used for this generation.";

    public static MelodayDjResolution Resolve(
        string? configuredDj,
        MelodayDjContext context,
        IDjStrategyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);

        var configured = MelodayDjSelections.Normalize(configuredDj);
        if (MelodayDjSelections.IsNone(configured))
        {
            return new MelodayDjResolution
            {
                ConfiguredDj = MelodayDjSelections.None,
                ResolvedDj = string.Empty,
                WasRandom = false,
                OccurrenceKey = context.OccurrenceKey,
            };
        }

        var eligible = catalog.GetEligible(context);

        if (eligible.Count == 0)
        {
            // Nothing can be built. Returning a resolution with an empty id lets the
            // caller fall back to the existing Meloday pipeline rather than emitting an
            // empty playlist.
            return new MelodayDjResolution
            {
                ConfiguredDj = configured,
                ResolvedDj = string.Empty,
                WasRandom = !string.Equals(configured, MelodayDjSelections.Random, StringComparison.Ordinal),
                OccurrenceKey = context.OccurrenceKey,
                FallbackDiagnostic =
                    "No registered DJ could build a playlist from this time slot's history, so the standard Meloday playlist was generated.",
            };
        }

        if (!string.Equals(configured, MelodayDjSelections.Random, StringComparison.Ordinal))
        {
            var specific = catalog.GetById(configured);
            if (specific is not null && eligible.Any(entry =>
                    string.Equals(entry.Id, specific.Id, StringComparison.Ordinal)))
            {
                return new MelodayDjResolution
                {
                    ConfiguredDj = configured,
                    ResolvedDj = specific.Id,
                    WasRandom = false,
                    OccurrenceKey = context.OccurrenceKey,
                };
            }

            // Configured for a specific DJ that no longer exists or cannot build here.
            // Falling back silently would make the UI's selector a lie.
            return Random(context, catalog, eligible, configured, UnknownDjFallback);
        }

        return Random(context, catalog, eligible, configured, null);
    }

    private static MelodayDjResolution Random(
        MelodayDjContext context,
        IDjStrategyCatalog catalog,
        IReadOnlyList<DjStrategyDescriptor> eligible,
        string configured,
        string? diagnostic)
    {
        // Sorted so the eligible set has one order regardless of registration order,
        // which would otherwise make the catalogue itself a hidden input to the pick.
        var ordered = eligible
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        var index = (int)(StableIndex(context.OccurrenceKey, ordered.Count) % (ulong)ordered.Count);

        return new MelodayDjResolution
        {
            ConfiguredDj = configured,
            ResolvedDj = ordered[index].Id,
            WasRandom = true,
            OccurrenceKey = context.OccurrenceKey,
            FallbackDiagnostic = diagnostic,
        };
    }

    /// <summary>
    /// A hash of the occurrence key, not a seeded <see cref="Random"/>.
    ///
    /// <para><see cref="string.GetHashCode()"/> is deliberately not used: it is not
    /// guaranteed stable across processes, so the same day could resolve to different
    /// DJs before and after a restart.</para>
    /// </summary>
    internal static ulong StableIndex(string key, int modulo)
    {
        if (modulo <= 0)
        {
            return 0UL;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var value = BitConverter.ToUInt64(bytes, 0);
        return value % (ulong)modulo;
    }

    /// <summary>A short, human-readable form for logs and the run message.</summary>
    public static string Describe(MelodayDjResolution resolution)
    {
        var configured = resolution.ConfiguredDj;
        var resolved = resolution.ResolvedDj;
        if (!resolution.WasRandom)
        {
            return resolved;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} (configured: {1})",
            resolved,
            configured);
    }
}
