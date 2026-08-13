using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagSharedClassificationToggleContractTests
{
    [Fact]
    public void AutoTagUi_SharesClassificationTogglesAcrossEnrichmentAndEnhancement()
    {
        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "../../../../"));
        var script = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));

        Assert.Contains("{ tag: \"genre\", label: \"Genre\"", script, StringComparison.Ordinal);
        Assert.Contains("{ tag: \"style\", label: \"Style / Subgenre\"", script, StringComparison.Ordinal);
        Assert.Contains("{ tag: \"mood\", label: \"Mood\"", script, StringComparison.Ordinal);
        Assert.Contains("{ tag: \"activity\", label: \"Activity\"", script, StringComparison.Ordinal);
        Assert.Contains("name === \"tags\" || name === \"gapFillTags\"", script, StringComparison.Ordinal);
        Assert.Contains("state.config.tags = sharedTags", script, StringComparison.Ordinal);
        Assert.Contains("state.config.gapFillTags = sharedTags.slice()", script, StringComparison.Ordinal);
        Assert.Contains("const sharedTags = enrichmentTags.length > 0 ? enrichmentTags : enhancementTags", script, StringComparison.Ordinal);
        Assert.Contains("id=\"autotag-tags\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"gap-fill-tags\"", view, StringComparison.Ordinal);
        Assert.Contains("data-tags-action=\"enable\" data-tags-target=\"gapFillTags\"", view, StringComparison.Ordinal);
        Assert.Contains("data-tags-action=\"disable\" data-tags-target=\"gapFillTags\"", view, StringComparison.Ordinal);
        Assert.Contains("data-tags-action=\"toggle\" data-tags-target=\"gapFillTags\"", view, StringComparison.Ordinal);
    }
}
