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
    public void DeezerAddWithSettingsQueueing_IsServerOwned()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DeezerDownloadApiController.cs");

        Assert.Contains("var cancellationToken = CancellationToken.None;", source, StringComparison.Ordinal);
        Assert.Contains("await gate.WaitAsync(CancellationToken.None);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var abortToken = HttpContext.RequestAborted", source, StringComparison.Ordinal);
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
