using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ProviderIntegrationSurfaceTests
{
    [Fact]
    public void QobuzBuildProviders_IncludesSpotByeProvider()
    {
        var service = new QobuzDownloadService(NullLogger<QobuzDownloadService>.Instance, trackResolver: null!);
        var method = typeof(QobuzDownloadService).GetMethod("BuildProviders", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var result = method!.Invoke(service, [123L, "27"]);
        Assert.NotNull(result);

        var providers = Assert.IsAssignableFrom<Array>(result);
        var names = providers
            .Cast<object>()
            .Select(provider => provider.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(provider)?.ToString())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();

        Assert.Contains("qobuz.spotbye.qzz.io", names);
    }

    [Fact]
    public void QobuzCleanUnverifiedExpectedOutput_RemovesStaleFallbackFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deezspotag-qobuz-stale-{Guid.NewGuid():N}.flac");
        File.WriteAllBytes(path, new byte[4096]);

        try
        {
            var method = typeof(QobuzDownloadService).GetMethod(
                "CleanUnverifiedExpectedOutput",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            method!.Invoke(null, [path]);

            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AmazonDirectStreamProviders_IncludeSpotByeProvider()
    {
        var field = typeof(AmazonDownloadService).GetField("StreamProviderHosts", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        var providers = Assert.IsAssignableFrom<string[]>(field!.GetValue(null));

        Assert.Contains("amazon.afkarxyz.fun", providers);
        Assert.Contains("amazon.spotbye.qzz.io", providers);
    }
}
