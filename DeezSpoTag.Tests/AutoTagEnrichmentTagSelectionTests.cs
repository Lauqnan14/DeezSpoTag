using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagEnrichmentTagSelectionTests
{
    private static readonly string[] ExpectedDownloadEnrichmentTags =
    {
        "artist",
        "genre"
    };

    private static readonly string[] ExpectedEnhancementOnlyTags =
    {
        "artist",
        "genre"
    };
    private static readonly string[] ExpectedMergedEnhancementTags =
    {
        "artist",
        "genre"
    };
    private static readonly string[] RequestedYearAndArtistTags = ["year", "artist"];
    private static readonly string[] ItunesPlatformOnly = ["itunes"];
    private static readonly string[] ExpectedReleaseDateOnly = ["releaseDate"];

    [Fact]
    public void ResolveEnrichmentRequestedTags_DownloadEnrichment_UsesOnlyEnrichmentTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveEnrichmentRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["tags"] = new JsonArray("artist", "genre"),
            ["downloadTags"] = new JsonArray("title", "trackId", "releaseId", "source", "url")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedDownloadEnrichmentTags, actual);
    }

    [Fact]
    public void ResolveEnrichmentRequestedTags_NonDownloadEnrichment_UsesOnlyEnrichmentTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveEnrichmentRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["tags"] = new JsonArray("artist", "genre"),
            ["downloadTags"] = new JsonArray("trackId", "releaseId", "source", "url")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedEnhancementOnlyTags, actual);
    }

    [Fact]
    public void ResolveEnhancementRequestedTags_UsesOnlyGapFillTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveEnhancementRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["gapFillTags"] = new JsonArray("artist", "genre"),
            ["downloadTags"] = new JsonArray("title", "trackId", "releaseId")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedMergedEnhancementTags, actual);
    }

    [Theory]
    [InlineData("year", "releaseDate")]
    [InlineData("date", "releaseDate")]
    [InlineData("length", "duration")]
    [InlineData("lyrics", "unsyncedLyrics")]
    [InlineData("cover", "albumArt")]
    public void NormalizeSupportedTagKey_MapsAliasesToCanonicalKeys(string input, string expected)
    {
        var method = typeof(AutoTagService).GetMethod(
            "NormalizeSupportedTagKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object?[] { input }) as string;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FilterSupportedTags_YearAliasIsAcceptedWhenPlatformSupportsReleaseDate()
    {
        var method = typeof(AutoTagService).GetMethod(
            "FilterSupportedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var capabilityType = typeof(AutoTagService).GetNestedType(
            "PlatformTagCapabilities",
            BindingFlags.NonPublic);
        Assert.NotNull(capabilityType);

        var capability = Activator.CreateInstance(
            capabilityType!,
            new object?[] { new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "releaseDate" }, false });
        Assert.NotNull(capability);

        var capsDictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), capabilityType!);
        var capsDictionary = Activator.CreateInstance(capsDictionaryType);
        Assert.NotNull(capsDictionary);

        var addMethod = capsDictionaryType.GetMethod("Add");
        Assert.NotNull(addMethod);
        addMethod!.Invoke(capsDictionary, new[] { "itunes", capability });

        var actual = Assert.IsType<List<string>>(method!.Invoke(
            null,
            new object?[]
            {
                RequestedYearAndArtistTags,
                ItunesPlatformOnly,
                capsDictionary!
            }));

        Assert.Equal(ExpectedReleaseDateOnly, actual);
    }
}
