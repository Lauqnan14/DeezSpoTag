using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SettingsSectionBackgroundLayoutGuardTest
{
    private static string View => File.ReadAllText(Path.Join(
        TestSourcePaths.RepositoryRoot,
        "DeezSpoTag.Web",
        "Views",
        "Settings",
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
    public void TopLevelSettingsSectionsUseTheAutoTagCardHierarchy()
    {
        var sectionRule = CssRuleFor(".settings-page .settings-width > .settings-section {");
        Assert.Contains("background: var(--bg-secondary);", sectionRule, StringComparison.Ordinal);
        Assert.Contains(
            "border: 1px solid color-mix(in srgb, var(--primary-color) 18%, transparent);",
            sectionRule,
            StringComparison.Ordinal);
        Assert.Contains("border-radius: 12px;", sectionRule, StringComparison.Ordinal);

        var contentRule = CssRuleFor(".settings-page .settings-width > .settings-section > .settings-content {");
        Assert.Contains(
            "background: color-mix(in srgb, var(--bg-secondary) 94%, black 6%);",
            contentRule,
            StringComparison.Ordinal);
        Assert.Contains(
            "border: 1px solid color-mix(in srgb, var(--primary-color) 18%, transparent);",
            contentRule,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsControlsRemainTheLighterLayer()
    {
        var controlRule = CssRuleFor(".form-group input,");
        Assert.Contains("background: var(--bg-tertiary);", controlRule, StringComparison.Ordinal);

        Assert.Contains(
            ".settings-page .settings-content .dropdown-trigger {\n    background: var(--bg-tertiary);",
            View,
            StringComparison.Ordinal);
        Assert.Contains(
            ".settings-page .settings-content .checkbox-item",
            View,
            StringComparison.Ordinal);
    }
}
