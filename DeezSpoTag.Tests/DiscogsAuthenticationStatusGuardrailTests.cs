using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DiscogsAuthenticationStatusGuardrailTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void RedactedDiscogsAuthentication_RemainsConnectedAfterStatusRefresh()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs");
        var login = ReadSource("DeezSpoTag.Web", "Views", "Login", "Index.cshtml");
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");
        var autotag = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "autotag.js");

        Assert.Contains("tokenSaved = !string.IsNullOrWhiteSpace(state.Discogs.Token)", controller, StringComparison.Ordinal);
        Assert.Contains("data.discogs.tokenSaved === true", login, StringComparison.Ordinal);
        Assert.Contains("authData.discogs?.tokenSaved === true", site, StringComparison.Ordinal);
        Assert.Contains("auth.discogs?.tokenSaved === true", autotag, StringComparison.Ordinal);
        Assert.DoesNotContain("return Boolean(auth.discogs?.token);", autotag, StringComparison.Ordinal);
        Assert.DoesNotContain("setStatus('discogsStatus', data.discogs.token)", login, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "applySimpleCredentialState(authData.discogs?.token,",
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
