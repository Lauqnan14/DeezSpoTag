using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagPlatformTagContractTests
{
    [Fact]
    public void ResolveRequestedTags_PrefersTagsThenGapFillTags()
    {
        var withTags = JsonNode.Parse("""{"tags":["genre","key"],"gapFillTags":["label"]}""")!.AsObject();
        var gapOnly = JsonNode.Parse("""{"gapFillTags":["label","bpm"]}""")!.AsObject();

        Assert.Equal(new[] { "genre", "key" }, AutoTagPlatformTagContract.ResolveRequestedTags(withTags));
        Assert.Equal(new[] { "label", "bpm" }, AutoTagPlatformTagContract.ResolveRequestedTags(gapOnly));
    }

    [Fact]
    public void FilterOfferedTags_UsesUnionOfAllWriterPlatforms()
    {
        var supported = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["deezer"] = new(StringComparer.OrdinalIgnoreCase) { "genre", "label" },
            ["shazam"] = new(StringComparer.OrdinalIgnoreCase) { "genre", "key", "composer" }
        };

        var offered = AutoTagPlatformTagContract.FilterOfferedTags(
            ["genre", "key", "composer", "mood"],
            ["deezer", "shazam"],
            supported,
            static tag => tag?.Trim());

        Assert.Equal(new[] { "genre", "key", "composer" }, offered);
    }

    [Fact]
    public void EnrichmentAndEnhancement_KeepTheSameWriterPlatformsAfterFingerprint()
    {
        var enrichment = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.EnrichmentStages.cs"));
        var enhancement = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("AutoTagPlatformTagContract.ResolveRequestedTags(baseRoot)", enrichment, StringComparison.Ordinal);
        Assert.Contains("ResolveEnhancementRequestedTags(baseRoot)", enhancement, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Where(platform => !string.Equals(platform, ShazamPlatformId",
            enrichment,
            StringComparison.Ordinal);
        Assert.Contains("includeConflictResolution: true", enhancement, StringComparison.Ordinal);
        Assert.Contains("includeSkipTagged: true", enhancement, StringComparison.Ordinal);
    }

    [Fact]
    public void ForcedFingerprint_DoesNotRemoveShazamAsAWriter()
    {
        var source = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.EnrichmentStages.cs"));
        Assert.Contains("Platforms = plan.Platforms.ToList()", source, StringComparison.Ordinal);
        Assert.Contains("ConfigureShazamFingerprintBootstrap(stageRoot)", source, StringComparison.Ordinal);
        var enhancement = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        Assert.Contains("ConfigureShazamFingerprintBootstrap(stageRoot)", enhancement, StringComparison.Ordinal);
        Assert.DoesNotContain("enrichmentPlatforms.Add(ShazamPlatformId)", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Join(directory, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
