using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class RecommendationImplementationContractTests
{
    [Fact]
    public async Task RejectRecommendation_ReturnsBadRequest_WhenTrackSourceIdIsNotNumeric()
    {
        var controller = new LibraryRecommendationsApiController(recommendationService: null!);

        var result = await controller.RejectRecommendation(
            new LibraryRecommendationsApiController.RecommendationRejectRequest(
                LibraryId: 1,
                FolderId: null,
                StationId: "daily-rotation:l1:f1",
                TrackSourceId: "not-a-deezer-track-id",
                Isrc: null,
                Title: null,
                Artist: null),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("trackSourceId must be a numeric Deezer track id.", badRequest.Value);
    }

    [Fact]
    public void StationsEndpoint_QueuesMissingDailyPools()
    {
        var source = ReadRecommendationServiceSource();
        var method = ExtractBetween(
            source,
            "public async Task<IReadOnlyList<RecommendationStationDto>> GetStationsAsync",
            "public async Task<RecommendationDetailDto?> GetRecommendationsAsync");

        Assert.DoesNotContain("GetRecommendationsAsync(", method, StringComparison.Ordinal);
        Assert.Contains("GetDailyPoolAsync(", method, StringComparison.Ordinal);
        Assert.Contains("CreateMissingDailyPoolResponseAsync(", method, StringComparison.Ordinal);
        Assert.Contains("stations.Add(missingDetail.Station)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectRecommendation_ResolvesCanonicalScopeBeforeWritingRejection()
    {
        var source = ReadRecommendationServiceSource();
        var method = ExtractBetween(
            source,
            "public async Task<RecommendationDetailDto?> RejectRecommendationTrackAsync",
            "private async Task<RecommendationBuildResult> BuildDailyPoolAsync");

        var resolveIndex = method.IndexOf("ResolveScope(", StringComparison.Ordinal);
        var writeIndex = method.IndexOf("AddRecommendationRejectionAsync", StringComparison.Ordinal);

        Assert.True(resolveIndex >= 0, "RejectRecommendationTrackAsync must resolve a canonical recommendation scope.");
        Assert.True(writeIndex > resolveIndex, "Recommendation rejection must be written only after scope resolution.");
        Assert.Contains("scope.LibraryId", method, StringComparison.Ordinal);
        Assert.Contains("scope.FolderId", method, StringComparison.Ordinal);
        Assert.Contains("scope.StationId", method, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRecommendationRebuild_DeletesPersistedDailyPool()
    {
        var source = ReadRecommendationServiceSource();
        var method = ExtractBetween(
            source,
            "public async Task<RecommendationDetailDto?> RebuildRecommendationsAsync",
            "public async Task<RecommendationDetailDto?> RejectRecommendationTrackAsync");
        var coordinator = ExtractBetween(
            source,
            "private async Task<bool> RunDailyRecommendationGenerationAsync",
            "private async Task FailRecommendationGenerationAsync");

        Assert.Contains("_dailyPoolCache.TryRemove(cacheKey, out _)", method, StringComparison.Ordinal);
        Assert.Contains("RunDailyRecommendationGenerationAsync", method, StringComparison.Ordinal);
        Assert.Contains("forceReset: true", method, StringComparison.Ordinal);
        Assert.Contains("DeletePlaylistTrackCandidateCacheAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("DailyPoolCacheSource", coordinator, StringComparison.Ordinal);
        Assert.Contains("scope.ScopeKey", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshDailyRecommendationFolder_DoesNotAbortRunOnNonShutdownTimeout()
    {
        var source = ReadRecommendationServiceSource();
        var method = ExtractBetween(
            source,
            "private async Task RefreshDailyRecommendationFolderAsync",
            "private async Task RefreshDailyRecommendationScopeAsync");

        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", method, StringComparison.Ordinal);
        Assert.Contains("Daily recommendation refresh timed out", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendationGeneration_UsesPersistentStateAndNoInMemoryBuildAuthority()
    {
        var source = ReadRecommendationServiceSource();

        Assert.Contains("RequestRecommendationGenerationAsync", source, StringComparison.Ordinal);
        Assert.Contains("TryStartRecommendationGenerationAsync", source, StringComparison.Ordinal);
        Assert.Contains("CompleteRecommendationGenerationAsync", source, StringComparison.Ordinal);
        Assert.Contains("FailRecommendationGenerationAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_backgroundDailyPoolBuilds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_dailyPoolBuildFailures", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryEnterDailyPoolBuild", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordDailyPoolBuildFailure", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendationAutomation_PassesRefreshReasonIntoService()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepoRoot(),
            "DeezSpoTag.Web",
            "Services",
            "LibraryRecommendationAutomationHostedService.cs"));

        Assert.Contains("_recommendationService.RefreshDailyRecommendationsAsync(reason, cancellationToken)", source, StringComparison.Ordinal);
    }

    private static string ReadRecommendationServiceSource()
        => File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "LibraryRecommendationService.cs"));

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"{startMarker} was not found.");
        }

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"{endMarker} was not found.");
        }

        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "DeezSpoTag.Web")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
