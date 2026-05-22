using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
using Xunit;

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
    public void SidebarUsesCheapDeezerStatus_AndLoginPageUsesExplicitValidation()
    {
        var repoRoot = ResolveRepoRoot();
        var siteSource = File.ReadAllText(Path.Join(repoRoot, DeezSpoTagWebDirectory, "wwwroot", "js", "site.js"));
        var loginSource = File.ReadAllText(Path.Join(repoRoot, DeezSpoTagWebDirectory, "Views", "Login", "Index.cshtml"));
        var layoutSource = File.ReadAllText(Path.Join(repoRoot, DeezSpoTagWebDirectory, "Views", "Shared", "_Layout.cshtml"));

        Assert.Contains(StatusUrl, siteSource, StringComparison.Ordinal);
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
        var user = new DeezSpoTag.Core.Models.Deezer.DeezerUser
        {
            Id = "456",
            Name = "Live Deezer User",
            Country = UnitedStatesCountry
        };
        typeof(DeezerSessionManager)
            .GetProperty("CurrentUser")!
            .SetValue(sessionManager, user);
        typeof(DeezerSessionManager)
            .GetProperty("LoggedIn")!
            .SetValue(sessionManager, true);
        client.SetSessionManager(sessionManager);
        return client;
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
