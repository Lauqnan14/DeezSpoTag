using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadMoveServiceMoveFallbackTests
{
    [Fact]
    public void ShouldUseCopyFallback_ReturnsFalse_ForSameVolumeLocalPaths()
    {
        var method = GetPrivateStaticMethod("ShouldUseCopyFallback");
        var root = Path.Combine(Path.GetTempPath(), "deezspotag-download-move-tests");
        var sourcePath = Path.Combine(root, "source", "track.lrc");
        var destinationPath = Path.Combine(root, "destination", "track.lrc");

        var result = (bool)method.Invoke(null, new object[] { sourcePath, destinationPath })!;

        Assert.False(result);
    }

    [Fact]
    public void ShouldUseCopyFallback_ReturnsTrue_ForSmbPaths()
    {
        var method = GetPrivateStaticMethod("ShouldUseCopyFallback");

        var result = (bool)method.Invoke(null, new object[] { "smb://nas/music/source/track.lrc", "smb://nas/music/destination/track.lrc" })!;

        Assert.True(result);
    }

    [Fact]
    public void MoveFileWithFallback_MovesLocalFile_AndRemovesSource()
    {
        var method = GetPrivateStaticMethod("MoveFileWithFallback");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-download-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var sourcePath = Path.Combine(tempRoot, "track.txt");
        var destinationPath = Path.Combine(tempRoot, "nested", "track.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllText(sourcePath, "lyrics");

        try
        {
            method.Invoke(null, new object[] { sourcePath, destinationPath });

            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(destinationPath));
            Assert.Equal("lyrics", File.ReadAllText(destinationPath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    [Fact]
    public void MoveWithFallback_VerifiesAndCompletesCrossDeviceCopy()
    {
        const string crossDeviceRoot = "/dev/shm";
        if (!OperatingSystem.IsLinux() || !Directory.Exists(crossDeviceRoot))
        {
            return;
        }

        var sourceRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-cross-device-source-{Guid.NewGuid():N}");
        var destinationRoot = Path.Combine(crossDeviceRoot, $"deezspotag-cross-device-destination-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(sourceRoot, "track.bin");
        var destinationPath = Path.Combine(destinationRoot, "track.bin");
        var content = new byte[128 * 1024];
        new Random(173).NextBytes(content);
        File.WriteAllBytes(sourcePath, content);

        try
        {
            FileMoveFallbackHelper.MoveWithFallback(sourcePath, destinationPath);

            Assert.False(File.Exists(sourcePath));
            Assert.Equal(content, File.ReadAllBytes(destinationPath));
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, recursive: true);
            }
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void MoveFiles_SkipsUntrackedAudioFiles()
    {
        var method = GetPrivateStaticMethod("MoveFiles");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-download-move-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "staging");
        var destinationRoot = Path.Combine(tempRoot, "library");
        var albumDir = Path.Combine(stagingRoot, "Deobi", "All Over You");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(destinationRoot);

        var trackedMp3 = Path.Combine(albumDir, "Deobi - All Over You.mp3");
        var stalePreviewFlac = Path.Combine(albumDir, "Deobi - All Over You.flac");
        var cover = Path.Combine(albumDir, "cover.jpg");
        File.WriteAllText(trackedMp3, "full");
        File.WriteAllText(stalePreviewFlac, "preview");
        File.WriteAllText(cover, "cover");

        var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [trackedMp3] = trackedMp3,
            [stalePreviewFlac] = stalePreviewFlac,
            [cover] = cover
        };
        var trackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            trackedMp3
        };

        try
        {
            var outcome = method.Invoke(
                null,
                new object[] { sourcePaths, trackedPaths, stagingRoot, destinationRoot, "y", CancellationToken.None })!;
            var moved = (IReadOnlyDictionary<string, string>)outcome.GetType().GetProperty("Moved")!.GetValue(outcome)!;

            Assert.True(moved.ContainsKey(trackedMp3));
            Assert.True(moved.ContainsKey(cover));
            Assert.False(moved.ContainsKey(stalePreviewFlac));
            Assert.False(File.Exists(trackedMp3));
            Assert.True(File.Exists(stalePreviewFlac));
            Assert.True(File.Exists(Path.Combine(destinationRoot, "Deobi", "All Over You", "Deobi - All Over You.mp3")));
            Assert.False(File.Exists(Path.Combine(destinationRoot, "Deobi", "All Over You", "Deobi - All Over You.flac")));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private static MethodInfo GetPrivateStaticMethod(string methodName)
    {
        return typeof(DownloadMoveService).GetMethod(
                   methodName,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{methodName} was not found.");
    }
}
