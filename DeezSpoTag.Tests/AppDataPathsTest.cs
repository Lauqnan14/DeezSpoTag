using System;
using System.IO;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppDataPathsTest
{
    [Fact]
    public void GetDataRoot_UsesEnvironmentOverrideBeforeProcessEnvironment()
    {
        var previousConfigDir = Environment.GetEnvironmentVariable(AppDataPathResolver.ConfigDirEnvVar);
        var previousDataDir = Environment.GetEnvironmentVariable(AppDataPathResolver.DataDirEnvVar);
        var configuredRoot = Path.Join(Path.GetTempPath(), "deezspotag-config-root-" + Path.GetRandomFileName());
        var overrideRoot = Path.Join(Path.GetTempPath(), "deezspotag-override-root-" + Path.GetRandomFileName());
        try
        {
            Environment.SetEnvironmentVariable(AppDataPathResolver.ConfigDirEnvVar, configuredRoot);
            Environment.SetEnvironmentVariable(AppDataPathResolver.DataDirEnvVar, configuredRoot);

            var resolved = AppDataPaths.GetDataRoot(new OverrideWebHostEnvironment(overrideRoot));

            Assert.Equal(Path.GetFullPath(overrideRoot), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPathResolver.ConfigDirEnvVar, previousConfigDir);
            Environment.SetEnvironmentVariable(AppDataPathResolver.DataDirEnvVar, previousDataDir);
        }
    }

    [Fact]
    public void CopyLegacyWorkersData_DoesNotRestoreDeletedAutotagCredentials()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-auth-migrate-" + Path.GetRandomFileName());
        var source = Path.Join(root, "legacy");
        var target = Path.Join(root, "canonical");
        try
        {
            Directory.CreateDirectory(Path.Join(source, "autotag"));
            Directory.CreateDirectory(Path.Join(target, "autotag"));
            File.WriteAllText(Path.Join(source, "autotag", "jellyfin.json"), """{"url":"http://nas:8096"}""");
            File.WriteAllText(Path.Join(source, "autotag", "plex.json"), """{"url":"http://nas:32400"}""");
            File.WriteAllText(Path.Join(target, "autotag", "plex.json"), """{"url":"http://local:32400"}""");

            AppDataPathResolver.CopyLegacyWorkersData(source, target);

            Assert.False(File.Exists(Path.Join(target, "autotag", "jellyfin.json")));
            Assert.Equal("""{"url":"http://local:32400"}""", File.ReadAllText(Path.Join(target, "autotag", "plex.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CopyLegacyWorkersData_CopiesAutotagCredentialsOnFirstMigration()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-auth-first-migrate-" + Path.GetRandomFileName());
        var source = Path.Join(root, "legacy");
        var target = Path.Join(root, "canonical");
        try
        {
            Directory.CreateDirectory(Path.Join(source, "autotag"));
            File.WriteAllText(Path.Join(source, "autotag", "jellyfin.json"), """{"url":"http://nas:8096"}""");

            AppDataPathResolver.CopyLegacyWorkersData(source, target);

            Assert.True(File.Exists(Path.Join(target, "autotag", "jellyfin.json")));
            Assert.Equal("""{"url":"http://nas:8096"}""", File.ReadAllText(Path.Join(target, "autotag", "jellyfin.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class OverrideWebHostEnvironment(string rootPath) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string WebRootPath { get; set; } = rootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = rootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string? AppDataRoot { get; } = rootPath;
    }
}
