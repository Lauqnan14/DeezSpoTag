using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayDurableTracklistGuardrailTests
{
    [Fact]
    public void TracklistUsesPersistedBoomplayMappingsInsteadOfBrowserResolver()
    {
        var root = FindSourceRoot();
        var view = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));
        var sourceSetStart = view.IndexOf("const deezerMatchedExternalSources", StringComparison.Ordinal);
        var sourceSetEnd = view.IndexOf("]);", sourceSetStart, StringComparison.Ordinal);
        var sourceSet = view[sourceSetStart..sourceSetEnd];
        var controller = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "BoomplayApiController.cs"));

        Assert.Contains("'boomplay'", sourceSet, StringComparison.Ordinal);
        Assert.Contains("GetBoomplayDeezerTrackMappingsAsync", controller, StringComparison.Ordinal);
        Assert.Contains("deezerId", controller, StringComparison.Ordinal);
        Assert.Contains("/api/boomplay/resolve-deezer", view, StringComparison.Ordinal);
        Assert.Contains("buildBoomplayDurableResolveQuery", view, StringComparison.Ordinal);
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
