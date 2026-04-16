using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagDownloadMoveServicePayloadPathTests
{
    [Fact]
    public void TryGetPropertyIgnoreCase_ReturnsFalse_WhenPropertyIsMissing()
    {
        using var document = JsonDocument.Parse("""{"FilePath":"/tmp/demo.flac"}""");
        var method = GetPrivateStaticMethod("TryGetPropertyIgnoreCase");
        var args = new object?[] { document.RootElement, "albumPath", null };

        var result = (bool)method.Invoke(null, args)!;

        Assert.False(result);
    }

    [Fact]
    public void CollectPayloadPaths_DoesNotThrow_WhenOptionalRootPropertiesAreMissing()
    {
        const string rootPath = "/home/edzoh/Music/Test/Downs";
        const string sourcePath = "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a";
        using var document = JsonDocument.Parse(
            """
            {
              "FilePath": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
              "Files": [
                {
                  "path": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
                  "albumPath": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album",
                  "artistPath": "/home/edzoh/Music/Test/Downs/Atmos/Artist"
                }
              ],
              "FinalDestinations": {
                "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a"
              }
            }
            """);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var method = GetPrivateStaticMethod("CollectPayloadPaths");
        var ex = Record.Exception(() =>
            method.Invoke(null, new object[] { rootPath, document.RootElement, files, roots }));

        Assert.Null(ex);
        Assert.Contains(sourcePath, files);
        Assert.Contains("/home/edzoh/Music/Test/Downs/Atmos/Artist/Album", roots);
    }

    [Fact]
    public void ResolveRoutingFolderId_MatchesYearRule_BeforeDefault()
    {
        var metadata = CreateRoutingMetadata(
            artist: "Artist",
            title: "Title",
            album: "Album",
            genres: new List<string> { "Pop" },
            explicitValue: false,
            releaseDate: "2024-07-01");
        var rules = new List<PlaylistTrackRoutingRule>
        {
            new("year", "gte", "2020", 42, 0)
        };

        var result = InvokeResolveRoutingFolderId(metadata, rules, defaultFolderId: 10);

        Assert.Equal(42, result);
    }

    [Fact]
    public void ResolveRoutingFolderId_MatchesGenreRule_CaseInsensitive()
    {
        var metadata = CreateRoutingMetadata(
            artist: "Artist",
            title: "Title",
            album: "Album",
            genres: new List<string> { "Melodic Progressive House" },
            explicitValue: null,
            releaseDate: "2019-05-11");
        var rules = new List<PlaylistTrackRoutingRule>
        {
            new("genre", "contains", "progressive", 77, 0)
        };

        var result = InvokeResolveRoutingFolderId(metadata, rules, defaultFolderId: 10);

        Assert.Equal(77, result);
    }

    [Fact]
    public void TryRewritePayloadDestinationFolderId_RewritesDestinationFolderId()
    {
        const string payload = """{"DestinationFolderId":12,"Title":"Demo"}""";
        var method = GetPrivateStaticMethod("TryRewritePayloadDestinationFolderId");
        var args = new object?[] { payload, 35L, null };

        var rewritten = (bool)method.Invoke(null, args)!;

        Assert.True(rewritten);
        var updatedJson = Assert.IsType<string>(args[2]);
        using var document = JsonDocument.Parse(updatedJson);
        Assert.Equal(35, document.RootElement.GetProperty("DestinationFolderId").GetInt64());
    }

    [Fact]
    public void ShouldUseCopyFallback_ReturnsFalse_ForSameVolumeLocalPaths()
    {
        var method = GetPrivateStaticMethod("ShouldUseCopyFallback");
        var root = Path.Combine(Path.GetTempPath(), "deezspotag-tests");
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
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-move-test-{Guid.NewGuid():N}");
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
    public void MoveFileUnderRoot_MultiQualityAtmos_StripsBucketAndMoves()
    {
        var method = GetPrivateStaticMethod("MoveFileUnderRoot");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-autotag-mq-atmos-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "Downs");
        var destinationRoot = Path.Combine(tempRoot, "Library");
        var sourcePath = Path.Combine(stagingRoot, "Atmos", "Artist", "Album", "01 - Demo.lrc");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(destinationRoot);
        File.WriteAllText(sourcePath, "lyrics");

        try
        {
            var moved = (string?)method.Invoke(null, new object?[]
            {
                stagingRoot,
                sourcePath,
                destinationRoot,
                new DeezSpoTagSettings { OverwriteFile = "y" },
                "atmos"
            });

            var expectedDestination = Path.Combine(destinationRoot, "Artist", "Album", "01 - Demo.lrc");
            Assert.Equal(
                DownloadPathResolver.NormalizeDisplayPath(expectedDestination),
                DownloadPathResolver.NormalizeDisplayPath(moved ?? string.Empty));
            Assert.True(File.Exists(expectedDestination));
            Assert.False(File.Exists(sourcePath));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void MoveFileUnderRoot_SingleQuality_PreservesRelativePath()
    {
        var method = GetPrivateStaticMethod("MoveFileUnderRoot");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-autotag-sq-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "Downs");
        var destinationRoot = Path.Combine(tempRoot, "Library");
        var sourcePath = Path.Combine(stagingRoot, "Artist", "Album", "01 - Demo.lrc");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(destinationRoot);
        File.WriteAllText(sourcePath, "lyrics");

        try
        {
            var moved = (string?)method.Invoke(null, new object?[]
            {
                stagingRoot,
                sourcePath,
                destinationRoot,
                new DeezSpoTagSettings { OverwriteFile = "y" },
                null
            });

            var expectedDestination = Path.Combine(destinationRoot, "Artist", "Album", "01 - Demo.lrc");
            Assert.Equal(
                DownloadPathResolver.NormalizeDisplayPath(expectedDestination),
                DownloadPathResolver.NormalizeDisplayPath(moved ?? string.Empty));
            Assert.True(File.Exists(expectedDestination));
            Assert.False(File.Exists(sourcePath));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void MoveFileUnderRoot_LegacyStereoPath_StripsKnownBucketWithoutPayloadBucket()
    {
        var method = GetPrivateStaticMethod("MoveFileUnderRoot");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-autotag-mq-stereo-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "Downs");
        var destinationRoot = Path.Combine(tempRoot, "Library");
        var sourcePath = Path.Combine(stagingRoot, "Stereo", "Artist", "Album", "01 - Demo.lrc");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(destinationRoot);
        File.WriteAllText(sourcePath, "lyrics");

        try
        {
            var moved = (string?)method.Invoke(null, new object?[]
            {
                stagingRoot,
                sourcePath,
                destinationRoot,
                new DeezSpoTagSettings { OverwriteFile = "y" },
                null
            });

            var expectedDestination = Path.Combine(destinationRoot, "Artist", "Album", "01 - Demo.lrc");
            Assert.Equal(
                DownloadPathResolver.NormalizeDisplayPath(expectedDestination),
                DownloadPathResolver.NormalizeDisplayPath(moved ?? string.Empty));
            Assert.True(File.Exists(expectedDestination));
            Assert.False(File.Exists(sourcePath));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static MethodInfo GetPrivateStaticMethod(string methodName)
    {
        return typeof(AutoTagDownloadMoveService).GetMethod(
                   methodName,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{methodName} was not found.");
    }

    private static object CreateRoutingMetadata(
        string artist,
        string title,
        string album,
        IReadOnlyList<string> genres,
        bool? explicitValue,
        string? releaseDate)
    {
        var metadataType = typeof(AutoTagDownloadMoveService).GetNestedType(
                               "RoutingMatchMetadata",
                               BindingFlags.NonPublic)
                           ?? throw new InvalidOperationException("RoutingMatchMetadata was not found.");
        return Activator.CreateInstance(metadataType, artist, title, album, genres, explicitValue, releaseDate)
               ?? throw new InvalidOperationException("RoutingMatchMetadata could not be created.");
    }

    private static long? InvokeResolveRoutingFolderId(
        object metadata,
        IReadOnlyList<PlaylistTrackRoutingRule> rules,
        long? defaultFolderId)
    {
        var method = GetPrivateStaticMethod("ResolveRoutingFolderId");
        return (long?)method.Invoke(null, new object?[] { metadata, rules, defaultFolderId });
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
