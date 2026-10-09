using System;
using System.Collections.Generic;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// What the UI and Random DJ need to know about one DJ, without knowing how it works.
/// </summary>
/// <param name="Id">
/// Stable slug. Stored in settings and in generation provenance, so it must not change
/// once written.
/// </param>
/// <param name="DisplayName">Shown in the Target Libraries DJ selector.</param>
/// <param name="Requirements">
/// Human-readable prerequisites. A DJ with unmet requirements is not offered for
/// Random, because a roll that lands on an unusable DJ wastes the whole generation.
/// </param>
public sealed record DjStrategyDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Requirements { get; init; } = Array.Empty<string>();

    public int MinTracks { get; init; } = 10;

    public int MaxTracks { get; init; } = 60;

    /// <summary>
    /// The implementation. Never exposed outside this process; the descriptor is the
    /// whole contract to the UI and to Random DJ.
    /// </summary>
    public required IMelodayDjStrategy Strategy { get; init; }

    /// <summary>
    /// Whether this DJ can build something from a given context.
    /// </summary>
    /// <remarks>
    /// A DJ with no usable seed has nothing to build from and would emit an empty
    /// playlist, so it is excluded from Random rather than rolled onto. Note that a DJ
    /// needing no sonic embeddings is <em>not</em> excluded for having none — it
    /// degrades and says so, which is a different thing from being unable to run.
    /// </remarks>
    /// <summary>
    /// Extra eligibility rule, when a DJ has prerequisites beyond usable seeds.
    ///
    /// <para>A predicate rather than a subclass, so the descriptor stays a sealed record
    /// and can still be constructed inline by tests and by the catalogue.</para>
    /// </summary>
    public Func<MelodayDjContext, bool>? EligibilityCheck { get; init; }

    public bool IsEligible(MelodayDjContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return EligibilityCheck?.Invoke(context) ?? context.Seeds.Count > 0;
    }

    /// <summary>
    /// A minimal definition for the strategy contract.
    /// </summary>
    /// <remarks>
    /// The strategies do not read the definition — they take seeds, candidates, a count
    /// and affinities — but the request type requires one. Synthesising it from the
    /// descriptor keeps every existing strategy and its tests untouched while dropping
    /// the definition-driven product that the DJ is no longer built around.
    /// </remarks>
    public DjDefinitionDto ToDefinition() => new()
    {
        DjDefinitionId = Id,
        Name = DisplayName,
        Description = Description,
        Strategy = Id,
        Enabled = true,
        MinTracks = MinTracks,
        MaxTracks = MaxTracks,
    };
}

/// <summary>
/// The set of registered DJs.
/// </summary>
/// <remarks>
/// <para>Adding a DJ is a DI registration and nothing else. There is no settings
/// schema change, no JavaScript array to extend and no switch statement, which is the
/// correction to the earlier design where three strategies were the product.</para>
///
/// <para>Nothing here may reference <c>MelodyLibrarySchedule</c> or a slot id. A
/// catalogue that knew about Meloday's settings would become a second thing to keep in
/// step with it.</para>
/// </remarks>
public interface IDjStrategyCatalog
{
    /// <summary>Every registered DJ, in stable id order.</summary>
    IReadOnlyList<DjStrategyDescriptor> GetAll();

    DjStrategyDescriptor? GetById(string? id);

    /// <summary>The subset that can build a playlist from this context.</summary>
    IReadOnlyList<DjStrategyDescriptor> GetEligible(MelodayDjContext context);
}

