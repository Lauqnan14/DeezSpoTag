using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexAuthenticationStatusGuardrailTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void RedactedPlexAuthentication_RemainsConnectedAfterStatusRefresh()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs");
        var login = ReadSource("DeezSpoTag.Web", "Views", "Login", "Index.cshtml");
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        Assert.Contains("tokenSaved = !string.IsNullOrWhiteSpace(state.Plex.Token)", controller, StringComparison.Ordinal);
        Assert.Contains("data.plex.tokenSaved === true", login, StringComparison.Ordinal);
        Assert.Contains("authData.plex?.tokenSaved === true", site, StringComparison.Ordinal);
        Assert.DoesNotContain("data.plex.url && data.plex.token)", login, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "applySimpleCredentialState(authData.plex?.url && authData.plex?.token,",
            site,
            StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
        => File.ReadAllText(Path.Join([RepoRoot, .. relativeParts]));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web", "Program.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root.");
    }
}
