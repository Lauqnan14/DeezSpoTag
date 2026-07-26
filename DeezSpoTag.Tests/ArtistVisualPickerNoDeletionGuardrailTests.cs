using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistVisualPickerNoDeletionGuardrailTests
{
    [Fact]
    public void PickerPreservesExistingArtworkAndUsesArtworkOnlyRefresh()
    {
        var root = FindSourceRoot();
        var script = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "library.js"));
        var controller = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryArtistArtworkApiController.cs"));
        var loader = ExtractBetween(
            script,
            "async function loadExternalArtistVisuals",
            "function renderArtistVisualPicker");

        Assert.DoesNotContain("cachedPickerImages = []", loader, StringComparison.Ordinal);
        Assert.Contains("mergeArtistVisualPickerResult(visuals, cached)", loader, StringComparison.Ordinal);
        Assert.Contains("mergeArtistVisualPickerResult(visuals, refreshed)", loader, StringComparison.Ordinal);
        Assert.True(
            loader.IndexOf("/artwork`", StringComparison.Ordinal)
            < loader.IndexOf("/artwork/refresh", StringComparison.Ordinal),
            "Cached artwork must load before an artwork refresh starts.");
        Assert.DoesNotContain("ArtistMetadataCacheRefreshService", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshArtistAsync", controller, StringComparison.Ordinal);
        Assert.Contains("_artwork.RefreshAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("forceProviderRefresh: force", controller, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Services")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("DeezSpoTag source root was not found.");
    }
}