/// <summary>Default catalogue: whatever strategies DI registered, and nothing else.</summary>
public sealed class DjStrategyCatalog : IDjStrategyCatalog
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<DjStrategyDescriptor> _descriptors;
    private readonly IReadOnlyDictionary<string, DjStrategyDescriptor> _byId;

    public DjStrategyCatalog(IEnumerable<IMelodayDjStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        var descriptors = new List<DjStrategyDescriptor>();
        var byId = new Dictionary<string, DjStrategyDescriptor>(StringComparer.Ordinal);
        foreach (var strategy in strategies)
        {
            var id = (strategy.Strategy ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0)
            {
                throw new InvalidOperationException(
                    "A registered IMelodayDjStrategy returned an empty Strategy id, so it cannot be offered to Random DJ.");
            }

            if (byId.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"Two DJ strategies registered the id '{id}'. Ids are stored in settings and must be unique.");
            }

            var descriptor = Describe(strategy);
            descriptors.Add(descriptor);
            byId[id] = descriptor;
        }

        _descriptors = descriptors
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToList();
        _byId = byId;
    }

    public IReadOnlyList<DjStrategyDescriptor> GetAll() => _descriptors;

    public DjStrategyDescriptor? GetById(string? id)
    {
        var normalized = (id ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : _byId.GetValueOrDefault(normalized);
    }

    public IReadOnlyList<DjStrategyDescriptor> GetEligible(MelodayDjContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _descriptors.Where(entry => entry.IsEligible(context)).ToList();
    }

    /// <summary>
    /// Display metadata for a registered strategy.
    /// </summary>
    /// <remarks>
    /// Defaults come from the strategy's own XML documentation, so registering a new DJ
    /// does not also mean maintaining a table somewhere else that will silently fall
    /// out of date. An explicit <see cref="DjStrategyDescriptorAttribute"/> overrides
    /// any of it.
    /// </remarks>
    private static DjStrategyDescriptor Describe(IMelodayDjStrategy strategy)
    {
        var id = strategy.Strategy.Trim().ToLowerInvariant();
        var declared = DjStrategyDescriptorAttribute.For(strategy.GetType());

        return new DjStrategyDescriptor
        {
            Id = id,
            DisplayName = declared?.DisplayName ?? DefaultDisplayName(strategy),
            Description = declared?.Description ?? SummaryFromDocumentation(strategy.GetType()),
            Requirements = declared?.Requirements ?? Array.Empty<string>(),
            Strategy = strategy,
        };
    }

    private static string DefaultDisplayName(IMelodayDjStrategy strategy)
    {
        var typeName = strategy.GetType().Name;
        var suffix = typeName.EndsWith("DjStrategy", StringComparison.Ordinal)
            ? typeName[..^"DjStrategy".Length]
            : typeName.EndsWith("Strategy", StringComparison.Ordinal)
                ? typeName[..^"Strategy".Length]
                : typeName;

        return string.Join(
            ' ',
            System.Text.RegularExpressions.Regex
                .Replace(suffix, "(?<=[a-z0-9])(?=[A-Z])", " ", System.Text.RegularExpressions.RegexOptions.None, RegexTimeout)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <remarks>
    /// No reflection over XML documentation here: it is not emitted into the assembly by
    /// default, and a missing description is not worth changing the build to obtain.
    /// A DJ with no descriptor attribute simply shows its id.
    /// </remarks>
    private static string? SummaryFromDocumentation(Type type)
    {
        var declared = DjStrategyDescriptorAttribute.For(type);
        return string.IsNullOrWhiteSpace(declared?.Description) ? null : declared.Description;
    }
}

/// <summary>
/// Optional display metadata for a registered DJ.
/// </summary>
/// <remarks>
/// <para>An attribute rather than a second registry, so a DJ's display name and its
/// behaviour cannot drift apart: the metadata is declared on the type that implements
/// it. A DJ with no attribute still works — its id becomes the label.</para>
///
/// <para>Requirements is a <see cref="string"/>[] because an attribute's named arguments
/// cannot be an interface-typed collection.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DjStrategyDescriptorAttribute : Attribute
{
    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string[] Requirements { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Overrides the reflective lookup for a strategy type.
    /// </summary>
    public static DjStrategyDescriptorAttribute? For(Type type)
        => type.GetCustomAttributes(typeof(DjStrategyDescriptorAttribute), inherit: false)
            .OfType<DjStrategyDescriptorAttribute>()
            .FirstOrDefault();
}
