using System;
using System.Collections.Generic;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// Why a track entered a DJ's playlist, as the strategy itself classified it.
/// </summary>
/// <remarks>
/// Deliberately coarse. These values are written to
/// <c>meloday_generation_item.reason</c> and read back in diagnostics months later, so
/// they need to stay readable without the strategy that produced them.
/// </remarks>
public static class DjItemReasons
{
    /// <summary>The track was chosen as a seed.</summary>
    public const string Seed = "seed";

    /// <summary>The track was acoustically close to a seed.</summary>
    public const string Neighbour = "neighbour";

    /// <summary>The track bridges one region of the playlist to the next.</summary>
    public const string Companion = "companion";

    /// <summary>The track moves the playlist along its arc.</summary>
    public const string Journey = "journey";
}

/// <summary>
/// A DJ, as a strategy needs to describe itself.
/// </summary>
/// <remarks>
/// This is not a stored entity. There is no DJ table and no DJ CRUD: a DJ is whatever
/// strategy is registered, and this record exists only because
/// <see cref="IMelodayDjStrategy.DjStrategyRequest"/> carries one. The catalogue builds
/// it from a <see cref="DjStrategyDescriptor"/>, so no user's settings reference it and
/// nothing has to be migrated when a DJ is renamed.
/// </remarks>
public sealed record DjDefinitionDto
{
    public string DjDefinitionId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>Which strategy builds this DJ's playlist.</summary>
    public string Strategy { get; init; } = "anchor";

    public bool Enabled { get; init; } = true;

    public int MinTracks { get; init; } = 10;

    public int MaxTracks { get; init; } = 60;

    /// <summary>
    /// A DJ that asks for fewer tracks than its minimum can never be satisfied, so the
    /// floor is raised to whatever was asked for rather than failing every run.
    /// </summary>
    public int EffectiveMinTracks => Math.Max(0, Math.Min(MinTracks, MaxTracks));

    public int EffectiveMaxTracks => Math.Max(EffectiveMinTracks, MaxTracks);
}

/// <summary>Provenance for one DJ-filled Meloday playlist.</summary>
public sealed record MelodayGenerationDto
{
    public long MelodayGenerationId { get; init; }

    public long? MixCacheId { get; init; }

    /// <summary>
    /// The playlist this describes. Not derived from the DJ: it is the existing Meloday
    /// identity, so a playlist keeps its server-side identity when a different DJ fills
    /// it next week.
    /// </summary>
    public string MixId { get; init; } = string.Empty;

    public long? LibraryId { get; init; }

    public string SlotId { get; init; } = string.Empty;

    public string WeekdayId { get; init; } = string.Empty;

    /// <summary>The concrete mode that ran. "both" produces two rows.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>What was configured: <c>random</c> or a strategy id.</summary>
    public string ConfiguredDj { get; init; } = string.Empty;

    /// <summary>What actually built the playlist.</summary>
    public string ResolvedDj { get; init; } = string.Empty;

    /// <summary>
    /// True when the DJ was rolled rather than chosen. Kept as its own column because
    /// "the user picked Journey" and "it happened to be Journey this week" are different
    /// facts and only one of them is a decision.
    /// </summary>
    public bool WasRandom { get; init; }

    /// <summary>Shared by every mode of one time occasion. See <see cref="DjOccurrenceKey"/>.</summary>
    public string OccurrenceKey { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="MelodayDjContextSources.Daypart"/> or
    /// <see cref="MelodayDjContextSources.AllDayFallback"/>.
    /// </summary>
    public string ContextSource { get; init; } = string.Empty;

    public string? SonicModelVersion { get; init; }

    public double? SonicCoveragePercent { get; init; }

    public string? SeedSummary { get; init; }

    public int TrackCount { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }
}

/// <summary>One track in a DJ playlist, with the reason it was chosen.</summary>
public sealed record MelodayGenerationItemDto
{
    public int Position { get; init; }

    public long? TrackId { get; init; }

    public double? Similarity { get; init; }

    /// <summary>A <see cref="DjItemReasons"/> value, as classified by the strategy.</summary>
    public string? Reason { get; init; }

    /// <summary>The seed this track was measured against, when the strategy measured one.</summary>
    public long? RelatedSeedId { get; init; }
}

/// <summary>A generated DJ playlist together with its tracklist.</summary>
public sealed record MelodayGenerationWithItems(
    MelodayGenerationDto Generation,
    IReadOnlyList<MelodayGenerationItemDto> Items);
