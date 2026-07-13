using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
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
    public void VibeAnalysisRuntime_IsCancellableAndDoesNotReadStaleProcessingRowsForCurrentTrack()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var controller = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryAnalysisStatusApiController.cs"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Contains("PauseActiveRun();", service, StringComparison.Ordinal);
        Assert.Contains("WaitForExitAsync(linked.Token)", service, StringComparison.Ordinal);
        Assert.Contains("ResetInterruptedProcessingRowsAsync", service, StringComparison.Ordinal);
        Assert.Contains("GetRuntimeSnapshot()", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProcessingTrackAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProcessingTrackAsync", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysisSettings_PersistCustomLibraryFolderOrder()
    {
        var repoRoot = ResolveRepoRoot();
        var settings = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "VibeAnalysisSettingsStore.cs"));
        var api = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "VibeAnalysisSettingsApiController.cs"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        Assert.Contains("bool UseLibraryOrder", settings, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<long> LibraryOrder", settings, StringComparison.Ordinal);
        Assert.Contains("request.UseLibraryOrder", api, StringComparison.Ordinal);
        Assert.Contains("analysis-use-library-order", view, StringComparison.Ordinal);
        Assert.Contains("analysis-folder-order-edit-toggle", view, StringComparison.Ordinal);
        Assert.Contains("analysis-folder-order-summary", view, StringComparison.Ordinal);
        Assert.Contains("analysisFolderOrderExpanded", view, StringComparison.Ordinal);
        Assert.Contains("analysis-folder-order-list", view, StringComparison.Ordinal);
        Assert.Contains("temp_analysis_library_scope", repository, StringComparison.Ordinal);
        Assert.Contains("CreateLibraryScopeTableAsync", repository, StringComparison.Ordinal);
        Assert.Contains("scope.sort_order", repository, StringComparison.Ordinal);
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
        Assert.DoesNotContain("self.load_audio(file_path", analyzer, StringComparison.Ordinal);
        Assert.Contains("startInfo.Environment[\"DEEZSPOTAG_FFMPEG_PATH\"] = FfmpegExecutablePath;", service, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeAnalysis_UsesAudioLibrariesAndOneFfmpegDecodePath()
    {
        var repoRoot = ResolveRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        var repository = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        Assert.Contains("isVibeAudioFolder(folder)", view, StringComparison.Ordinal);
        Assert.Contains("desiredQuality.includes('video') || desiredQuality.includes('podcast')", view, StringComparison.Ordinal);
        Assert.Contains("desired_quality_value, '')) NOT LIKE '%video%'", repository, StringComparison.Ordinal);
        Assert.Contains("desired_quality_value, '')) NOT LIKE '%podcast%'", repository, StringComparison.Ordinal);
        Assert.Contains("coalesce(af.size, 0) > 0", repository, StringComparison.Ordinal);
        Assert.Contains("coalesce(af.sample_rate_hz, 0) > 0", repository, StringComparison.Ordinal);

        Assert.Contains("TryReadWithFfmpeg(track.FilePath", service, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"0:a:0\")", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IsFfmpegHandledExtension", service, StringComparison.Ordinal);
        Assert.DoesNotContain("TryReadMp3Samples", service, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAudioStream", service, StringComparison.Ordinal);
    }

    [Fact]
    public void VibeSampleDecoder_DecodesValidOpusAndRejectsEmptyOpus()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("deezspotag-vibe-opus-");
        try
        {
            var opusPath = Path.Join(tempDirectory.FullName, "tone.opus");
            GenerateOpusFixture(opusPath);

            var decodeMethod = typeof(TrackAnalysisBackgroundService).GetMethod(
                "TryLoadTrackSamples",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(decodeMethod);

            var decodeArguments = new object?[]
            {
                new TrackAnalysisInputDto(1, 1, opusPath, 1000),
                null,
                0,
                null
            };
            Assert.True(Assert.IsType<bool>(decodeMethod!.Invoke(null, decodeArguments)));
            Assert.NotEmpty(Assert.IsType<float[]>(decodeArguments[1]));
            Assert.Equal(44100, Assert.IsType<int>(decodeArguments[2]));
            Assert.Null(decodeArguments[3]);

            var emptyOpusPath = Path.Join(tempDirectory.FullName, "empty.opus");
            File.WriteAllBytes(emptyOpusPath, Array.Empty<byte>());
            var emptyArguments = new object?[]
            {
                new TrackAnalysisInputDto(2, 1, emptyOpusPath, null),
                null,
                0,
                null
            };
            Assert.False(Assert.IsType<bool>(decodeMethod.Invoke(null, emptyArguments)));
            var failure = Assert.IsType<TrackAnalysisResultDto>(emptyArguments[3]);
            Assert.Contains("empty", failure.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static void GenerateOpusFixture(string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                     "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                     "-c:a", "libopus", outputPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stderr = process!.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        Assert.True(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0);
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
