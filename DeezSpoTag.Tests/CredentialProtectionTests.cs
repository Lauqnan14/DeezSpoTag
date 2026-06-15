using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("DataRoot Environment")]
public sealed class CredentialProtectionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousDataDir;
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public CredentialProtectionTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), $"deezspotag-credentials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _previousDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _tempRoot);
        _dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Join(_tempRoot, "keys")));
    }

    [Fact]
    public async Task DeezerLoginStorage_LoadsPlaintextLegacyFile_AndRewritesProtected()
    {
        var loginPath = Path.Join(_tempRoot, "login.json");
        await File.WriteAllTextAsync(loginPath, JsonSerializer.Serialize(new
        {
            arl = "legacy-deezer-arl",
            user = new { id = "1", name = "Deezer User" }
        }));

        var storage = new LoginStorageService(
            NullLogger<LoginStorageService>.Instance,
            _dataProtectionProvider);

        var loaded = await storage.LoadLoginCredentialsAsync();

        Assert.Equal("legacy-deezer-arl", loaded?.Arl);
        var stored = await File.ReadAllTextAsync(loginPath);
        Assert.Contains("deezspotag-protected-credential", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-deezer-arl", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpotifyBlobService_LoadsLegacyBlobs_AndRewritesProtected()
    {
        var service = CreateSpotifyBlobService();
        var webBlobPath = Path.Join(_tempRoot, "web.json");
        var librespotBlobPath = Path.Join(_tempRoot, "librespot.json");

        await File.WriteAllTextAsync(webBlobPath, JsonSerializer.Serialize(new
        {
            version = 1,
            createdAt = DateTimeOffset.UtcNow,
            userAgent = "test-agent",
            cookies = new[]
            {
                new { name = "sp_dc", value = "spotify-cookie-secret", domain = ".spotify.com", path = "/" }
            }
        }));
        await File.WriteAllTextAsync(librespotBlobPath, JsonSerializer.Serialize(new
        {
            username = "spotify-user",
            credentials = "librespot-secret",
            type = "AUTHENTICATION_STORED_SPOTIFY_CREDENTIALS"
        }));

        Assert.True(await service.IsWebPlayerBlobAsync(webBlobPath));
        Assert.True(await service.IsLibrespotBlobAsync(librespotBlobPath));

        var protectedWebBlob = await File.ReadAllTextAsync(webBlobPath);
        var protectedLibrespotBlob = await File.ReadAllTextAsync(librespotBlobPath);
        Assert.Contains("deezspotag-protected-credential", protectedWebBlob, StringComparison.Ordinal);
        Assert.Contains("deezspotag-protected-credential", protectedLibrespotBlob, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify-cookie-secret", protectedWebBlob, StringComparison.Ordinal);
        Assert.DoesNotContain("librespot-secret", protectedLibrespotBlob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpotifyUserAuthStore_LoadsPlaintextLegacyState_AndRewritesProtected()
    {
        var authStore = CreateSpotifyUserAuthStore();
        var userId = "default";
        var authPath = authStore.GetUserAuthFilePath(userId);
        Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);

        await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
        {
            activeAccount = "main",
            accounts = new[]
            {
                new
                {
                    name = "main",
                    librespotBlobPath = Path.Join(_tempRoot, "spotify", "users", userId, "blobs", "main.json")
                }
            }
        }));

        var loaded = await authStore.LoadAsync(userId);

        Assert.Equal("main", loaded.ActiveAccount);
        Assert.Single(loaded.Accounts);
        var stored = await File.ReadAllTextAsync(authPath);
        Assert.Contains("deezspotag-protected-credential", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("main.json", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneTaggerSpotifyTokenCache_CanBeProtectedAtRest_WithoutLosingPayload()
    {
        var cachePath = Path.Join(_tempRoot, ".config", "onetagger", "spotify_token_cache.json");
        var plaintext = JsonSerializer.Serialize(new
        {
            access_token = "onetagger-access-token",
            expires_in = 300,
            scope = "user-read-private"
        });
        var store = new ProtectedCredentialFileStore(
            _dataProtectionProvider,
            "DeezSpoTag.OneTagger.SpotifyTokenCache");

        await store.WriteTextAsync(cachePath, plaintext);

        var stored = await File.ReadAllTextAsync(cachePath);
        Assert.Contains("deezspotag-protected-credential", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("onetagger-access-token", stored, StringComparison.Ordinal);
        Assert.Equal(plaintext, await store.ReadTextAsync(cachePath));
    }

    [Fact]
    public async Task PlatformAuthService_ProtectsQobuzCredentials_AndMigratesPlaintext()
    {
        var authDirectory = Path.Join(_tempRoot, "autotag");
        Directory.CreateDirectory(authDirectory);
        var authPath = Path.Join(authDirectory, "qobuz.json");
        await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
        {
            appId = "qobuz-app-id",
            appSecret = "qobuz-app-secret",
            authToken = "qobuz-user-token",
            country = "KE"
        }));

        var service = new PlatformAuthService(
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<PlatformAuthService>.Instance,
            _dataProtectionProvider);

        var loaded = await service.LoadAsync();

        Assert.Equal("qobuz-app-id", loaded.Qobuz?.AppId);
        Assert.Equal("qobuz-app-secret", loaded.Qobuz?.AppSecret);
        Assert.Equal("qobuz-user-token", loaded.Qobuz?.AuthToken);
        Assert.Equal("KE", loaded.Qobuz?.Country);

        var stored = await File.ReadAllTextAsync(authPath);
        Assert.True(ProtectedCredentialFileStore.IsProtectedText(stored));
        Assert.DoesNotContain("qobuz-app-secret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("qobuz-user-token", stored, StringComparison.Ordinal);

        var reloaded = await service.LoadAsync();
        Assert.Equal("qobuz-app-secret", reloaded.Qobuz?.AppSecret);
        Assert.Equal("qobuz-user-token", reloaded.Qobuz?.AuthToken);
    }

    [Fact]
    public async Task QobuzPublicProviderRegistry_ProtectsEndpoints_AndPersistsToggleState()
    {
        var registry = new QobuzPublicProviderRegistry(
            new StubWebHostEnvironment(_tempRoot),
            _dataProtectionProvider,
            NullLogger<QobuzPublicProviderRegistry>.Instance);

        var providers = await registry.GetProvidersAsync(CancellationToken.None);
        var squid = Assert.Single(providers, provider => provider.Id == "squid-us");
        Assert.True(squid.Enabled);

        await registry.SetEnabledAsync(squid.Id, false, CancellationToken.None);
        var reloaded = await registry.GetProvidersAsync(CancellationToken.None);
        Assert.False(Assert.Single(reloaded, provider => provider.Id == squid.Id).Enabled);

        var storedPath = Path.Join(_tempRoot, "autotag", "qobuz-public-providers.json");
        var stored = await File.ReadAllTextAsync(storedPath);
        Assert.True(ProtectedCredentialFileStore.IsProtectedText(stored));
        Assert.DoesNotContain("qobuz.squid.wtf", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("download-music", stored, StringComparison.OrdinalIgnoreCase);
    }

    private SpotifyBlobService CreateSpotifyBlobService()
        => new(
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<SpotifyBlobService>.Instance,
            _dataProtectionProvider);

    private SpotifyUserAuthStore CreateSpotifyUserAuthStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IsSingleUser"] = "true"
            })
            .Build();

        return new SpotifyUserAuthStore(
            new StubWebHostEnvironment(_tempRoot),
            configuration,
            NullLogger<SpotifyUserAuthStore>.Instance,
            _dataProtectionProvider);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _previousDataDir);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
            WebRootPath = contentRootPath;
            WebRootFileProvider = new PhysicalFileProvider(contentRootPath);
            ApplicationName = "DeezSpoTag.Tests";
            EnvironmentName = "Development";
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
