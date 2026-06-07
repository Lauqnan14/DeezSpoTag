using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzResolutionGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void QobuzProcessor_ValidatesCandidateBeforePersistingStagingPath()
    {
        var source = ReadSource("DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs");

        var resolveIndex = source.IndexOf(
            "var resolvedTrack = await ResolveAndPersistPreferredTrackAsync",
            StringComparison.Ordinal);
        var contextIndex = source.IndexOf(
            "var context = await BuildTrackContextAsync",
            StringComparison.Ordinal);
        var persistIndex = source.IndexOf(
            "PersistExpectedStagingPathAsync",
            StringComparison.Ordinal);
        var failureIndex = source.IndexOf(
            "Qobuz track not found for ISRC or metadata.",
            StringComparison.Ordinal);

        Assert.True(resolveIndex >= 0);
        Assert.True(contextIndex > resolveIndex);
        Assert.True(persistIndex > contextIndex);
        Assert.True(failureIndex > resolveIndex && failureIndex < contextIndex);
    }

    [Fact]
    public void QobuzDirectEnqueue_UsesSingleDedupeService()
    {
        var controller = ReadSource("DeezSpoTag.Web/Controllers/Api/QobuzDownloadApiController.cs");
        var helper = ReadSource("DeezSpoTag.Web/Controllers/Api/DownloadQueueEnqueueHelper.cs");

        Assert.Contains("DownloadDedupeService dedupeService", controller, StringComparison.Ordinal);
        Assert.Contains("_dedupeService", controller, StringComparison.Ordinal);
        Assert.Contains("await dedupeService.CheckAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("RequeueAsync(existing.QueueUuid", helper, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
