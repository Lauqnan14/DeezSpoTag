using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagDownloadMoveServicePayloadPathTests
{
    [Theory]
    [InlineData("completed", "running", true)]
    [InlineData("not_required", "pending", true)]
    [InlineData("completed", "moved", false)]
    [InlineData("not_required", "not_required", false)]
    public void NeedsEnrichmentPipelineWork_RecoversIncompleteFinalization(
        string enrichmentStatus,
        string finalizationStatus,
        bool expected)
    {
        var method = typeof(DownloadOrchestrationService).GetMethod(
            "NeedsEnrichmentPipelineWork",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NeedsEnrichmentPipelineWork was not found.");
        var item = CreateQueueItem(enrichmentStatus, finalizationStatus);

        Assert.Equal(expected, (bool)method.Invoke(null, [item])!);
    }

    [Fact]
    public void ResolveRecordedSourceAudioFilesUnderRoot_KeepsMissingAudioAndExcludesArtwork()
    {
        var method = typeof(DownloadOrchestrationService).GetMethod(
            "ResolveRecordedSourceAudioFilesUnderRoot",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveRecordedSourceAudioFilesUnderRoot was not found.");
        const string rootPath = "/downloads";
        const string audioPath = "/downloads/Atmos/Artist/Album/Track.m4a";
        const string artworkPath = "/downloads/Atmos/Artist/Album/cover - animated_artwork.mp4";
        var payload = JsonSerializer.Serialize(new
        {
            filePath = audioPath,
            files = new object[]
            {
                new { path = audioPath },
                new { path = artworkPath, type = "artwork" }
            }
        });
        var item = CreateQueueItem("completed", "running") with { PayloadJson = payload };

        var resolved = Assert.IsType<List<string>>(method.Invoke(null, new object[]
        {
            new[] { item },
            rootPath
        }));

        Assert.Single(resolved);
        Assert.Equal(audioPath, resolved[0]);
    }

    [Fact]
    public void TryApplyFinalDestinationTransitions_RejectsIdentityAndMissingDestinations()
    {
        var method = GetPrivateStaticMethod("TryApplyFinalDestinationTransitions");
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-finalize-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(stagingRoot, "Artist", "track.flac");
        var missingDestination = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.flac");
        var payload = JsonSerializer.Serialize(new { filePath = sourcePath, files = new[] { new { path = sourcePath } } });

        var identityArgs = new object?[]
        {
            payload, null, stagingRoot,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sourcePath] = sourcePath },
            null, null
        };
        var missingArgs = new object?[]
        {
            payload, null, stagingRoot,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sourcePath] = missingDestination },
            null, null
        };

        Assert.False((bool)method.Invoke(null, identityArgs)!);
        Assert.False((bool)method.Invoke(null, missingArgs)!);
    }

    [Fact]
    public void TryApplyFinalDestinationTransitions_AcceptsExistingDestinationOutsideStagingRoot()
    {
        var method = GetPrivateStaticMethod("TryApplyFinalDestinationTransitions");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-finalize-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "Downs");
        var sourcePath = Path.Combine(stagingRoot, "Artist", "track.flac");
        var destinationPath = Path.Combine(tempRoot, "Library", "Artist", "track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllText(destinationPath, "audio");

        try
        {
            var payload = JsonSerializer.Serialize(new { filePath = sourcePath, files = new[] { new { path = sourcePath } } });
            var args = new object?[]
            {
                payload, null, stagingRoot,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sourcePath] = destinationPath },
                null, null
            };

            Assert.True((bool)method.Invoke(null, args)!);
            var updatedPayload = Assert.IsType<string>(args[4]);
            Assert.Contains(destinationPath, updatedPayload, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"filePath\":{JsonSerializer.Serialize(sourcePath)}", updatedPayload, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void HasVerifiedFinalDestination_RejectsSelfMappingAndRequiresExistingDestinationOutsideStagingRoot()
    {
        var method = typeof(DownloadOrchestrationService).GetMethod(
            "HasVerifiedFinalDestination",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HasVerifiedFinalDestination was not found.");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-orchestration-final-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "downloads");
        var sourcePath = Path.Combine(stagingRoot, "Artist", "Track.flac");
        var stagingDestination = Path.Combine(stagingRoot, "Other", "Track.flac");
        var libraryDestination = Path.Combine(tempRoot, "library", "Artist", "Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryDestination)!);
        File.WriteAllText(libraryDestination, "audio");

        try
        {
            var item = CreateQueueItem("completed", "moved");

            Assert.False((bool)method.Invoke(null, [
                item with { FinalDestinationsJson = JsonSerializer.Serialize(new Dictionary<string, string> { [sourcePath] = sourcePath }) },
                stagingRoot
            ])!);
            Assert.False((bool)method.Invoke(null, [
                item with { FinalDestinationsJson = JsonSerializer.Serialize(new Dictionary<string, string> { [sourcePath] = stagingDestination }) },
                stagingRoot
            ])!);
            Assert.True((bool)method.Invoke(null, [
                item with { FinalDestinationsJson = JsonSerializer.Serialize(new Dictionary<string, string> { [sourcePath] = libraryDestination }) },
                stagingRoot
            ])!);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void TryApplyFinalDestinationTransitions_RewritesPascalCaseQueuePayload()
    {
        var method = GetPrivateStaticMethod("TryApplyFinalDestinationTransitions");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-finalize-case-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "Downs");
        var sourcePath = Path.Combine(stagingRoot, "Artist", "Track.flac");
        var destinationPath = Path.Combine(tempRoot, "Library", "Artist", "Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllText(destinationPath, "audio");

        try
        {
            var payload = JsonSerializer.Serialize(new { FilePath = sourcePath, Files = new[] { new { Path = sourcePath } } });
            var args = new object?[]
            {
                payload,
                null,
                stagingRoot,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sourcePath] = destinationPath },
                null,
                null
            };

            Assert.True((bool)method.Invoke(null, args)!);
            var updatedPayload = Assert.IsType<string>(args[4]);
            using var updatedDocument = JsonDocument.Parse(updatedPayload);
            Assert.Equal(destinationPath, updatedDocument.RootElement.GetProperty("FilePath").GetString());
            Assert.Equal(destinationPath, updatedDocument.RootElement.GetProperty("Files")[0].GetProperty("Path").GetString());
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

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
        const string rootPath = "/home/user/Music/Test/Downs";
        const string sourcePath = "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a";
        using var document = JsonDocument.Parse(
            """
            {
              "FilePath": "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
              "Files": [
                {
                  "path": "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
                  "albumPath": "/home/user/Music/Test/Downs/Atmos/Artist/Album",
                  "artistPath": "/home/user/Music/Test/Downs/Atmos/Artist"
                }
              ],
              "FinalDestinations": {
                "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a": "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a"
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
        Assert.Contains("/home/user/Music/Test/Downs/Atmos/Artist/Album", roots);
        Assert.DoesNotContain("/home/user/Music/Test/Downs/Atmos/Artist", roots);
    }

    [Fact]
    public void CollectPayloadPaths_AddsArtistArtworkFilesWithoutAddingArtistRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-artist-art-map-{Guid.NewGuid():N}");
        var rootPath = Path.Combine(tempRoot, "Downs");
        var artistPath = Path.Combine(rootPath, "Artist");
        var albumPath = Path.Combine(artistPath, "Album");
        var sourcePath = Path.Combine(albumPath, "01 - Demo.flac");
        var artistArtworkPath = Path.Combine(artistPath, "Artist.jpg");
        Directory.CreateDirectory(albumPath);
        File.WriteAllText(sourcePath, "audio");
        File.WriteAllText(artistArtworkPath, "artist-art");

        try
        {
            using var document = JsonDocument.Parse(
                $$"""
                  {
                    "filePath": {{JsonSerializer.Serialize(sourcePath)}},
                    "files": [
                      {
                        "path": {{JsonSerializer.Serialize(sourcePath)}},
                        "albumPath": {{JsonSerializer.Serialize(albumPath)}},
                        "artistPath": {{JsonSerializer.Serialize(artistPath)}}
                      }
                    ]
                  }
                  """);

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var method = GetPrivateStaticMethod("CollectPayloadPaths");

            method.Invoke(null, new object[] { rootPath, document.RootElement, files, roots });

            Assert.Contains(sourcePath, files);
            Assert.Contains(DownloadPathResolver.NormalizeDisplayPath(artistArtworkPath), files);
            Assert.Contains(albumPath, roots);
            Assert.DoesNotContain(artistPath, roots);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void CollectPayloadPaths_AddsConfiguredAlbumArtworkFromGeneratedFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-album-art-map-{Guid.NewGuid():N}");
        var rootPath = Path.Combine(tempRoot, "Downs");
        var albumPath = Path.Combine(rootPath, "Artist", "Album");
        var sourcePath = Path.Combine(albumPath, "Track.flac");
        var artworkPath = Path.Combine(albumPath, "Artist - Album.png");
        Directory.CreateDirectory(albumPath);
        File.WriteAllText(sourcePath, "audio");
        File.WriteAllText(artworkPath, "artwork");

        try
        {
            using var document = JsonDocument.Parse(
                $$"""
                  {
                    "filePath": {{JsonSerializer.Serialize(sourcePath)}},
                    "files": [
                      {
                        "path": {{JsonSerializer.Serialize(sourcePath)}},
                        "albumPath": {{JsonSerializer.Serialize(albumPath)}}
                      },
                      {
                        "path": {{JsonSerializer.Serialize(artworkPath)}},
                        "albumPath": {{JsonSerializer.Serialize(albumPath)}},
                        "type": "artwork"
                      }
                    ]
                  }
                  """);

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            GetPrivateStaticMethod("CollectPayloadPaths").Invoke(
                null,
                new object[] { rootPath, document.RootElement, files, roots });

            Assert.Contains(DownloadPathResolver.NormalizeDisplayPath(artworkPath), files);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void CollectPayloadPaths_AddsUnrecordedAlbumSidecarsAndOwnedTemporaryFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-album-sidecars-{Guid.NewGuid():N}");
        var rootPath = Path.Combine(tempRoot, "Downs");
        var albumPath = Path.Combine(rootPath, "Artist", "Album");
        var sourcePath = Path.Combine(albumPath, "Track.flac");
        var coverPath = Path.Combine(albumPath, "cover.jpg");
        var temporaryPath = Path.Combine(albumPath, "Track.candidate-1.part.flac.m4a.tmp");
        Directory.CreateDirectory(albumPath);
        File.WriteAllText(coverPath, "cover");
        File.WriteAllText(temporaryPath, "partial");

        try
        {
            using var document = JsonDocument.Parse(
                $$"""
                  {
                    "FilePath": {{JsonSerializer.Serialize(sourcePath)}},
                    "Files": [
                      {
                        "path": {{JsonSerializer.Serialize(sourcePath)}},
                        "albumPath": {{JsonSerializer.Serialize(albumPath)}}
                      }
                    ]
                  }
                  """);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            GetPrivateStaticMethod("CollectPayloadPaths").Invoke(
                null,
                new object[] { rootPath, document.RootElement, files, roots });

            Assert.Contains(DownloadPathResolver.NormalizeDisplayPath(coverPath), files);
            Assert.Contains(DownloadPathResolver.NormalizeDisplayPath(temporaryPath), files);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
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

    [Fact]
    public void MoveDirectoryTreeUnderRoot_DeletesTemporaryArtifactsInsteadOfMovingThem()
    {
        var method = GetPrivateStaticMethod("MoveDirectoryTreeUnderRoot");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-temp-finalize-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "Downs");
        var albumRoot = Path.Combine(stagingRoot, "Artist", "Album");
        var destinationRoot = Path.Combine(tempRoot, "Library");
        var temporaryPath = Path.Combine(albumRoot, "Track.candidate-1.part.flac.m4a.tmp");
        Directory.CreateDirectory(albumRoot);
        Directory.CreateDirectory(destinationRoot);
        File.WriteAllText(temporaryPath, "partial");

        try
        {
            method.Invoke(null, new object?[]
            {
                stagingRoot,
                albumRoot,
                destinationRoot,
                new DeezSpoTagSettings { OverwriteFile = "y" },
                null
            });

            Assert.False(File.Exists(temporaryPath));
            Assert.False(File.Exists(Path.Combine(destinationRoot, "Artist", "Album", Path.GetFileName(temporaryPath))));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ResolveAlreadyMovedPathUnderRoot_FindsDestinationAfterInterruptedMove()
    {
        var method = GetPrivateStaticMethod("ResolveAlreadyMovedPathUnderRoot");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-resume-move-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "Downs");
        var destinationRoot = Path.Combine(tempRoot, "Library");
        var sourcePath = Path.Combine(stagingRoot, "Artist", "Album", "Track.flac");
        var destinationPath = Path.Combine(destinationRoot, "Artist", "Album", "Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllText(destinationPath, "audio");

        try
        {
            var resolved = (string?)method.Invoke(null, new object?[]
            {
                stagingRoot,
                sourcePath,
                destinationRoot,
                null
            });

            Assert.Equal(
                DownloadPathResolver.NormalizeDisplayPath(destinationPath),
                DownloadPathResolver.NormalizeDisplayPath(resolved ?? string.Empty));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ResolveResidualSuccessRoot_ReturnsNull_WhenMoveTaggedPathIsNotConfigured()
    {
        var method = GetPrivateStaticMethod("ResolveResidualSuccessRoot");
        var context = CreateResidualMoveContext(new AutoTagOrganizerOptions());

        var result = (string?)method.Invoke(null, new[] { context });

        Assert.Null(result);
    }

    [Fact]
    public void ResolveResidualSuccessRoot_UsesConfiguredMoveTaggedPath()
    {
        var method = GetPrivateStaticMethod("ResolveResidualSuccessRoot");
        var context = CreateResidualMoveContext(new AutoTagOrganizerOptions
        {
            MoveTaggedPath = "/music/success"
        });

        var result = (string?)method.Invoke(null, new[] { context });

        Assert.Equal("/music/success", result);
    }

    private static MethodInfo GetPrivateStaticMethod(string methodName)
    {
        return typeof(AutoTagDownloadMoveService).GetMethod(
                   methodName,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{methodName} was not found.");
    }

    private static DownloadQueueItem CreateQueueItem(string enrichmentStatus, string finalizationStatus)
        => new(
            Id: 1,
            QueueUuid: "queue-finalization-recovery",
            Engine: "qobuz",
            ArtistName: "Artist",
            TrackTitle: "Track",
            Isrc: null,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: 180000,
            DestinationFolderId: 7,
            QualityRank: 1,
            QueueOrder: 1,
            ContentType: "stereo",
            FinalizationStatus: finalizationStatus,
            EnrichmentStatus: enrichmentStatus,
            Status: "completed",
            PayloadJson: "{}",
            Progress: 100,
            Downloaded: 1,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

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

    private static object CreateResidualMoveContext(AutoTagOrganizerOptions options)
    {
        var conversionPlan = CreateConversionPlan();
        var contextType = typeof(AutoTagDownloadMoveService).GetNestedType(
                              "ResidualMoveContext",
                              BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("ResidualMoveContext was not found.");
        return Activator.CreateInstance(
                   contextType,
                   "/downloads",
                   options,
                   "y",
                   conversionPlan,
                   Array.Empty<string>(),
                   Array.Empty<string>())
               ?? throw new InvalidOperationException("ResidualMoveContext could not be created.");
    }

    private static object CreateConversionPlan()
    {
        var conversionPlanType = typeof(AutoTagDownloadMoveService).GetNestedType(
                                     "ConversionPlan",
                                     BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException("ConversionPlan was not found.");
        return Activator.CreateInstance(
                   conversionPlanType,
                   false,
                   null,
                   null,
                   false,
                   false,
                   string.Empty,
                   false,
                   false)
               ?? throw new InvalidOperationException("ConversionPlan could not be created.");
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
