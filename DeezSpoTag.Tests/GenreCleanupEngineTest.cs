using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers what Genre Cleanup decides about the genre tags already present in a
/// file: recognition, alias reconciliation, cross-field correction, explicit
/// exclusion, unknown preservation, idempotence, and the absence of any
/// Style Construction.
/// </summary>
public sealed class GenreCleanupEngineTest
{
    private static PersonalGenreResolution Resolve(
        params (string Value, PersonalGenreTaxonKind Field)[] values)
        => PersonalGenreResolver.Resolve(
            values.Select((item, index) =>
                new GenreTagObservation(item.Value, item.Field, index)).ToArray());

    // ------------------------------------------------------------------ canonical recognition

    [Fact]
    public void AKnownGenreStaysAGenre()
    {
        var resolution = Resolve(("Hip Hop", PersonalGenreTaxonKind.Genre));

        Assert.Equal("Hip-Hop", resolution.PrimaryGenre);
        Assert.Equal(["Hip-Hop"], resolution.Genres);
    }

    [Fact]
    public void AKnownStyleStaysAStyle()
    {
        var resolution = Resolve(("Kenyan Drill", PersonalGenreTaxonKind.Style));

        Assert.Equal(["Kenyan Drill"], resolution.Styles);
        Assert.Empty(resolution.Genres);
    }

    /// <summary>
    /// A Style written into GENRE is moved to Style, and a Genre written into STYLE
    /// is moved to Genre. The taxonomy decides the musical type; the field the value
    /// happened to arrive in is only evidence of where it came from.
    /// </summary>
    [Theory]
    [InlineData("Kenyan Drill", PersonalGenreTaxonKind.Genre, "Kenyan Drill", true)]
    [InlineData("Bongo Flava", PersonalGenreTaxonKind.Style, "Bongo Flava", false)]
    [InlineData("Afrosounds", PersonalGenreTaxonKind.Genre, "Afrosounds", false)]
    public void AValueIsClassifiedByTheTaxonomyRatherThanByTheFieldItSatIn(
        string value,
        PersonalGenreTaxonKind field,
        string expected,
        bool expectStyle)
    {
        var resolution = Resolve((value, field));

        var decision = Assert.Single(resolution.Decisions, item => item.RawValue == value);
        Assert.Equal(expected, decision.CanonicalValue);
        Assert.Equal(field, decision.InputField);

        if (expectStyle)
        {
            Assert.Contains(expected, resolution.Styles);
            Assert.DoesNotContain(expected, resolution.Genres);
        }
        else
        {
            Assert.DoesNotContain(expected, resolution.Styles);
            Assert.NotEmpty(resolution.Genres.Concat(resolution.Contexts));
        }
    }

    // ------------------------------------------------------------------ alias reconciliation

    [Theory]
    [InlineData("hindie", "Indian Indie")]
    [InlineData("k-hiphop", "Korean Hip-Hop")]
    [InlineData("neo-swing", "swing revival")]
    [InlineData("powernoise", "power noise")]
    [InlineData("desi hip-hop", "Desi Hip Hop")]
    public void AResearchedAliasResolvesToOneCanonicalValue(string alias, string canonical)
    {
        var resolution = Resolve((alias, PersonalGenreTaxonKind.Genre));

        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal(canonical, decision.CanonicalValue);
        Assert.Empty(resolution.Preserved);
        Assert.DoesNotContain(alias, resolution.Genres.Concat(resolution.Styles));
    }

    [Fact]
    public void AnAliasAndItsCanonicalFormProduceOneValue()
    {
        var resolution = Resolve(
            ("hindie", PersonalGenreTaxonKind.Genre),
            ("Indian Indie", PersonalGenreTaxonKind.Genre));

        Assert.Single(resolution.Styles);
        Assert.Empty(resolution.Preserved);
    }

    // ------------------------------------------------------------------ deduplication

    [Fact]
    public void DifferentSpellingsOfOneTermProduceOneValue()
    {
        var resolution = Resolve(
            ("Hip-Hop", PersonalGenreTaxonKind.Genre),
            ("hip hop", PersonalGenreTaxonKind.Genre),
            ("HipHop", PersonalGenreTaxonKind.Genre));

        Assert.Single(resolution.Genres);
    }

    [Fact]
    public void AValueInBothFieldsIsClassifiedOnceAndWrittenToItsRealDimension()
    {
        var resolution = Resolve(
            ("Kenyan Drill", PersonalGenreTaxonKind.Genre),
            ("Kenyan Drill", PersonalGenreTaxonKind.Style));

        Assert.Single(resolution.Styles);
        Assert.Empty(resolution.Genres);
        var classification = Assert.Single(resolution.Classifications, item => item.Name == "Kenyan Drill");
        Assert.Equal(
            [PersonalGenreTaxonKind.Genre, PersonalGenreTaxonKind.Style],
            classification.OriginFields);
    }

