using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Enhancement runs with genre in overwriteTags rewrote genre tags on every run in
/// the matching platform's order, so the history diff showed order-only churn
/// (HipHop, Rap → Rap, HipHop) with no content change. When the sanitized genre set
/// equals the file's current set, the file's order must be preserved.
/// </summary>
public sealed class AutoTagGenreOrderPreservationTest
{
    [Fact]
    public void SameGenreSet_PreservesTheFileOrder()
    {
        var result = DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.PreserveGenreOrderWhenSetEqual(
            new List<string> { "Rap", "HipHop" },
            new[] { "HipHop", "Rap" });

        Assert.Equal(new[] { "HipHop", "Rap" }, result);
    }

    [Fact]
    public void SameGenreSet_AdoptsSanitizedCasingButKeepsFileOrder()
    {
        var result = DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.PreserveGenreOrderWhenSetEqual(
            new List<string> { "rap", "hiphop" },
            new[] { "HipHop", "Rap" });

        Assert.Equal(new[] { "hiphop", "rap" }, result);
    }

    [Fact]
    public void DifferentGenreSet_KeepsThePlatformOrder()
    {
        var result = DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.PreserveGenreOrderWhenSetEqual(
            new List<string> { "Rap", "HipHop", "Pop" },
            new[] { "HipHop", "Rap" });

        Assert.Equal(new[] { "Rap", "HipHop", "Pop" }, result);
    }

    [Fact]
    public void DifferentValuesWithSameCount_KeepsThePlatformOrder()
    {
        var result = DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.PreserveGenreOrderWhenSetEqual(
            new List<string> { "Rap", "Trap" },
            new[] { "HipHop", "Rap" });

        Assert.Equal(new[] { "Rap", "Trap" }, result);
    }

    [Fact]
    public void EmptyExistingTags_KeepsThePlatformOrder()
    {
        var result = DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.PreserveGenreOrderWhenSetEqual(
            new List<string> { "Rap", "HipHop" },
            Array.Empty<string>());

        Assert.Equal(new[] { "Rap", "HipHop" }, result);
    }

    [Fact]
    public void DuplicateSanitizedValues_KeepsThePlatformOrder()
    {
        var result = DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.PreserveGenreOrderWhenSetEqual(
            new List<string> { "Rap", "rap" },
            new[] { "HipHop", "Rap" });

        Assert.Equal(new[] { "Rap", "rap" }, result);
    }

    [Fact]
    public void BothGenreWritePaths_PreserveOrderWhenSetEqual()
    {
        var runnerSource = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        // Standard bindings (SetField) and raw-tag writes (SetRaw) both keep the
        // file's genre order when the sanitized set is unchanged — otherwise every
        // run reorders the tags.
        Assert.Contains(
            "values = PreserveGenreOrderWhenSetEqual(values, context.File.Tag?.Genres);",
            runnerSource,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            runnerSource.Split("PreserveGenreOrderWhenSetEqual(values, context.File.Tag?.Genres);").Length - 1);
    }

    [Theory]
    [InlineData("R&B", "R&B")]
    [InlineData("r&b", "R&B")]
    [InlineData("r&b dance", "R&B Dance")]
    [InlineData("hip hop", "Hip Hop")]
    [InlineData("HipHop", "HipHop")]
    [InlineData("EDM", "EDM")]
    [InlineData("edm", "Edm")]
    [InlineData("drum & bass", "Drum & Bass")]
    public void CapitalizeGenre_KeepsAmpersandCompoundsAndExistingCasing(string input, string expected)
    {
        var result = DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.CapitalizeGenre(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CapitalizeGenre_NeverFlattensExistingCasing()
    {
        var runnerSource = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        var capitalizeStart = runnerSource.IndexOf(
            "internal static string CapitalizeGenre(",
            StringComparison.Ordinal);
        Assert.True(capitalizeStart >= 0, "Missing CapitalizeGenre.");
        var body = runnerSource[capitalizeStart..(capitalizeStart + 1600)];

        // The old implementation title-cased via word[1..].ToLowerInvariant(), which
        // corrupted R&B → R&b and HipHop → Hiphop on every capitalization pass.
        Assert.DoesNotContain("ToLowerInvariant()", body, StringComparison.Ordinal);
        // The ampersand boundary keeps compound genres like R&B intact.
        Assert.Contains("chars[c - 1] == '&'", body, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
        => PartialSourceReader.ReadTypeSource(pathParts);

    private static string ResolveRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
