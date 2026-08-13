using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BpmSupremeAuthenticationStatusGuardrailTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void RedactedBpmSupremeAuthentication_RemainsConnectedAfterStatusRefresh()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs");
        var login = ReadSource("DeezSpoTag.Web", "Views", "Login", "Index.cshtml");
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");
        var autotag = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "autotag.js");

        Assert.Contains("passwordSaved = !string.IsNullOrWhiteSpace(state.BpmSupreme.Password)", controller, StringComparison.Ordinal);
        Assert.Contains("data.bpmSupreme.passwordSaved === true", login, StringComparison.Ordinal);
        Assert.Contains("authData.bpmSupreme?.passwordSaved === true", site, StringComparison.Ordinal);
        Assert.Contains("auth.bpmSupreme?.passwordSaved === true", autotag, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.bpmSupreme?.email && auth.bpmSupreme?.password)", autotag, StringComparison.Ordinal);
        Assert.DoesNotContain("data.bpmSupreme.email && data.bpmSupreme.password)", login, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "authData.bpmSupreme?.email && authData.bpmSupreme?.password,",
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
