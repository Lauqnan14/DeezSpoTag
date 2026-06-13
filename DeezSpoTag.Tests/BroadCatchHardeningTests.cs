using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed partial class BroadCatchHardeningTests
{
    [Fact]
    public void RecommendationUnavailableMessage_ExplainsPersistFailure()
    {
        var method = typeof(LibraryRecommendationService).GetMethod(
            "BuildRecommendationUnavailableMessage",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var message = (string)method.Invoke(null, [new List<string> { "persist_failed" }])!;

        Assert.Equal("Recommendation generation completed but failed to save. Try regenerating the station.", message);
    }

    [Fact]
    public void RecommendationUnavailableMessage_ExplainsBackgroundFailure()
    {
        var method = typeof(LibraryRecommendationService).GetMethod(
            "BuildRecommendationUnavailableMessage",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var message = (string)method.Invoke(null, [new List<string> { "background_generation_failed" }])!;

        Assert.Equal("Recommendation generation failed in the background. Try regenerating the station.", message);
    }

    [Fact]
    public void ShazamPersistedFailureReason_IncludesExceptionMessage()
    {
        var method = typeof(LibraryRecommendationService).GetMethod(
            "BuildPersistedFailureReason",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var reason = (string)method.Invoke(null, ["Shazam recognition failed", new InvalidOperationException("python bridge missing")])!;

        Assert.Equal("Shazam recognition failed: python bridge missing", reason);
    }

    [Fact]
    public async Task AppleDecodeValidation_ReturnsToolStartupFailureDetail()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-apple-tool-{Guid.NewGuid():N}");
        var fakeFfmpeg = Path.Combine(tempRoot, "ffmpeg");
        var mediaPath = Path.Combine(tempRoot, "audio.m4a");
        var previous = Environment.GetEnvironmentVariable("DEEZSPOTAG_FFMPEG_PATH");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await File.WriteAllTextAsync(fakeFfmpeg, "not executable");
            await File.WriteAllTextAsync(mediaPath, "not media");
            Environment.SetEnvironmentVariable("DEEZSPOTAG_FFMPEG_PATH", fakeFfmpeg);

            var runner = new AppleExternalToolRunner(NullLogger<AppleExternalToolRunner>.Instance);
            var result = await runner.ValidateDecodableAudioAsync(mediaPath, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("ffmpeg failed before producing output", result.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_FFMPEG_PATH", previous);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DownloadStagingGate_ScanFailureContinuesEnhancement()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "DeezSpoTag.Web",
            "Services",
            "DownloadOrchestrationService.cs"));

        Assert.Contains("LogStagingEnhancementScanBypass", source);
        Assert.Matches(
            DownloadStagingGateScanFailureRegex(),
            source);
    }

    [GeneratedRegex(
        @"catch\s*\(Exception ex\)\s*when\s*\(DeezSpoTag\.Core\.Diagnostics\.ExpectedExceptionPolicy\.IsRecoverable\(ex\)\)\s*\{\s*LogStagingEnhancementScanBypass\(ex,.*?\);\s*return false;",
        RegexOptions.Singleline)]
    private static partial Regex DownloadStagingGateScanFailureRegex();

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null
               && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
               && !Directory.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
