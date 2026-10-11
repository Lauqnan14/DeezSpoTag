using System;
using System.Linq;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Structural checks over the whole researched vocabulary. A catalog that loads
/// is not evidence that every Genre and Style survived.
/// </summary>
public sealed class GenreTaxonomyIntegrityTest
{
    [Fact]
    public void OverloadedLabelsKeepEveryMusicalIdentity()
    {
        var report = GenreTaxonomyReconciliations.BuildDiscrepancyReport(
            PersonalGenreCatalog.Default,
            ResearchedGenreCatalog.Current);

        Assert.Empty(report.SuppressedResearchedMusicalIdentities);
        Assert.Empty(report.UnexplainedMusicalKindConflicts);
        Assert.Empty(report.InvalidCollisions);
        Assert.Equal(3, report.ExplicitReconciliations);
        Assert.Contains(report.LegitimateOverloadedLabels, item => item.LookupKey == "bollywood");
        Assert.Contains(report.LegitimateOverloadedLabels, item => item.LookupKey == "opm");
        Assert.Contains(report.LegitimateOverloadedLabels, item => item.LookupKey == "swahili");

        foreach (var label in report.LegitimateOverloadedLabels)
        {
            var lookup = PersonalGenreCatalog.Default.LookupLabel(label.LookupKey);
            Assert.Equal(PersonalGenreCatalog.LabelLookupStatus.Ambiguous, lookup.Status);
            Assert.True(lookup.Candidates.Select(item => item.Kind).Distinct().Count() > 1, label.LookupKey);
            Assert.False(PersonalGenreCatalog.Default.TryMatch(label.LookupKey, out _));
        }
    }

    [Fact]
    public void BollywoodOpmAndSwahiliResolveFromTheInputField()
    {
        var catalog = PersonalGenreCatalog.Default;
        var research = ResearchedGenreCatalog.Current;

        Assert.True(research.TryMatch("Bollywood", out var researchedBollywood, out _));
        Assert.Equal("Style", researchedBollywood.Kind);
        Assert.True(catalog.TryGetById("bollywood", out var bollywoodContext));
        Assert.Equal(PersonalGenreTaxonKind.Context, bollywoodContext.Kind);
        Assert.True(catalog.TryGetById("bollywood-style", out var bollywoodStyle));
        Assert.Equal(PersonalGenreTaxonKind.Style, bollywoodStyle.Kind);
        Assert.NotEqual(bollywoodContext.Id, bollywoodStyle.Id);
        Assert.True(catalog.TryGetById("filmi", out var filmi));
        Assert.Equal(PersonalGenreTaxonKind.Genre, filmi.Kind);
        Assert.NotEqual(filmi.Id, bollywoodStyle.Id);

        AssertResolved("Bollywood", PersonalGenreTaxonKind.Style, "bollywood-style", styles: ["Bollywood"]);
        AssertResolved("Bollywood", PersonalGenreTaxonKind.Substyle, "bollywood-style", styles: ["Bollywood"]);
        AssertResolved("Bollywood", PersonalGenreTaxonKind.Genre, "bollywood-style", styles: ["Bollywood"]);
        AssertResolved("Bollywood", PersonalGenreTaxonKind.Context, "bollywood", contexts: ["Bollywood"]);

        Assert.True(research.TryMatch("OPM", out var researchedOpm, out _));
        Assert.Equal("Genre", researchedOpm.Kind);
        Assert.Equal("opm", researchedOpm.Id);
        Assert.True(catalog.TryGetById("opm", out var opmContext));
        Assert.Equal(PersonalGenreTaxonKind.Context, opmContext.Kind);
        Assert.True(catalog.TryGetById("opm-genre", out var opmGenre));
        Assert.Equal(PersonalGenreTaxonKind.Genre, opmGenre.Kind);
        Assert.Equal("OPM", opmGenre.Name);
        Assert.NotEqual(opmContext.Id, opmGenre.Id);
        Assert.Contains("Original Pilipino Music", opmGenre.Aliases ?? []);

        AssertResolved("OPM", PersonalGenreTaxonKind.Genre, "opm-genre", genres: ["OPM"]);
        AssertResolved("OPM", PersonalGenreTaxonKind.Style, "opm-genre", genres: ["OPM"]);
        AssertResolved("OPM", PersonalGenreTaxonKind.Substyle, "opm-genre", genres: ["OPM"]);
        AssertResolved("OPM", PersonalGenreTaxonKind.Context, "opm", contexts: ["OPM"]);

        var expanded = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Original Pilipino Music", PersonalGenreTaxonKind.Style)]);
        Assert.Equal(new[] { "OPM" }, expanded.Genres);
        Assert.Empty(expanded.Styles);

        Assert.True(research.TryMatch("Swahili", out var researchedSwahili, out _));
        Assert.Equal("Style", researchedSwahili.Kind);
        Assert.True(catalog.TryGetById("swahili", out var swahiliLanguage));
        Assert.Equal(PersonalGenreTaxonKind.Language, swahiliLanguage.Kind);
        Assert.True(catalog.TryGetById("swahili-style", out var swahiliStyle));
        Assert.Equal(PersonalGenreTaxonKind.Style, swahiliStyle.Kind);
        Assert.NotEqual(swahiliLanguage.Id, swahiliStyle.Id);
        Assert.True(research.TryMatch("taarab", out var taarab, out _));
        Assert.Equal("Genre", taarab.Kind);
        Assert.NotEqual(taarab.Id, swahiliStyle.Id);

        AssertResolved("Swahili", PersonalGenreTaxonKind.Style, "swahili-style", styles: ["Swahili"]);
        AssertResolved("Swahili", PersonalGenreTaxonKind.Substyle, "swahili-style", styles: ["Swahili"]);
        AssertResolved("Swahili", PersonalGenreTaxonKind.Genre, "swahili-style", styles: ["Swahili"]);
        AssertResolved("Swahili", PersonalGenreTaxonKind.Language, "swahili", languages: ["Swahili"]);

        var neutral = catalog.LookupLabel("Swahili");
        Assert.Equal(PersonalGenreCatalog.LabelLookupStatus.Ambiguous, neutral.Status);
        Assert.Contains(neutral.Candidates, item => item.Id == "swahili");
        Assert.Contains(neutral.Candidates, item => item.Id == "swahili-style");
        Assert.False(catalog.TryMatch("Swahili", out _));
    }

