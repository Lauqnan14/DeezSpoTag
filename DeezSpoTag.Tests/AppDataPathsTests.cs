using System;
using System.IO;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppDataPathsTests
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
