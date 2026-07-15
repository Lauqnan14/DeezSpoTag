using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BeatportAuthenticationTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), $"beatport-auth-{Guid.NewGuid():N}");

    public BeatportAuthenticationTests() => Directory.CreateDirectory(_tempRoot);

    [Fact]
    public void Platform_requires_authentication_and_registry_targets_beatport_login()
    {
        var descriptor = new BeatportPlatform(new StubWebHostEnvironment(_tempRoot)).Describe();
        Assert.True(descriptor.RequiresAuth);
        Assert.True(descriptor.Platform.RequiresAuth);

        var createEntry = typeof(PlatformRegistryApiController).GetMethod(
            "CreateEntry",
            BindingFlags.NonPublic | BindingFlags.Static);
        var entry = Assert.IsType<PlatformRegistryApiController.PlatformRegistryEntry>(
            createEntry?.Invoke(null, ["beatport", "Beatport"]));
        Assert.True(entry.RequiresAuth);
        Assert.Equal("beatport-login", entry.LoginTabId);
    }

    [Fact]
    public void Public_state_reports_readiness_without_exposing_secrets_or_tokens()
    {
        var status = PlatformAuthApiController.ToPublicBeatport(new BeatportAuth
        {
            ClientId = "beatport-client",
            ClientSecret = "beatport-secret",
            RedirectUri = "https://music.example/api/platform-auth/beatport/callback",
            Scope = "catalog",
            AccessToken = "beatport-access-token",
            RefreshToken = "beatport-refresh-token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        });

        Assert.True(status.Configured);
        Assert.True(status.Connected);
        Assert.True(status.ClientSecretSaved);
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("beatport-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("beatport-access-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("beatport-refresh-token", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configure_preserves_secret_for_same_client_and_invalidates_tokens_for_new_client()
    {
        var authService = CreateAuthService();
        await authService.UpdateAsync(state =>
        {
            state.Beatport = new BeatportAuth
            {
                ClientId = "old-client",
                ClientSecret = "saved-secret",
                RedirectUri = "https://music.example/api/platform-auth/beatport/callback",
                AccessToken = "old-access",
                RefreshToken = "old-refresh",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            return true;
        });
        var tokens = new BeatportTokenService(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")), authService);
        var controller = new BeatportAuthApiController(authService, tokens);

        var result = await controller.Configure(new BeatportAuthApiController.BeatportConfigureRequest(
            "new-client",
            null,
            "https://music.example/api/platform-auth/beatport/callback",
            "catalog"));

        Assert.IsType<OkObjectResult>(result);
        var stored = Assert.IsType<BeatportAuth>((await authService.LoadAsync()).Beatport);
        Assert.Equal("new-client", stored.ClientId);
        Assert.Equal("saved-secret", stored.ClientSecret);
        Assert.Null(stored.AccessToken);
        Assert.Null(stored.RefreshToken);
        Assert.Null(stored.ExpiresAtUtc);
    }

    [Fact]
    public async Task OAuth_authorization_code_exchange_saves_tokens()
    {
        var authService = CreateAuthService();
        await SaveConfigurationAsync(authService);
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer"}
            """);
        var tokens = new BeatportTokenService(new HttpClient(handler), authService);

        var authorization = await tokens.CreateAuthorizationRequestAsync(CancellationToken.None);
        Assert.Contains("code_challenge_method=S256", authorization.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Fmusic.example%2Fapi%2Fplatform-auth%2Fbeatport%2Fcallback", authorization.AuthorizationUrl, StringComparison.Ordinal);

        await tokens.CompleteAuthorizationAsync("authorization-code", authorization.State, CancellationToken.None);

        var stored = Assert.IsType<BeatportAuth>((await authService.LoadAsync()).Beatport);
        Assert.Equal("new-access", stored.AccessToken);
        Assert.Equal("new-refresh", stored.RefreshToken);
        Assert.True(stored.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Equal("Basic", handler.LastAuthorizationScheme);
        Assert.Contains("grant_type=authorization_code", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_refresh_clears_runtime_tokens_and_requires_reconnection()
    {
        var authService = CreateAuthService();
        await authService.UpdateAsync(state =>
        {
            state.Beatport = new BeatportAuth
            {
                ClientId = "beatport-client",
                ClientSecret = "beatport-secret",
                RedirectUri = "https://music.example/api/platform-auth/beatport/callback",
                AccessToken = "expired-access",
                RefreshToken = "rejected-refresh",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            };
            return true;
        });
        var tokens = new BeatportTokenService(
            new HttpClient(new StubHandler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")),
            authService);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tokens.GetAccessTokenAsync(false, CancellationToken.None));

        Assert.Contains("reconnect", error.Message, StringComparison.OrdinalIgnoreCase);
        var stored = Assert.IsType<BeatportAuth>((await authService.LoadAsync()).Beatport);
        Assert.Equal("beatport-client", stored.ClientId);
        Assert.Equal("beatport-secret", stored.ClientSecret);
        Assert.Null(stored.AccessToken);
        Assert.Null(stored.RefreshToken);
        Assert.Null(stored.ExpiresAtUtc);
    }

    [Fact]
    public void Login_and_autotag_use_the_canonical_beatport_authentication_state()
    {
        var root = ResolveRepositoryRoot();
        var login = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml"));
        var autotag = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var controller = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "BeatportAuthApiController.cs"));

        Assert.Contains("data-login-target=\"beatport-login\"", login, StringComparison.Ordinal);
        Assert.Contains("/api/platform-auth/beatport/configure", login, StringComparison.Ordinal);
        Assert.Contains("/api/platform-auth/beatport/connect", login, StringComparison.Ordinal);
        Assert.Contains("deezspotag:beatport-auth", login, StringComparison.Ordinal);
        Assert.Contains("auth.beatport?.connected === true", autotag, StringComparison.Ordinal);
        Assert.Contains("/Login?tab=beatport-login", autotag, StringComparison.Ordinal);
        Assert.Contains("!isPlatformAuthenticated(\"beatport\")", autotag, StringComparison.Ordinal);
        Assert.Contains("normalizePlatformId(platformId) !== \"beatport\"", autotag, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpGet(\"status\")", controller, StringComparison.Ordinal);
    }

    private PlatformAuthService CreateAuthService()
        => new(
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Join(_tempRoot, "keys"))));

    private static Task SaveConfigurationAsync(PlatformAuthService authService)
        => authService.UpdateAsync(state =>
        {
            state.Beatport = new BeatportAuth
            {
                ClientId = "beatport-client",
                ClientSecret = "beatport-secret",
                RedirectUri = "https://music.example/api/platform-auth/beatport/callback",
                Scope = "catalog"
            };
            return true;
        });

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); }
        catch { }
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public string? LastAuthorizationScheme { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string root)
        {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new PhysicalFileProvider(root);
            WebRootFileProvider = new PhysicalFileProvider(root);
        }

        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
