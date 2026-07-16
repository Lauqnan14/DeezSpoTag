using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EmbeddedArtworkPreferenceTests
{
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
