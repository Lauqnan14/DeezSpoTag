using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MediaOperationsSectionBackgroundLayoutGuardTest
{
    private static string View => File.ReadAllText(Path.Join(
        TestSourcePaths.RepositoryRoot,
        "DeezSpoTag.Web",
        "Views",
        "Activities",
        "Index.cshtml"));

    private static string CssRuleFor(string selector)
    {
        var ruleAt = View.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(ruleAt >= 0, $"CSS selector is missing: {selector}");

        var ruleEnd = View.IndexOf('}', ruleAt);
        Assert.True(ruleEnd > ruleAt, $"CSS rule is not closed: {selector}");
        return View.Substring(ruleAt, ruleEnd - ruleAt + 1);
    }

    [Fact]
    public void MelodayAndMetadataUpdaterUseTheAutoTagBackgroundHierarchy()
    {
        Assert.Contains(
            ".media-operations-tab #meloday-card,\n.media-operations-tab #metadata-updater-card {\n    background: var(--bg-secondary);\n}",
            View,
            StringComparison.Ordinal);

        var insetRule = CssRuleFor(
            ".media-operations-tab .meloday-section,\n.media-operations-tab #metadata-updater-card .metadata-updater-select-row,\n.media-operations-tab #metadata-updater-card .metadata-updater-option-group {");
        Assert.Contains(
            "border: 1px solid color-mix(in srgb, var(--primary-color) 18%, transparent);",
            insetRule,
            StringComparison.Ordinal);
        Assert.Contains("border-radius: 12px;", insetRule, StringComparison.Ordinal);
        Assert.Contains(
            "background: color-mix(in srgb, var(--bg-secondary) 94%, black 6%);",
            insetRule,
            StringComparison.Ordinal);

        foreach (var selector in new[]
        {
            ".media-operations-tab .meloday-time-slot",
            ".media-operations-tab .meloday-library-card",
            ".media-operations-tab .meloday-library-max select",
            ".media-operations-tab .meloday-slot-chip",
            ".media-operations-tab .meloday-target-option",
            ".media-operations-tab .meloday-advanced-grid input",
            ".media-operations-tab #metadata-updater-card .tool-card-controls select",
            ".media-operations-tab .metadata-updater-option"
        })
        {
            Assert.Contains(
                "background: var(--bg-tertiary);",
                CssRuleFor($"{selector} {{"),
                StringComparison.Ordinal);
        }
    }
}
