using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamLogoRecognitionGuardrailTests
{
    [Fact]
    public void LogoCapture_UsesSingleSessionWithFastAndFinalAttempts()
    {
        var root = ResolveRepoRoot();
        var scriptPath = Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "shazam-listen.js");
        Assert.True(File.Exists(scriptPath), $"Missing Shazam logo script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);

        Assert.Contains("activeLogoSession", source, StringComparison.Ordinal);
        Assert.Contains("completeLogoSession", source, StringComparison.Ordinal);
        Assert.Contains("runLogoRecognitionAttempt(sessionId, 'quick'", source, StringComparison.Ordinal);
        Assert.Contains("runLogoRecognitionAttempt(sessionId, 'final'", source, StringComparison.Ordinal);
        Assert.Contains("phase: 'logo'", source, StringComparison.Ordinal);
        Assert.Contains("attempt: phase", source, StringComparison.Ordinal);
        Assert.Contains("logoSessionId: `logo-${sessionId}`", source, StringComparison.Ordinal);
        Assert.Contains("activeQuickProbeController.abort();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoRecognitionApi_ReturnsSessionAndAttemptMetadataWithPayloads()
    {
        var root = ResolveRepoRoot();
        var controllerPath = Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "ShazamApiController.cs");
        Assert.True(File.Exists(controllerPath), $"Missing Shazam API controller: {controllerPath}");

        var source = File.ReadAllText(controllerPath);

        Assert.Contains("[FromForm] string? captureAttempt", source, StringComparison.Ordinal);
        Assert.Contains("[FromForm] string? logoSessionId", source, StringComparison.Ordinal);
        Assert.Contains("\"logo\" => \"logo\"", source, StringComparison.Ordinal);
        Assert.Contains("captureAttempt,", source, StringComparison.Ordinal);
        Assert.Contains("logoSessionId,", source, StringComparison.Ordinal);
        Assert.Contains("related = relatedList", source, StringComparison.Ordinal);
        Assert.Contains("similar = relatedList", source, StringComparison.Ordinal);
        Assert.Contains("searchResults = searchList", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShazamResults_BackfillsSimilarSongsFromLivePayloadTrackId()
    {
        var root = ResolveRepoRoot();
        var viewPath = Path.Join(root, "DeezSpoTag.Web", "Views", "Shazam", "Results.cshtml");
        Assert.True(File.Exists(viewPath), $"Missing Shazam results view: {viewPath}");

        var source = File.ReadAllText(viewPath);

        Assert.Contains("let effectiveTrackId = normalizeText(trackId);", source, StringComparison.Ordinal);
        Assert.Contains("livePayload?.track?.id || livePayload?.recognition?.trackId", source, StringComparison.Ordinal);
        Assert.Contains("fetchJson(`/api/shazam/related/${encodeURIComponent(effectiveTrackId)}?limit=20`)", source, StringComparison.Ordinal);
        Assert.Contains("if (effectiveTrackId && (!match || similar.length === 0))", source, StringComparison.Ordinal);
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
