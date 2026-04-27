using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
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
}
