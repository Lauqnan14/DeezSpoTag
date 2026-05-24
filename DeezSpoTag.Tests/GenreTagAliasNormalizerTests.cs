using System;
using System.Linq;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class GenreTagAliasNormalizerTests
{
    private static readonly string[] RnbSoulComposite = ["R&B/Soul"];
    private static readonly string[] HipHopPopComposite = ["Hip-Hop/Pop"];
    private static readonly string[] HipHopRapComposite = ["HipHop, Rap", "Rap", "Hip-Hop"];
    private static readonly string[] ExpectedHipHopRap = ["HipHop", "Rap"];
    private static readonly string[] BlockedGenres = ["other", "others"];

    [Fact]
    public void NormalizeAndExpandValues_PrefersWholeAliasBeforeCompositeSplit()
    {
        var aliasMap = GenreTagAliasNormalizer.BuildAliasMap(
        [
            new GenreTagAliasRule
            {
                Alias = "R&B/Soul",
                Canonical = "RnB Soul"
            }
        ]);

        var values = GenreTagAliasNormalizer.NormalizeAndExpandValues(
            RnbSoulComposite,
            aliasMap,
            splitComposite: true);

        Assert.Single(values);
        Assert.Equal("RnB Soul", values[0]);
    }

    [Fact]
    public void NormalizeAndExpandValues_SplitsAndNormalizesTokens_WhenWholeAliasDoesNotExist()
    {
        var aliasMap = GenreTagAliasNormalizer.BuildAliasMap(
        [
            new GenreTagAliasRule
            {
                Alias = "Hip-Hop",
                Canonical = "HipHop"
            }
        ]);

        var values = GenreTagAliasNormalizer.NormalizeAndExpandValues(
            HipHopPopComposite,
            aliasMap,
            splitComposite: true);

        Assert.Equal(["HipHop", "Pop"], values);
    }

    [Fact]
    public void NormalizeAndExpandValues_SplitsJoinedGenreStringsBeforeDedupe()
    {
        var aliasMap = GenreTagAliasNormalizer.BuildAliasMap(
        [
            new GenreTagAliasRule
            {
                Alias = "Hip-Hop",
                Canonical = "HipHop"
            }
        ]);

        var values = GenreTagAliasNormalizer.NormalizeAndExpandValues(
                HipHopRapComposite,
                aliasMap,
                splitComposite: true)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(ExpectedHipHopRap, values);
    }

    [Fact]
    public void NormalizeExpandFilterAndDedupeValues_DedupesByLookupKey()
    {
        var values = GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            ["Afro Pop", "Afro-Pop", "afro pop", "AfroPop"],
            aliasMap: null,
            splitComposite: false,
            BlockedGenres);

        Assert.Single(values);
        Assert.Equal("Afro Pop", values[0]);
    }

    [Fact]
    public void NormalizeExpandFilterAndDedupeValues_DedupesAfterAliasNormalization()
    {
        var aliasMap = GenreTagAliasNormalizer.BuildAliasMap(
        [
            new GenreTagAliasRule
            {
                Alias = "Afro-Pop",
                Canonical = "Afropop"
            },
            new GenreTagAliasRule
            {
                Alias = "Afro Pop",
                Canonical = "Afropop"
            }
        ]);

        var values = GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            ["Afro Pop", "Afro-Pop", "Afropop", "Other"],
            aliasMap,
            splitComposite: true,
            BlockedGenres);

        Assert.Single(values);
        Assert.Equal("Afropop", values[0]);
    }

    [Fact]
    public void NormalizeExpandFilterAndDedupeValues_SplitsCompositeBeforeDedupe()
    {
        var aliasMap = GenreTagAliasNormalizer.BuildAliasMap(
        [
            new GenreTagAliasRule
            {
                Alias = "Hip-Hop",
                Canonical = "HipHop"
            }
        ]);

        var values = GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            ["Hip-Hop/Pop", "hip hop", "Pop"],
            aliasMap,
            splitComposite: true,
            BlockedGenres);

        Assert.Equal(["HipHop", "Pop"], values);
    }

    [Fact]
    public void NormalizeBlockedValues_DedupesConfiguredBlockedGenres()
    {
        var values = GenreTagAliasNormalizer.NormalizeBlockedValues(["Christian", "christian", "Other"]);

        Assert.Equal(["Christian", "Other"], values);
    }

    [Fact]
    public void NormalizeBlockedValues_UsesDefaultBlockedGenresWhenUnset()
    {
        var values = GenreTagAliasNormalizer.NormalizeBlockedValues(null);

        Assert.Equal(["other", "others", "Worldwide"], values);
    }

    [Fact]
    public void NormalizeExpandFilterAndDedupeValues_UsesConfiguredBlockList()
    {
        var values = GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            ["Christian", "Afropop", "Other"],
            aliasMap: null,
            splitComposite: false,
            GenreTagAliasNormalizer.NormalizeBlockedValues(["Christian", "Other"]));

        Assert.Single(values);
        Assert.Equal("Afropop", values[0]);
    }
}
