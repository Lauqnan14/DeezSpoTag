using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Apple;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AnimatedArtworkSizeBudgetTests
{
    [Fact]
    public void MaxSizeDefaultsToTenMegabytes()
    {
        var settings = new DeezSpoTagSettings();

        Assert.Equal(10, settings.AnimatedArtworkMaxSizeMb);
        Assert.Equal(10, AppleQueueHelpers.ResolveAnimatedArtworkMaxSizeMb(settings));
        Assert.Equal(10, AppleQueueHelpers.DefaultAnimatedArtworkMaxSizeMb);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(25, 25)]
    [InlineData(5000, 200)]
    public void MaxSizeIsNormalizedIntoASupportedRange(int configured, int expected)
    {
        var settings = new DeezSpoTagSettings { AnimatedArtworkMaxSizeMb = configured };

        Assert.Equal(expected, AppleQueueHelpers.ResolveAnimatedArtworkMaxSizeMb(settings));
    }

    [Fact]
    public void GifEncodingUsesAPaletteSoColoursAreNotCrushed()
    {
        var source = ReadHelpers();

        Assert.Contains("palettegen=stats_mode=diff", source, StringComparison.Ordinal);
        Assert.Contains("paletteuse=dither=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeLadderStepsDownAndNeverUpscales()
    {
        var source = ReadHelpers();

        Assert.Contains("GetAnimatedArtworkEncodeLadder", source, StringComparison.Ordinal);
        Assert.Contains("scale='min(iw,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("startInfo.ArgumentList.Add(\"fps=15,scale=iw:-2:flags=lanczos\");", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegFailuresAreNoLongerSilenced()
    {
        var source = ReadHelpers();

        Assert.DoesNotContain("startInfo.ArgumentList.Add(\"quiet\");", source, StringComparison.Ordinal);
        Assert.Contains("stderr.Trim()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimatedArtworkFfmpegIsSerializedAndBounded()
    {
        var source = ReadHelpers();

        Assert.Contains("AnimatedArtworkFfmpegGate", source, StringComparison.Ordinal);
        Assert.Contains("AnimatedArtworkFfmpegTimeout", source, StringComparison.Ordinal);
        Assert.Contains("process.Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"-nostdin\");", source, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"-threads\");", source, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"1\");", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll(conversions)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebpIsReEncodedUntilItFitsTheConfiguredBudget()
    {
        var root = CreateTempDirectory();
        await BuildOversizedSourceAsync(Path.Join(root, "cover.mp4"));

        var savedPaths = await AppleQueueHelpers.SaveExistingAnimatedArtworkVariantsAsync(
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                OutputDir = root,
                BaseFileName = "cover",
                OutputFormats = new[] { "webp" },
                MaxSizeMb = 2,
                Logger = NullLogger.Instance
            },
            NullLogger.Instance,
            CancellationToken.None);

        var webp = Path.Join(root, "cover.webp");
        Assert.Contains(webp, savedPaths);
        var length = new FileInfo(webp).Length;
        Assert.True(length > 0, "The webp variant was not produced.");
        Assert.True(
            length <= 2L * 1024 * 1024,
            $"webp was {length} bytes, which exceeds the 2 MB budget.");
    }

    [Fact]
    public async Task GifIsReEncodedUntilItFitsTheConfiguredBudget()
    {
        var root = CreateTempDirectory();
        await BuildOversizedSourceAsync(Path.Join(root, "cover.mp4"));

        var savedPaths = await AppleQueueHelpers.SaveExistingAnimatedArtworkVariantsAsync(
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                OutputDir = root,
                BaseFileName = "cover",
                OutputFormats = new[] { "gif" },
                MaxSizeMb = 2,
                Logger = NullLogger.Instance
            },
            NullLogger.Instance,
            CancellationToken.None);

        var gif = Path.Join(root, "cover.gif");
        Assert.Contains(gif, savedPaths);
        var length = new FileInfo(gif).Length;
        Assert.True(length > 0, "The gif variant was not produced.");
        Assert.True(
            length <= 2L * 1024 * 1024,
            $"gif was {length} bytes, which exceeds the 2 MB budget.");
    }

    [Fact]
    public async Task RequestingWebpOnlyNeverWritesAnMp4()
    {
        var root = CreateTempDirectory();
        await BuildOversizedSourceAsync(Path.Join(root, "source.mp4"));

        await AppleQueueHelpers.SaveExistingAnimatedArtworkVariantsAsync(
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                OutputDir = root,
                BaseFileName = "source",
                OutputFormats = new[] { "webp" },
                MaxSizeMb = 4,
                Logger = NullLogger.Instance
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(File.Exists(Path.Join(root, "source.webp")));
        Assert.False(File.Exists(Path.Join(root, "source_tall.mp4")));
        Assert.Empty(Directory.GetFiles(root, "*.gif"));
    }

    [Fact]
    public async Task AnAlbumWithoutATallVariantIsTreatedAsComplete()
    {
        var root = CreateTempDirectory();
        await BuildSmallSourceAsync(Path.Join(root, "cover.webp"));

        var complete = typeof(AppleQueueHelpers)
            .GetMethod(
                "AreAllCanonicalAnimatedArtworkOutputsPresent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[]
            {
                new AppleQueueHelpers.AnimatedArtworkSaveRequest
                {
                    OutputDir = root,
                    BaseFileName = "cover",
                    OutputFormats = new[] { "webp" },
                    Logger = NullLogger.Instance
                }
            });

        Assert.True((bool)complete!, "A square-only album should not be re-resolved on every run.");
    }

    private static string ReadHelpers()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Download", "Apple", "AppleQueueHelpers.cs"));

    private static string CreateTempDirectory()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-animated-budget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static Task BuildOversizedSourceAsync(string path)
        => RunFfmpegAsync(
            "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1080x1080:rate=30",
            "-t", "12", "-c:v", "libx264", "-pix_fmt", "yuv420p", path);

    private static Task BuildSmallSourceAsync(string path)
        => RunFfmpegAsync(
            "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=12",
            "-t", "1", "-loop", "0", path);

    private static async Task RunFfmpegAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stderr = process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
    }

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
