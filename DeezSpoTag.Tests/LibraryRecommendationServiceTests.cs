using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryRecommendationServiceTests
{
    private static readonly MethodInfo MergeRotatingMethod = typeof(LibraryRecommendationService).GetMethod(
        "MergeRotating",
        BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo TopUpRecommendationSelectionMethod = typeof(LibraryRecommendationService).GetMethod(
        "TopUpRecommendationSelection",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ResolveFolderContentTypeMethod = typeof(LibraryRecommendationService).GetMethod(
        "ResolveFolderContentType",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo BuildVisibleDailySelectionMethod = typeof(LibraryRecommendationService).GetMethod(
        "BuildVisibleDailySelection",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo BuildRecommendationArtworkAssignmentsMethod = typeof(LibraryRecommendationService).GetMethod(
        "BuildRecommendationArtworkAssignments",
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo BuildRecommendationUnavailableMessageMethod = typeof(LibraryRecommendationService).GetMethod(
        "BuildRecommendationUnavailableMessage",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo CreateUnavailableRecommendationDetailMethod = typeof(LibraryRecommendationService).GetMethod(
        "CreateUnavailableRecommendationDetail",
        BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void MergeRotating_UsesRecommendationPoolLimit()
    {
        var deezerTracks = CreateTracks("deezer", 240, 1);
        var shazamTracks = CreateTracks("shazam", 240, 10001);

        var result = (List<RecommendationTrackDto>)MergeRotatingMethod.Invoke(
            null,
            [deezerTracks, shazamTracks, 150, new DateOnly(2026, 4, 12)])!;

        Assert.Equal(150, result.Count);
        Assert.Equal(Enumerable.Range(1, 150), result.Select(track => track.TrackPosition));
    }

    [Fact]
    public void TopUpRecommendationSelection_FillsToRequestedLimit()
    {
        var primarySelection = CreateTracks("primary", 34, 1);
        var fallbackCandidates = CreateTracks("fallback", 180, 1001);

        var result = (List<RecommendationTrackDto>)TopUpRecommendationSelectionMethod.Invoke(
            null,
            [primarySelection, fallbackCandidates, 50, new DateOnly(2026, 4, 12)])!;

        Assert.Equal(50, result.Count);
        Assert.Equal(50, result.Select(track => track.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(1, 50), result.Select(track => track.TrackPosition));
    }

    [Fact]
    public void ResolveFolderContentType_TreatsLegacyZeroAsMusicByDefault()
    {
        var folder = new FolderDto(
            Id: 1,
            RootPath: "/music/library",
            DisplayName: "Main Music",
            Enabled: true,
            LibraryId: 1,
            LibraryName: "Library",
            DesiredQuality: "0",
            AutoTagProfileId: null,
            AutoTagEnabled: false,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);

        var contentType = (string)ResolveFolderContentTypeMethod.Invoke(null, [folder])!;
        Assert.Equal("music", contentType);
    }

    [Fact]
    public void ResolveFolderContentType_TreatsLegacyAtmosRankAsAtmos()
    {
        var folder = new FolderDto(
            Id: 1,
            RootPath: "/music/atmos",
            DisplayName: "Atmos",
            Enabled: true,
            LibraryId: 1,
            LibraryName: "Library",
            DesiredQuality: "5",
            AutoTagProfileId: null,
            AutoTagEnabled: false,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);

        var contentType = (string)ResolveFolderContentTypeMethod.Invoke(null, [folder])!;
        Assert.Equal("atmos", contentType);
    }

    [Fact]
    public void BuildVisibleDailySelection_BackfillsIgnoredTracks()
    {
        var day = new DateOnly(2026, 4, 12);
        var tracks = CreateTracks("daily", 120, 1);
        var baseline = (List<RecommendationTrackDto>)BuildVisibleDailySelectionMethod.Invoke(
            null,
            [tracks, new HashSet<string>(StringComparer.Ordinal), 50, day])!;
        var ignored = new HashSet<string>(
            baseline.Take(10).Select(track => track.Id),
            StringComparer.Ordinal);

        var result = (List<RecommendationTrackDto>)BuildVisibleDailySelectionMethod.Invoke(
            null,
            [tracks, ignored, 50, day])!;

        Assert.Equal(50, result.Count);
        Assert.DoesNotContain(result, track => ignored.Contains(track.Id));
        Assert.Equal(Enumerable.Range(1, 50), result.Select(track => track.TrackPosition));
    }

    [Fact]
    public void BuildRecommendationArtworkAssignments_IsStableAcrossSameLocalDay()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-recommendations-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(webRoot, "images", "recommendations", "set-a"));
            Directory.CreateDirectory(Path.Combine(webRoot, "images", "recommendations", "set-b"));
            File.WriteAllText(Path.Combine(webRoot, "images", "recommendations", "set-a", "sunday.jpg"), string.Empty);
            File.WriteAllText(Path.Combine(webRoot, "images", "recommendations", "set-b", "sunday.jpg"), string.Empty);
            var service = CreateRecommendationService(webRoot);
            var folders = new List<FolderDto>
            {
                CreateFolder(1, "Main Music"),
                CreateFolder(2, "Second Music")
            };

            var early = (Dictionary<string, string>)BuildRecommendationArtworkAssignmentsMethod.Invoke(
                service,
                [folders, new DateTimeOffset(2026, 4, 12, 1, 0, 0, TimeSpan.Zero)])!;
            var late = (Dictionary<string, string>)BuildRecommendationArtworkAssignmentsMethod.Invoke(
                service,
                [folders, new DateTimeOffset(2026, 4, 12, 23, 0, 0, TimeSpan.Zero)])!;

            Assert.Equal(early, late);
        }
        finally
        {
            if (Directory.Exists(webRoot))
            {
                Directory.Delete(webRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildRecommendationUnavailableMessage_GenerationQueuedMessageIsExplicit()
    {
        var message = (string)BuildRecommendationUnavailableMessageMethod.Invoke(
            null,
            [new List<string> { "generation_queued" }])!;

        Assert.Equal("Recommendation generation is running in the background. Refresh this tracklist shortly.", message);
    }

    [Fact]
    public void CreateUnavailableRecommendationDetail_UsesGeneratingStatusWhenQueued()
    {
        var scopeType = typeof(LibraryRecommendationService).GetNestedType("RecommendationScope", BindingFlags.NonPublic)!;
        var scope = Activator.CreateInstance(scopeType, 1L, 2L, "Main", "daily-rotation:l1:f2", "l1:f2");
        var detail = (RecommendationDetailDto)CreateUnavailableRecommendationDetailMethod.Invoke(
            null,
            [scope!, "/images/recommendations/V1/Sunday.jpg", new DateOnly(2026, 5, 24), new List<string> { "generation_queued" }])!;

        Assert.Equal("generating", detail.Status);
        Assert.Equal("generating", detail.Station.Status);
    }

    private static List<RecommendationTrackDto> CreateTracks(string prefix, int count, int idStart)
    {
        var tracks = new List<RecommendationTrackDto>(count);
        for (var index = 0; index < count; index++)
        {
            var id = (idStart + index).ToString();
            tracks.Add(new RecommendationTrackDto(
                id,
                $"{prefix}-title-{id}",
                180 + (index % 60),
                $"{prefix}-isrc-{id}",
                index + 1,
                new RecommendationArtistDto(
                    (100000 + (index % 24)).ToString(),
                    $"{prefix}-artist-{index % 24}"),
                new RecommendationAlbumDto(
                    (200000 + (index % 36)).ToString(),
                    $"{prefix}-album-{index % 36}",
                    $"https://example.com/{prefix}/{id}.jpg")));
        }

        return tracks;
    }

    private static FolderDto CreateFolder(long id, string name)
    {
        return new FolderDto(
            Id: id,
            RootPath: $"/music/{id}",
            DisplayName: name,
            Enabled: true,
            LibraryId: 1,
            LibraryName: "Library",
            DesiredQuality: "0",
            AutoTagProfileId: null,
            AutoTagEnabled: false,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);
    }

    private static LibraryRecommendationService CreateRecommendationService(string webRootPath)
    {
        return new LibraryRecommendationService(
            new LibraryRecommendationService.LibraryRecommendationCollaborators
            {
                DeezerRecommendations = null!,
                Repository = null!,
                ShazamRecognitionService = null!,
                ShazamDiscoveryService = null!,
                DeezerClient = null!,
                DeezerGatewayService = null!,
                SongLinkResolver = null!,
                DedupeService = null!
            },
            new TestWebHostEnvironment(webRootPath),
            NullLogger<LibraryRecommendationService>.Instance);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string webRootPath)
        {
            WebRootPath = webRootPath;
            ContentRootPath = webRootPath;
            WebRootFileProvider = new PhysicalFileProvider(webRootPath);
            ContentRootFileProvider = WebRootFileProvider;
        }

        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider ContentRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
