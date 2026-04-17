using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class GenreTagAliasNormalizerTests
{
    private static readonly string[] RnbSoulComposite = ["R&B/Soul"];
    private static readonly string[] HipHopPopComposite = ["Hip-Hop/Pop"];

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
}
