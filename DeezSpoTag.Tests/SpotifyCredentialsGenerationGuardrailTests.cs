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
        Assert.Contains("parser.add_argument(\"--listen-port\"", source, StringComparison.Ordinal);
        Assert.Contains(".set_listen_port(listen_port)", source, StringComparison.Ordinal);
        Assert.Contains("return server, device_name, None", source, StringComparison.Ordinal);
        Assert.Contains("device_name=actual_device_name", source, StringComparison.Ordinal);
        Assert.Contains("PROGRESS:", source, StringComparison.Ordinal);
        Assert.Contains("Spotify Connect authentication helper failed", source, StringComparison.Ordinal);
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
        Assert.Contains("generateButton.textContent = 'Waiting for Spotify Connect...';", source, StringComparison.Ordinal);
        Assert.Contains("spotifyConnectInstructions", source, StringComparison.Ordinal);
        Assert.Contains("generation-status", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyZeroconf_UsesStableLanAdvertisementAndBufferedHttpParsing()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Tools", "spotify_librespot", "spotizerr-phoenix", "librespot", "zeroconf.py");

        Assert.Contains("A valid stable Spotify Connect listener port is required.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOTIFY_ZEROCONF_IP_PROBE_HOST", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dns.google", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEZSPOTAG_SPOTIFY_ZEROCONF_INTERFACE", source, StringComparison.Ordinal);
        Assert.Contains("_get_default_route_interface", source, StringComparison.Ordinal);
        Assert.Contains("/proc/net/route", source, StringComparison.Ordinal);
        Assert.Contains("_get_interface_ipv4", source, StringComparison.Ordinal);
        Assert.Contains("__read_http_request", source, StringComparison.Ordinal);
        Assert.Contains("while b\"\\r\\n\\r\\n\" not in buffer:", source, StringComparison.Ordinal);
        Assert.Contains("urllib.parse.parse_qs(body, keep_blank_values=True)", source, StringComparison.Ordinal);
        Assert.Contains("Spotify Connect listener stopped before credential capture completed.", ReadRepoFile("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs"), StringComparison.Ordinal);
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
