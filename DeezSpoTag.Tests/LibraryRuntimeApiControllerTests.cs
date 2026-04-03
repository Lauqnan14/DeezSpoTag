using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryRuntimeApiControllerTests
{
    [Fact]
    public async Task Get_ReturnsExpectedContractShape()
    {
        var provider = new StubRuntimeProvider();
        var controller = new LibraryRuntimeApiController(provider);

        var result = await controller.Get(folderId: 42, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("scanStatus", out var scanStatus));
        Assert.True(scanStatus.TryGetProperty("running", out _));
        Assert.True(scanStatus.TryGetProperty("progress", out _));
        Assert.True(scanStatus.TryGetProperty("lastCounts", out _));

        Assert.True(root.TryGetProperty("stats", out var stats));
        Assert.True(stats.TryGetProperty("totals", out _));
        Assert.True(stats.TryGetProperty("libraries", out _));

        Assert.True(root.TryGetProperty("refreshPolicy", out var policy));
        Assert.True(policy.TryGetProperty("scanStatusActiveMs", out _));
        Assert.True(policy.TryGetProperty("scanStatusIdleMs", out _));
        Assert.True(policy.TryGetProperty("analysisMs", out _));
        Assert.True(policy.TryGetProperty("minArtistRefreshMs", out _));

        Assert.Equal(42, provider.LastRequestedFolderId);
    }

    [Fact]
    public async Task Get_ForwardsNullFolderId_ToProvider()
    {
        var provider = new StubRuntimeProvider();
        var controller = new LibraryRuntimeApiController(provider);

        var result = await controller.Get(folderId: null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Null(provider.LastRequestedFolderId);
    }

    private sealed class StubRuntimeProvider : ILibraryRuntimeSnapshotProvider
    {
        public long? LastRequestedFolderId { get; private set; }

        public Task<LibraryRuntimeSnapshotService.LibraryRuntimeSnapshotDto> BuildSnapshotAsync(long? folderId, CancellationToken cancellationToken)
        {
            LastRequestedFolderId = folderId;

            var snapshot = new LibraryRuntimeSnapshotService.LibraryRuntimeSnapshotDto(
                ScanStatus: new
                {
                    lastRunUtc = DateTimeOffset.UtcNow,
                    lastCounts = new { artists = 10, albums = 20, tracks = 30 },
                    running = true,
                    progress = new
                    {
                        processedFiles = 5,
                        totalFiles = 10,
                        errorCount = 0,
                        currentFile = "track.flac",
                        artistsDetected = 2,
                        albumsDetected = 3,
                        tracksDetected = 4
                    },
                    dbConfigured = true
                },
                Stats: new
                {
                    totals = new { artists = 10, albums = 20, tracks = 30, videoItems = 0, podcastItems = 0 },
                    lastRunUtc = DateTimeOffset.UtcNow,
                    libraries = Array.Empty<object>(),
                    detail = (object?)null
                },
                RefreshPolicy: new LibraryRuntimeSnapshotService.LibraryRefreshPolicyDto(5000, 15000, 15000, 10000));

            return Task.FromResult(snapshot);
        }
    }
}
