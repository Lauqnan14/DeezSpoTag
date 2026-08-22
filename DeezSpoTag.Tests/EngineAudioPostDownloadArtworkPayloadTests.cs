using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineAudioPostDownloadArtworkPayloadTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(
        Path.GetTempPath(),
        $"deezspotag-artwork-payload-{Guid.NewGuid():N}");

    [Fact]
    public void UpdateAudioPayloadFiles_PreservesGeneratedArtworkWithConfiguredFilename()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var outputPath = Path.Join(albumPath, "Track.flac");
        var configuredArtworkPath = Path.Join(albumPath, "Artist - Album.png");
        Directory.CreateDirectory(albumPath);
        File.WriteAllText(outputPath, "audio");
        File.WriteAllText(configuredArtworkPath, "artwork");
        var payload = new QobuzQueueItem
        {
            Files =
            [
                new Dictionary<string, object>
                {
                    ["path"] = configuredArtworkPath,
                    ["albumPath"] = albumPath,
                    ["artistPath"] = Path.GetDirectoryName(albumPath)!,
                    ["type"] = "artwork"
                }
            ]
        };

        EngineAudioPostDownloadHelper.UpdateAudioPayloadFiles(
            payload,
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            },
            outputPath);

        Assert.Contains(payload.Files, file =>
            string.Equals(file["path"].ToString(), outputPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(payload.Files, file =>
            string.Equals(file["path"].ToString(), configuredArtworkPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file["type"].ToString(), "artwork", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdateAudioPayloadFiles_DoesNotClaimUntrackedAlbumImage()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var outputPath = Path.Join(albumPath, "Track.flac");
        var unrelatedImagePath = Path.Join(albumPath, "untracked.jpg");
        Directory.CreateDirectory(albumPath);
        File.WriteAllText(outputPath, "audio");
        File.WriteAllText(unrelatedImagePath, "image");
        var payload = new QobuzQueueItem();

        EngineAudioPostDownloadHelper.UpdateAudioPayloadFiles(
            payload,
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            },
            outputPath);

        Assert.DoesNotContain(payload.Files, file =>
            string.Equals(file["path"].ToString(), unrelatedImagePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdateAudioPayloadFiles_UsesRecordedLyricsArtifactPathInsteadOfGuessingFromAudioName()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var outputPath = Path.Join(albumPath, "01 - Final Track.flac");
        var prefetchedLyricsPath = Path.Join(_tempRoot, "staging", "provider-name.lrc");
        var payload = new QobuzQueueItem
        {
            LyricsArtifacts = new LyricsArtifactState
            {
                FilesByFormat = new Dictionary<string, string> { ["lrc"] = prefetchedLyricsPath }
            }
        };

        EngineAudioPostDownloadHelper.UpdateAudioPayloadFiles(
            payload,
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            },
            outputPath);

        Assert.Contains(payload.Files, file =>
            string.Equals(file["path"].ToString(), prefetchedLyricsPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddGeneratedSidecars_MarksConfiguredArtworkForFinalization()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var configuredArtworkPath = Path.Join(albumPath, "Artist - Album.png");
        var files = new List<Dictionary<string, object>>();
        var method = GetPrivateStaticMethod("AddGeneratedSidecars");

        method.Invoke(null,
        [
            files,
            new[] { configuredArtworkPath },
            Array.Empty<string>(),
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            }
        ]);

        var artwork = Assert.Single(files);
        Assert.Equal(configuredArtworkPath, artwork["path"]);
        Assert.Equal("artwork", artwork["type"]);
    }

    [Fact]
    public void CleanupTemporaryEmbeddedArtwork_RemovesOnlyItsQueueDirectory()
    {
        var buildDirectory = GetPrivateStaticMethod("BuildTemporaryEmbeddedArtworkDirectory");
        var cleanup = GetPrivateStaticMethod("CleanupTemporaryEmbeddedArtwork");
        var temporaryDirectory = Assert.IsType<string>(buildDirectory.Invoke(null, [Guid.NewGuid().ToString("N")]));
        var temporaryArtwork = Path.Join(temporaryDirectory, "cover.jpg");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(temporaryArtwork, "artwork");

        cleanup.Invoke(null, [temporaryArtwork]);

        Assert.False(Directory.Exists(temporaryDirectory));
    }

    private static MethodInfo GetPrivateStaticMethod(string name)
    {
        return typeof(EngineAudioPostDownloadHelper).GetMethod(
                   name,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{name} was not found.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
