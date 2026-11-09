using System;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerDownloadApiControllerPreferenceTest
{
    private static readonly MethodInfo ParseInputMetadataMethod = typeof(DeezerDownloadApiController)
        .GetMethod("ParseInputMetadata", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ParseInputMetadata method not found.");

    private static readonly MethodInfo ApplyInputMetadataMethod = typeof(DeezerDownloadApiController)
        .GetMethod("ApplyInputMetadata", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyInputMetadata method not found.");

    private static readonly MethodInfo ShouldBypassDirectDeezerRoutingMethod = typeof(DeezerDownloadApiController)
        .GetMethod("ShouldBypassDirectDeezerRouting", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ShouldBypassDirectDeezerRouting method not found.");

    [Fact]
    public void AllResolvedDownloadsUseThePersistedQueueBeforeMatching()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "DeezSpoTag.Web", "Program.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(root!.FullName,
            "DeezSpoTag.Web", "Controllers", "Api", "DeezerDownloadApiController.cs"));
        Assert.Contains("_intentService.EnqueueManualVisibleAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_backgroundQueue.Enqueue(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("deferred = true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDownloadIntentBackgroundQueue", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseInputMetadata_ReadsPreferredEngine()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "sourceService": "deezer",
              "sourceUrl": "https://www.deezer.com/track/123",
              "preferredEngine": "qobuz"
            }
            """);

        var parsed = (DownloadIntent)ParseInputMetadataMethod.Invoke(null, [document.RootElement])!;

        Assert.Equal("qobuz", parsed.PreferredEngine);
    }

    [Fact]
    public void ApplyInputMetadata_CopiesPreferredEngine_WhenTargetIsUnset()
    {
        var target = new DownloadIntent
        {
            SourceService = "deezer",
            SourceUrl = "https://www.deezer.com/track/123"
        };

        var metadata = new DownloadIntent
        {
            PreferredEngine = "apple"
        };

        ApplyInputMetadataMethod.Invoke(null, [target, metadata]);

        Assert.Equal("apple", target.PreferredEngine);
    }

    [Fact]
    public void ShouldBypassDirectDeezerRouting_WhenPreferredEngineOverridesDeezer()
    {
        var metadata = new DownloadIntent
        {
            SourceService = "deezer",
            PreferredEngine = "tidal"
        };

        var shouldBypass = (bool)ShouldBypassDirectDeezerRoutingMethod.Invoke(
            null,
            ["https://www.deezer.com/track/123", "deezer", metadata])!;

        Assert.True(shouldBypass);
    }
}
