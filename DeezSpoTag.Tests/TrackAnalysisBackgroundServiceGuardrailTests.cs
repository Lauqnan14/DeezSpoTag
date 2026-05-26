using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackAnalysisBackgroundServiceGuardrailTests
{
    [Fact]
    public void PerTrackAnalyzerFailures_DoNotDisableEnhancedCapabilityGlobally()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        Assert.Contains("IsMlCapabilityFailure(analyzerFailure.ErrorCode)", source, StringComparison.Ordinal);
        Assert.Contains("\"ESSENTIA_MISSING_REQUIRED\" or", source, StringComparison.Ordinal);
        Assert.Contains("\"VIBE_ANALYZER_NOT_INITIALIZED\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetMlCapabilityUnavailable(analyzerFailure.Reason);\r\n                LogMlUnavailable(analyzerFailure.Reason);\r\n                failureReason = analyzerFailure.Reason", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticAndManualAnalysisPasses_AreSerialized()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        Assert.Contains("await _analysisLock.WaitAsync(stoppingToken);", source, StringComparison.Ordinal);
        Assert.Contains("await _analysisLock.WaitAsync(cancellationToken);", source, StringComparison.Ordinal);
        Assert.Contains("DefaultVibeAnalyzerTimeoutSeconds = 180", source, StringComparison.Ordinal);
        Assert.Contains("process.WaitForExit(5000);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackAnalysisCandidateSelection_PrefersEssentiaDecodableCopiesOverAtmosEac3()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Contains("lower(coalesce(af.codec, '')) LIKE '%eac3%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.codec, '')) LIKE '%dolby digital plus%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.audio_variant, '')) LIKE '%atmos%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.codec, '')) LIKE '%opus%'", source, StringComparison.Ordinal);
        Assert.Contains("lower(coalesce(af.extension, '')) = '.opus'", source, StringComparison.Ordinal);
        Assert.Contains("af.quality_rank DESC NULLS LAST", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalyzer_UsesFfmpegDecodeFallbackForEssentiaUnsupportedCodecs()
    {
        var repoRoot = ResolveRepoRoot();
        var analyzer = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Tools", "vibe_analyzer.py"));
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));

        Assert.Contains("DEEZSPOTAG_FFMPEG_PATH", analyzer, StringComparison.Ordinal);
        Assert.Contains("tempfile.NamedTemporaryFile", analyzer, StringComparison.Ordinal);
        Assert.Contains("subprocess.run", analyzer, StringComparison.Ordinal);
        Assert.Contains("\"-map\"", analyzer, StringComparison.Ordinal);
        Assert.Contains("\"0:a:0\"", analyzer, StringComparison.Ordinal);
        Assert.Contains("startInfo.Environment[\"DEEZSPOTAG_FFMPEG_PATH\"] = FfmpegExecutablePath;", service, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
