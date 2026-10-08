using System.Collections.Generic;
using System.Linq;
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
    public void FolderOrder_IsAlphabeticalByDisplayName()
    {
        var folders = new[]
        {
            Folder(7, "Zulu"),
            Folder(3, "Alpha"),
            Folder(5, "Middle")
        };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(folders);

        Assert.Equal([3, 5, 7], ordered);
    }

    [Fact]
    public void FolderOrder_AlwaysCoversEveryEnabledMusicFolder()
    {
        // Regression: a stored folder order containing a single folder id used to
        // restrict analysis to that folder, so every other library and any new
        // files in it were silently never analysed. Ordering no longer accepts any
        // order input, so every enabled music folder is always in scope.
        var folders = new[]
        {
            Folder(11, "Charlie"),
            Folder(22, "Alpha"),
            Folder(33, "Bravo"),
            Folder(44, "Delta"),
            Folder(55, "Echo")
        };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(folders);

        Assert.Equal([22, 33, 11, 44, 55], ordered);
        Assert.Equal(folders.Length, ordered.Count);
    }

    [Fact]
    public void FolderOrder_BreaksTiesOnFolderIdSoOrderingIsFullyDeterministic()
    {
        var folders = new[] { Folder(7, "Zulu"), Folder(5, "Alpha"), Folder(3, "Alpha") };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(folders);

        Assert.Equal([3, 5, 7], ordered);
    }

    [Fact]
    public void CustomFolderOrder_IsHonouredWhenEnabled()
    {
        var folders = new[]
        {
            Folder(11, "Charlie"),
            Folder(22, "Alpha"),
            Folder(33, "Bravo")
        };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(
            folders,
            Settings(useLibraryOrder: true, libraryOrder: [33, 11]));

        Assert.Equal([33, 11, 22], ordered);
    }

    [Fact]
    public void CustomFolderOrder_StillCoversEveryFolderWhenTheStoredOrderGoesStale()
    {
        // The bug this feature previously had: a stored order treated as the whole
        // scope, so a list left stale by a renamed or re-added library silently stopped
        // that library being analysed at all. Omissions are appended, never dropped.
        var folders = new[]
        {
            Folder(11, "Charlie"),
            Folder(22, "Alpha"),
            Folder(33, "Bravo"),
            Folder(44, "Delta")
        };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(
            folders,
            Settings(useLibraryOrder: true, libraryOrder: [44]));

        Assert.Equal(folders.Length, ordered.Count);
        Assert.Equal([44, 22, 33, 11], ordered);
        Assert.Equal(folders.Select(f => f.Id).Order(), ordered.Order());
    }

    [Fact]
    public void CustomFolderOrder_IgnoresIdsThatNoLongerResolveToALibrary()
    {
        // A stored order outlives the libraries it names. An id that no longer resolves
        // must not be passed downstream as a scope, or it silently narrows the run.
        var folders = new[] { Folder(22, "Alpha"), Folder(33, "Bravo") };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(
            folders,
            Settings(useLibraryOrder: true, libraryOrder: [999_999, 33, 888_888]));

        Assert.Equal([33, 22], ordered);
    }

    [Fact]
    public void CustomFolderOrder_IsIgnoredWhileTheToggleIsOff()
    {
        var folders = new[] { Folder(22, "Alpha"), Folder(33, "Bravo") };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(
            folders,
            Settings(useLibraryOrder: false, libraryOrder: [33]));

        Assert.Equal([22, 33], ordered);
    }

    [Fact]
    public void CustomFolderOrder_FallsBackToAlphabeticalWhenTheStoredOrderIsEmpty()
    {
        // Enabling the toggle with nothing stored must not analyse nothing.
        var folders = new[] { Folder(11, "Charlie"), Folder(22, "Alpha") };

        var ordered = TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(
            folders,
            Settings(useLibraryOrder: true, libraryOrder: []));

        Assert.Equal([22, 11], ordered);
    }

    [Fact]
    public void CustomFolderOrder_WithNoSettingsAtAllStaysAlphabetical()
    {
        var folders = new[] { Folder(7, "Zulu"), Folder(3, "Alpha") };

        Assert.Equal([3, 7], TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(folders));
        Assert.Equal([3, 7], TrackAnalysisBackgroundService.ResolveAnalysisFolderOrder(folders, null));
    }

    private static VibeAnalysisSettingsDto Settings(bool useLibraryOrder, long[] libraryOrder)
        => VibeAnalysisSettingsDto.Defaults() with
        {
            UseLibraryOrder = useLibraryOrder,
            LibraryOrder = libraryOrder,
        };

    private static TrackAnalysisInputDto Track(long id, string path)
        => new(id, 1, path, 180000);

    private static FolderDto Folder(long id, string name)
        => new(id, $"/music/{id}", name, true, id, name, "flac", null, true, false, null, null);
}
