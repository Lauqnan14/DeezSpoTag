using System;
using System.IO;
using System.Reflection;
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

    private static MethodInfo GetPrivateStaticMethod(string methodName)
    {
        return typeof(DownloadMoveService).GetMethod(
                   methodName,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{methodName} was not found.");
    }
}
