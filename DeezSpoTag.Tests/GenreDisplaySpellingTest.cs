using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Pins the display-formatting boundary for Genre Cleanup.
/// </summary>
/// <remarks>
/// <para>
/// The researched master is the semantic authority and its spelling is left exactly
/// as generated. What must not happen is a newly recognised term reaching a file as
/// a visibly poor value such as "southern hip hop" or "rap". Presentation spelling
/// is therefore a separate step applied at the write and display boundary, using the
/// formatting behaviour DeezSpoTag already applies to provider genres.
/// </para>
/// <para>
/// These tests exist because a formatter that improves "swing revival" can easily
/// damage "R&amp;B", "K-Pop" or "Coupé-Décalé". The formatter is only safe here
/// because it capitalises word starts without flattening existing casing, and these
/// cases pin that.
/// </para>
/// </remarks>
public sealed class GenreDisplaySpellingTest
{
    [Theory]
    [InlineData("album rock", "Album Rock")]
    [InlineData("algorave", "Algorave")]
    [InlineData("alt-country", "Alt-Country")]
    [InlineData("alternative ccm", "Alternative CCM")]
    [InlineData("edm ebm aor uk us usa dnb idm r&b", "EDM EBM AOR UK US USA DnB IDM R&B")]
    [InlineData("HipHop J-Pop coupé-décalé 16-bit", "HipHop J-Pop Coupé-Décalé 16-bit")]
    [InlineData("ccmusic", "Ccmusic")]
    [InlineData("", "")]
    public void VocabularyAndTagFormattingPreserveEstablishedSpelling(string input, string expected)
    {
        Assert.Equal(expected, LocalAutoTagRunner.CapitalizeGenre(input));
        Assert.Equal(expected, DeezSpoTag.Web.Controllers.Api.PersonalGenreApiController.FormatVocabularyDisplayName(input));
    }

    [Theory]
    [InlineData(true, "Alternative CCM", "Album Rock")]
    [InlineData(false, "alternative ccm", "album rock")]
    public void SemanticPlanHonoursCapitalizationForGenreAndStyle(bool enabled, string style, string genre)
    {
        var resolution = PersonalGenreResolver.Resolve([]) with
        {
            Genres = ["album rock"], Styles = ["alternative ccm"], Contexts = ["baile funk"],
            Preserved = [new PreservedTagValue("my own tag", PersonalGenreTaxonKind.Style, 0, GenreObservationOrigin.PostPlatform)]
        };
        var plan = GenreSemanticTagIo.PlanFields(resolution, enabled);
        Assert.Equal(genre, Assert.Single(plan[PersonalGenreTaxonKind.Genre]));
        Assert.Equal(new[] { style, "my own tag" }, plan[PersonalGenreTaxonKind.Style]);
        Assert.Equal("Baile Funk", Assert.Single(plan[PersonalGenreTaxonKind.Context]));
    }

    // ------------------------------------------------------------------ the shared authority

    /// <summary>
    /// The terms whose capitalisation carries meaning. A formatter that mangles any
    /// of these is not fit to sit on the write path.
    /// </summary>
    [Theory]
    [InlineData("R&B", "R&B")]
    [InlineData("r&b", "R&B")]
    [InlineData("EBM", "EBM")]
    [InlineData("K-Pop", "K-Pop")]
    [InlineData("k-pop", "K-Pop")]
    [InlineData("Hip-Hop", "Hip-Hop")]
    [InlineData("hip-hop", "Hip-Hop")]
    [InlineData("2 Tone", "2 Tone")]
    [InlineData("2 tone", "2 Tone")]
    [InlineData("AOR", "AOR")]
    [InlineData("UK Garage", "UK Garage")]
    [InlineData("J-Pop", "J-Pop")]
    [InlineData("j-pop", "J-Pop")]
    [InlineData("Afrobeats", "Afrobeats")]
    [InlineData("afrobeats", "Afrobeats")]
    [InlineData("EDM", "EDM")]
    [InlineData("HipHop", "HipHop")]
    [InlineData("drum & bass", "Drum & Bass")]
    [InlineData("16-bit", "16-bit")]
    public void TheFormatterKeepsTermsWhoseCapitalisationCarriesMeaning(
        string input,
        string expected)
    {
        Assert.Equal(expected, LocalAutoTagRunner.CapitalizeGenre(input));
    }

