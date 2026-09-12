using System;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Locks the full wire for the "Remove featured artists from album title" option.
///
/// A settings option has to survive seven places to actually reach the tagging engine. This test
/// fails if any link is dropped, which is the failure mode that makes a checkbox look like it
/// silently does nothing.
/// </summary>
public sealed class RemoveFeaturedFromAlbumTitleWiringTest
{
    private const string Property = "RemoveFeaturedFromAlbumTitle";
    private const string JsonKey = "removeFeaturedFromAlbumTitle";
    private const string FieldId = "removeFeaturedFromAlbumTitle";

    private static string RepoFile(params string[] parts)
        => Path.Join(TestSourcePaths.RepositoryRoot, Path.Join(parts));

    private static string Read(params string[] parts) => File.ReadAllText(RepoFile(parts));

    [Fact]
    public void TheGlobalSettingsModelCarriesTheOptionAndKeepsTheExistingDefault()
    {
        var settings = new DeezSpoTagSettings();

        // Default must be off: existing users keep their current folder naming until they opt in.
        Assert.False(settings.RemoveFeaturedFromAlbumTitle);
    }

    [Fact]
    public void TheProfileTechnicalSettingsCarryTheOption()
    {
        var technical = new TechnicalTagSettings();

        Assert.False(technical.RemoveFeaturedFromAlbumTitle);
    }

    [Fact]
    public void TheProfileConfigJsonPublishesTheOptionForTheUi()
    {
        var profile = new TaggingProfile
        {
            Technical = new TechnicalTagSettings { RemoveFeaturedFromAlbumTitle = true }
        };

        var json = new AutoTagConfigBuilder().BuildConfigJson(profile);

        // The builder serialises TechnicalTagSettings wholesale, so a rename here would silently
        // stop the checkbox from ever being restored.
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json!);
        var technical = document.RootElement.GetProperty("technical");
        Assert.True(technical.GetProperty(JsonKey).GetBoolean());
    }

    [Theory]
    [InlineData("DeezSpoTag.Services", "Settings", "TaggingProfileSettingsOverlay.cs")]
    [InlineData("DeezSpoTag.Services", "Download", "Shared", "TechnicalLyricsSettingsApplier.cs")]
    [InlineData("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.TagWrites.cs")]
    [InlineData("DeezSpoTag.Web", "Services", "AutoTagLibraryOrganizer.cs")]
    public void EveryApplierCopiesTheOptionOntoRuntimeSettings(params string[] path)
    {
        var source = Read(path);

        Assert.Contains(Property, source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValidatorRegistersTheOptionAndShipsItAsAFalseDefault()
    {
        var source = Read("DeezSpoTag.Services", "Download", "Shared", "Settings", "DeezSpoTagSettingsValidator.cs");

        Assert.Contains($"nameof(settings.{Property})", source, StringComparison.Ordinal);
        Assert.Contains($"{Property} = false,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsCopyKeepsTheOption()
    {
        var source = Read("DeezSpoTag.Services", "Download", "Shared", "Advanced", "PerformanceOptimizationService.cs");

        Assert.Contains($"{Property} = baseSettings.{Property}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTechnicalTabOffersTheCheckboxWithATooltipThatMentionsTheAlbumFolder()
    {
        var view = Read("DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml");

        Assert.Contains($"id=\"{FieldId}\"", view, StringComparison.Ordinal);
        Assert.Contains($"for=\"{FieldId}\"", view, StringComparison.Ordinal);
        Assert.Contains("Remove featured artists from album title", view, StringComparison.Ordinal);

        // The tooltip must explain the album folder consequence, not just the tag.
        var checkedBox = view.IndexOf(FieldId, StringComparison.Ordinal);
        var tooltipWindow = view.Substring(checkedBox, Math.Min(1400, view.Length - checkedBox));
        Assert.Contains("album folder", tooltipWindow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%album%", tooltipWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTechnicalTabScriptSavesAndRestoresTheCheckbox()
    {
        var script = Read("DeezSpoTag.Web", "wwwroot", "js", "autotag.js");

        Assert.Contains($"applyFieldCheckedWhenBoolean(\"{FieldId}\"", script, StringComparison.Ordinal);
        Assert.Contains($"getChecked(\"{FieldId}\"", script, StringComparison.Ordinal);
    }
}
