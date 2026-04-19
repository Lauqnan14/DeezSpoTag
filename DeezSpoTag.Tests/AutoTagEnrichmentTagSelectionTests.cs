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
        "genre",
        "title",
        "trackId",
        "releaseId",
        "source",
        "url"
    };

    private static readonly string[] ExpectedEnhancementOnlyTags =
    {
        "artist",
        "genre",
        "trackId",
        "releaseId",
        "source",
        "url"
    };

    [Fact]
    public void ResolveEnrichmentRequestedTags_DownloadEnrichment_CarriesIdAndSourceTagsFromDownloadTags()
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

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root, "download_enrichment" }));
        Assert.Equal(ExpectedDownloadEnrichmentTags, actual);
    }

    [Fact]
    public void ResolveEnrichmentRequestedTags_NonDownloadEnrichment_StillMergesDownloadTagsForConsistency()
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

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root, "enhancement_only" }));
        Assert.Equal(ExpectedEnhancementOnlyTags, actual);
    }

    [Fact]
    public void ResolveEnhancementRequestedTags_MergesDownloadTagsForConsistency()
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
        Assert.Equal(new[] { "artist", "genre", "title", "trackId", "releaseId" }, actual);
    }
}
