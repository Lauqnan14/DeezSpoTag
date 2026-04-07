using System;
using System.IO;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Tests;

internal sealed class TestConfigRootScope : IDisposable
{
    private readonly string? _previousConfigDir;
    private readonly string? _previousDataDir;

    public TestConfigRootScope(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path is required.", nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);

        _previousConfigDir = Environment.GetEnvironmentVariable(AppDataPathResolver.ConfigDirEnvVar);
        _previousDataDir = Environment.GetEnvironmentVariable(AppDataPathResolver.DataDirEnvVar);

        Environment.SetEnvironmentVariable(AppDataPathResolver.ConfigDirEnvVar, RootPath);
        Environment.SetEnvironmentVariable(AppDataPathResolver.DataDirEnvVar, RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPathResolver.ConfigDirEnvVar, _previousConfigDir);
        Environment.SetEnvironmentVariable(AppDataPathResolver.DataDirEnvVar, _previousDataDir);
    }
}
