using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MediaManagementSettingsTabRemovalGuardrailTests
{
    [Fact]
    public void MediaManagementView_DoesNotContainRemovedSettingsTabOrLegacyLibrarySettingsFields()
    {
        var repoRoot = ResolveRepoRoot();
        var viewPath = Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "MediaManagement", "Index.cshtml");
        Assert.True(File.Exists(viewPath), $"Missing media management view: {viewPath}");

        var source = File.ReadAllText(viewPath);
        Assert.DoesNotContain("id=\"settings-tab\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"settings-content\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Library Settings", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Fuzzy threshold", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Include all folders", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Open Activities", source, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
