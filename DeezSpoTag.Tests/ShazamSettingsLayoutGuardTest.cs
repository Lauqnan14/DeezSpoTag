using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamSettingsLayoutGuardTest
{
    private static string View => File.ReadAllText(Path.Join(
        TestSourcePaths.RepositoryRoot,
        "DeezSpoTag.Web",
        "Views",
        "Settings",
        "Index.cshtml"));

    [Fact]
    public void ShazamCheckboxesUseTwoColumnsAndTwoRows()
    {
        Assert.Contains(
            ".shazam-options-grid {\n    display: grid;\n    grid-template-columns: repeat(2, minmax(0, 1fr));",
            View,
            StringComparison.Ordinal);
        Assert.Contains(
            "@@media (max-width: 768px) {\n    .shazam-options-grid {\n        grid-template-columns: 1fr;\n    }",
            View,
            StringComparison.Ordinal);

        var sectionStart = View.IndexOf("<div class=\"settings-section\" id=\"shazam-settings\">", StringComparison.Ordinal);
        var sectionEnd = View.IndexOf("<div class=\"settings-section settings-section--toggle-style\">", sectionStart, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0 && sectionEnd > sectionStart, "Shazam settings section is missing");

        var section = View.Substring(sectionStart, sectionEnd - sectionStart);
        var gridStart = section.IndexOf("<div class=\"shazam-options-grid\">", StringComparison.Ordinal);
        var durationStart = section.IndexOf("id=\"shazamCaptureDurationSeconds\"", StringComparison.Ordinal);
        Assert.True(gridStart >= 0 && durationStart > gridStart, "Shazam options grid must precede capture duration");

        foreach (var id in new[]
        {
            "shazamEnabled",
            "shazamUseCenteredOverlay",
            "shazamAllowHttpFileFallback",
            "shazamRemoteMemoryOnly"
        })
        {
            var optionStart = section.IndexOf($"id=\"{id}\"", gridStart, StringComparison.Ordinal);
            Assert.True(optionStart > gridStart && optionStart < durationStart, $"{id} must be inside the Shazam options grid");
        }
    }
}
