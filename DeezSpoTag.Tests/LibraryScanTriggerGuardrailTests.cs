using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryScanTriggerGuardrailTests
{
    private static readonly long[] ExpectedChangedFolderIds = [5L];

    [Fact]
    public void DownloadOrchestration_UsesTargetedChangedFileScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("GetRecentMovedAudioFilesByDestinationAsync", source);
        Assert.Contains("_scanRunner.RunChangedFilesAsync", source);
        Assert.DoesNotContain("_scanRunner.RunAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await _scanRunner.RunChangedFoldersAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryStatusPolling_DoesNotReloadArtistsDuringActiveScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library.js");
        var methodStart = source.IndexOf("async function refreshArtistsDuringActiveScan", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var activeScanStart = source.IndexOf("libraryState.wasScanRunning = true;", methodStart, StringComparison.Ordinal);
        Assert.True(activeScanStart >= 0);
        var methodEnd = source.IndexOf("\n}\n\nasync function saveLibrarySettings", activeScanStart, StringComparison.Ordinal);
        Assert.True(methodEnd > activeScanStart);
        var activeScanBody = source.Substring(activeScanStart, methodEnd - activeScanStart);

        Assert.DoesNotContain("loadArtists()", activeScanBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagPostMoveScan_UsesTargetedChangedFileScansWhenPathsAreKnown()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("ResolveChangedLibraryFolderIdsAsync", source);
        Assert.Contains("autoMoveSummary.ChangedFilePaths", source);
        Assert.Contains("_libraryScanRunner.RunChangedFilesAsync", source);
        Assert.Contains("_libraryScanRunner.RunChangedFoldersAsync", source);
        Assert.DoesNotContain("_libraryScanRunner.EnqueueAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_IsNotRegisteredAsHostedService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Program.cs");

        Assert.DoesNotContain("LibraryRealtimeScanService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagMoveSummary_TracksChangedFolderIdsAndFilePaths()
    {
        var summary = new AutoTagMoveSummary();

        summary.MarkChangedFolder(5);
        summary.MarkChangedFolder(5);
        summary.MarkChangedFolder(0);
        summary.MarkChangedFile("/music/Artist/Album/Track.flac");
        summary.MarkChangedFile("/music/Artist/Album/Track.flac");
        var clone = summary.Clone();

        Assert.Equal(ExpectedChangedFolderIds, summary.ChangedFolderIds);
        Assert.Equal(ExpectedChangedFolderIds, clone.ChangedFolderIds);
        Assert.Equal(["/music/Artist/Album/Track.flac"], summary.ChangedFilePaths);
        Assert.Equal(["/music/Artist/Album/Track.flac"], clone.ChangedFilePaths);
    }

    private static string ReadSource(params string[] pathParts)
    {
        var path = Path.Combine(GetRepositoryRoot(), Path.Combine(pathParts));
        return File.ReadAllText(path);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
