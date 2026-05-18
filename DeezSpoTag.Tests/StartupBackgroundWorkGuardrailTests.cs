using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class StartupBackgroundWorkGuardrailTests
{
    [Fact]
    public void StartupHeavyHostedServices_RespectBackgroundWorkCoordinator()
    {
        var root = ResolveRepoRoot();
        var serviceFiles = new[]
        {
            Path.Join(root, "DeezSpoTag.Web", "Services", "PlaylistWatchHostedService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyHomeFeedRefreshHostedService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyAuthWarmupService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "LyricsRefreshQueueService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "LibraryRealtimeScanService.cs"),
            Path.Join(root, "DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagServiceExtensions.cs")
        };

        foreach (var path in serviceFiles)
        {
            Assert.True(File.Exists(path), $"Missing service file: {path}");
            var source = File.ReadAllText(path);
            Assert.Contains("BackgroundWorkCoordinator", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LibraryRealtimeWatcher_EntersDegradedModeWhenInotifyLimitIsReached()
    {
        var root = ResolveRepoRoot();
        var servicePath = Path.Join(root, "DeezSpoTag.Web", "Services", "LibraryRealtimeScanService.cs");
        var source = File.ReadAllText(servicePath);

        Assert.Contains("EnterWatcherDegradedMode", source, StringComparison.Ordinal);
        Assert.Contains("MarkLibraryWatchersDegraded", source, StringComparison.Ordinal);
        Assert.Contains("IsWatcherResourceLimit", source, StringComparison.Ordinal);
        Assert.Contains("inotify", source, StringComparison.OrdinalIgnoreCase);
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
}
