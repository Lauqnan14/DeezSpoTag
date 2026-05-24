using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Conversion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LiveDiagnosticsTests
{
    private const string AppleWrapperLiveFlag = "DEEZSPOTAG_LIVE_APPLE_WRAPPER_TESTS";
    private const string ShazamLiveFlag = "DEEZSPOTAG_LIVE_SHAZAM_TESTS";
    private const string FfmpegLiveFlag = "DEEZSPOTAG_LIVE_FFMPEG_TESTS";
    private const string LibraryScanLiveFlag = "DEEZSPOTAG_LIVE_LIBRARY_SCAN_TESTS";
    private const string LibraryScanRootEnv = "DEEZSPOTAG_LIVE_LIBRARY_SCAN_ROOT";

    [Fact]
    public async Task FfmpegLiveTools_AreUsableWhenEnabled()
    {
        if (!IsEnabled(FfmpegLiveFlag))
        {
            return;
        }

        Assert.True(await RunToolVersionAsync("ffmpeg"), "ffmpeg must be executable for live conversion tests.");
        Assert.True(await RunToolVersionAsync("ffprobe"), "ffprobe must be executable for live audio validation tests.");
    }

    [Fact]
    public async Task ShazamLiveRuntime_IsUsableWhenEnabled()
    {
        if (!IsEnabled(ShazamLiveFlag))
        {
            return;
        }

        var python = FirstNonEmpty(Environment.GetEnvironmentVariable("SHAZAM_PYTHON"), "python3");
        Assert.True(
            await RunProcessAsync(python, "-c", "import shazamio"),
            "Shazam live tests require a Python runtime that can import shazamio.");
    }

    [Fact]
    public void AppleWrapperLiveTools_AreUsableWhenEnabled()
    {
        if (!IsEnabled(AppleWrapperLiveFlag))
        {
            return;
        }

        Assert.True(AppleExternalToolRunner.HasMp4Decrypt(), "Apple live tests require mp4decrypt.");
        Assert.True(AppleExternalToolRunner.HasMp4Box(), "Apple live tests require MP4Box.");
    }

    [Fact]
    public void LibraryScanLiveRoot_IsUsableWhenEnabled()
    {
        if (!IsEnabled(LibraryScanLiveFlag))
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable(LibraryScanRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(root), $"{LibraryScanRootEnv} must point to a real library folder.");
        Assert.True(Directory.Exists(root), $"{LibraryScanRootEnv} does not exist: {root}");
        Assert.NotEmpty(Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OfflineFailurePaths_ReturnControlledFailures()
    {
        var appleTools = new AppleExternalToolRunner(NullLogger<AppleExternalToolRunner>.Instance);
        Assert.False(await appleTools.RunMp4DecryptAsync("", "/missing/in.m4a", "/missing/out.m4a", CancellationToken.None));
        Assert.False(await AppleExternalToolRunner.HasAudioTrackAsync("/missing/audio.m4a", CancellationToken.None));

        var converter = new FfmpegConversionService(NullLogger<FfmpegConversionService>.Instance);
        var conversion = await converter.ConvertIfNeededAsync(
            "/missing/source.flac",
            "mp3",
            "320k",
            ConversionOptions.Default,
            CancellationToken.None);

        Assert.False(conversion.WasConverted);
        Assert.Equal("Input file not found.", conversion.Error);
    }

    private static bool IsEnabled(string envName)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static async Task<bool> RunToolVersionAsync(string tool)
        => await RunProcessAsync(tool, "-version");

    private static async Task<bool> RunProcessAsync(string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
