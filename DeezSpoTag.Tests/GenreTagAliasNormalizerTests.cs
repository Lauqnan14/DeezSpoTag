using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class GenreTagAliasNormalizerTests
{
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
            new[] { "R&B/Soul" },
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
            new[] { "Hip-Hop/Pop" },
            aliasMap,
            splitComposite: true);

        Assert.Equal(["HipHop", "Pop"], values);
    }
}
