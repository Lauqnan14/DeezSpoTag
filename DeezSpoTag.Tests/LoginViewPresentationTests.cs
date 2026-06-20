using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LoginViewPresentationTests
{
    [Fact]
    public void QobuzAndTidalPersonalSections_RenderFromInitialServerAuthState()
    {
        var repoRoot = ResolveRepoRoot();
        var loginSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml"));

        Assert.Contains("@inject PlatformAuthService PlatformAuthService", loginSource, StringComparison.Ordinal);
        Assert.Contains("var initialPlatformAuthState = await PlatformAuthService.LoadAsync();", loginSource, StringComparison.Ordinal);
        Assert.Contains("var initialQobuzConnected =", loginSource, StringComparison.Ordinal);
        Assert.Contains("var initialTidalConnected = initialTidalAuth?.CredentialsValid == true;", loginSource, StringComparison.Ordinal);
        Assert.Contains("id=\"qobuzLoginFormSection\" class=\"@(initialQobuzConnected ? \"hidden\" : \"\")\"", loginSource, StringComparison.Ordinal);
        Assert.Contains("WrapperCssClass = initialQobuzConnected ? \"settings-group mt-6\" : \"settings-group mt-6 hidden\"", loginSource, StringComparison.Ordinal);
        Assert.Contains("id=\"tidalLoginFormSection\" class=\"@(initialTidalConnected ? \"hidden\" : \"\")\"", loginSource, StringComparison.Ordinal);
        Assert.Contains("WrapperCssClass = initialTidalConnected ? \"settings-group mt-6\" : \"settings-group mt-6 hidden\"", loginSource, StringComparison.Ordinal);
        Assert.DoesNotContain("hydratePlatformAuthStateFromCache", loginSource, StringComparison.Ordinal);
        Assert.Contains("<span class=\"block mb-2\">User Auth Token</span>", loginSource, StringComparison.Ordinal);
        Assert.DoesNotContain("User Auth Token (optional)", loginSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Optional Qobuz user auth token", loginSource, StringComparison.Ordinal);
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
