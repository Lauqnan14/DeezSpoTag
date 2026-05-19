using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ManualQueueDuringEnrichmentGuardrailTests
{
    [Fact]
    public void ManualQueuePaths_UseManualQueueGate()
    {
        Assert.Contains(
            "EvaluateManualQueueGateAsync",
            ReadSource("DeezSpoTag.Web", "Controllers", "Api", "EngineDownloadControllerCommon.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "EvaluateManualQueueGateAsync",
            ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AppleDownloadApiController.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "EvaluateManualQueueGateAsync",
            ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DeezerDownloadApiController.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualIntentQueueing_UsesManualEntryPoint()
    {
        var manualSources = new[]
        {
            ReadSource("DeezSpoTag.Web", "Controllers", "ArtistController.cs"),
            ReadSource("DeezSpoTag.Web", "Controllers", "TracklistController.cs"),
            ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DownloadIntentApiController.cs"),
            ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AppleDownloadApiController.cs"),
            ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DeezerDownloadApiController.cs"),
            ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentBackgroundService.cs")
        };

        foreach (var source in manualSources)
        {
            Assert.Contains("EnqueueManualAsync", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WatchlistQueueing_RemainsOnStrictDownloadGate()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistWatchService.cs");

        Assert.Contains("EvaluateDownloadGateAsync", source, StringComparison.Ordinal);
        Assert.Contains("intentService.EnqueueAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EvaluateManualQueueGateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnqueueManualAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueWorkers_CheckExecutionGateBeforeDequeuing()
    {
        var appSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagApp.cs");
        var hostedSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagServiceExtensions.cs");
        var engineQueueSource = ReadSource("DeezSpoTag.Services", "Download", "Queue", "EngineQueueBackgroundService.cs");
        var orchestrationSource = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("IDownloadQueueExecutionGate", hostedSource, StringComparison.Ordinal);
        Assert.Contains("CanStartQueueItemAsync", appSource, StringComparison.Ordinal);
        Assert.True(
            appSource.IndexOf("CanStartQueueItemAsync(CancellationToken.None)", StringComparison.Ordinal)
            < appSource.IndexOf("DequeueNextAnyAsync", StringComparison.Ordinal));
        Assert.True(
            engineQueueSource.IndexOf("CanStartDownloadAsync(stoppingToken)", StringComparison.Ordinal)
            < engineQueueSource.IndexOf("DequeueNextAsync", StringComparison.Ordinal));
        Assert.Contains(
            "DownloadOrchestrationService : BackgroundService, IDownloadQueueExecutionGate",
            orchestrationSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QueueExecutionGate_IsRequiredAndHasNoPermissiveFallback()
    {
        var appSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagApp.cs");
        var serviceSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagServiceExtensions.cs");
        var serviceFiles = Directory.GetFiles(
            Path.Join(ResolveRepoRoot(), "DeezSpoTag.Services"),
            "*.cs",
            SearchOption.AllDirectories);

        Assert.Contains("GetRequiredService<IDownloadQueueExecutionGate>", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetService<IDownloadQueueExecutionGate>", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("executionGate == null", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AllowDownloadQueueExecutionGate",
            string.Join(Environment.NewLine, serviceFiles.Select(File.ReadAllText)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualQueueGate_AllowsQueueingButExecutionGateUsesStrictDownloadGate()
    {
        var orchestrationSource = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("EvaluateManualQueueGateAsync", orchestrationSource, StringComparison.Ordinal);
        Assert.Contains("allowManualQueueDuringEnrichment: true", orchestrationSource, StringComparison.Ordinal);
        Assert.Contains("allowManualQueueDuringEnrichment: false", orchestrationSource, StringComparison.Ordinal);
        Assert.Contains("CanStartDownloadAsync", orchestrationSource, StringComparison.Ordinal);
        Assert.Contains("EvaluateDownloadGateAsync(cancellationToken)", orchestrationSource, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Join([ResolveRepoRoot(), .. pathParts]));

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
