using System.Collections.Generic;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class VibeAnalysisStablePassTest
{
    [Fact]
    public void StablePassRanges_KeepAlbumsTogetherAcrossBatchBoundary()
    {
        var snapshot = new[]
        {
            Track(1, "/music/Artist/Album A/01.flac"),
            Track(2, "/music/Artist/Album A/02.flac"),
            Track(3, "/music/Artist/Album A/03.flac"),
            Track(4, "/music/Artist/Album B/01.flac"),
            Track(5, "/music/Artist/Album B/02.flac")
        };

        var ranges = TrackAnalysisBackgroundService.BuildStablePassRanges(snapshot, batchSize: 2);

        Assert.Equal([(0, 3), (3, 5)], ranges);
    }

    [Fact]
    public void FolderOrder_UsesCustomOrderAndExcludesUnavailableFolders()
    {
        var folders = new[]
        {
            Folder(7, "Zulu"),
            Folder(3, "Alpha"),
            Folder(5, "Middle")
        };
        var settings = new VibeAnalysisSettingsDto(true, 20, 30, true, [5, 99, 7, 3]);

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(settings, folders);

        Assert.Equal([5, 7, 3], ordered);
    }

    [Fact]
    public void FolderOrder_IsDeterministicWithoutCustomOrder()
    {
        var folders = new[] { Folder(7, "Zulu"), Folder(5, "Alpha"), Folder(3, "Alpha") };
        var settings = new VibeAnalysisSettingsDto(true, 20, 30, false, []);

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(settings, folders);

        Assert.Equal([3, 5, 7], ordered);
    }

    private static TrackAnalysisInputDto Track(long id, string path)
        => new(id, 1, path, 180000);

    private static FolderDto Folder(long id, string name)
        => new(id, $"/music/{id}", name, true, id, name, "flac", null, true, false, null, null);
}
