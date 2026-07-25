using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EnhancementQualityBatchTests
{
    [Fact]
    public void TechnicalUpgrade_FinalizationRequiresHigherIncomingQualityAndQuarantinesTheLoser()
    {
        var root = ResolveRepoRoot();
        var orchestration = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Services",
            "DownloadOrchestrationService.cs"));
        var organizer = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Services",
            "AutoTagLibraryOrganizer.cs"));

        Assert.Contains("DuplicateConflictMoveToDuplicates", orchestration, StringComparison.Ordinal);
        Assert.Contains("RequireIncomingQualityReplacement = technicalUpgrade.IsTechnicalUpgrade", orchestration, StringComparison.Ordinal);
        Assert.Contains("options.RequireIncomingQualityReplacement && !preferIncoming", organizer, StringComparison.Ordinal);
        Assert.Contains("MoveFileOverwrite(action.DestinationPath, target)", organizer, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
