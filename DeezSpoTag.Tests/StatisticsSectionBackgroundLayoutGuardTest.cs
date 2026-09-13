using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class StatisticsSectionBackgroundLayoutGuardTest
{
    private static string View => File.ReadAllText(Path.Join(
        TestSourcePaths.RepositoryRoot,
        "DeezSpoTag.Web",
        "Views",
        "Statistics",
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
    public void StatisticsModulesUseTheAutoTagBackgroundHierarchy()
    {
        var moduleRule = CssRuleFor(".statistics-container .statistics-module {");
        Assert.Contains("background: var(--bg-secondary);", moduleRule, StringComparison.Ordinal);

        var subsectionRule = CssRuleFor(".statistics-subsection {");
        Assert.Contains(
            "border: 1px solid color-mix(in srgb, var(--primary-color) 18%, transparent);",
            subsectionRule,
            StringComparison.Ordinal);
        Assert.Contains("border-radius: 12px;", subsectionRule, StringComparison.Ordinal);
        Assert.Contains(
            "background: color-mix(in srgb, var(--bg-secondary) 94%, black 6%);",
            subsectionRule,
            StringComparison.Ordinal);

        Assert.Contains(
            "<div class=\"statistics-subsection\">\n            <div class=\"stats-grid\">\n                <div class=\"stat-card\">\n                    <h4>Active Downloads</h4>",
            View,
            StringComparison.Ordinal);

        foreach (var selector in new[]
        {
            ".stat-card",
            ".library-card",
            ".library-breakdown-grid > .text-muted",
            ".detail-panel",
            ".breakdown-list .text-muted"
        })
        {
            Assert.Contains(
                "background: var(--bg-tertiary);",
                CssRuleFor($"{selector} {{"),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LibraryAndScanBreakdownsExposeClearInternalBoundaries()
    {
        foreach (var selector in new[] { ".library-card", ".detail-panel" })
        {
            Assert.Contains(
                "border: 1px solid color-mix(in srgb, var(--primary-color) 32%, transparent);",
                CssRuleFor($"{selector} {{"),
                StringComparison.Ordinal);
        }

        foreach (var selector in new[] { ".library-card-header", ".detail-panel h4" })
        {
            var headerRule = CssRuleFor($"{selector} {{");
            Assert.Contains("padding-bottom: 10px;", headerRule, StringComparison.Ordinal);
            Assert.Contains(
                "border-bottom: 1px solid color-mix(in srgb, var(--primary-color) 18%, transparent);",
                headerRule,
                StringComparison.Ordinal);
        }

        foreach (var selector in new[] { ".library-card-stats .library-stat", ".breakdown-item" })
        {
            var itemRule = CssRuleFor($"{selector} {{");
            Assert.Contains("padding: 8px 10px;", itemRule, StringComparison.Ordinal);
            Assert.Contains(
                "border: 1px solid color-mix(in srgb, var(--primary-color) 18%, transparent);",
                itemRule,
                StringComparison.Ordinal);
            Assert.Contains("border-radius: 8px;", itemRule, StringComparison.Ordinal);
            Assert.Contains(
                "background: color-mix(in srgb, var(--bg-secondary) 94%, black 6%);",
                itemRule,
                StringComparison.Ordinal);
        }
    }
}