    // ------------------------------------------------------------------ exclusions and ambiguity

    [Fact]
    public void AResearchedNonMusicalValueIsExcludedFromGenreAndStyleButStillRecorded()
    {
        var resolution = Resolve(("Bosstown Sound", PersonalGenreTaxonKind.Genre));

        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Styles);

        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("preserved_excluded", decision.Outcome);
        Assert.Contains("excludes this value", decision.Reason, StringComparison.OrdinalIgnoreCase);

        // The fact that the value existed is not erased.
        Assert.Equal("Bosstown Sound", Assert.Single(resolution.Preserved).Value);
    }

    [Fact]
    public void AResearchedAmbiguousValueIsNotForcedIntoEitherDimension()
    {
        var resolution = Resolve(("Terror EBM", PersonalGenreTaxonKind.Genre));

        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Styles);

        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("preserved_unmapped", decision.Outcome);
        Assert.Contains("has not established", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ precedence

    /// <summary>
    /// A blocked value is removed by the shared authority during preprocessing, so
    /// no later stage can rescue it. A user rule pointing at it must have no effect,
    /// which is the only safe reading: the user's own block list is the last word.
    /// </summary>
    [Fact]
    public void ABlockedValueIsNotResurrectedByAUserRule()
    {
        var snapshot = GenreNormalizationSnapshot.Create(true, null, ["Worldwide"]);
        var preprocessed = GenreNormalizationPreprocessor.Apply(
            new GenreSemanticSnapshot(
                [new GenreTagObservation("Worldwide", PersonalGenreTaxonKind.Genre, 0)],
                DateTimeOffset.UnixEpoch),
            snapshot);

        Assert.Empty(preprocessed.Observations);

        var resolution = PersonalGenreResolver.Resolve(
            preprocessed.Observations,
            rules: [new PersonalGenreRule(1, "Worldwide", "hip-hop")],
            removedByNormalization: preprocessed.Removed);

        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Styles);
        Assert.Empty(resolution.Preserved);

        // The rule was never consulted: the only decision is the removal.
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("blocked", decision.Outcome);
        Assert.Empty(resolution.AppliedRuleIds);
    }

    /// <summary>
    /// A mapping cannot bring a blocked value back, and a lock cannot either.
    /// </summary>
    /// <remarks>
    /// The lock is a separate user instruction about the track rather than about the
    /// blocked value, so it is entitled to do its own job. What must never happen is
    /// the blocked value reappearing, which is why the assertion is about that value
    /// and not about the presence of the locked taxon.
    /// </remarks>
    [Fact]
    public void ABlockedValueIsNotResurrectedByAMappingOrALock()
    {
        var snapshot = GenreNormalizationSnapshot.Create(true, null, ["Worldwide"]);
        var preprocessed = GenreNormalizationPreprocessor.Apply(
            new GenreSemanticSnapshot(
                [new GenreTagObservation("Worldwide", PersonalGenreTaxonKind.Genre, 0)],
                DateTimeOffset.UnixEpoch),
            snapshot);

        var resolution = PersonalGenreResolver.Resolve(
            preprocessed.Observations,
            mappings: [new PersonalGenreMapping(1, "Worldwide", "hip-hop")],
            locks: [new PersonalGenreLock(1, "hip-hop")],
            removedByNormalization: preprocessed.Removed);

        // The mapping had nothing to act on, so it was never applied.
        Assert.Empty(resolution.AppliedRuleIds);
        Assert.DoesNotContain(
            resolution.Genres.Concat(resolution.Styles),
            value => value.Contains("Worldwide", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            resolution.Preserved,
            item => item.Value.Contains("Worldwide", StringComparison.OrdinalIgnoreCase));

        // The only decision is the removal, recorded so the gap is explainable.
        Assert.Equal("blocked", Assert.Single(resolution.Decisions).Outcome);
    }

    /// <summary>
    /// The documented order is preprocess, then classify, then post-classify. This
    /// pins that the shared preprocessing really is outside the resolver: the
    /// resolver receives observations that are already normalized, and its only
    /// normalization-shaped input is a report of what was removed, not a rule set it
    /// could apply itself.
    /// </summary>
    [Fact]
    public void TheResolverHoldsNoNormalizationRulesOfItsOwn()
    {
        var resolve = typeof(PersonalGenreResolver)
            .GetMethod(nameof(PersonalGenreResolver.Resolve))!;

        var parameters = resolve.GetParameters().ToArray();
        Assert.Contains(parameters, parameter => parameter.Name == "observations");

        // The only rule-shaped inputs are the user's own configuration. There is no
        // alias map, no block list and no composite-splitting flag anywhere in the
        // signature, so the resolver cannot form a second opinion about what the
        // shared authority decided.
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(IReadOnlyDictionary<string, string>)
                         || (parameter.ParameterType == typeof(PersonalGenreSettings)
                             && parameter.Name != "settings"));

        // And the removal parameter is a report, not a rule set.
        var removals = Assert.Single(parameters, parameter => parameter.Name == "removedByNormalization");
        var removalType = removals.ParameterType.GetGenericArguments()[0];
        Assert.Equal(typeof(RemovedGenreTagValue), removalType);

        // It carries a value and a reason, and nothing that could re-admit a value.
        Assert.DoesNotContain(
            typeof(RemovedGenreTagValue).GetProperties(),
            property => property.PropertyType == typeof(IReadOnlyDictionary<string, string>));
    }

    // ------------------------------------------------------------------ unknown preservation

    /// <summary>
    /// Unknown is not invalid. A term the research has not covered survives, because
    /// the vocabulary being incomplete must never destroy a user's own tagging.
    /// </summary>
    [Theory]
    [InlineData("obscure-new-scene")]
    [InlineData("My Personal Genre")]
    [InlineData("zzz-not-researched")]
    public void AnUnknownValueSurvivesUnchangedInItsOriginalField(string value)
    {
        var resolution = Resolve((value, PersonalGenreTaxonKind.Style));

        var preserved = Assert.Single(resolution.Preserved);
        Assert.Equal(value, preserved.Value);
        Assert.Equal(PersonalGenreTaxonKind.Style, preserved.InputField);
        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Styles);
    }

    [Fact]
    public void ABlockedValueIsRemovedRatherThanPreserved()
    {
        var snapshot = GenreNormalizationSnapshot.Create(true, null, ["Worldwide"]);
        var preprocessed = GenreNormalizationPreprocessor.Apply(
            new GenreSemanticSnapshot(
                [
                    new GenreTagObservation("Worldwide", PersonalGenreTaxonKind.Genre, 0),
                    new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 1)
                ],
                DateTimeOffset.UnixEpoch),
            snapshot);

        var resolution = PersonalGenreResolver.Resolve(
            preprocessed.Observations,
            removedByNormalization: preprocessed.Removed);

        Assert.Equal(["Hip-Hop"], resolution.Genres);
        Assert.Empty(resolution.Preserved);
        Assert.Equal("Worldwide", Assert.Single(resolution.Decisions, item => item.Outcome == "blocked").RawValue);
    }

    // ------------------------------------------------------------------ no Style Construction

    /// <summary>
    /// Genre Cleanup must not synthesise a Style. Location, era and label are
    /// preserved for a later phase and are never combined into a new term here.
    /// </para>
    /// <remarks>
    /// "Kenyan Rap" and "Asakaa" are both researched Styles, so this is the case
    /// that would be easiest to get wrong: the vocabulary contains the term, and the
    /// evidence is present, and the answer must still be no.
    /// </remarks>
    [Theory]
    [InlineData("Hip Hop", "Ghana", "Asakaa")]
    [InlineData("Rap", "Kenya", "Kenyan Rap")]
    [InlineData("Hip-Hop", "Nigeria", "Asakaa")]
    public void NoStyleIsInferredFromALocationAndABroadGenre(
        string genre,
        string country,
        string forbiddenStyle)
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation(genre, PersonalGenreTaxonKind.Genre)],
            artistLocations: [new GenreArtistLocationContext(country, null, null, IsMainArtist: true)]);

        Assert.DoesNotContain(
            forbiddenStyle,
            resolution.Styles.Concat(resolution.Substyles).Concat(resolution.Contexts),
            StringComparer.OrdinalIgnoreCase);

        // The location is audited, never turned into a term.
        Assert.All(
            resolution.Decisions,
            decision => Assert.Contains("Location context", decision.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARegionalStyleAlreadyInTheFileIsKeptRatherThanInvented()
    {
        // The distinction is that the value was already there. Cleanup keeps it; it
        // never creates one.
        var resolution = Resolve(("Kenyan Rap", PersonalGenreTaxonKind.Genre));

        Assert.Equal(["Kenyan Rap"], resolution.Styles);
        Assert.Equal("canonical_match", Assert.Single(resolution.Decisions).Outcome);
    }

    // ------------------------------------------------------------------ idempotence

    /// <summary>
    /// Cleanup(Cleanup(file)) == Cleanup(file). A second pass over an already-clean
    /// file has to produce the same result, or repeated AutoTag runs would keep
    /// changing a library.
    /// </summary>
    [Fact]
    public void RunningCleanupTwiceProducesAnIdenticalResult()
    {
        var first = Resolve(
            ("hindie", PersonalGenreTaxonKind.Genre),
            ("Kenyan Drill", PersonalGenreTaxonKind.Genre),
            ("Bongo Flava", PersonalGenreTaxonKind.Style),
            ("obscure-new-scene", PersonalGenreTaxonKind.Style));

        // Feed the first result back in as the file's new state.
        var second = PersonalGenreResolver.Resolve(
            first.Genres.Select(value => new GenreTagObservation(value, PersonalGenreTaxonKind.Genre))
                .Concat(first.Styles.Select(value => new GenreTagObservation(value, PersonalGenreTaxonKind.Style)))
                .Concat(first.Preserved.Select(item => new GenreTagObservation(
                    item.Value, item.InputField, item.Order, item.Origin)))
                .ToArray());

        Assert.Equal(first.Genres, second.Genres);
        Assert.Equal(first.Styles, second.Styles);
        Assert.Equal(
            first.Preserved.Select(item => item.Value).OrderBy(value => value, StringComparer.Ordinal),
            second.Preserved.Select(item => item.Value).OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void CanonicalizationIsStableAcrossPasses()
    {
        var once = Resolve(("powernoise", PersonalGenreTaxonKind.Genre));
        var twice = Resolve((once.Styles.First(), PersonalGenreTaxonKind.Style));

        Assert.Equal(once.Styles, twice.Styles);
    }

    // ------------------------------------------------------------------ preview

    [Fact]
    public void ThePreviewReportsEveryProposedChange()
    {
        var observations = new[]
        {
            new GenreTagObservation("Worldwide", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Kenyan Drill", PersonalGenreTaxonKind.Genre, 1),
            new GenreTagObservation("Hip Hop", PersonalGenreTaxonKind.Genre, 2),
            new GenreTagObservation("obscure-new-scene", PersonalGenreTaxonKind.Style, 0)
        };
        var snapshot = GenreNormalizationSnapshot.Create(true, null, ["Worldwide"]);
        var preprocessed = GenreNormalizationPreprocessor.Apply(
            new GenreSemanticSnapshot(observations, DateTimeOffset.UnixEpoch),
            snapshot);

        var resolution = PersonalGenreResolver.Resolve(
            preprocessed.Observations,
            removedByNormalization: preprocessed.Removed);

        var preview = GenreCleanupPreviewBuilder.Build(resolution, observations, preprocessed.Removed);

        // Before and after for both dimensions. "Hip Hop" is written back as the
        // built-in display spelling "Hip-Hop", because a built-in term keeps its own
        // spelling even though the researched master is what recognises it.
        Assert.Equal(["Worldwide", "Kenyan Drill", "Hip Hop"], preview.BeforeGenres);
        Assert.Equal(["Hip-Hop"], preview.AfterGenres);
        // "Kenyan Drill" was in GENRE and is a researched Style, so it leaves Genre
        // and arrives in Style. STYLE also keeps the value the vocabulary does not
        // know, because the preview reports what the field will actually hold.
        Assert.Equal(["obscure-new-scene"], preview.BeforeStyles);
        Assert.Equal(["Kenyan Drill", "obscure-new-scene"], preview.AfterStyles);

        Assert.Contains(preview.Removed, item => item.StartsWith("Worldwide", StringComparison.Ordinal));
        Assert.Contains(preview.Moved, item => item.Contains("Kenyan Drill", StringComparison.Ordinal));
        Assert.Contains(preview.PreservedUnknown, item => item.Contains("obscure-new-scene", StringComparison.Ordinal));
        Assert.True(preview.HasChanges);
        Assert.True(preview.WouldWrite);
        Assert.False(string.IsNullOrWhiteSpace(preview.CatalogVersion));

        // Every value that was read has a decision, so nothing disappears silently.
        Assert.Equal(4, preview.Decisions.Count);
    }

    [Fact]
    public void AnAlreadyCleanFileReportsNoChanges()
    {
        var observations = new[]
        {
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0)
        };
        var resolution = PersonalGenreResolver.Resolve(observations);
        var preview = GenreCleanupPreviewBuilder.Build(resolution, observations, []);

        Assert.False(preview.HasChanges);
        Assert.False(preview.WouldWrite);
        Assert.Empty(preview.Removed);
        Assert.Empty(preview.Moved);
    }

    [Fact]
    public void BuildingAPreviewHasNoSideEffectOnTheResolution()
    {
        var observations = new[]
        {
            new GenreTagObservation("hindie", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("obscure", PersonalGenreTaxonKind.Style, 0)
        };
        var resolution = PersonalGenreResolver.Resolve(observations);

        var first = GenreCleanupPreviewBuilder.Build(resolution, observations, []);
        var second = GenreCleanupPreviewBuilder.Build(resolution, observations, []);

        Assert.Equal(first.Decisions.Count, second.Decisions.Count);
        Assert.Equal(resolution.Genres, second.AfterGenres);
    }

    // ------------------------------------------------------------------ decision precedence

    /// <summary>
    /// A user rule outranks the researched vocabulary, so an explicit instruction is
    /// never overruled by the catalog.
    /// </summary>
    [Fact]
    public void AUserRuleOutranksTheResearchedClassification()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Kenyan Drill", PersonalGenreTaxonKind.Genre)],
            rules: [new PersonalGenreRule(1, "Kenyan Drill", "hip-hop")]);

        Assert.Equal(["Hip-Hop"], resolution.Genres);
        Assert.Equal("rule_applied", Assert.Single(resolution.Decisions).Outcome);
    }

    /// <summary>
    /// A user mapping outranks the researched vocabulary too, including a mapping
    /// that contradicts what the research says the value is.
    /// </summary>
    [Fact]
    public void AUserMappingOutranksTheResearchedClassification()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Kenyan Drill", PersonalGenreTaxonKind.Genre)],
            mappings: [new PersonalGenreMapping(1, "Kenyan Drill", "hip-hop")]);

        Assert.Equal(["Hip-Hop"], resolution.Genres);
        Assert.Empty(resolution.Styles);
    }

    [Fact]
    public void AnIgnoredValueIsDroppedEvenThoughTheResearchRecognisesIt()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Kenyan Drill", PersonalGenreTaxonKind.Genre)],
            mappings:
            [
                new PersonalGenreMapping(
                    1, "Kenyan Drill", "kenyan-drill", Action: PersonalGenreMappingAction.Ignore)
            ]);

        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Styles);
        Assert.Empty(resolution.Preserved);
        Assert.Equal("ignored", Assert.Single(resolution.Decisions).Outcome);
    }

    /// <summary>
    /// A user's own term outranks the researched vocabulary for the same reason.
    /// </summary>
    [Fact]
    public void AUserTermOutranksTheResearchedClassification()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Testville Drill", PersonalGenreTaxonKind.Genre)],
            customTaxa:
            [
                new PersonalGenreTaxon("testville-drill-local", "Testville Drill", PersonalGenreTaxonKind.Genre)
            ]);

        Assert.Equal(["Testville Drill"], resolution.Genres);
        Assert.Equal("personal_taxonomy_match", Assert.Single(resolution.Decisions).Outcome);
    }

    // ------------------------------------------------------------------ compatibility

    /// <summary>
    /// The researched vocabulary extends the built-in one; it does not replace the
    /// dimensions the research does not cover.
    /// </summary>
    [Theory]
    [InlineData("Afrosounds", PersonalGenreTaxonKind.Context)]
    [InlineData("Kenya", PersonalGenreTaxonKind.Context)]
    [InlineData("Swahili", PersonalGenreTaxonKind.Language)]
    [InlineData("Zilizopendwa", PersonalGenreTaxonKind.Scene)]
    public void TheBuiltInNonGenreDimensionsStillResolve(string value, PersonalGenreTaxonKind expected)
    {
        var resolution = Resolve((value, PersonalGenreTaxonKind.Genre));

        var classification = Assert.Single(resolution.Classifications, item => item.Name == value);
        Assert.Equal(expected, classification.Kind);
    }

    /// <summary>
    /// A saved lock refers to a built-in id, and it must still resolve after the
    /// researched vocabulary was merged in.
    /// </summary>
    [Fact]
    public void ABuiltInIdStillResolvesSoSavedLocksKeepWorking()
    {
        var catalog = new PersonalGenreCatalog();

        foreach (var id in new[] { "hip-hop", "bongo-flava", "afrosounds", "kenyan-drill", "swahili" })
        {
            Assert.True(catalog.TryGetById(id, out var taxon), $"built-in id '{id}' must still resolve.");
            Assert.NotNull(taxon);
        }
    }
}
