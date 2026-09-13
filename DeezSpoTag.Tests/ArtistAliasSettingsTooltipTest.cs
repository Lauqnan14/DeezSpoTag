using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistAliasSettingsTooltipTest
{
    [Fact]
    public void MergeExplanationTargetsTheArtistSearchLabel()
    {
        var view = File.ReadAllText(Path.Join(
            TestSourcePaths.RepositoryRoot,
            "DeezSpoTag.Web",
            "Views",
            "Settings",
            "Index.cshtml"));

        const string explanation = "Merge two or more artist names that refer to the same artist";
        var explanationAt = view.IndexOf(explanation, StringComparison.Ordinal);
        Assert.True(explanationAt >= 0, "Artist alias merge explanation is missing");

        var tagStart = view.LastIndexOf("<div", explanationAt, StringComparison.Ordinal);
        var tagEnd = view.IndexOf('>', tagStart);
        Assert.True(tagStart >= 0 && tagEnd > tagStart, "Artist alias merge explanation container is missing");

        var openingTag = view.Substring(tagStart, tagEnd - tagStart + 1);
        Assert.Contains("class=\"warning-text\"", openingTag, StringComparison.Ordinal);
        Assert.Contains("data-tooltip-target=\"artistAliasSearch\"", openingTag, StringComparison.Ordinal);
    }
}
