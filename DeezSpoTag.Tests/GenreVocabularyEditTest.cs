using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class GenreVocabularyEditTest
{
    [Fact]
    public void ASharedSpellingRewritesTheWordWithoutMergingTheTerms()
    {
        var conventions = new[] { new PersonalGenreSpellingConvention("hiphop", "HipHop") };

        Assert.Equal("HipHop", GenreSpellingConventions.Apply("Hip-Hop", conventions));
        Assert.Equal("Kenyan HipHop", GenreSpellingConventions.Apply("Kenyan hip hop", conventions));
        Assert.Equal("Kapuka", GenreSpellingConventions.Apply("Kapuka", conventions));
    }

    [Fact]
    public void TheWritePlanAppliesASharedSpellingToWhatWouldBeWritten()
    {
        var plan = GenreSemanticTagIo.PlanFields(
            Resolution(genres: ["Hip-Hop"]),
            spellingConventions: [new PersonalGenreSpellingConvention("hiphop", "HipHop")]);

        Assert.Equal(["HipHop"], plan[PersonalGenreTaxonKind.Genre]);
    }

    [Fact]
    public void TheCleanupPreviewAppliesTheSameSpellingConventionsAsTheWriter()
    {
        var resolution = Resolution(genres: ["Hip-Hop"]);
        var preview = GenreCleanupPreviewBuilder.Build(
            resolution,
            beforeObservations: [],
            removedByNormalization: [],
            spellingConventions: [new PersonalGenreSpellingConvention("hiphop", "HipHop")]);

        Assert.Equal(["HipHop"], preview.AfterGenres);
    }

    [Fact]
    public void MakingAnAliasTheOriginalNameKeepsTheIdAndTheOtherAliases()
    {
        var catalog = new PersonalGenreCatalog(edits:
        [
            new PersonalGenreTermEdit("kapuka", OriginalName: "Boomba", Aliases: ["Kapuka", "Kapuka rap"])
        ]);

        Assert.True(catalog.TryGetById("kapuka", out var term));
        Assert.Equal("Boomba", term.Name);
        Assert.Contains("Kapuka rap", term.Aliases!);
        Assert.DoesNotContain("Boomba", term.Aliases!);
    }

    [Fact]
    public void APromotedAliasOwnedByAnotherTermIsRefusedAndStaysAnAlias()
    {
        var custom = new List<PersonalGenreTaxon>
        {
            new("test-alpha", "Alpha", PersonalGenreTaxonKind.Genre, Aliases: ["Beta"]),
            new("test-beta", "Beta", PersonalGenreTaxonKind.Genre)
        };
        var catalog = new PersonalGenreCatalog(
            customTaxa: custom,
            edits: [new PersonalGenreTermEdit("test-alpha", OriginalName: "Beta")]);

        Assert.True(catalog.TryGetById("test-alpha", out var refused));
        Assert.Equal("Alpha", refused.Name);
        Assert.Contains("Beta", refused.Aliases!);
        Assert.True(catalog.TryMatch("beta", out var matched));
        Assert.Equal("test-beta", matched.Id);
    }

    [Fact]
    public async Task SavedVocabularyEditsAndSpellingConventionsSurviveAStoreReload()
    {
        var directory = Directory.CreateTempSubdirectory("pg-vocab-edit-");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory.FullName, "library.db")
            }).Build();
            var store = new PersonalGenreStore(configuration);
            await store.SaveSettingsAsync(new PersonalGenreSettings(
                TermEdits:
                [
                    new PersonalGenreTermEdit(
                        "kapuka",
                        OriginalName: "Boomba",
                        Aliases: ["Kapuka", "Kapuka rap"])
                ],
                SpellingConventions: [new PersonalGenreSpellingConvention("hiphop", "HipHop")]));

            var stored = await new PersonalGenreStore(configuration).GetSettingsAsync();
            var edit = Assert.Single(stored.TermEdits!, item => item.TaxonId == "kapuka");
            Assert.Equal("Boomba", edit.OriginalName);
            Assert.Equal(["Kapuka", "Kapuka rap"], edit.Aliases);
            var convention = Assert.Single(stored.SpellingConventions!);
            Assert.Equal("hiphop", convention.Key);
            Assert.Equal("HipHop", convention.Spelling);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheRegionalImportLeavesAnUnresearchedEuropeTermInPlace()
    {
        Assert.True(ResearchedGenreCatalog.Current.TryGetById("georgian", out var georgian));
        Assert.Contains("europe", georgian.Regions);
        Assert.Null(georgian.Research);
    }

    private static PersonalGenreResolution Resolution(
        IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? styles = null,
        IReadOnlyList<string>? substyles = null,
        IReadOnlyList<string>? contexts = null,
        IReadOnlyList<string>? scenes = null,
        IReadOnlyList<string>? languages = null,
        IReadOnlyList<PreservedTagValue>? preserved = null)
        => new(
            genres?.FirstOrDefault(),
            genres ?? [],
            styles ?? [],
            substyles ?? [],
            contexts ?? [],
            scenes ?? [],
            languages ?? [],
            preserved ?? [],
            [],
            [],
            [],
            [],
            PersonalGenreResolver.Version);
}
