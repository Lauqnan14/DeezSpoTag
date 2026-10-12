using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class AmazonMusicMetadataApiGuardrailTest
{
    [Theory]
    [InlineData("music.amazon.com")]
    [InlineData("music.amazon.co.uk")]
    public async Task AnonymousBootstrapUsesSkyfirePostAndPassesIssuedCsrfToSearch(string host)
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-bootstrap-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            await auth.SaveAsync(new PlatformAuthState { AmazonMusic = new AmazonMusicAuth { Host = host } });
            using var handler = new BootstrapHandler();
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            await service.SearchAsync("Believer", "track", 3, CancellationToken.None);
            Assert.Equal(HttpMethod.Post, handler.BootstrapMethod);
            Assert.Equal($"https://{host}/config.json?skipToken=false&clientApplication=skyfire", handler.BootstrapUri);
            Assert.False(handler.HadCookie);
            Assert.NotNull(handler.SearchBody);
            using var body = JsonDocument.Parse(handler.SearchBody!);
            using var headers = JsonDocument.Parse(body.RootElement.GetProperty("headers").GetString()!);
            using var csrf = JsonDocument.Parse(headers.RootElement.GetProperty("x-amzn-csrf").GetString()!);
            Assert.Equal("guest-csrf", csrf.RootElement.GetProperty("token").GetString());
            Assert.Equal("issued-ts", csrf.RootElement.GetProperty("timestamp").GetString());
            Assert.Equal("issued-nonce", csrf.RootElement.GetProperty("rndNonce").GetString());
            using var authentication = JsonDocument.Parse(headers.RootElement.GetProperty("x-amzn-authentication").GetString()!);
            Assert.Equal(string.Empty, authentication.RootElement.GetProperty("accessToken").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class BootstrapHandler : HttpMessageHandler, IHttpClientFactory
    {
        public HttpMethod? BootstrapMethod { get; private set; }
        public string? BootstrapUri { get; private set; }
        public bool HadCookie { get; private set; }
        public string? SearchBody { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HadCookie |= request.Headers.Contains("Cookie");
            string response;
            if (request.RequestUri!.AbsolutePath == "/config.json")
            {
                BootstrapMethod = request.Method;
                BootstrapUri = request.RequestUri.AbsoluteUri;
                response = request.Method == HttpMethod.Post && request.RequestUri.Query == "?skipToken=false&clientApplication=skyfire"
                    ? "{\"sessionId\":\"guest-session\",\"deviceId\":\"guest-device\",\"csrf\":{\"token\":\"guest-csrf\",\"ts\":\"issued-ts\",\"rnd\":\"issued-nonce\"}}"
                    : "{\"sessionId\":\"guest-session\",\"deviceId\":\"guest-device\"}";
            }
            else { SearchBody = await request.Content!.ReadAsStringAsync(token); response = "{\"methods\":[]}"; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    private sealed class BootstrapEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void AmazonMetadataSessionFailure_ReportsMissingFieldNamesWithoutValues()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");
        var start = source.IndexOf("if (string.IsNullOrWhiteSpace(sessionId) ||", StringComparison.Ordinal);
        var end = source.IndexOf("return new AmazonSession", start, StringComparison.Ordinal);
        var failure = source[start..end];
        Assert.Contains("missingFields.Add(\"sessionId\")", failure);
        Assert.Contains("missingFields.Add(\"deviceId\")", failure);
        Assert.Contains("missingFields.Add(\"csrf\")", failure);
        Assert.DoesNotContain("missingFields.Add(sessionId)", failure);
        Assert.DoesNotContain("missingFields.Add(deviceId)", failure);
        Assert.DoesNotContain("missingFields.Add(csrf)", failure);
    }

    [Fact]
    public void AmazonMetadata_UsesRegionalSkillEndpointsWithLegacyFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("RegionConfigs", source, StringComparison.Ordinal);
        Assert.Contains("https://na.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://eu.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://fe.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://na.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://eu.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://fe.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("ResolveRegionConfig(host)", source, StringComparison.Ordinal);
        Assert.Contains("session.SkillApiBaseUrls", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_UsesOnlyDedicatedTypedSearchEndpoints()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchTracksPath = \"/searchCatalogTracks\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchAlbumsPath = \"/searchCatalogAlbums\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchArtistsPath = \"/searchCatalogArtists\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchPlaylistsPath = \"/searchCatalogPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchCommunityPlaylistsPath = \"/searchCommunityPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("TrySearchTypedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAllPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/showSearch", source, StringComparison.Ordinal);
        Assert.Contains("TrySearchCommunityPlaylistsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_UsesTypedCatalogFetchWithoutDeeplinkFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("TrackInfoPath = \"/cosmicTrack/displayCatalogTrack\"", source, StringComparison.Ordinal);
        Assert.Contains("AlbumInfoPath = \"/showCatalogAlbum\"", source, StringComparison.Ordinal);
        Assert.Contains("PlaylistInfoPath = \"/showCatalogPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("CommunityPlaylistInfoPath = \"/showLibraryPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("FetchCatalogDocumentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchCatalogDocumentOrHomeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchHomeDocumentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("showHome", source, StringComparison.Ordinal);
        Assert.DoesNotContain("deeplink home fetch", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonTrackResolution_UsesValidatedTypedTrackSearchOnly()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchAsync(session, trackQuery, \"track\"", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackFromCatalogAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackCatalogCandidatesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandAlbumTracksForSearchAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAtmosTrackAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonTrackResolution_ValidatesAlbumVariant()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("string? album,\n        int? durationMs", source, StringComparison.Ordinal);
        Assert.Contains("Album: album", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Album: null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_WiresRemainingReferenceMetadataEndpoints()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchCommunityPlaylistsPath = \"/searchCommunityPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("CommunityPlaylistInfoPath = \"/showLibraryPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("ArtistTopTracksPath = \"/showCatalogTracks\"", source, StringComparison.Ordinal);
        Assert.Contains("ExtractCommunityPlaylistSearchItems", source, StringComparison.Ordinal);
        Assert.Contains("FetchArtistTopTracksAsync", source, StringComparison.Ordinal);
        Assert.Contains("IsCommunityPlaylistUrl", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonSession_UsesCurrentMontanaCsrfToken()
    {
        using var document = JsonDocument.Parse("""{"montanaCsrf":"current-token"}""");

        var token = ResolveAmazonCsrf(document.RootElement);

        Assert.Equal("current-token", token);
    }

    [Fact]
    public void AmazonSession_KeepsLegacyCsrfTokenCompatibility()
    {
        using var document = JsonDocument.Parse("""{"csrf":{"token":"legacy-token","ts":"1","rnd":"2"}}""");

        var token = ResolveAmazonCsrf(document.RootElement);

        Assert.Equal("legacy-token", token);
    }

    [Fact]
    public void AmazonSession_RejectsResponseWithoutCsrfToken()
    {
        using var document = JsonDocument.Parse("""{"sessionId":"session","deviceId":"device"}""");

        var token = ResolveAmazonCsrf(document.RootElement);

        Assert.Null(token);
    }

    private static string? ResolveAmazonCsrf(JsonElement root)
    {
        var method = typeof(AmazonMusicMetadataService).GetMethod(
            "ResolveAmazonCsrf",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [root]) as string;
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Combine(new[] { repoRoot }.Concat(relativeParts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }
}
