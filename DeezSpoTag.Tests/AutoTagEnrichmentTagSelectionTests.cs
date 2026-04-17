using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagEnrichmentTagSelectionTests
{
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
        Assert.Equal(new[] { "artist", "genre", "trackId", "releaseId", "source", "url" }, actual);
    }

    [Fact]
    public void ResolveEnrichmentRequestedTags_NonDownloadEnrichment_DoesNotCarryDownloadOnlyTags()
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
        Assert.Equal(new[] { "artist", "genre" }, actual);
    }
}
