using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyCredentialsGenerationGuardrailTests
{
    [Fact]
    public void SpotifyBlobService_UsesPerAccountGenerationLock_AndConflictException()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");

        Assert.Contains("BlobGenerationLocks", source, StringComparison.Ordinal);
        Assert.Contains("SpotifyBlobGenerationInProgressException", source, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(0, cancellationToken)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyBlobService_PassesWritableCredentialsDirectory_ToHelper()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");

        Assert.Contains("CreatePythonScriptStartInfo(", source, StringComparison.Ordinal);
        Assert.Contains("\"--credentials-dir\", authWorkingDir", source, StringComparison.Ordinal);
        Assert.Contains("helperPath,", source, StringComparison.Ordinal);
        Assert.Contains("authWorkingDir,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyZeroconfHelper_UsesCredentialsDirInsteadOfImplicitCwd()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Tools", "spotify_zeroconf_auth.py");

        Assert.Contains("parser.add_argument(\"--credentials-dir\"", source, StringComparison.Ordinal);
        Assert.Contains("credential_file = credentials_dir / \"credentials.json\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateAccount_MapsInProgressException_ToConflict()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SpotifyCredentialsApiController.cs");

        Assert.Contains("SpotifyBlobService.SpotifyBlobGenerationInProgressException", source, StringComparison.Ordinal);
        Assert.Contains("mappedResult = Conflict(invalidOperationException.Message);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginPage_GuardsAgainstDuplicateSpotifyGenerateSubmissions()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Views", "Login", "Index.cshtml");

        Assert.Contains("let spotifyBlobGenerationInFlight = false;", source, StringComparison.Ordinal);
        Assert.Contains("if (spotifyBlobGenerationInFlight)", source, StringComparison.Ordinal);
        Assert.Contains("generateButton.disabled = true;", source, StringComparison.Ordinal);
        Assert.Contains("generateButton.textContent = 'Generating...';", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine(ResolveRepoRoot(), Path.Combine(parts));
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.GetFullPath(Path.Combine(current, "..", "..", "..", ".."));
            if (Directory.Exists(Path.Combine(candidate, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Combine(candidate, "DeezSpoTag.Tests")))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Unable to resolve repository root for guardrail tests.");
    }
}
