using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EmbeddedArtworkPreferenceTest
{
    [Theory]
    [InlineData("tidal", "")]
    [InlineData("apple", "")]
    [InlineData("amazon", "")]
    [InlineData("tidal", "2022-04-22")]
    [InlineData("apple", "2022-04-22")]
    [InlineData("amazon", "2022-04-22")]
    public async Task AtmosMp4_TaggingHandlesUnavailableDatesWithoutInventingThem(string source, string date)
    {
        var root = Path.Join(Path.GetTempPath(), $"atmos-tag-dates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Join(root, "track.m4a");
            await RunProcessAsync("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "anullsrc=r=48000:cl=5.1", "-t", "0.2", "-c:a", "eac3", "-f", "mp4", path);
            var track = new Track
            {
                Source = source, SourceId = "123", Title = "Atmos date regression",
                MainArtist = new Artist("Artist"), Album = new Album("Album"),
                Date = CustomDate.FromString(date), DateString = string.IsNullOrEmpty(date) ? "0000-00-00" : date
            };
            track.Album.Date = track.Date;
            track.Album.DateString = track.DateString;
            var settings = new DeezSpoTagSettings { Tags = new TagSettings { Cover = false, ReleaseDate = true } };
            var tagger = new AudioTagger(NullLogger<AudioTagger>.Instance,
                new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance));
            await tagger.TagTrackAsync(path, track, settings);
            var result = new ATL.Track(path);
            Assert.Equal(track.Title, result.Title);
            Assert.Equal("Artist", result.Artist);
            Assert.Equal("Album", result.Album);
            Assert.Equal(string.IsNullOrEmpty(date) ? 0 : 2022, result.Year);
            Assert.DoesNotContain(result.AdditionalFields.Values, value => value.Contains("0000-00-00", StringComparison.Ordinal));
            Assert.DoesNotContain(result.AdditionalFields.Values, value => value.Contains("0001-01-01", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RequiresEmbeddedAlbumArtwork_ObeysCoverTagPreference()
    {
        var payload = new TidalQueueItem { CollectionType = "album" };

        Assert.True(EngineAudioPostDownloadHelper.RequiresEmbeddedAlbumArtwork(
            payload,
            Settings(coverEnabled: true)));
        Assert.False(EngineAudioPostDownloadHelper.RequiresEmbeddedAlbumArtwork(
            payload,
            Settings(coverEnabled: false)));
    }

    [Fact]
    public void RequiresEmbeddedAlbumArtwork_ObeysPlaylistCoverPreference()
    {
        var payload = new TidalQueueItem { CollectionType = "playlist" };

        Assert.False(EngineAudioPostDownloadHelper.RequiresEmbeddedAlbumArtwork(
            payload,
            Settings(coverEnabled: true, albumCoverForPlaylist: false)));
        Assert.True(EngineAudioPostDownloadHelper.RequiresEmbeddedAlbumArtwork(
            payload,
            Settings(coverEnabled: true, albumCoverForPlaylist: true)));
    }

    [Fact]
    public void PostDownloadFlow_WaitsForRequiredArtworkBeforeTaggingAndThenVerifiesEmbedding()
    {
        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "EngineAudioPostDownloadHelper.cs"));
        var methodStart = source.IndexOf("public static async Task<string> ApplyPostDownloadSettingsAsync(", StringComparison.Ordinal);
        var awaitArtwork = source.IndexOf("AwaitRequiredArtworkBeforeTaggingAsync(", methodStart, StringComparison.Ordinal);
        var tagAudio = source.IndexOf("TagAudioWithResolvedCoverAsync(", methodStart, StringComparison.Ordinal);
        var verifyEmbedding = source.IndexOf("EnsureEmbeddedArtworkPreferenceSatisfied(request);", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(awaitArtwork > methodStart);
        Assert.True(tagAudio > awaitArtwork);
        Assert.True(verifyEmbedding > tagAudio);
    }

    [Fact]
    public void DeezerDirectDownload_AlsoObeysPlaylistCoverPreference()
    {
        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "TrackDownloader.cs"));

        Assert.Contains(
            "context.Playlist != null && !context.Settings.DlAlbumcoverForPlaylist",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var allowAlbumArtwork = playlist == null || settings.DlAlbumcoverForPlaylist;",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("opus")]
    public async Task AudioTagger_EmbedsAlbumArtworkInSupportedAudioContainer(string extension)
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-flac-artwork", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audioPath = Path.Join(root, $"track.{extension}");
            var coverPath = Path.Join(root, "cover.png");
            await RunProcessAsync(
                "ffmpeg",
                "-loglevel", "error",
                "-y",
                "-f", "lavfi",
                "-i", "anullsrc=r=44100:cl=stereo",
                "-t", "0.1",
                audioPath);
            await RunProcessAsync(
                "ffmpeg",
                "-loglevel", "error",
                "-y",
                "-f", "lavfi",
                "-i", "color=c=blue:s=64x64",
                "-frames:v", "1",
                coverPath);

            var track = new Track
            {
                Title = "Artwork Test",
                Album = new Album("Artwork Test Album") { EmbeddedCoverPath = coverPath }
            };
            var settings = new DeezSpoTagSettings
            {
                Tags = new TagSettings
                {
                    Cover = true,
                    Year = false,
                    Date = false
                }
            };
            var settingsService = new DeezSpoTagSettingsService(
                NullLogger<DeezSpoTagSettingsService>.Instance);
            var tagger = new AudioTagger(NullLogger<AudioTagger>.Instance, settingsService);

            await tagger.TagTrackAsync(audioPath, track, settings);

            if (extension == "m4a")
            {
                var taggedFile = new ATL.Track(audioPath);
                Assert.Contains(
                    taggedFile.EmbeddedPictures,
                    static picture => picture.PictureData?.Length > 0);
            }
            else
            {
                using var taggedFile = TagLib.File.Create(audioPath);
                var picture = Assert.Single(taggedFile.Tag.Pictures);
                Assert.NotEmpty(picture.Data.Data);
                Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("opus")]
    public async Task PreparedArtworkWriter_PersistsExpectedBytesAndDimensions(string extension)
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-prepared-artwork", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audioPath = Path.Join(root, $"track.{extension}");
            var coverPath = Path.Join(root, "source.png");
            await RunProcessAsync("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "anullsrc=r=44100:cl=stereo", "-t", "0.1", audioPath);
            await RunProcessAsync("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "color=c=blue:s=160x120", "-frames:v", "1", coverPath);

            var prepared = await ArtworkAssetProcessor.PrepareAsync(new ArtworkAssetRequest(
                await File.ReadAllBytesAsync(coverPath), "webp", 100, 60, false, 90, $".{extension}"));
            EmbeddedArtworkWriter.WriteAndVerify(audioPath, prepared.Embedded);

            byte[] bytes;
            if (extension == "m4a")
            {
                bytes = new ATL.Track(audioPath).EmbeddedPictures
                    .First(static picture => picture.PictureData is { Length: > 0 }).PictureData;
            }
            else
            {
                using var file = TagLib.File.Create(audioPath);
                bytes = Assert.Single(file.Tag.Pictures).Data.Data;
            }
            using var image = Image.Load(bytes);
            Assert.Equal((60, 45), (image.Width, image.Height));
            Assert.Equal("image/webp", CoverArtMimeTypeResolver.Resolve("misleading.jpg", bytes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunProcessAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static DeezSpoTagSettings Settings(bool coverEnabled, bool albumCoverForPlaylist = false)
        => new()
        {
            Tags = new TagSettings { Cover = coverEnabled },
            DlAlbumcoverForPlaylist = albumCoverForPlaylist
        };
}
