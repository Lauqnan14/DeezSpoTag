using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagSectionBackgroundLayoutGuardTest
{
    private static string Read(string relativePath) => File.ReadAllText(
        Path.Join(TestSourcePaths.RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string View => Read("DeezSpoTag.Web/Views/AutoTag/Index.cshtml");
    private static string Css => Read("DeezSpoTag.Web/wwwroot/css/autotag.css");

    private static readonly string[] MarkedCardTitles =
    {
        "Profile Manager",
        "Library Folders",
        "Folder Structure",
        "Multi-Artist Handling",
        "File Templates",
        "Run Scope",
        "Download Tag Metadata Source",
        "Download Tags",
        "Enrichment Tags",
        "Manual Enrichment",
        "Platforms",
        "Matching Settings",
        "Artwork Settings",
        "Lyrics Settings",
        "ID3 & Format Settings",
        "Text Processing",
        "Style/Genre Handling",
        "Tag Overwrite Settings"
    };

    private static string OpeningCardTagFor(string title)
    {
        var contentAt = View.IndexOf(
            "<div class=\"tab-content\" id=\"autotagTabsContent\">",
            StringComparison.Ordinal);
        Assert.True(contentAt >= 0, "AutoTag tab content is missing");

        var titleAt = View.IndexOf($"></i>{title}", contentAt, StringComparison.Ordinal);
        Assert.True(titleAt >= 0, $"{title} heading is missing");

        var cardAt = View.LastIndexOf("<div class=\"card bg-darker", titleAt, StringComparison.Ordinal);
        Assert.True(cardAt >= 0, $"{title} card is missing");

        var cardTagEnd = View.IndexOf('>', cardAt);
        return View.Substring(cardAt, cardTagEnd - cardAt + 1);
    }

    private static string CssRuleFor(string selector)
    {
        var ruleAt = Css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(ruleAt >= 0, $"CSS selector is missing: {selector}");

        var ruleEnd = Css.IndexOf('}', ruleAt);
        Assert.True(ruleEnd > ruleAt, $"CSS rule is not closed: {selector}");
        return Css.Substring(ruleAt, ruleEnd - ruleAt + 1);
    }

    [Fact]
    public void EveryGenericAutoTagCardUsesTheSharedBackgroundMarker()
    {
        foreach (var title in MarkedCardTitles)
        {
            Assert.Contains(
                "autotag-quality-background",
                OpeningCardTagFor(title),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SpecializedDownloadCardsUseTheExistingSectionWrapper()
    {
        foreach (var title in new[] { "Download Tag Metadata Source", "Download Tags" })
        {
            var titleAt = View.IndexOf(title, StringComparison.Ordinal);
            var bodyAt = View.IndexOf("<div class=\"card-body autotag-card-body\">", titleAt, StringComparison.Ordinal);
            var wrapperAt = View.IndexOf("<div class=\"download-section\">", bodyAt, StringComparison.Ordinal);
            var nextCardAt = View.IndexOf("<div class=\"card", bodyAt + 1, StringComparison.Ordinal);

            Assert.True(bodyAt > titleAt, $"{title} card body is missing");
            Assert.True(wrapperAt > bodyAt, $"{title} section wrapper is missing");
            Assert.True(nextCardAt < 0 || wrapperAt < nextCardAt, $"{title} wrapper belongs to the next card");
        }
    }

    [Fact]
    public void FolderStructureExtrasExposeTheSharedPanelAndStyleEachControl()
    {
        var containerRule = CssRuleFor(".folder-structure-extras {");
        Assert.Contains("display: grid;", containerRule, StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: repeat(3, minmax(0, 1fr));",
            containerRule,
            StringComparison.Ordinal);

        Assert.Contains(
            "@media (max-width: 767px) {\n    .autotag-page .folder-structure-extras {\n        grid-template-columns: 1fr;\n    }\n}",
            Css,
            StringComparison.Ordinal);
        Assert.Contains("padding: 0;", containerRule, StringComparison.Ordinal);
        Assert.Contains("border: 0;", containerRule, StringComparison.Ordinal);
        Assert.Contains("background: transparent;", containerRule, StringComparison.Ordinal);
        Assert.Contains("margin-bottom: 0;", containerRule, StringComparison.Ordinal);

        var checkboxRule = CssRuleFor(".folder-structure-extras > .checkbox-group {");
        Assert.Contains("min-height: 36px;", checkboxRule, StringComparison.Ordinal);
        Assert.Contains("padding: 8px 10px;", checkboxRule, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--border-secondary);", checkboxRule, StringComparison.Ordinal);
        Assert.Contains("border-radius: 10px;", checkboxRule, StringComparison.Ordinal);
        Assert.Contains("background: var(--bg-tertiary);", checkboxRule, StringComparison.Ordinal);
    }

    [Fact]
    public void GapFillingTagsUseTheExistingEnhancementSectionPanel()
    {
        var gapAt = View.IndexOf("<!-- Gap Filling -->", StringComparison.Ordinal);
        var sidecarsAt = View.IndexOf("<!-- Sidecars -->", gapAt, StringComparison.Ordinal);
        Assert.True(gapAt >= 0 && sidecarsAt > gapAt, "Gap Filling region is missing");

        var gapRegion = View.Substring(gapAt, sidecarsAt - gapAt);
        var tagsTitleAt = gapRegion.IndexOf(
            "<div class=\"playlist-settings-section-title\">Tags</div>",
            StringComparison.Ordinal);
        Assert.True(tagsTitleAt >= 0, "Gap Filling Tags section title is missing");

        var sectionAt = gapRegion.LastIndexOf(
            "<section class=\"playlist-settings-section\">",
            tagsTitleAt,
            StringComparison.Ordinal);
        var sectionEnd = gapRegion.IndexOf("</section>", tagsTitleAt, StringComparison.Ordinal);
        Assert.True(sectionAt >= 0 && sectionEnd > tagsTitleAt, "Gap Filling Tags section is incomplete");

        var tagsSection = gapRegion.Substring(sectionAt, sectionEnd - sectionAt);
        Assert.Contains("data-tags-target=\"gapFillTags\"", tagsSection, StringComparison.Ordinal);
        Assert.Contains("id=\"gap-fill-tags\"", tagsSection, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingReferenceAndPlatformSpecificDesignsStayIndependent()
    {
        Assert.DoesNotContain(
            "autotag-quality-background",
            OpeningCardTagFor("Platform-specific Configuration"),
            StringComparison.Ordinal);

        foreach (var title in new[] { "Gap Filling", "Sidecars", "Folder Uniformity", "Quality Checks" })
        {
            Assert.DoesNotContain(
                "autotag-quality-background",
                OpeningCardTagFor(title),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SharedMarkerReusesTheQualityChecksPanelTreatment()
    {
        Assert.Contains(
            ".autotag-page .autotag-quality-background .download-section",
            Css,
            StringComparison.Ordinal);
        Assert.DoesNotContain("technical-quality-background", View, StringComparison.Ordinal);
        Assert.DoesNotContain("technical-quality-background", Css, StringComparison.Ordinal);
        Assert.Contains(
            "border: 1px solid color-mix(in srgb, var(--primary-color) 18%, transparent);",
            Css,
            StringComparison.Ordinal);
        Assert.Contains("border-radius: 12px;", Css, StringComparison.Ordinal);
        Assert.Contains(
            "background: color-mix(in srgb, var(--bg-secondary) 94%, black 6%);",
            Css,
            StringComparison.Ordinal);
    }
}
