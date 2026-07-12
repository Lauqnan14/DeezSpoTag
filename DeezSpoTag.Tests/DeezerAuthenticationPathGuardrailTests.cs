using System;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerAuthenticationPathGuardrailTests
{
    [Fact]
    public void GlobalSettings_DoNotExposeLegacyDeezerCredentials()
    {
        Assert.Null(typeof(DeezSpoTagSettings).GetProperty("Arl"));

        var json = JsonSerializer.Serialize(
            new DeezSpoTagSettings(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.DoesNotContain("\"arl\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsApi_DoesNotPublishOrInspectDeezerAuthentication()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "SettingsApiController.cs");

        Assert.DoesNotContain("hasArl", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ILoginStorageService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.Arl", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsAndAutoTag_UseAuthoritativeDeezerAuthenticationService()
    {
        var lyrics = ReadSource("DeezSpoTag.Services", "Download", "Utils", "LyricsService.cs");
        var matcher = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "DeezerMatcher.cs");
        var autoTagClient = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "DeezerClient.cs");
        var runner = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var autoTag = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("_authenticatedDeezerService.GetArlAsync()", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.Arl", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticatedDeezerService", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLyricsAsync", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("SetArl", autoTagClient, StringComparison.Ordinal);
        Assert.DoesNotContain("song.getLyrics", autoTagClient, StringComparison.Ordinal);
        Assert.Contains("_downloadLyricsService.ResolveLyricsAsync", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("InjectDeezerAuthAsync", autoTag, StringComparison.Ordinal);
        Assert.DoesNotContain("InjectDeezerDownloadOptions", autoTag, StringComparison.Ordinal);
    }

    [Fact]
    public void DeezerResolution_UsesOrderedCountryCandidatesForSearchAndDownloads()
    {
        var client = ReadSource("DeezSpoTag.Integrations", "Deezer", "DeezerClient.cs");
        var session = ReadSource("DeezSpoTag.Integrations", "Deezer", "DeezerSessionManager.cs");
        var resolver = ReadSource("DeezSpoTag.Web", "Services", "TrackIdentityResolver.cs");

        Assert.Contains("var countries = CountryCandidates", client, StringComparison.Ordinal);
        Assert.Contains("SearchMetadataAttemptAsync(attempt, searchInput, countries[countryIndex])", client, StringComparison.Ordinal);
        Assert.Contains("GetCountryCandidates()", session, StringComparison.Ordinal);
        Assert.Contains("GetTracksUrlWithStatusAsync", session, StringComparison.Ordinal);
        Assert.Contains("var countries = _deezerClient.CountryCandidates", resolver, StringComparison.Ordinal);
        Assert.Contains("SearchTracksForCountryAsync", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void WebApplication_DoesNotRegisterOrExposeLegacyAuthenticationPath()
    {
        var program = ReadSource("DeezSpoTag.Web", "Program.cs");
        var apiProgram = ReadSource("DeezSpoTag.API", "Program.cs");
        var apiLogin = ReadSource("DeezSpoTag.API", "Controllers", "LoginController.cs");
        var repoRoot = FindRepoRoot();

        Assert.DoesNotContain("AddDeezSpoTagAuthentication", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IDeezerAuthenticationService", apiProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezerAuthenticationService", apiProgram, StringComparison.Ordinal);
        Assert.Contains("AuthenticatedDeezerService", apiProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("IDeezerAuthenticationService", apiLogin, StringComparison.Ordinal);
        Assert.DoesNotContain("loginEmail", apiLogin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AuthenticatedDeezerService", apiLogin, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LoginFixApiController.cs")));
        Assert.False(File.Exists(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Authentication",
            "DeezerAuthenticationService.cs")));
        Assert.False(File.Exists(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Authentication",
            "IDeezerAuthenticationService.cs")));
    }

    private static string ReadSource(params string[] relativeParts)
        => File.ReadAllText(Path.Join(FindRepoRoot(), Path.Join(relativeParts)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
