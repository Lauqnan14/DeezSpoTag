using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Services.Download.Queue;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadStagingFileOwnershipTests
{
    [Fact]
    public void ResolveOwnedStagingAudioFiles_DoesNotSwallowSiblingAlbumFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-owned-{Guid.NewGuid():N}");
        var album = Path.Combine(tempRoot, "Artist", "Album");
        var ownedPath = Path.Combine(album, "New Track.flac");
        var leftoverPath = Path.Combine(album, "Old Track.flac");
        Directory.CreateDirectory(album);
        File.WriteAllText(ownedPath, "new");
        File.WriteAllText(leftoverPath, "old");

        try
        {
            var item = CreateItem(
                payload: JsonSerializer.Serialize(new
                {
                    filePath = ownedPath,
                    files = new[]
                    {
                        new { path = ownedPath, albumPath = album, artistPath = Path.Combine(tempRoot, "Artist") }
                    }
                }));

            var owned = DownloadStagingFileOwnership.ResolveOwnedStagingAudioFiles(item, tempRoot);

            Assert.Equal([ownedPath], owned);
            Assert.DoesNotContain(leftoverPath, owned);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public void ResolveOwnedStagingAudioFiles_UsesFinalDestinationSelfMappingWhenPayloadIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-owned-final-{Guid.NewGuid():N}");
        var stagingPath = Path.Combine(tempRoot, "Artist", "Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        File.WriteAllText(stagingPath, "audio");

        try
        {
            var item = CreateItem(payload: null) with
            {
                FinalDestinationsJson = JsonSerializer.Serialize(
                    new Dictionary<string, string> { [stagingPath] = stagingPath })
            };

            var owned = DownloadStagingFileOwnership.ResolveOwnedStagingAudioFiles(item, tempRoot);

            Assert.Equal([stagingPath], owned);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public void ResolveOwnedStagingAudioFiles_ExcludesArtworkTypedFiles()
    {
        const string rootPath = "/downloads";
        const string audioPath = "/downloads/Artist/Album/Track.m4a";
        const string artworkPath = "/downloads/Artist/Album/cover.jpg";
        var item = CreateItem(JsonSerializer.Serialize(new
        {
            filePath = audioPath,
            files = new object[]
            {
                new { path = audioPath },
                new { path = artworkPath, type = "artwork" }
            }
        }));

        var recorded = DownloadStagingFileOwnership.ResolveOwnedStagingAudioFiles(
            item,
            rootPath,
            requireExisting: false);

        Assert.Equal([Path.GetFullPath(audioPath)], recorded);
    }

    [Fact]
    public void HasAcquiredAudio_ReadsPayloadFlag()
    {
        var withFlag = CreateItem("""{"AudioAcquired":true,"filePath":"/tmp/track.flac"}""");
        var withoutFlag = CreateItem("""{"filePath":"/tmp/track.flac"}""");

        Assert.True(DownloadStagingFileOwnership.HasAcquiredAudio(withFlag));
        Assert.False(DownloadStagingFileOwnership.HasAcquiredAudio(withoutFlag));
    }

    [Fact]
    public void TwoDestinationsInSameAlbumFolder_KeepDisjointOwnedFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-owned-dests-{Guid.NewGuid():N}");
        var album = Path.Combine(tempRoot, "Artist", "Album");
        var destOnePath = Path.Combine(album, "Stereo Track.flac");
        var destTwoPath = Path.Combine(album, "Atmos Track.m4a");
        Directory.CreateDirectory(album);
        File.WriteAllText(destOnePath, "stereo");
        File.WriteAllText(destTwoPath, "atmos");

        try
        {
            var destOne = CreateItem(JsonSerializer.Serialize(new
            {
                filePath = destOnePath,
                files = new[] { new { path = destOnePath, albumPath = album } }
            })) with { QueueUuid = "dest-one", DestinationFolderId = 1 };
            var destTwo = CreateItem(JsonSerializer.Serialize(new
            {
                filePath = destTwoPath,
                files = new[] { new { path = destTwoPath, albumPath = album } }
            })) with { QueueUuid = "dest-two", DestinationFolderId = 2 };

            var ownedOne = DownloadStagingFileOwnership.ResolveOwnedStagingAudioFiles(destOne, tempRoot);
            var ownedTwo = DownloadStagingFileOwnership.ResolveOwnedStagingAudioFiles(destTwo, tempRoot);
            var groups = new[] { destOne, destTwo }
                .GroupBy(item => item.DestinationFolderId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .SelectMany(item => DownloadStagingFileOwnership.ResolveOwnedStagingAudioFiles(item, tempRoot))
                        .ToList());

            Assert.Equal([destOnePath], ownedOne);
            Assert.Equal([destTwoPath], ownedTwo);
            Assert.Equal(2, groups.Count);
            Assert.Equal([destOnePath], groups[1]);
            Assert.Equal([destTwoPath], groups[2]);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public void ReadDestinationFolderId_FallsBackToPayload()
    {
        var item = CreateItem("""{"DestinationFolderId":44}""") with { DestinationFolderId = null };

        Assert.Equal(44, DownloadStagingFileOwnership.ReadDestinationFolderId(item));
    }

    private static DownloadQueueItem CreateItem(string? payload)
        => new(
            Id: 1,
            QueueUuid: "owned-item",
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
            FinalizationStatus: "pending",
            EnrichmentStatus: "pending",
            Status: "completed",
            PayloadJson: payload,
            Progress: 100,
            Downloaded: 1,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static void TryDelete(string path)
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
            // Best effort.
        }
    }
}
