using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayLoginUiGuardrailTest
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void Boomplay_IsAuthenticationRequiredInPlatformRegistry()
    {
        var field = typeof(PlatformRegistryApiController).GetField(
            "AuthRequiredPlatforms",
            BindingFlags.NonPublic | BindingFlags.Static);

        var platforms = Assert.IsAssignableFrom<IEnumerable<string>>(field?.GetValue(null));
        Assert.Contains("boomplay", platforms);
    }

    [Fact]
    public void Boomplay_PreLoginSetup_InstallsBookmarkletBeforeSessionLogin()
    {
        var pane = ExtractBetween(
            ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml"),
            "<div class=\"tab-pane\" id=\"boomplay-login\"",
            "<div class=\"tab-pane\" id=\"plex-login\"");

        Assert.Contains("Before logging in", pane, StringComparison.Ordinal);
        Assert.Contains("bookmarks bar", pane, StringComparison.Ordinal);
        Assert.Contains("id=\"boomplayLoginFormSection\" class=\"@(initialBoomplayConnected ? \"hidden\" : \"\")\"", pane, StringComparison.Ordinal);
        Assert.True(
            pane.IndexOf("id=\"boomplayImportBookmarklet\"", StringComparison.Ordinal)
            < pane.IndexOf("id=\"boomplayCookie\"", StringComparison.Ordinal));
        Assert.True(
            pane.IndexOf("id=\"boomplayLoginFormSection\"", StringComparison.Ordinal)
            < pane.IndexOf("ContainerId = \"boomplayLoggedInInfo\"", StringComparison.Ordinal));
        Assert.DoesNotContain("public-api-provider-panel", pane, StringComparison.Ordinal);
    }

    [Fact]
    public void Boomplay_UsesTheSharedConnectedStateTransition()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");
        var toggle = ExtractBetween(
            login,
            "function togglePlatformLoginState(platform, isConnected)",
            "function renderDiscogsInfo(auth)");
        var site = ReadSource("DeezSpoTag.Web/wwwroot/js/site.js");

        Assert.DoesNotContain("platform === 'boomplay'", toggle, StringComparison.Ordinal);
        Assert.DoesNotContain("Boomplay works without any login", site, StringComparison.Ordinal);
        Assert.DoesNotContain("Boomplay requires no login", site, StringComparison.Ordinal);
        Assert.Contains("authData.boomplay?.connected === true", site, StringComparison.Ordinal);
        Assert.Contains("const connected = data?.connected === true;", login, StringComparison.Ordinal);
        Assert.Contains("const isConnected = info?.connected === true;", login, StringComparison.Ordinal);
        Assert.DoesNotContain("const connected = data?.cookieSaved === true;", login, StringComparison.Ordinal);
        Assert.DoesNotContain("const isConnected = info?.cookieSaved === true;", login, StringComparison.Ordinal);
    }

    [Fact]
    public void Boomplay_MissingSessionMessage_RequiresLogin()
    {
        var controller = ReadSource("DeezSpoTag.Web/Controllers/Api/BoomplayApiController.cs");

        Assert.Contains("log in through Login → Boomplay", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetches it without a session", controller, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
