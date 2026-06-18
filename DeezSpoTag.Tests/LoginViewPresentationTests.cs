using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LoginViewPresentationTests
{
    [Fact]
    public void QobuzAndTidalPublicApiProviders_AreCollapsible()
    {
        var repoRoot = ResolveRepoRoot();
        var loginSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml"));

        Assert.Contains("id=\"qobuzProviderToggle\"", loginSource, StringComparison.Ordinal);
        Assert.Contains("id=\"qobuzProviderEditor\" hidden", loginSource, StringComparison.Ordinal);
        Assert.Contains("id=\"tidalProviderToggle\"", loginSource, StringComparison.Ordinal);
        Assert.Contains("id=\"tidalProviderEditor\" hidden", loginSource, StringComparison.Ordinal);
        Assert.Contains("bindProviderPanelToggle('qobuzProviderToggle', 'qobuzProviderEditor')", loginSource, StringComparison.Ordinal);
        Assert.Contains("bindProviderPanelToggle('tidalProviderToggle', 'tidalProviderEditor')", loginSource, StringComparison.Ordinal);
        Assert.Contains("setProviderPanelExpanded", loginSource, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root.");
    }
}