    /// <summary>
    /// Non-English and accented names must survive unchanged apart from the word
    /// start, so a locale-specific spelling is never mangled.
    /// </summary>
    [Theory]
    [InlineData("coupé-décalé", "Coupé-Décalé")]
    [InlineData("Coupé-Décalé", "Coupé-Décalé")]
    [InlineData("rock en español", "Rock En Español")]
    [InlineData("forró", "Forró")]
    [InlineData("norteño", "Norteño")]
    [InlineData("raï", "Raï")]
    [InlineData("поп", "Поп")]
    [InlineData("dingele", "Dingele")]
    [InlineData("baile funk", "Baile Funk")]
    [InlineData("fado", "Fado")]
    [InlineData("chanson", "Chanson")]
    public void TheFormatterLeavesAccentedAndNonEnglishNamesIntact(string input, string expected)
    {
        Assert.Equal(expected, LocalAutoTagRunner.CapitalizeGenre(input));
    }

    /// <summary>
    /// The researched master publishes many terms in lowercase because that is how
    /// the source lists them. Those are the values that would otherwise reach a file.
    /// </summary>
    [Theory]
    [InlineData("southern hip hop", "Southern Hip Hop")]
    [InlineData("swing revival", "Swing Revival")]
    [InlineData("power noise", "Power Noise")]
    [InlineData("rap", "Rap")]
    [InlineData("indian indie", "Indian Indie")]
    [InlineData("korean hip-hop", "Korean Hip-Hop")]
    [InlineData("desi hip hop", "Desi Hip Hop")]
    [InlineData("gospel reggae", "Gospel Reggae")]
    [InlineData("bongo flava", "Bongo Flava")]
    public void TheFormatterFixesTheMasterLowercaseSpellings(string input, string expected)
    {
        Assert.Equal(expected, LocalAutoTagRunner.CapitalizeGenre(input));
    }

    [Fact]
    public void TheFormatterNeverLowersAnExistingCapital()
    {
        // Every one of these already reads correctly; none may be flattened.
        foreach (var value in new[] { "R&B", "EBM", "AOR", "K-Pop", "J-Pop", "UK Garage", "EDM", "HipHop" })
        {
            Assert.Equal(value, LocalAutoTagRunner.CapitalizeGenre(value));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TheFormatterLeavesBlankInputAlone(string input)
    {
        Assert.Equal(input, LocalAutoTagRunner.CapitalizeGenre(input));
    }

    /// <summary>
    /// An initialism is vocabulary knowledge, not formatting knowledge, so it is
    /// resolved by the catalog preferring an established built-in spelling rather
    /// than by the formatter guessing. These are asserted at the write boundary,
    /// which is where they actually matter.
    /// </summary>
    [Theory]
    [InlineData("uk garage", "UK Garage")]
    [InlineData("r&b", "R&B")]
    [InlineData("k-pop", "K-Pop")]
    [InlineData("j-pop", "J-Pop")]
    [InlineData("hip hop", "Hip-Hop")]
    [InlineData("coupé-décalé", "Coupé-Décalé")]
    [InlineData("2 tone", "2 Tone")]
    public void AnEstablishedSpellingComesFromTheVocabularyNotTheFormatter(
        string input,
        string expected)
    {
        var plan = GenreSemanticTagIo.PlanFields(
            PersonalGenreResolver.Resolve(
                [new GenreTagObservation(input, PersonalGenreTaxonKind.Genre)]));

        // The dimension the value lands in is whatever the research says it is, which
        // is why this reads across every dimension rather than assuming Genre.
        Assert.Equal([expected], Flatten(plan));
    }

    private static List<string> Flatten(
        IReadOnlyDictionary<PersonalGenreTaxonKind, List<string>> plan)
        => plan.Values.SelectMany(values => values).ToList();

    // ------------------------------------------------------------------ semantic identity is untouched

    /// <summary>
    /// Formatting is presentation only. The catalog keeps the master's spelling, and
    /// a value still resolves to the same term whatever it is displayed as.
    /// </summary>
    [Fact]
    public void TheCatalogKeepsTheResearchedSpellingAsItsCanonicalName()
    {
        var catalog = ResearchedGenreCatalog.Current;

        Assert.True(catalog.TryMatch("southern hip hop", out var southern, out _));
        Assert.Equal("southern hip hop", southern.Name);

        Assert.True(catalog.TryMatch("swing revival", out var swing, out _));
        Assert.Equal("swing revival", swing.Name);

        Assert.True(catalog.TryMatch("power noise", out var power, out _));
        Assert.Equal("power noise", power.Name);
    }

    [Fact]
    public void LookupIsUnaffectedByTheDisplaySpelling()
    {
        var catalog = ResearchedGenreCatalog.Current;

        // Both the master's spelling and its formatted form reach the same term.
        Assert.True(catalog.TryMatch("southern hip hop", out var master, out _));
        Assert.True(catalog.TryMatch("Southern Hip Hop", out var formatted, out _));
        Assert.Equal(master.Id, formatted.Id);

        // And an alias still resolves after formatting.
        Assert.True(catalog.TryMatch("hindie", out var alias, out var viaAlias));
        Assert.True(viaAlias);
        Assert.Equal("Indian Indie", alias.Name);
    }

    [Fact]
    public void ClassificationStillUsesTheResearchedSpelling()
    {
        // The resolver reports the canonical value the research published. Only the
        // write and the display layer format it, so the decision record stays
        // comparable with the researched vocabulary.
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("southern hip hop", PersonalGenreTaxonKind.Genre)]);

        Assert.Equal("southern hip hop", Assert.Single(resolution.Decisions).CanonicalValue);
    }