    [Fact]
    public void OverloadedCandidateSetsDoNotDependOnInsertionOrder()
    {
        var styleThenContext = CatalogWith(
            new PersonalGenreTaxon("order-bollywood-style", "Bollywood", PersonalGenreTaxonKind.Style),
            new PersonalGenreTaxon("order-opm-genre", "OPM", PersonalGenreTaxonKind.Genre),
            new PersonalGenreTaxon("order-bollywood-context", "Bollywood", PersonalGenreTaxonKind.Context),
            new PersonalGenreTaxon("order-opm-context", "OPM", PersonalGenreTaxonKind.Context));
        var contextThenStyle = CatalogWith(
            new PersonalGenreTaxon("order-bollywood-context", "Bollywood", PersonalGenreTaxonKind.Context),
            new PersonalGenreTaxon("order-opm-context", "OPM", PersonalGenreTaxonKind.Context),
            new PersonalGenreTaxon("order-bollywood-style", "Bollywood", PersonalGenreTaxonKind.Style),
            new PersonalGenreTaxon("order-opm-genre", "OPM", PersonalGenreTaxonKind.Genre));

        Assert.Equal(Ids(styleThenContext, "Bollywood"), Ids(contextThenStyle, "Bollywood"));
        Assert.Equal(Ids(styleThenContext, "OPM"), Ids(contextThenStyle, "OPM"));
        Assert.False(styleThenContext.TryMatch("Bollywood", out _));
        Assert.False(contextThenStyle.TryMatch("OPM", out _));
        Assert.Contains("bollywood", Ids(styleThenContext, "Bollywood"));
        Assert.Contains("bollywood-style", Ids(styleThenContext, "Bollywood"));
        Assert.Contains("order-bollywood-style", Ids(styleThenContext, "Bollywood"));
        Assert.Contains("order-bollywood-context", Ids(contextThenStyle, "Bollywood"));
    }

    private static string[] Ids(PersonalGenreCatalog catalog, string label)
        => catalog.LookupLabel(label).Candidates.Select(item => item.Id).OrderBy(item => item, StringComparer.Ordinal).ToArray();

    private static PersonalGenreCatalog CatalogWith(params PersonalGenreTaxon[] taxa)
        => new(taxa);

    [Fact]
    public void EveryResearchedGenreAndStyleIsReachableOrExplicitlyReconciled()
    {
        var runtime = PersonalGenreCatalog.Default;
        var research = ResearchedGenreCatalog.Current;

        foreach (var term in research.Terms)
        {
            var lookup = runtime.LookupLabel(term.Name);
            Assert.NotEqual(PersonalGenreCatalog.LabelLookupStatus.None, lookup.Status);
            Assert.Contains(lookup.Candidates, item => item.Kind.ToString() == term.Kind);
            if (lookup.Status == PersonalGenreCatalog.LabelLookupStatus.Unique)
            {
                Assert.Equal(term.Kind, lookup.Candidates[0].Kind.ToString());
            }
        }
    }

    private static void AssertResolved(
        string value,
        PersonalGenreTaxonKind field,
        string taxonId,
        string[]? genres = null,
        string[]? styles = null,
        string[]? contexts = null,
        string[]? languages = null)
    {
        var resolution = PersonalGenreResolver.Resolve([new GenreTagObservation(value, field)]);
        Assert.Equal(genres ?? [], resolution.Genres);
        Assert.Equal(styles ?? [], resolution.Styles);
        Assert.Equal(contexts ?? [], resolution.Contexts);
        Assert.Equal(languages ?? [], resolution.Languages);
        Assert.Contains(resolution.Classifications, item => item.TaxonId == taxonId);
    }

    [Fact]
    public void CanonicalExclusionAndAmbiguityKeysDoNotCollideWithoutARecord()
    {
        var research = ResearchedGenreCatalog.Current;
        var canonical = research.Terms
            .SelectMany(term => new[] { Key(term.Name), Key(term.Id) })
            .Concat(research.Aliases.Select(alias => Key(alias.Value)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in research.ExclusionReasons.Keys)
        {
            Assert.DoesNotContain(key, canonical);
        }

        foreach (var key in research.AmbiguityReasons.Keys)
        {
            Assert.DoesNotContain(key, canonical);
        }

        Assert.Equal("greek-pop", research.ResolvedExclusionCollisions["greekpop"]);
        Assert.Contains(Key("Greek-Pop"), canonical);
    }

    private static string Key(string value)
        => new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
