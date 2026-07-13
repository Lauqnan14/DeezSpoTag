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
            Path.Join(root, "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyHomeFeedRefreshHostedService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyAuthWarmupService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "LyricsRefreshQueueService.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"),
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
    public void LibraryRealtimeWatcher_IsNotStartedAutomatically()
    {
        var root = ResolveRepoRoot();
        var programPath = Path.Join(root, "DeezSpoTag.Web", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.DoesNotContain("AddDeferredHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupLoginService_IsPostReadyBackgroundService()
    {
        var root = ResolveRepoRoot();
        var servicePath = Path.Join(root, "DeezSpoTag.Web", "Services", "StartupLoginService.cs");
        var source = File.ReadAllText(servicePath);

        Assert.Contains(": BackgroundService", source, StringComparison.Ordinal);
        Assert.Contains("IHostApplicationLifetime", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(": IHostedService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public async Task StartAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_ReleasesBackgroundWorkersAfterHttpReady()
    {
        var root = ResolveRepoRoot();
        var programPath = Path.Join(root, "DeezSpoTag.Web", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("startup: {StartupCheckpoint}", source, StringComparison.Ordinal);
        Assert.Contains("\"/api/runtime/startup\"", source, StringComparison.Ordinal);
        Assert.Contains(".RequireAuthorization()", source, StringComparison.Ordinal);
        Assert.Contains("/health", source, StringComparison.Ordinal);
        Assert.Contains(".AllowAnonymous()", source, StringComparison.Ordinal);
        Assert.Contains("workers =", source, StringComparison.Ordinal);
        Assert.Contains("\"http ready\"", source, StringComparison.Ordinal);
        Assert.Contains("MarkApplicationStarted", source, StringComparison.Ordinal);
        Assert.Contains("\"background workers released\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredHostedService_RechecksStartedServiceDuringStop()
    {
        var root = ResolveRepoRoot();
        var servicePath = Path.Join(root, "DeezSpoTag.Web", "Services", "DeferredHostedService.cs");
        var source = File.ReadAllText(servicePath);

        Assert.Contains("await startTask.WaitAsync(cancellationToken);", source, StringComparison.Ordinal);
        Assert.Contains("service = _service;", source, StringComparison.Ordinal);
        Assert.Contains("await service.StopAsync(cancellationToken);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupWorkers_HaveExplicitCategories()
    {
        var root = ResolveRepoRoot();
        var programPath = Path.Join(root, "DeezSpoTag.Web", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("StartupWorkerCategory.Critical", source, StringComparison.Ordinal);
        Assert.Contains("StartupWorkerCategory.Deferred", source, StringComparison.Ordinal);
        Assert.Contains("StartupWorkerCategory.Manual", source, StringComparison.Ordinal);
        Assert.Contains("MandatoryStartupInitialization", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryRealtimeScanService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalHostedServices_AreDeferredExceptStartupLogin()
    {
        var root = ResolveRepoRoot();
        var programPath = Path.Join(root, "DeezSpoTag.Web", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("AddHostedService<StartupLoginService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<DeezSpoTag.Services.Download.Shared.DeezSpoTagQueueBackgroundService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<DeezSpoTag.Web.Services.PlexMetadataRefreshService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<DeezSpoTag.Web.Services.SpotifyAuthWarmupService>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedQueueRegistration_DoesNotStartPostDownloadSchedulerBeforeReadiness()
    {
        var root = ResolveRepoRoot();
        var sharedPath = Path.Join(root, "DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagServiceExtensions.cs");
        var source = File.ReadAllText(sharedPath);

        Assert.Contains("AddSingleton<PostDownloadTaskScheduler>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService(sp => sp.GetRequiredService<PostDownloadTaskScheduler>())", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_DoesNotPerformDirectNetworkCallsBeforeRunAsync()
    {
        var root = ResolveRepoRoot();
        var programPath = Path.Join(root, "DeezSpoTag.Web", "Program.cs");
        var source = File.ReadAllText(programPath);
        var runIndex = source.IndexOf("await app.RunAsync()", StringComparison.Ordinal);

        Assert.True(runIndex > 0, "Program.cs must call app.RunAsync().");
        var beforeRun = source[..runIndex];

        Assert.DoesNotContain(".GetAsync(", beforeRun, StringComparison.Ordinal);
        Assert.DoesNotContain(".PostAsync(", beforeRun, StringComparison.Ordinal);
        Assert.DoesNotContain(".SendAsync(", beforeRun, StringComparison.Ordinal);
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
