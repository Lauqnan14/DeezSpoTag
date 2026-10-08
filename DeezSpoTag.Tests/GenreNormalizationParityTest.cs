using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Proves that Genre Intelligence preprocesses its input through the one shared
/// Genre Normalization authority, and not through a rule set of its own.
///
/// <para>
/// The AutoTag provider stage sanitizes a value with
/// <c>SanitizeGenres</c>, which is a thin wrapper over
/// <see cref="GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues"/>.
/// Genre Intelligence now calls that same function with the same arguments. These
/// tests assert the resulting equivalence directly rather than asserting that both
/// sides mention the same class, because a shared class reference is not the same
/// thing as shared behaviour: a wrapper can still pass different inputs.
/// </para>
/// </summary>
public sealed class GenreNormalizationParityTest
{
    /// <summary>
    /// The exact call AutoTag's <c>SanitizeGenres</c> makes, reproduced here so
    /// the expected value cannot drift away from what AutoTag really does.
    /// </summary>
    private static List<string> AutoTagSanitizeGenres(
        IEnumerable<string> values,
        IReadOnlyDictionary<string, string> aliasMap,
        IReadOnlyList<string> blockList,
        bool splitComposite)
        => GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            values,
            aliasMap,
            splitComposite,
            blockList);

    private static GenreNormalizationSnapshot Snapshot(
        bool enabled = true,
        IEnumerable<PersonalGenreAliasRule>? rules = null,
        IEnumerable<string>? blocked = null)
        => GenreNormalizationSnapshot.Create(
            enabled,
            rules?.ToArray(),
            blocked?.ToArray());

    private static GenreSemanticSnapshot SnapshotOf(params (string Value, PersonalGenreTaxonKind Field)[] values)
    {
        var perField = new Dictionary<PersonalGenreTaxonKind, int>();
        var observations = new List<GenreTagObservation>();
        foreach (var (value, field) in values)
        {
            perField.TryGetValue(field, out var order);
            perField[field] = order + 1;
            observations.Add(new GenreTagObservation(value, field, order));
        }

        return new GenreSemanticSnapshot(observations, DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// The core parity guarantee: for the same input values and the same saved
    /// preferences, AutoTag's preprocessing and Genre Intelligence's preprocessing
    /// produce exactly the same surviving values in the same order.
    /// </summary>
    [Theory]
    [InlineData(true, new[] { "Rap/HipHop" }, new string[0])]
    [InlineData(true, new[] { "HipHop/Rap" }, new string[0])]
    [InlineData(true, new[] { "Rap/HipHop", "HipHop/Rap" }, new string[0])]
    [InlineData(true, new[] { "other" }, new[] { "other" })]
    [InlineData(true, new[] { "others" }, new[] { "others" })]
    [InlineData(true, new[] { "Worldwide" }, new[] { "Worldwide" })]
    [InlineData(true, new[] { "My Own Genre", "Pop" }, new[] { "My Own Genre" })]
    [InlineData(false, new[] { "Rap/HipHop" }, new string[0])]
    [InlineData(false, new[] { "other", "others", "Worldwide" }, new[] { "other", "others", "Worldwide" })]
    [InlineData(true, new[] { "Pop", "pop", "POP" }, new string[0])]
    [InlineData(true, new[] { "Afro Pop", "Afro-Pop", "Afropop" }, new string[0])]
    public void GenreIntelligencePreprocessing_MatchesAutoTagExactly(
        bool normalizationEnabled,
        string[] input,
        string[] blocked)
    {
        var snapshot = Snapshot(normalizationEnabled, blocked: blocked);
        var preprocessed = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(input.Select(value => (value, PersonalGenreTaxonKind.Genre)).ToArray()),
            snapshot);

        var actual = preprocessed.Observations
            .Where(item => item.InputField == PersonalGenreTaxonKind.Genre)
            .Select(item => item.RawValue)
            .ToList();

        // AutoTag splits composites only while the toggle is on, and applies the
        // block list whether or not it is on. Both are reproduced explicitly.
        var expected = AutoTagSanitizeGenres(
            input,
            snapshot.AliasMap,
            snapshot.BlockList,
            splitComposite: normalizationEnabled);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The block list has to apply to Genre Intelligence even when the normalize
    /// toggle is off, because that is the behaviour the shared authority already
    /// has and AutoTag depends on it.
    /// </summary>
    [Fact]
    public void TheBlockListAppliesWhileNormalizationIsOff()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(("Worldwide", PersonalGenreTaxonKind.Genre), ("Pop", PersonalGenreTaxonKind.Genre)),
            Snapshot(enabled: false, blocked: ["Worldwide"]));

        Assert.Equal(["Pop"], result.Observations.Select(item => item.RawValue));
        Assert.Equal("Worldwide", Assert.Single(result.Removed).Value);
    }

    /// <summary>
    /// A removed value is reported with a reason naming the block list, so the
    /// decision trail can explain the removal instead of leaving a silent gap.
    /// </summary>
    [Fact]
    public void ABlockedValueIsReportedWithAReasonThatNamesTheBlockList()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(("Worldwide", PersonalGenreTaxonKind.Genre)),
            Snapshot(blocked: ["Worldwide"]));

        var removed = Assert.Single(result.Removed);
        Assert.Equal("Worldwide", removed.Value);
        Assert.Equal(PersonalGenreTaxonKind.Genre, removed.InputField);
        Assert.Contains("block list", removed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A user-created alias preference is honoured, and the value the file held is
    /// still reported so the run can say what changed.
    /// </summary>
    [Fact]
    public void AUserAliasPreferenceIsAppliedAndTheOriginalSpellingIsKept()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(("hip hop", PersonalGenreTaxonKind.Genre)),
            Snapshot(rules: [new PersonalGenreAliasRule("hip hop", "Hip-Hop")]));

        var observation = Assert.Single(result.Observations);
        Assert.Equal("Hip-Hop", observation.RawValue);
        Assert.Equal("hip hop", observation.OriginalValue);
        Assert.True(observation.WasNormalized);
    }

    /// <summary>
    /// The same user preference must produce the same result as an equal AutoTag
    /// run, including the order the values come out in.
    /// </summary>
    [Fact]
    public void AConfiguredUserAliasProducesTheSameResultAsAutoTag()
    {
        var snapshot = Snapshot(rules: [new PersonalGenreAliasRule("Afro Pop", "Afropop")]);
        var input = new[] { "Afro Pop", "Rock", "afro pop" };

        var actual = GenreNormalizationPreprocessor
            .Apply(SnapshotOf(input.Select(value => (value, PersonalGenreTaxonKind.Genre)).ToArray()), snapshot)
            .Observations
            .Where(item => item.InputField == PersonalGenreTaxonKind.Genre)
            .Select(item => item.RawValue)
            .ToList();

        Assert.Equal(AutoTagSanitizeGenres(input, snapshot.AliasMap, snapshot.BlockList, true), actual);
        Assert.Contains("Afropop", actual);
    }

    /// <summary>
    /// The user's preferred spelling is applied to every semantic field, not only
    /// to Genre, because a spelling preference should not depend on which field a
    /// value happened to be written into.
    /// </summary>
    [Theory]
    [InlineData(PersonalGenreTaxonKind.Genre)]
    [InlineData(PersonalGenreTaxonKind.Style)]
    [InlineData(PersonalGenreTaxonKind.Substyle)]
    [InlineData(PersonalGenreTaxonKind.Context)]
    [InlineData(PersonalGenreTaxonKind.Scene)]
    [InlineData(PersonalGenreTaxonKind.Language)]
    public void TheAliasPreferenceAppliesToEverySemanticField(PersonalGenreTaxonKind field)
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(("afro pop", field)),
            Snapshot(rules: [new PersonalGenreAliasRule("Afro Pop", "Afropop")]));

        Assert.Equal("Afropop", Assert.Single(result.Observations).RawValue);
    }

    /// <summary>
    /// The genre block list is a genre block list. Applying it to STYLE would be new
    /// behaviour that the shared authority never had, so it is deliberately not
    /// applied there.
    /// </summary>
    [Fact]
    public void TheGenreBlockListIsNotAppliedToNonGenreFields()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(("Worldwide", PersonalGenreTaxonKind.Style)),
            Snapshot(blocked: ["Worldwide"]));

        Assert.Empty(result.Removed);
        Assert.Equal("Worldwide", Assert.Single(result.Observations).RawValue);
    }

    /// <summary>
    /// A value the taxonomy has never heard of survives preprocessing untouched.
    /// Losing it here would lose it before classification ever got to preserve it.
    /// </summary>
    [Fact]
    public void AnUnknownValueSurvivesPreprocessingUnchanged()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(("obscure-new-scene", PersonalGenreTaxonKind.Genre)),
            Snapshot(blocked: ["Worldwide"]));

        var observation = Assert.Single(result.Observations);
        Assert.Equal("obscure-new-scene", observation.RawValue);
        Assert.False(observation.WasNormalized);
        Assert.Empty(result.Removed);
    }

    /// <summary>
    /// A field the resolver does not understand is passed through rather than
    /// silently dropped.
    /// </summary>
    [Fact]
    public void AnUnrecognisedFieldIsPassedThroughRatherThanDropped()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(("anything", (PersonalGenreTaxonKind)999)),
            Snapshot(blocked: ["anything"]));

        Assert.Equal("anything", Assert.Single(result.Observations).RawValue);
    }

    /// <summary>
    /// A value the preferences rewrite must not also be kept under its old
    /// spelling, or the file would end up holding both.
    /// </summary>
    [Fact]
    public void AnAliasAndItsCanonicalFormCollapseToOneValue()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(
                ("Afro Pop", PersonalGenreTaxonKind.Genre),
                ("Afropop", PersonalGenreTaxonKind.Genre)),
            Snapshot(rules: [new PersonalGenreAliasRule("Afro Pop", "Afropop")]));

        var observation = Assert.Single(result.Observations);
        Assert.Equal("Afropop", observation.RawValue);
    }

    /// <summary>
    /// A blocked value must not come back through carry-forward. The resolver keeps
    /// unrecognised pre-AutoTag values so a user's own tagging survives, and a
    /// blocked value is unrecognised, so without this guard the block would be
    /// silently undone.
    /// </summary>
    [Fact]
    public void ABlockedValueIsNotResurrectedByCarryForward()
    {
        var removed = new List<RemovedGenreTagValue>
        {
            new("Worldwide", PersonalGenreTaxonKind.Genre, "blocked by the saved block list")
        };
        var preSnapshot = SnapshotOf(("Worldwide", PersonalGenreTaxonKind.Genre));

        var result = GenreNormalizationPreprocessor.Apply(preSnapshot, Snapshot(blocked: ["Worldwide"]));

        var resolution = PersonalGenreResolver.Resolve(
            result.Observations,
            originalObservations: preSnapshot.Observations,
            settings: new PersonalGenreSettings(PreserveUnmappedTags: true),
            removedByNormalization: removed);

        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Styles);
        Assert.Empty(resolution.Preserved);
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("blocked", decision.Outcome);
        Assert.Equal("Worldwide", decision.RawValue);
    }

    /// <summary>
    /// The block list is applied whichever way the preferences were configured, so
    /// a user who never touched the shipped defaults still gets the defaults.
    /// </summary>
    [Fact]
    public void TheShippedDefaultBlockListAppliesWithoutAnyUserConfiguration()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            SnapshotOf(
                ("other", PersonalGenreTaxonKind.Genre),
                ("others", PersonalGenreTaxonKind.Genre),
                ("Worldwide", PersonalGenreTaxonKind.Genre),
                ("Pop", PersonalGenreTaxonKind.Genre)),
            null);

        Assert.Equal(["Pop"], result.Observations.Select(item => item.RawValue));
        Assert.Equal(3, result.Removed.Count);
    }
}