    // ------------------------------------------------------------------ the write boundary

    /// <summary>
    /// What the plan hands to the writer is formatted, so a recognised term does not
    /// reach the file in the master's lowercase spelling.
    /// </summary>
    [Fact]
    public void TheWritePlanCarriesDisplayFormattedValues()
    {
        var plan = GenreSemanticTagIo.PlanFields(
            PersonalGenreResolver.Resolve(
                [new GenreTagObservation("southern hip hop", PersonalGenreTaxonKind.Genre)]));

        Assert.Equal(["Southern Hip Hop"], Flatten(plan));
    }

    /// <summary>
    /// A preserved value is the user's own value and is returned exactly as it was
    /// found. Formatting it would be changing something cleanup was told to leave
    /// alone.
    /// </summary>
    [Fact]
    public void ABuiltInStyleKeepsItsOwnEstablishedSpelling()
    {
        // "uk garage" is a researched Style, and the built-in spelling "UK Garage" is
        // what the catalog prefers and therefore what gets written.
        var plan = GenreSemanticTagIo.PlanFields(
            PersonalGenreResolver.Resolve(
                [new GenreTagObservation("uk garage", PersonalGenreTaxonKind.Style)]));

        Assert.Equal(["UK Garage"], Flatten(plan));
    }

    [Fact]
    public void AValueTheVocabularyDoesNotKnowIsWrittenBackUnchanged()
    {
        var plan = GenreSemanticTagIo.PlanFields(
            PersonalGenreResolver.Resolve(
                [new GenreTagObservation("my own tag", PersonalGenreTaxonKind.Genre)]));

        Assert.Equal(["my own tag"], plan[PersonalGenreTaxonKind.Genre]);
    }

    /// <summary>
    /// A built-in term keeps the built-in spelling rather than being reformatted, so
    /// an established display name is never replaced by the master's.
    /// </summary>
    [Fact]
    public void ABuiltInTermKeepsItsOwnEstablishedSpelling()
    {
        var plan = GenreSemanticTagIo.PlanFields(
            PersonalGenreResolver.Resolve(
                [new GenreTagObservation("hip hop", PersonalGenreTaxonKind.Genre)]));

        Assert.Equal(["Hip-Hop"], plan[PersonalGenreTaxonKind.Genre]);
    }

    // ------------------------------------------------------------------ preview matches write

    /// <summary>
    /// The preview has to show what the write would actually do, or it is not a
    /// review surface. Both read the same formatting, so this cannot drift.
    /// </summary>
    [Fact]
    public void ThePreviewShowsTheSameSpellingTheWriterWouldUse()
    {
        var observations = new[]
        {
            new GenreTagObservation("southern hip hop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("rap", PersonalGenreTaxonKind.Genre, 1)
        };
        var resolution = PersonalGenreResolver.Resolve(observations);
        var plan = GenreSemanticTagIo.PlanFields(resolution);
        var preview = GenreCleanupPreviewBuilder.Build(resolution, observations, []);

        // The preview's after-lists are read straight from the write plan, so the two
        // cannot drift. The research classifies "southern hip hop" as a Style and
        // "rap" as a Genre, and that split is asserted rather than absorbed.
        Assert.Equal(["Southern Hip Hop"], plan[PersonalGenreTaxonKind.Style]);
        Assert.Equal(["Rap"], plan[PersonalGenreTaxonKind.Genre]);
        Assert.Equal(plan[PersonalGenreTaxonKind.Genre], preview.AfterGenres);
        Assert.Equal(plan[PersonalGenreTaxonKind.Style], preview.AfterStyles);
    }

    [Fact]
    public void FormattingIsIdempotentSoARepeatedRunChangesNothing()
    {
        var once = LocalAutoTagRunner.CapitalizeGenre("southern hip hop");
        Assert.Equal(once, LocalAutoTagRunner.CapitalizeGenre(once));
    }
}
