using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DuplicationConsolidationTests
{
    [Fact]
    public void SpotifyContentCandidates_TraversesNestedDataInPriorityOrder()
    {
        using var document = JsonDocument.Parse("""
            {"name":"outer","data":{"name":"inner","data":{"name":"deep"}}}
            """);

        var candidates = SpotifyContentCandidates.Expand(document.RootElement).ToList();

        Assert.Equal(3, candidates.Count);
        Assert.Equal("outer", SpotifyContentCandidates.FirstString(
            candidates,
            candidate => candidate.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData(true, "/music", "FLAC", true)]
    [InlineData(false, "/music", "FLAC", false)]
    [InlineData(true, "", "FLAC", false)]
    [InlineData(true, "/music", "Video", false)]
    [InlineData(true, "/music", "Podcast", false)]
    public void WatchlistDestinationFolderResolver_PreservesMusicEligibility(
        bool enabled,
        string rootPath,
        string desiredQuality,
        bool expected)
    {
        var folder = new FolderDto(
            1,
            rootPath,
            "Test",
            enabled,
            null,
            null,
            desiredQuality,
            "default",
            true,
            false,
            null,
            null);

        Assert.Equal(expected, WatchlistDestinationFolderResolver.IsMusicDestinationFolder(folder));
    }

    [Fact]
    public void DownloadEngineOrderDefaults_PreserveEngineAndQualityOrder()
    {
        var settings = DownloadEngineOrderSettings.CreateDefault();

        Assert.False(settings.Enabled);
        Assert.Collection(
            settings.Engines,
            engine => AssertEngine(engine, "qobuz", "27", "7", "6", "5"),
            engine => AssertEngine(engine, "tidal", "HI_RES_LOSSLESS", "HI_RES", "LOSSLESS", "HIGH", "LOW", "DOLBY_ATMOS"),
            engine => AssertEngine(engine, "apple", "ALAC", "AAC", "ATMOS"),
            engine => AssertEngine(engine, "amazon", "ULTRA_HD_FLAC", "HD_FLAC", "OPUS", "DOLBY_ATMOS"),
            engine => AssertEngine(engine, "deezer", "9", "3", "1"));
    }

    [Fact]
    public void SharedVideoPreview_IsLoadedAndUsedByAllThreeSurfaces()
    {
        var root = ResolveRepoRoot();
        var layout = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Shared", "_Layout.cshtml"));
        var artist = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Artist", "Index.cshtml"));
        var search = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Search", "Index.cshtml"));
        var extras = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "library-apple-extras.js"));
        var shared = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "video-preview.js"));

        Assert.Contains("~/js/video-preview.js", layout, StringComparison.Ordinal);
        Assert.Contains("DeezSpoTagVideoPreview.play", artist, StringComparison.Ordinal);
        Assert.Contains("DeezSpoTagVideoPreview.play", search, StringComparison.Ordinal);
        Assert.Contains("DeezSpoTagVideoPreview.play", extras, StringComparison.Ordinal);
        Assert.Contains("hls.js@1.5.17", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("hls.js@1.5.17", artist, StringComparison.Ordinal);
        Assert.DoesNotContain("hls.js@1.5.17", search, StringComparison.Ordinal);
        Assert.DoesNotContain("hls.js@1.5.17", extras, StringComparison.Ordinal);
    }

    private static void AssertEngine(DownloadEngineOrderItem engine, string name, params string[] qualities)
    {
        Assert.Equal(name, engine.Engine);
        Assert.True(engine.Enabled);
        Assert.Equal(qualities, engine.Qualities.Select(quality => quality.Quality));
        Assert.All(engine.Qualities, quality => Assert.True(quality.Enabled));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
