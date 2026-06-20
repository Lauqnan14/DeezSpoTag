using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalTrackResolverGuardrailTests
{
    [Fact]
    public void TidalResolver_DoesNotAcceptFirstSearchResultWithoutValidation()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepoRoot(),
            "DeezSpoTag.Services",
            "Download",
            "Tidal",
            "TidalDownloadService.cs"));

        Assert.Contains("FindValidatedMetadataMatch", source);
        Assert.DoesNotContain("return allTracks[0];", source);
        Assert.DoesNotContain("return allTracks.First()", source);
    }

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Services"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
