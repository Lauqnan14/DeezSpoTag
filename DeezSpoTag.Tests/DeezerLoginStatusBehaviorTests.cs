using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using DeezerSearchTrack = DeezSpoTag.Integrations.Deezer.Track;

namespace DeezSpoTag.Tests;

public sealed class DeezerLoginStatusBehaviorTests
{
    private const string StoredAuthState = "stored";
    private const string LiveAuthState = "live";
    private const string StatusUrl = "/api/login/status";
    private const string ValidateStatusUrl = "/api/login/status/validate";
    private const string ConnectedPlatformsCacheKey = "connected-platforms-cache";
    private const string DeezerWarmupServiceName = "DeezerLoginWarmupService";
    private const string DeezSpoTagWebDirectory = "DeezSpoTag.Web";
    private const string StatusPropertyName = "status";
    private const string LivePropertyName = "live";
    private const string AuthStatePropertyName = "authState";
    private const string UserPropertyName = "user";
    private const string NamePropertyName = "name";
    private const string UnitedStatesCountry = "US";

    [Fact]
    public async Task Status_WithStoredCredentials_DoesNotReportLiveWithoutLiveSession()
    {
        var client = CreateDeezerClient();
        var controller = CreateController(client, new StubLoginStorage(new LoginData
        {
            Arl = new string('a', 192),
            User = new UserData
            {
                Id = "123",
                Name = "Stored Deezer User",
                Country = UnitedStatesCountry,
                CanStreamHq = true,
                CanStreamLossless = false
            }
        }));

        var result = await controller.Status();

        var json = SerializeOkResult(result);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var statusCode = root.GetProperty(StatusPropertyName).GetInt32();
        Assert.Equal(1, statusCode);
        Assert.False(root.GetProperty(LivePropertyName).GetBoolean());
        var authState = root.GetProperty(AuthStatePropertyName).GetString();
        Assert.Equal(StoredAuthState, authState);

        if (statusCode == 1 && root.TryGetProperty(UserPropertyName, out var userElement))
        {
            Assert.Equal("Stored Deezer User", userElement.GetProperty(NamePropertyName).GetString());
        }

        Assert.False(client.LoggedIn);
    }

