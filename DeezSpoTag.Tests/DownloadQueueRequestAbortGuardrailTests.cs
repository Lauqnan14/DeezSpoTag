using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueRequestAbortGuardrailTests
{
    [Fact]
    public void EngineDownloadBatchQueueing_IsNotBoundToRequestAbort()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "EngineDownloadControllerCommon.cs");

        Assert.Contains("var cancellationToken = CancellationToken.None;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("controller.HttpContext.RequestAborted", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadIntentImmediateQueueing_IsStartedWithServerOwnedToken()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DownloadIntentApiController.cs");

        Assert.Contains("EnqueueImmediatelyAsync(request, CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status500InternalServerError", source, StringComparison.Ordinal);
        Assert.Contains("download_enqueue_internal_error", source, StringComparison.Ordinal);
        Assert.DoesNotContain("throw;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryDownloadControllers_DoNotBindQueueCreationToRequestAbort()
    {
        var root = ResolveRepoRoot();
        var files = new[]
        {
            Path.Join(root, "DeezSpoTag.Web", "Controllers", "ArtistController.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Controllers", "TracklistController.cs"),
            Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "AppleDownloadApiController.cs")
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("RequestAborted", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LegacyMusicDownloadActions_KeepSourceIdentitySeparateFromPreferredEngine()
    {
        var tracklistSource = ReadSource("DeezSpoTag.Web", "Controllers", "TracklistController.cs");
        var artistSource = ReadSource("DeezSpoTag.Web", "Controllers", "ArtistController.cs");
        var deezerApiSource = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DeezerDownloadApiController.cs");
        var downloadIntentSource = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var resolverSource = ReadSource("DeezSpoTag.Services", "Download", "ManualDownloadPreferenceResolver.cs");

        Assert.Contains("ManualDownloadPreferenceResolver.ResolvePreferredEngine(settings)", tracklistSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PreferredEngine = DeezerSource", tracklistSource, StringComparison.Ordinal);

        Assert.Contains("ManualDownloadPreferenceResolver.ResolvePreferredEngine(settings)", artistSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PreferredEngine = \"deezer\"", artistSource, StringComparison.Ordinal);

        Assert.Contains("ApplyManualDownloadPreferenceIfMissing(intent, request.Settings)", deezerApiSource, StringComparison.Ordinal);
        Assert.Contains("ManualDownloadPreferenceResolver.ResolvePreferredEngine(settings)", deezerApiSource, StringComparison.Ordinal);
        Assert.Contains("PreferredEngine = preferredEngine", deezerApiSource, StringComparison.Ordinal);
        Assert.Contains("ApplyManualDownloadPreferenceIfMissing(intent, settings)", downloadIntentSource, StringComparison.Ordinal);
        Assert.Contains("intent.PreferredEngine = ManualDownloadPreferenceResolver.ResolvePreferredEngine(settings);", downloadIntentSource, StringComparison.Ordinal);
        Assert.Contains("NormalizeSourcePolicy(settings.Service)", resolverSource, StringComparison.Ordinal);
        Assert.Contains("\"auto\" or \"custom\" or \"amazon\" or \"apple\" or \"deezer\" or \"qobuz\" or \"tidal\"", resolverSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DeezerAddWithSettingsQueueing_IsServerOwned()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DeezerDownloadApiController.cs");

        Assert.Contains("var cancellationToken = CancellationToken.None;", source, StringComparison.Ordinal);
        Assert.Contains("await gate.WaitAsync(CancellationToken.None);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var abortToken = HttpContext.RequestAborted", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveDownloadCancellation_DoesNotMarkQueueTerminalBeforeEngineUnwinds()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagApp.cs");

        Assert.Contains("var activeCancellationRequested = _cancellationRegistry.Cancel(uuid);", source, StringComparison.Ordinal);
        Assert.Contains("if (activeCancellationRequested)", source, StringComparison.Ordinal);
        Assert.Contains("Listener?.Send(\"cancellingCurrentItem\", uuid);", source, StringComparison.Ordinal);
        Assert.Contains("return;", source, StringComparison.Ordinal);
        Assert.Contains("await _queueRepository.UpdateStatusAsync(uuid, CanceledStatus);", source, StringComparison.Ordinal);
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
