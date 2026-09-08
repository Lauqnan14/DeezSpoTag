using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("DataRoot Environment")]
public sealed class PlatformAuthDisconnectTests : IDisposable
{
    private readonly string _root;

    public PlatformAuthDisconnectTests()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-jellyfin-disconnect-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ClearingJellyfin_RemovesLiveAndLegacyDebugCopies_AndRestartMigrationDoesNotRestore()
    {
        var dataRoot = Path.Join(_root, "DeezSpoTag.Workers", "Data");
        var livePath = Path.Join(dataRoot, "autotag", "jellyfin.json");
        var debugPath = Path.Join(_root, "DeezSpoTag.Workers", "bin", "Debug", "net10.0", "Data", "autotag", "jellyfin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(debugPath)!);

        var payload = JsonSerializer.Serialize(new
        {
            url = "http://192.168.28.24:8096",
            apiKey = "nas-key",
            username = "nas-user"
        });
        await File.WriteAllTextAsync(livePath, payload);
        await File.WriteAllTextAsync(debugPath, payload);
        await File.WriteAllTextAsync(Path.Join(dataRoot, "autotag", "plex.json"), """{"url":"http://plex","token":"plex-token"}""");

        var auth = new PlatformAuthService(
            new OverrideWebHostEnvironment(dataRoot),
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Join(_root, "keys"))));

        await auth.UpdateAsync(state =>
        {
            state.Jellyfin = null;
            return 0;
        });

        Assert.False(File.Exists(livePath));
        Assert.False(File.Exists(debugPath));
        Assert.Null((await auth.LoadAsync()).Jellyfin);

        await File.WriteAllTextAsync(debugPath, payload);
        AppDataPathResolver.CopyLegacyWorkersData(
            Path.Join(_root, "DeezSpoTag.Workers", "bin", "Debug", "net10.0", "Data"),
            dataRoot);

        Assert.False(File.Exists(livePath));
        Assert.Null((await auth.LoadAsync()).Jellyfin);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed class OverrideWebHostEnvironment(string rootPath) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string WebRootPath { get; set; } = rootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = rootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string? AppDataRoot { get; } = rootPath;
    }
}
