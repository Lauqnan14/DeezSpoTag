using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Pins the file-source behaviour of the Personal Genre resolver.
///
/// The resolver no longer receives provider-labelled evidence: it receives the
/// values Genre Intelligence read out of an audio file, and the field each came
/// from. These tests therefore describe file contents, not source weights.
/// </summary>
public sealed class PersonalGenreResolverTest
{
    [Fact]
    public void BongoFlava_IsCanonicalGenre_NotHipHopOrAfropop()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Bongo-Flava", PersonalGenreTaxonKind.Genre)
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.Contains("Bongo Flava", resolution.Genres);
        Assert.DoesNotContain("Hip-Hop", resolution.Genres);
        Assert.DoesNotContain("Afropop", resolution.Genres);
    }

    [Fact]
    public void Afrosounds_IsContextOnly_NeverFinalGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Afrosounds", PersonalGenreTaxonKind.Genre),
            new GenreTagObservation("Amapiano", PersonalGenreTaxonKind.Style)
        ]);

        Assert.Equal("Amapiano", resolution.PrimaryGenre);
        Assert.Contains("Afrosounds", resolution.Contexts);
        Assert.DoesNotContain("Afrosounds", resolution.Genres);
    }

    [Fact]
    public void Afrobeat_And_Afrobeats_RemainDistinct()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Afrobeat", PersonalGenreTaxonKind.Genre),
            new GenreTagObservation("Afrobeats", PersonalGenreTaxonKind.Genre)
        ]);

        Assert.Contains("Afrobeat", resolution.Genres);
        Assert.Contains("Afrobeats", resolution.Genres);
        Assert.NotEqual(
            PersonalGenreTaxonomy.Normalize("Afrobeat"),
            PersonalGenreTaxonomy.Normalize("Afrobeats"));
    }

    [Fact]
    public void Afropop_IsStyle_WithMultipleParents()
    {
        Assert.True(PersonalGenreTaxonomy.TryGetById("afropop", out var taxon));
        Assert.Equal(PersonalGenreTaxonKind.Style, taxon.Kind);
        Assert.Contains("afrobeats", taxon.ParentIds!);
        Assert.Contains("pop", taxon.ParentIds!);
    }

    [Fact]
    public void UserRule_WinsOverBuiltInTaxonomy()
    {
        var rules = new[]
        {
            new PersonalGenreRule(42, "Amapiano", "house", Priority: 5000, Enabled: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Amapiano", PersonalGenreTaxonKind.Genre)
        ],
        rules: rules);

        Assert.Equal("House", resolution.PrimaryGenre);
        Assert.Contains("42", resolution.AppliedRuleIds);
        Assert.DoesNotContain("Amapiano", resolution.Genres);
    }

    [Fact]
    public void UserLock_ReplacesAutomaticClassificationForSameKind()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Amapiano", PersonalGenreTaxonKind.Genre)
        ],
        locks:
        [
            new PersonalGenreLock(99, "bongo-flava")
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.DoesNotContain("Amapiano", resolution.Genres);
        var locked = Assert.Single(resolution.Classifications.Where(item => item.TaxonId == "bongo-flava"));
        Assert.True(locked.UserLocked);
        Assert.Equal("locked", locked.Status);
    }

    /// <summary>
    /// A rule scoped to one file field must not fire for the same value read
    /// from a different field. This replaces the old provider-scoped rule test:
    /// field is a real input, provider identity is not.
    /// </summary>
    [Fact]
    public void FieldScopedRule_DoesNotApplyToTheSameValueInAnotherField()
    {
        var rules = new[]
        {
            new PersonalGenreRule(
                7,
                "Trap",
                "pop",
                InputField: PersonalGenreTaxonKind.Genre,
                Priority: 5000,
                Enabled: true)
        };

        var fromGenre = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Trap", PersonalGenreTaxonKind.Genre)],
            rules: rules);
        var fromStyle = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Trap", PersonalGenreTaxonKind.Style)],
            rules: rules);

        Assert.Equal("Pop", fromGenre.PrimaryGenre);
        Assert.Contains("Trap", fromStyle.Styles);
        Assert.DoesNotContain("Pop", fromStyle.Genres);
        Assert.Empty(fromStyle.AppliedRuleIds);
    }

    [Fact]
    public void UnscopedRule_AppliesInAnyField()
    {
        var rules = new[]
        {
            new PersonalGenreRule(8, "Trap", "pop", Priority: 5000, Enabled: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Trap", PersonalGenreTaxonKind.Style)],
            rules: rules);

        Assert.Equal("Pop", resolution.PrimaryGenre);
    }

    [Fact]
    public void ContextOnlyCustomTaxon_CannotBecomeGenreEvenWithWrongKind()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "regional-bucket",
                "Regional Bucket",
                PersonalGenreTaxonKind.Genre,
                ContextOnly: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Regional Bucket", PersonalGenreTaxonKind.Genre)],
            customTaxa: customTaxa);

        Assert.Null(resolution.PrimaryGenre);
        Assert.DoesNotContain("Regional Bucket", resolution.Genres);
        Assert.Contains("Regional Bucket", resolution.Contexts);
    }

    [Fact]
    public void Swahili_IsLanguage_NotContextOrGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Swahili", PersonalGenreTaxonKind.Language)]);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Contains("Swahili", resolution.Languages);
        Assert.DoesNotContain("Swahili", resolution.Contexts);
        Assert.DoesNotContain("Swahili", resolution.Genres);
    }

    [Fact]
    public void Zilizopendwa_IsScene_NotGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Zilizopendwa", PersonalGenreTaxonKind.Scene)]);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Contains("Zilizopendwa", resolution.Scenes);
        Assert.DoesNotContain("Zilizopendwa", resolution.Genres);
    }

    [Fact]
    public void CustomSceneAndLanguage_PreserveTheirDimensions()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon("dar-club-scene", "Dar Club Scene", PersonalGenreTaxonKind.Scene, ContextOnly: true),
            new PersonalGenreTaxon("luganda", "Luganda", PersonalGenreTaxonKind.Language, ContextOnly: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Dar Club Scene", PersonalGenreTaxonKind.Scene),
            new GenreTagObservation("Luganda", PersonalGenreTaxonKind.Language)
        ],
            customTaxa: customTaxa);

        Assert.Contains("Dar Club Scene", resolution.Scenes);
        Assert.Contains("Luganda", resolution.Languages);
        Assert.DoesNotContain("Dar Club Scene", resolution.Contexts);
        Assert.DoesNotContain("Luganda", resolution.Contexts);
    }

    [Fact]
    public void Context_NeverBecomesPrimaryGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Tanzania", PersonalGenreTaxonKind.Context),
            new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre)
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.Contains("Tanzania", resolution.Contexts);
        Assert.DoesNotContain("Tanzania", resolution.Genres);
    }

    [Fact]
    public void CustomTaxonAlias_ParticipatesInResolution()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "coastal-fusion-test",
                "Coastal Fusion Test",
                PersonalGenreTaxonKind.Genre,
                Aliases: ["Coastal Test Sound", "Coastal Fusion Alias"])
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Coastal Test Sound", PersonalGenreTaxonKind.Genre)],
            customTaxa: customTaxa);

        Assert.Equal("Coastal Fusion Test", resolution.PrimaryGenre);
        Assert.Contains("Coastal Fusion Test", resolution.Genres);
    }

    [Fact]
    public void CustomStyle_CanContributeBuiltInParentGenre()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "urban-fusion-test",
                "Urban Fusion Test",
                PersonalGenreTaxonKind.Style,
                ParentIds: ["hip-hop"])
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Urban Fusion Test", PersonalGenreTaxonKind.Style)],
            customTaxa: customTaxa,
            settings: new PersonalGenreSettings(IncludeParentGenres: true));

        Assert.Contains("Urban Fusion Test", resolution.Styles);
        Assert.Contains("Hip-Hop", resolution.Genres);
    }

    [Fact]
    public void UserLock_CanTargetCustomTaxon()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon("custom-locked-genre", "Custom Locked Genre", PersonalGenreTaxonKind.Genre)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Amapiano", PersonalGenreTaxonKind.Genre)],
            locks:
            [
                new PersonalGenreLock(99, "custom-locked-genre")
            ],
            customTaxa: customTaxa);

        Assert.Equal("Custom Locked Genre", resolution.PrimaryGenre);
        Assert.DoesNotContain("Amapiano", resolution.Genres);
    }

    [Fact]
    public void IgnoreMapping_SuppressesClassification()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(
                11,
                "Regional Pop Bucket",
                "pop",
                Action: PersonalGenreMappingAction.Ignore)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Regional Pop Bucket", PersonalGenreTaxonKind.Genre)],
            mappings: mappings);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("ignored", decision.Outcome);
        Assert.Null(decision.TaxonId);
    }

    [Fact]
    public void AmbiguousMapping_SuppressesClassification()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(
                12,
                "Urban",
                "hip-hop",
                InputField: PersonalGenreTaxonKind.Style,
                Action: PersonalGenreMappingAction.Ambiguous)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Urban", PersonalGenreTaxonKind.Style)],
            mappings: mappings);

        Assert.Null(resolution.PrimaryGenre);
        var decision = Assert.Single(resolution.Decisions);
        // An ambiguous value is one nothing could decide, so under the
        // preservation policy it is kept in the field it was read from rather
        // than being classified or dropped.
        Assert.Equal("preserved_unmapped", decision.Outcome);
        Assert.Equal("Urban", Assert.Single(resolution.Preserved).Value);
        Assert.Equal(PersonalGenreTaxonKind.Style, Assert.Single(resolution.Preserved).InputField);
    }

    [Fact]
    public void ContextOnlyMapping_CannotCreateFinalGenre()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(
                13,
                "East African",
                "bongo-flava",
                Action: PersonalGenreMappingAction.ContextOnly)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("East African", PersonalGenreTaxonKind.Genre)],
            mappings: mappings);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
        Assert.Contains("Bongo Flava", resolution.Contexts);
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("context_only", decision.Outcome);
        Assert.Equal("bongo-flava", decision.TaxonId);
    }

    [Fact]
    public void TrackLock_OutranksAlbumAndArtistLocksForSameKind()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Amapiano", PersonalGenreTaxonKind.Genre)],
            locks:
            [
                new PersonalGenreLock(99, "afrobeats", ScopeType: "artist", ScopeId: 1),
                new PersonalGenreLock(99, "hip-hop", ScopeType: "album", ScopeId: 2),
                new PersonalGenreLock(99, "bongo-flava", ScopeType: "track", ScopeId: 99)
            ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.DoesNotContain("Hip-Hop", resolution.Genres);
        Assert.DoesNotContain("Afrobeats", resolution.Genres);
        Assert.Equal("bongo-flava", Assert.Single(resolution.Classifications.Where(item => item.UserLocked)).TaxonId);
    }

    [Fact]
    public void AlbumLock_OutranksArtistLockWhenTrackLockIsAbsent()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Amapiano", PersonalGenreTaxonKind.Genre)],
            locks:
            [
                new PersonalGenreLock(99, "afrobeats", ScopeType: "artist", ScopeId: 1),
                new PersonalGenreLock(99, "hip-hop", ScopeType: "album", ScopeId: 2)
            ]);

        Assert.Equal("Hip-Hop", resolution.PrimaryGenre);
        Assert.DoesNotContain("Afrobeats", resolution.Genres);
        Assert.Equal("hip-hop", Assert.Single(resolution.Classifications.Where(item => item.UserLocked)).TaxonId);
    }

    [Fact]
    public void CustomGlobalGenre_ResolvesWithoutRegionalAssumptions()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "reggaeton",
                "Reggaeton",
                PersonalGenreTaxonKind.Genre,
                Aliases: ["Reggaetón", "Reggaeton Music"])
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Reggaetón", PersonalGenreTaxonKind.Genre)],
            customTaxa: customTaxa);

        Assert.Equal("Reggaeton", resolution.PrimaryGenre);
        Assert.DoesNotContain("Reggaeton", resolution.Contexts);
        Assert.DoesNotContain("Reggaeton", resolution.Styles);
    }

    // ---------------------------------------------------------------------
    // File-source behaviour
    // ---------------------------------------------------------------------

    /// <summary>GENRE = Bongo Flava resolves as a Genre.</summary>
    [Fact]
    public void FileGenreValue_ResolvesAsGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre)]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.Equal("Bongo Flava", Assert.Single(resolution.Genres));
    }

    /// <summary>
    /// A value that is really a Style can be corrected out of GENRE, which is the
    /// misclassification case the whole stage exists to fix.
    /// </summary>
    [Fact]
    public void ValueInWrongField_MovesToItsCorrectDimension()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Trap", PersonalGenreTaxonKind.Genre)]);

        Assert.Null(resolution.PrimaryGenre);
        Assert.DoesNotContain("Trap", resolution.Genres);
        Assert.Equal("Trap", Assert.Single(resolution.Styles));
        var trap = Assert.Single(resolution.Classifications.Where(item => item.TaxonId == "trap"));
        Assert.Contains(PersonalGenreTaxonKind.Genre, trap.OriginFields);
    }

    [Fact]
    public void MultipleFields_RemainDistinct()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre),
            new GenreTagObservation("Trap", PersonalGenreTaxonKind.Style)
        ]);

        Assert.Equal("Hip-Hop", resolution.PrimaryGenre);
        Assert.Equal("Hip-Hop", Assert.Single(resolution.Genres));
        Assert.Equal("Trap", Assert.Single(resolution.Styles));
    }

    [Fact]
    public void PersonalTaxonomyValue_InTheFile_ResolvesThroughTheCustomCatalog()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon("my-afro-mix", "My Afro Mix", PersonalGenreTaxonKind.Style, ParentIds: ["afro-fusion"])
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("My Afro Mix", PersonalGenreTaxonKind.Genre)],
            customTaxa: customTaxa);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Equal("My Afro Mix", Assert.Single(resolution.Styles));
    }

    [Fact]
    public void PersonalAlias_ResolvesToTheCustomTaxon()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "my-afro-mix",
                "My Afro Mix",
                PersonalGenreTaxonKind.Style,
                Aliases: ["Sunday Chill", "My Afro"])
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Sunday Chill", PersonalGenreTaxonKind.Genre)],
            customTaxa: customTaxa);

        Assert.Equal("My Afro Mix", Assert.Single(resolution.Styles));
    }

    [Fact]
    public void ArbitraryFileTag_IsMappedByUserConfiguration()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(1, "My Afro Mix", "afro-fusion", Priority: 500, Enabled: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("My Afro Mix", PersonalGenreTaxonKind.Genre)],
            mappings: mappings);

        Assert.Equal("Afro-Fusion", Assert.Single(resolution.Styles));
        Assert.Equal("mapping_applied", Assert.Single(resolution.Decisions).Outcome);
    }

    [Fact]
    public void UnmappedFileTag_IsPreservedWhenPreservationIsEnabled()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre)]);

        var preserved = Assert.Single(resolution.Preserved);
        Assert.Equal("My Personal Genre", preserved.Value);
        Assert.Equal(PersonalGenreTaxonKind.Genre, preserved.InputField);
        Assert.Null(resolution.PrimaryGenre);
    }

    [Fact]
    public void UnmappedFileTag_IsDroppedWhenPreservationIsDisabled()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre)],
            settings: new PersonalGenreSettings(PreserveUnmappedTags: false));

        Assert.Empty(resolution.Preserved);
    }

    /// <summary>
    /// A preserved value keeps the field it was read from, so the writer can put
    /// it back exactly where the user had it.
    /// </summary>
    [Fact]
    public void PreservedValue_KeepsItsOriginalField()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Local Fusion", PersonalGenreTaxonKind.Substyle)]);

        var preserved = Assert.Single(resolution.Preserved);
        Assert.Equal(PersonalGenreTaxonKind.Substyle, preserved.InputField);
    }

    /// <summary>
    /// The defining property of the file-source architecture: the resolver cannot
    /// see, and therefore cannot care about, which platform produced a value. The
    /// same file contents resolve identically regardless.
    /// </summary>
    [Fact]
    public void ResolutionIsIndependentOfAnyProviderIdentity()
    {
        var observations = new[]
        {
            new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre),
            new GenreTagObservation("Trap", PersonalGenreTaxonKind.Style),
            new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre)
        };

        var first = PersonalGenreResolver.Resolve(observations);
        var second = PersonalGenreResolver.Resolve(observations.ToList());

        Assert.Equal(first.Genres, second.Genres);
        Assert.Equal(first.Styles, second.Styles);
        Assert.Equal(first.PrimaryGenre, second.PrimaryGenre);
        Assert.Equal(
            first.Preserved.Select(item => item.Value),
            second.Preserved.Select(item => item.Value));
    }

    [Fact]
    public void ResolutionDoesNotDependOnVibeOrAnalysisState()
    {
        // The resolver has no analysis parameter at all. A file's tags are
        // sufficient, which is what makes the track page able to re-resolve
        // without re-running anything.
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre)]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.Equal(PersonalGenreResolver.Version, resolution.ResolverVersion);
    }

    [Fact]
    public void FileOrder_DeterminesOutputOrder()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Reggae", PersonalGenreTaxonKind.Genre, 1),
            new GenreTagObservation("Pop", PersonalGenreTaxonKind.Genre, 2)
        ]);

        Assert.Equal(new[] { "Hip-Hop", "Reggae", "Pop" }, resolution.Genres);
    }

    [Fact]
    public void MaxGenres_LimitsFinalGenres_ButNotPreservedValues()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Reggae", PersonalGenreTaxonKind.Genre, 1),
            new GenreTagObservation("Pop", PersonalGenreTaxonKind.Genre, 2),
            new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre, 3)
        ],
            settings: new PersonalGenreSettings(MaxGenres: 2));

        Assert.Equal(2, resolution.Genres.Count);
        // A personal value is not competing for a genre slot, so capping the
        // number of genres must not silently delete it.
        Assert.Equal("My Personal Genre", Assert.Single(resolution.Preserved).Value);
    }

    // ---------------------------------------------------------------------
    // Carrying the file's original values forward
    // ---------------------------------------------------------------------

    private static GenreTagObservation[] Original(params string[] values)
        => values
            .Select((value, index) => new GenreTagObservation(value, PersonalGenreTaxonKind.Genre, index))
            .ToArray();

    /// <summary>
    /// The case that motivates the whole reconciliation step: AutoTag overwrote a
    /// value the taxonomy has never heard of, and it comes back anyway.
    /// </summary>
    [Fact]
    public void UninterpretableOriginalValue_IsCarriedForward()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre)],
            originalObservations:
            [
                new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre, 0),
                new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre, 1)
            ]);

        var carried = Assert.Single(resolution.Preserved);
        Assert.Equal("My Personal Genre", carried.Value);
        Assert.Equal(GenreObservationOrigin.PreservedOriginal, carried.Origin);
        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
    }

    /// <summary>
    /// Reconciliation must not defeat AutoTag. When a platform replaces a value
    /// the taxonomy understands, that replacement stands.
    /// </summary>
    [Fact]
    public void InterpretableOriginalValue_ReplacedByAPlatform_StaysReplaced()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre)],
            originalObservations: Original("Pop"));

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.DoesNotContain("Pop", resolution.Genres);
        Assert.Empty(resolution.Preserved);
    }

    [Fact]
    public void OriginalValueStillPresent_IsNotCarriedTwice()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre)],
            originalObservations: Original("Bongo Flava", "My Personal Genre"));

        Assert.Equal("Bongo Flava", Assert.Single(resolution.Genres));
        Assert.Equal("My Personal Genre", Assert.Single(resolution.Preserved).Value);
    }

    /// <summary>
    /// A value the platforms moved to a different field still counts as replaced,
    /// so no stale duplicate is resurrected in the old field. The classification
    /// follows the taxon, not the field it happened to be found in: Bongo Flava
    /// is a Genre even when read from a style tag.
    /// </summary>
    [Fact]
    public void OriginalValueMovedToAnotherField_CountsAsReplaced()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Style)],
            originalObservations:
            [
                new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre, 0)
            ]);

        Assert.Equal("Bongo Flava", Assert.Single(resolution.Genres));
        Assert.Empty(resolution.Styles);
        Assert.Empty(resolution.Preserved);
    }

    /// <summary>
    /// Once a value has been carried forward it is not carried again: two original
    /// fields holding the same personal value yield one preserved entry.
    /// </summary>
    [Fact]
    public void CarriedValueIsNotDuplicated()
    {
        var resolution = PersonalGenreResolver.Resolve(
            Array.Empty<GenreTagObservation>(),
            originalObservations:
            [
                new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre, 0),
                new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Style, 1)
            ]);

        Assert.Equal("My Personal Genre", Assert.Single(resolution.Preserved).Value);
    }

    /// <summary>
    /// A value the taxonomy can interpret is never carried back after a platform
    /// replaced it, even if the interpretation is a custom term. The platform's
    /// write stands; this only protects values that would otherwise be lost.
    /// </summary>
    [Fact]
    public void InterpretableOriginalValueViaCustomTaxon_IsNotCarriedBack()
    {
        var resolution = PersonalGenreResolver.Resolve(
            Array.Empty<GenreTagObservation>(),
            originalObservations: Original("My Afro Mix"),
            customTaxa:
            [
                new PersonalGenreTaxon("my-afro-mix", "My Afro Mix", PersonalGenreTaxonKind.Style)
            ]);

        Assert.Empty(resolution.Styles);
        Assert.Empty(resolution.Preserved);
    }

    /// <summary>
    /// The same value, still in the file when the resolver runs, is classified.
    /// Carry-forward only applies to values a platform actually removed.
    /// </summary>
    [Fact]
    public void CustomTaxonValueStillInTheFile_IsClassified()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("My Afro Mix", PersonalGenreTaxonKind.Genre)],
            customTaxa:
            [
                new PersonalGenreTaxon("my-afro-mix", "My Afro Mix", PersonalGenreTaxonKind.Style)
            ]);

        Assert.Equal("My Afro Mix", Assert.Single(resolution.Styles));
        Assert.Empty(resolution.Preserved);
    }

    [Fact]
    public void OriginalValueResolvingViaRule_IsNotCarriedBack()
    {
        var resolution = PersonalGenreResolver.Resolve(
            Array.Empty<GenreTagObservation>(),
            originalObservations: Original("Sunday Chill"),
            rules:
            [
                new PersonalGenreRule(1, "Sunday Chill", "worship", Priority: 10)
            ]);

        Assert.Empty(resolution.Styles);
        Assert.Empty(resolution.Preserved);
    }

    [Fact]
    public void IgnoredOriginalValue_IsNotCarriedBack()
    {
        var resolution = PersonalGenreResolver.Resolve(
            Array.Empty<GenreTagObservation>(),
            originalObservations: Original("Pop"),
            mappings:
            [
                new PersonalGenreMapping(1, "Pop", "bongo-flava", Action: PersonalGenreMappingAction.Ignore)
            ]);

        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Preserved);
    }

    // ---------------------------------------------------------------------
    // Equivalent-name handling (Hip-Hop / Hip Hop / HipHop, Rap)
    // ---------------------------------------------------------------------

    /// <summary>
    /// The three spellings of one term must collapse to a single final concept.
    ///
    /// This is not a coincidence of one term's alias list: matching normalizes to
    /// letters and digits, so "Hip-Hop", "Hip Hop" and "HipHop" all reduce to the
    /// same key before the taxonomy is consulted. That is what stops a file
    /// carrying three spellings from producing three genres.
    /// </summary>
    [Fact]
    public void ThreeSpellingsOfHipHopProduceOneGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Hip Hop", PersonalGenreTaxonKind.Genre, 1),
            new GenreTagObservation("HipHop", PersonalGenreTaxonKind.Genre, 2)
        ]);

        Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
        Assert.Equal("Hip-Hop", resolution.PrimaryGenre);
        // One taxon, not three spellings of it.
        Assert.Equal("hip-hop", Assert.Single(resolution.Classifications).TaxonId);
    }

    /// <summary>
    /// The order the spellings appear in must not change the result, and the
    /// display value is the taxonomy's, not whichever spelling was seen last.
    /// </summary>
    [Fact]
    public void DisplayValueComesFromTheTaxonomyNotTheEncounteredSpelling()
    {
        foreach (var order in new[]
                 {
                     new[] { "HipHop", "Hip-Hop", "Hip Hop" },
                     new[] { "Hip Hop", "HipHop", "Hip-Hop" },
                     new[] { "Hip-Hop", "HipHop", "Hip Hop" }
                 })
        {
            var observations = order
                .Select((value, index) => new GenreTagObservation(value, PersonalGenreTaxonKind.Genre, index))
                .ToList();
            var resolution = PersonalGenreResolver.Resolve(observations);

            Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
            Assert.DoesNotContain("HipHop", resolution.Genres);
            Assert.DoesNotContain("Hip Hop", resolution.Genres);
        }
    }

    /// <summary>
    /// DeezSpoTag ships no built-in relationship that merges Rap into Hip-Hop, so
    /// both survive. The researched master now carries Rap as a Genre of its own,
    /// so it is classified rather than preserved as an unknown value, but it is
    /// still a separate Genre and the two never collapse on their own.
    /// </summary>
    /// <remarks>
    /// This test previously asserted that Rap produced no genre at all, because the
    /// 156-term taxonomy had no Rap term and the value was preserved. Adopting the
    /// researched vocabulary means Rap is recognised, so the old expectation is
    /// intentionally replaced. The guarantee the test actually protects, that Rap is
    /// not merged into Hip-Hop, is unchanged.
    /// </remarks>
    [Fact]
    public void RapIsNotSilentlyMergedIntoHipHopByDefault()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Rap", PersonalGenreTaxonKind.Genre, 1)
        ]);

        Assert.Equal("Hip-Hop", resolution.PrimaryGenre);
        // Two Genres, not one: Rap is classified as its own Genre and the two are
        // never merged without the user saying so.
        Assert.Equal(["Hip-Hop", "rap"], resolution.Genres);
        Assert.Empty(resolution.Preserved);
    }

    /// <summary>
    /// A user-created term for a value the researched master does not carry is
    /// still honoured, which is the point of custom taxonomy support.
    /// </summary>
    /// <remarks>
    /// This test previously used "Rap", which the 156-term taxonomy did not know, so
    /// the custom term was the only way to classify it. The researched master now
    /// carries Rap itself, so the same scenario is expressed with a term the master
    /// genuinely does not contain, which keeps the guarantee being tested intact.
    /// </remarks>
    [Fact]
    public void AUserTermIsStillHonouredForAValueTheResearchedMasterDoesNotCarry()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon("nightcore-local", "Nightcore Local", PersonalGenreTaxonKind.Genre)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Nightcore Local", PersonalGenreTaxonKind.Genre, 1)
        ],
            customTaxa: customTaxa);

        Assert.Contains("Hip-Hop", resolution.Genres);
        Assert.Contains("Nightcore Local", resolution.Genres);
        Assert.Empty(resolution.Preserved);
        Assert.Equal("personal_taxonomy_match", Assert.Single(
            resolution.Decisions, d => d.RawValue == "Nightcore Local").Outcome);
    }

    /// <summary>
    /// When the user maps Rap to Hip-Hop, the two collapse to the mapped term and
    /// Rap is no longer preserved separately. The user's configuration decides,
    /// not a hard-coded relationship.
    /// </summary>
    [Fact]
    public void RapCollapsesToHipHopWhenTheUserMapsIt()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(1, "Rap", "hip-hop", Priority: 500, Enabled: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Rap", PersonalGenreTaxonKind.Genre, 1)
        ],
            mappings: mappings);

        Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
        Assert.Equal("Hip-Hop", resolution.PrimaryGenre);
        // The mapped value is classified, so it is not also preserved.
        Assert.Empty(resolution.Preserved);
        Assert.Equal("mapping_applied", Assert.Single(
            resolution.Decisions, d => d.RawValue == "Rap").Outcome);
    }

    /// <summary>
    /// The same, expressed as a custom taxonomy term rather than a mapping.
    /// </summary>
    /// <remarks>
    /// Rap is no longer a candidate for this scenario: the researched master carries
    /// it as a Genre, so a custom term with the same id is rejected as a built-in id.
    /// The guarantee under test is that a user term wins for a value the research
    /// does not carry, so an absent term is used instead.
    /// </remarks>
    [Fact]
    public void AUserTermCanGiveAValueItsOwnClassification()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon("nightcore-local-2", "Nightcore Local 2", PersonalGenreTaxonKind.Genre)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Nightcore Local 2", PersonalGenreTaxonKind.Genre, 1)
        ],
            customTaxa: customTaxa);

        Assert.Contains("Hip-Hop", resolution.Genres);
        Assert.Contains("Nightcore Local 2", resolution.Genres);
        Assert.Empty(resolution.Preserved);
        Assert.Equal("personal_taxonomy_match", Assert.Single(
            resolution.Decisions, d => d.RawValue == "Nightcore Local 2").Outcome);
    }

    /// <summary>
    /// A user who explicitly ignores a spelling must not have it reintroduced by
    /// the preservation policy.
    /// </summary>
    [Fact]
    public void IgnoredValueIsNotPreserved()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(1, "Worldwide", "pop", Action: PersonalGenreMappingAction.Ignore)
        };

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Worldwide", PersonalGenreTaxonKind.Genre)],
            mappings: mappings);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Preserved);
    }

    // ---------------------------------------------------------------------
    // Location boundary
    // ---------------------------------------------------------------------

    /// <summary>
    /// Location may corroborate a term the file already contains. The audit trail
    /// records that it did, so the support is visible rather than implicit. A location
    /// that already appears in a value is audited as support, and never as the reason
    /// the value was classified.
    /// </summary>
    /// <remarks>
    /// The fixture term was "Southern Rap", which the 156-term taxonomy did not carry
    /// and which the user therefore had to define as a custom term. The researched
    /// master now carries "southern rap" as a Style, so a custom Genre with that id
    /// is rejected as a built-in id. The guarantee under test is the location audit
    /// itself, so the fixture uses a term the research genuinely does not contain.
    /// </remarks>
    [Fact]
    public void LocationThatAlreadyAppearsInAValue_IsAuditedAsSupport()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Testville Trap", PersonalGenreTaxonKind.Genre)],
            customTaxa:
            [
                new PersonalGenreTaxon("testville-trap-local", "Testville Trap", PersonalGenreTaxonKind.Genre)
            ],
            artistLocations:
            [
                new GenreArtistLocationContext("United States", "Georgia", "Atlanta", IsMainArtist: true)
            ]);

        Assert.Equal("Testville Trap", resolution.PrimaryGenre);
        Assert.Contains("did not add or change", Assert.Single(resolution.Decisions).Reason);

        var corroborated = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Atlanta Testville Trap", PersonalGenreTaxonKind.Genre)],
            customTaxa:
            [
                new PersonalGenreTaxon("atlanta-testville-trap-local", "Atlanta Testville Trap", PersonalGenreTaxonKind.Genre)
            ],
            artistLocations:
            [
                new GenreArtistLocationContext("United States", "Georgia", "Atlanta", IsMainArtist: true)
            ]);

        Assert.Equal("Atlanta Testville Trap", corroborated.PrimaryGenre);
        Assert.Contains("corroborated", Assert.Single(corroborated.Decisions).Reason);
        Assert.Contains("Atlanta", Assert.Single(corroborated.Decisions).Reason);
    }

    [Fact]
    public void LocationAlone_NeverCreatesAGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre)],
            artistLocations:
            [
                new GenreArtistLocationContext("United States", "Georgia", "Atlanta", IsMainArtist: true)
            ]);

        Assert.Equal("Hip-Hop", Assert.Single(resolution.Genres));
        Assert.DoesNotContain(resolution.Genres, value => value.Contains("Atlanta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocationAlone_DoesNotIntroduceAnyTermWhenNoTagsExist()
    {
        var resolution = PersonalGenreResolver.Resolve(
            Array.Empty<GenreTagObservation>(),
            artistLocations:
            [
                new GenreArtistLocationContext("United States", "Georgia", "Atlanta", IsMainArtist: true)
            ]);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Styles);
        Assert.Empty(resolution.Preserved);
    }

    /// <summary>
    /// Featured, remix and guest credits are not main artists, so a location attached
    /// to one may not corroborate anything.
    /// </summary>
    [Fact]
    public void NonMainArtistLocation_IsIgnored()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Atlanta Southern Rap", PersonalGenreTaxonKind.Genre)],
            customTaxa:
            [
                new PersonalGenreTaxon("atlanta-southern-rap", "Atlanta Southern Rap", PersonalGenreTaxonKind.Genre)
            ],
            artistLocations:
            [
                new GenreArtistLocationContext("United States", "Georgia", "Atlanta", IsMainArtist: false)
            ]);

        Assert.Contains("did not add or change", Assert.Single(resolution.Decisions).Reason);
    }

    [Fact]
    public void MultipleMainArtists_MayEachCorroborate()
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Atlanta Southern Rap", PersonalGenreTaxonKind.Genre)],
            customTaxa:
            [
                new PersonalGenreTaxon("atlanta-southern-rap", "Atlanta Southern Rap", PersonalGenreTaxonKind.Genre)
            ],
            artistLocations:
            [
                new GenreArtistLocationContext("United States", "Georgia", "Atlanta", IsMainArtist: true),
                new GenreArtistLocationContext("Nigeria", null, "Lagos", IsMainArtist: true)
            ]);

        Assert.Equal("Atlanta Southern Rap", resolution.PrimaryGenre);
        Assert.Contains("Atlanta", Assert.Single(resolution.Decisions).Reason);
        Assert.DoesNotContain("Lagos", Assert.Single(resolution.Decisions).Reason);
    }
}
