using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the rendering of the "Flag album edition conflicts for review" control.
///
/// It used to be a Bootstrap `form-check` block nested inside the 3-column Version grid cell
/// belonging to "Use ID3v2.4", carrying its explanation as a `<div class="form-text">`. The
/// AutoTag page loads only autotag.css — never settings.css, which is where this app defines
/// `.form-check` and `.form-text` — so that explanation rendered at full body size inside a grid
/// cell, stretched the cell, and dragged the checkboxes beside it out of alignment.
///
/// The control is now a normal grid cell like its neighbours, and the explanation is a tooltip
/// on the label.
/// </summary>
public sealed class EditionConflictReviewLayoutGuardTest
{
    private const string Explanation =
        "When a provider matches a different edition of the same album (standard vs deluxe, remaster), " +
        "keep the downloaded edition and flag the file for review instead of rewriting it.";

    private static string Read(string relativePath) => File.ReadAllText(
        Path.Join(TestSourcePaths.RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string View => Read("DeezSpoTag.Web/Views/AutoTag/Index.cshtml");

    /// <summary>
    /// The markup for the control only, bounded by the Version grid it now lives in, so unrelated
    /// `form-check` usage elsewhere in the view cannot make these assertions pass or fail.
    /// </summary>
    private static string ControlRegion()
    {
        var gridStart = View.IndexOf("technical-checkbox-grid", StringComparison.Ordinal);
        Assert.True(gridStart >= 0, "the Version checkbox grid is missing from the view");

        var controlAt = View.IndexOf("id=\"autotag-edition-conflict-review\"", gridStart, StringComparison.Ordinal);
        Assert.True(controlAt > gridStart, "the edition-conflict control is not inside the Version grid");

        var lastMember = View.IndexOf("id=\"useNullSeparator\"", controlAt, StringComparison.Ordinal);
        Assert.True(lastMember > controlAt, "useNullSeparator is missing from the Version grid");

        var gridEnd = View.IndexOf("</div>", lastMember, StringComparison.Ordinal);
        Assert.True(gridEnd > lastMember, "the Version grid is not closed after its members");

        return View.Substring(gridStart, gridEnd - gridStart);
    }

    [Fact]
    public void TheControlIsNoLongerRenderedWithBootstrapFormCheckClasses()
    {
        var region = ControlRegion();

        Assert.DoesNotContain("class=\"form-check\"", region, StringComparison.Ordinal);
        Assert.DoesNotContain("form-check-input", region, StringComparison.Ordinal);
        Assert.DoesNotContain("form-check-label", region, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"form-text\"", region, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExplanationIsATooltipOnTheLabelNotVisibleInlineText()
    {
        var region = ControlRegion();

        // The explanation must appear as the tooltip's title...
        Assert.Contains($"title=\"{Explanation}\"", region, StringComparison.Ordinal);

        // ...and must NOT be a visible text node, which is what broke the layout.
        Assert.DoesNotContain("class=\"helper\"", region, StringComparison.Ordinal);
        Assert.DoesNotContain($">{Explanation}<", region, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTooltipFollowsTheHousesAutotagTooltipConvention()
    {
        var region = ControlRegion();

        Assert.Contains("autotag-tooltip-icon", region, StringComparison.Ordinal);
        // The house pattern pairs title with aria-label for accessibility.
        Assert.Contains($"aria-label=\"{Explanation}\"", region, StringComparison.Ordinal);
        Assert.Contains("fa-question-circle", region, StringComparison.Ordinal);
    }

    [Fact]
    public void TheControlIsANormalGridCellWithTheHousesCheckboxMarkup()
    {
        var region = ControlRegion();

        Assert.Contains("class=\"checkbox-group\"", region, StringComparison.Ordinal);
        Assert.Contains("id=\"autotag-edition-conflict-review\"", region, StringComparison.Ordinal);
        Assert.Contains("for=\"autotag-edition-conflict-review\"", region, StringComparison.Ordinal);

        // No leftover wrapper that would re-introduce a full-width row for a control that no
        // longer needs one.
        Assert.DoesNotContain("id=\"autotag-edition-conflict-review-group\"", View, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckboxIdIsUnchangedSoTheFeatureKeepsWorking()
    {
        // autotag.js reads and restores this exact id; renaming it would silently disable the
        // option. The markup fix must not touch it.
        var script = Read("DeezSpoTag.Web/wwwroot/js/autotag.js");

        Assert.Contains("getChecked(\"autotag-edition-conflict-review\"", script, StringComparison.Ordinal);
        Assert.Contains("setChecked(\"autotag-edition-conflict-review\"", script, StringComparison.Ordinal);
        Assert.Contains("id=\"autotag-edition-conflict-review\"", View, StringComparison.Ordinal);
    }
}
