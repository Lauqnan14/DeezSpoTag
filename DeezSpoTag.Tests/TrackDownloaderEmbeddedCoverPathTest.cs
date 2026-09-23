using System;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackDownloaderEmbeddedCoverPathTest
{
    [Fact]
    public void ApplyEmbeddedCoverPathForTagging_CopiesContextAlbumCoverOntoTrackAlbum()
    {
        var trackAlbum = new Album("Track Album");
        var contextAlbum = new Album("Context Album");
        var track = new Track
        {
            Title = "All Over You",
            Album = trackAlbum
        };

        InvokePrivateStatic(
            "ApplyEmbeddedCoverPathForTagging",
            track,
            contextAlbum,
            "/tmp/deezspotag-embedded-cover.jpg");

        Assert.Equal("/tmp/deezspotag-embedded-cover.jpg", contextAlbum.EmbeddedCoverPath);
        Assert.Same(trackAlbum, track.Album);
        Assert.Equal("/tmp/deezspotag-embedded-cover.jpg", track.Album.EmbeddedCoverPath);
    }

    [Fact]
    public void EnsureTrackEmbeddedCoverPathForTagging_UsesContextAlbumWhenTrackAlbumIsMissingCover()
    {
        var trackAlbum = new Album("Track Album");
        var contextAlbum = new Album("Context Album")
        {
            EmbeddedCoverPath = "/tmp/deezspotag-context-cover.jpg"
        };
        var track = new Track
        {
            Title = "All Over You",
            Album = trackAlbum
        };

        InvokePrivateStatic(
            "EnsureTrackEmbeddedCoverPathForTagging",
            track,
            contextAlbum);

        Assert.Equal("/tmp/deezspotag-context-cover.jpg", track.Album!.EmbeddedCoverPath);
    }

    [Fact]
    public void EnsureTrackEmbeddedCoverPathForTagging_DoesNotReplaceExistingTrackAlbumCover()
    {
        var trackAlbum = new Album("Track Album")
        {
            EmbeddedCoverPath = "/tmp/deezspotag-existing-cover.jpg"
        };
        var contextAlbum = new Album("Context Album")
        {
            EmbeddedCoverPath = "/tmp/deezspotag-context-cover.jpg"
        };
        var track = new Track
        {
            Title = "All Over You",
            Album = trackAlbum
        };

        InvokePrivateStatic(
            "EnsureTrackEmbeddedCoverPathForTagging",
            track,
            contextAlbum);

        Assert.Equal("/tmp/deezspotag-existing-cover.jpg", track.Album!.EmbeddedCoverPath);
    }

    [Fact]
    public async Task PrepareDirectDownloadArtworkAsync_UsesProfileSizesAndWritesEverySelectedFormat()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-direct-artwork", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Join(root, "source.jpg");
            using (var image = new Image<Rgba32>(1200, 900))
            {
                await image.SaveAsPngAsync(sourcePath);
            }
            var settings = new DeezSpoTagSettings
            {
                SaveArtwork = true,
                LocalArtworkFormat = "jpg,png,webp",
                LocalArtworkSize = 600,
                EmbeddedArtworkSize = 300,
                EmbedMaxQualityCover = false,
                JpegImageQuality = 75,
                OverwriteFile = "y"
            };
            var method = typeof(TrackDownloader).GetMethod(
                "PrepareDirectDownloadArtworkAsync",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Direct artwork preparation method not found.");
            var task = (Task<string>)method.Invoke(null,
                [sourcePath, root, "cover", ".flac", settings, CancellationToken.None])!;
            var embeddedPath = await task;

            foreach (var extension in new[] { "jpg", "png", "webp" })
            {
                using var sidecar = await Image.LoadAsync(Path.Join(root, $"cover.{extension}"));
                Assert.Equal((600, 450), (sidecar.Width, sidecar.Height));
            }
            using var embedded = await Image.LoadAsync(embeddedPath);
            Assert.Equal((300, 225), (embedded.Width, embedded.Height));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void InvokePrivateStatic(string methodName, params object?[] args)
    {
        var method = typeof(TrackDownloader).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        _ = method.Invoke(null, args);
    }
}