    [Fact]
    public async Task Status_WithLiveSession_ReturnsLiveStateWithoutValidation()
    {
        var client = CreateDeezerClientWithLiveUser();
        var controller = CreateController(client, new StubLoginStorage(new LoginData
        {
            Arl = new string('b', 192),
            User = new UserData
            {
                Id = "456",
                Name = "Stored User",
                Country = UnitedStatesCountry
            }
        }));

        var result = await controller.Status();

        var json = SerializeOkResult(result);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty(StatusPropertyName).GetInt32());
        Assert.True(root.GetProperty(LivePropertyName).GetBoolean());
        Assert.Equal(LiveAuthState, root.GetProperty(AuthStatePropertyName).GetString());
        Assert.Equal("Live Deezer User", root.GetProperty(UserPropertyName).GetProperty(NamePropertyName).GetString());
    }

    [Fact]
    public void SidebarUsesSharedCachedPlatformStatus_AndLoginPageUsesExplicitValidation()
    {
        var repoRoot = ResolveRepoRoot();
        var siteSource = File.ReadAllText(Path.Join(repoRoot, DeezSpoTagWebDirectory, "wwwroot", "js", "site.js"));
        var loginSource = File.ReadAllText(Path.Join(repoRoot, DeezSpoTagWebDirectory, "Views", "Login", "Index.cshtml"));
        var layoutSource = File.ReadAllText(Path.Join(repoRoot, DeezSpoTagWebDirectory, "Views", "Shared", "_Layout.cshtml"));

        Assert.Contains("/api/platform-auth", siteSource, StringComparison.Ordinal);
        Assert.DoesNotContain(StatusUrl, siteSource, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidateStatusUrl, siteSource, StringComparison.Ordinal);
        Assert.Contains(ValidateStatusUrl, loginSource, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{ConnectedPlatformsCacheKey}',", layoutSource, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupWarmup_UsesSingleDeezerStartupService()
    {
        var repoRoot = ResolveRepoRoot();
        var programSource = File.ReadAllText(Path.Join(repoRoot, DeezSpoTagWebDirectory, "Program.cs"));

        Assert.Contains("AddHostedService<StartupLoginService>", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain(DeezerWarmupServiceName, programSource, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(repoRoot, DeezSpoTagWebDirectory, "Services", $"{DeezerWarmupServiceName}.cs")));
    }

    [Fact]
    public void CountryCandidates_PrioritizeConfiguredCountryBeforeAuthenticatedCountry()
    {
        var sessionManager = new DeezerSessionManager(
            NullLogger<DeezerSessionManager>.Instance,
            () => new DeezSpoTagSettings { DeezerCountry = UnitedStatesCountry });
        SetLiveUser(sessionManager, country: "KE");

        Assert.Equal(new[] { UnitedStatesCountry, "KE" }, sessionManager.GetCountryCandidates());
    }

    [Fact]
    public void CountryCandidates_DoNotRepeatAuthenticatedCountryWhenItMatchesConfiguration()
    {
        var sessionManager = new DeezerSessionManager(
            NullLogger<DeezerSessionManager>.Instance,
            () => new DeezSpoTagSettings { DeezerCountry = UnitedStatesCountry });
        SetLiveUser(sessionManager, country: UnitedStatesCountry);

        Assert.Equal(new[] { UnitedStatesCountry }, sessionManager.GetCountryCandidates());
    }

    [Fact]
    public void SearchTrackConversion_AcceptsNewtonsoftPayloadRows()
    {
        var row = JObject.Parse("""
            {
              "id": "3135556",
              "title": "Harder, Better, Faster, Stronger",
              "duration": 224,
              "isrc": "GBDUW0000059",
              "artist": { "id": 27, "name": "Daft Punk" },
              "album": { "id": 302127, "title": "Discovery" }
            }
            """);
        var method = typeof(DeezerClient).GetMethod(
            "ConvertSearchResultTracks",
            BindingFlags.NonPublic | BindingFlags.Static);

        var tracks = Assert.IsType<List<DeezerSearchTrack>>(method!.Invoke(null, new object?[] { new object[] { row } }));
        var track = Assert.Single(tracks);
        Assert.Equal("3135556", track.Id);
        Assert.Equal("GBDUW0000059", track.ISRC);
        Assert.Equal("Daft Punk", track.MainArtist?.Name);
        Assert.Equal("Discovery", track.Album?.Title);
    }

    private static LoginApiController CreateController(DeezerClient client, ILoginStorageService loginStorage)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("IsSingleUser", "true")
            })
            .Build();
        var coordinator = new DeezerLoginCoordinator(client, NullLogger<DeezerLoginCoordinator>.Instance);
        var services = new LoginApiServices(
            configuration,
            settings: null!,
            auth: null!,
            appleWrapper: null!,
            coordinator);
        var controller = new LoginApiController(
            NullLogger<LoginApiController>.Instance,
            client,
            loginStorage,
            services);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        return controller;
    }

    private static DeezerClient CreateDeezerClient()
    {
        var sessionManager = new DeezerSessionManager(
            NullLogger<DeezerSessionManager>.Instance,
            () => new DeezSpoTagSettings());
        return new DeezerClient(NullLogger<DeezerClient>.Instance, sessionManager);
    }

    private static DeezerClient CreateDeezerClientWithLiveUser()
    {
        var client = CreateDeezerClient();
        var sessionManager = new DeezerSessionManager(
            NullLogger<DeezerSessionManager>.Instance,
            () => new DeezSpoTagSettings());
        SetLiveUser(sessionManager, UnitedStatesCountry);
        client.SetSessionManager(sessionManager);
        return client;
    }

    private static void SetLiveUser(DeezerSessionManager sessionManager, string country)
    {
        var user = new DeezSpoTag.Core.Models.Deezer.DeezerUser
        {
            Id = "456",
            Name = "Live Deezer User",
            Country = country
        };
        typeof(DeezerSessionManager)
            .GetProperty("CurrentUser")!
            .SetValue(sessionManager, user);
        typeof(DeezerSessionManager)
            .GetProperty("LoggedIn")!
            .SetValue(sessionManager, true);
    }

    private static string SerializeOkResult(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.Serialize(ok.Value);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }

    private sealed class StubLoginStorage : ILoginStorageService
    {
        private LoginData? _loginData;

        public StubLoginStorage(LoginData? loginData)
        {
            _loginData = loginData;
        }

        public Task<LoginData?> LoadLoginCredentialsAsync()
            => Task.FromResult(_loginData);

        public Task SaveLoginCredentialsAsync(LoginData loginData)
        {
            _loginData = loginData;
            return Task.CompletedTask;
        }

        public Task ResetLoginCredentialsAsync()
        {
            _loginData = null;
            return Task.CompletedTask;
        }

        public Task ForceFixCorruptedFileAsync()
            => Task.CompletedTask;
    }
}
